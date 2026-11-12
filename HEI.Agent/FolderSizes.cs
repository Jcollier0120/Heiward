// /*
//     Copyright (C) 2026 Jeremy Collier
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent {
	/// <summary>What a folder holds, everything below it counted.</summary>
	/// <param name="Bytes">
	/// Size on disk: the space its files take on this PC's drive (Windows' "Size on disk"), not their logical size. A file
	/// kept only in the cloud (OneDrive's "available when online") takes next to nothing; a compressed or sparse file, what
	/// it really takes.
	/// </param>
	/// <param name="Files">The files below it.</param>
	/// <param name="Unreadable">Folders below it that Windows wouldn't list for Heiward: what's in them isn't counted.</param>
	/// <param name="Denied">The folder itself couldn't be listed: nothing in it is counted.</param>
	/// <param name="Link">
	/// A link to another place (a junction, a symbolic link, a drive mounted in a folder): never followed, and nothing
	/// counted, so what it points to is counted once, where it is.
	/// </param>
	readonly record struct FolderTotals(long Bytes, long Files, int Unreadable, bool Denied = false, bool Link = false) {
		public static readonly FolderTotals LinkTotals = new(0, 0, 0, Link: true);
		public static readonly FolderTotals DeniedTotals = new(0, 0, 0, Denied: true);

		/// <summary>This folder's totals with a subfolder's added: a subfolder that couldn't be listed counts as one unreadable.</summary>
		public FolderTotals With(FolderTotals sub) =>
			sub.Link ? this : this with { Bytes = Bytes + sub.Bytes, Files = Files + sub.Files, Unreadable = Unreadable + sub.Unreadable + (sub.Denied ? 1 : 0) };
	}

	/// <summary>One folder's size for the page, or null fields while it's measured.</summary>
	sealed record FolderSizeView(string Path, long? Bytes, long? Files, int Unreadable, bool Denied, bool Link, DateTime? MeasuredAtUtc);

	/// <summary>A folder the page shows, its size and its subfolders' (each as soon as it's measured).</summary>
	/// <param name="Pending">Not all of it is measured yet, or it's being measured again: the page asks again shortly.</param>
	sealed record FolderSizesView(string Path, FolderSizeView? Own, List<FolderSizeView> Children, bool Pending);

	/// <summary>
	/// The size on disk of the folders the review page shows, beside each one in the folder tree and the Folders table, so
	/// it shows where the data is. Measured lazily, for the folders the page asks about (an open folder: itself and its
	/// subfolders), on one thread of its own below normal priority, in Windows' background mode (low disk and memory
	/// priority), never on a request: a request answers from what's measured and asks for the rest, and the page asks
	/// again until it has it all. C:\Users or C:\Windows take seconds; nothing waits for them.
	/// <para>
	/// Kept in memory only, for <see cref="Keep"/>, and measured again after that when the page asks: nothing is written to
	/// disk, ever. Measuring a folder keeps its subfolders' sizes too, and their subfolders', so opening one is quick; up to
	/// <see cref="MaxEntries"/> folders are kept, the oldest let go first. A folder's own listing (FILE_FULL_DIR_INFO) gives
	/// each file's size on disk with its name, so nothing is opened but the folders. Links are never followed, and what
	/// Windows won't let Heiward list is skipped and counted (<see cref="FolderTotals.Unreadable"/>). Hard links count once
	/// per name, as File Explorer counts them.
	/// </para>
	/// </summary>
	sealed class FolderSizes {
		/// <summary>The review page's.</summary>
		public static readonly FolderSizes Shared = new(() => DateTime.UtcNow, background: true);

		/// <summary>How long a size stands before the page's asking measures it again.</summary>
		internal static readonly TimeSpan Keep = TimeSpan.FromMinutes(15);

		/// <summary>A folder the page stopped asking about this long ago isn't measured (any further).</summary>
		internal static readonly TimeSpan Wanted = TimeSpan.FromSeconds(60);

		/// <summary>The most folders kept: tens of bytes each, a few megabytes in all.</summary>
		internal const int MaxEntries = 20_000;

		/// <summary>The most folders one request asks about: the open folders of the tree, and the one shown.</summary>
		public const int MaxAsked = 32;

		readonly Func<DateTime> clock;
		readonly bool background;
		readonly object gate = new();
		readonly Dictionary<string, (FolderTotals Totals, DateTime MeasuredUtc)> sizes = new(StringComparer.OrdinalIgnoreCase);
		/// <summary>Each measured folder's subfolders, as it was listed: the page's rows.</summary>
		readonly Dictionary<string, List<string>> subfolders = new(StringComparer.OrdinalIgnoreCase);
		/// <summary>The folders to measure, in the order the page last asked for them, and when each was last asked about.</summary>
		readonly List<string> queue = new();
		readonly Dictionary<string, DateTime> asked = new(StringComparer.OrdinalIgnoreCase);
		/// <summary>Each folder's place in the page's last question: the folder shown first, then the tree's deepest open ones.</summary>
		readonly Dictionary<string, int> rank = new(StringComparer.OrdinalIgnoreCase);
		string? measuring;
		readonly AutoResetEvent signal = new(false);
		Thread? worker;

		/// <param name="background">Measure on a thread of its own; otherwise only <see cref="Drain"/> measures (tests).</param>
		internal FolderSizes(Func<DateTime> clock, bool background = false) {
			this.clock = clock;
			this.background = background;
		}

		/// <summary>
		/// The sizes of <paramref name="folders"/> and their subfolders as far as they're measured, from memory: the rest are
		/// asked for, these first, and measured meanwhile. Reads nothing from disk.
		/// </summary>
		public List<FolderSizesView> Look(IReadOnlyList<string> folders) {
			DateTime now = clock();
			var views = new List<FolderSizesView>();
			lock (gate) {
				var stale = new List<string>();
				foreach (string requested in folders) {
					string folder = Key(requested);
					bool fresh = Fresh(folder, now) != null;
					List<string>? subs = subfolders.GetValueOrDefault(folder);
					var children = subs?.Select(s => View(s)).ToList() ?? new();
					// A subfolder let go (MaxEntries) is measured again with its folder; the others it already has are reused.
					bool complete = fresh && subs != null && subs.All(s => Fresh(s, now) != null);
					bool inProgress = measuring?.Equals(folder, StringComparison.OrdinalIgnoreCase) == true;
					if (!complete && !inProgress) stale.Add(folder);
					views.Add(new FolderSizesView(requested, sizes.ContainsKey(folder) ? View(folder) : null, children, !complete || inProgress));
				}
				// In the order asked: the folder shown, then the tree's. A folder ahead of the one being measured is
				// measured first; the other goes on after it, from the subfolders it hasn't measured yet.
				int at = 0;
				foreach (string folder in folders.Select(Key)) {
					asked[folder] = now;
					rank[folder] = at++;
				}
				foreach (string folder in stale)
					if (!queue.Contains(folder, StringComparer.OrdinalIgnoreCase)) queue.Add(folder);
				Order();
				if (asked.Count > 8 * MaxAsked)
					foreach (string old in asked.Where(a => now - a.Value >= Wanted).Select(a => a.Key).ToList()) {
						asked.Remove(old);
						rank.Remove(old);
					}
				if (stale.Count > 0 && background && worker == null) {
					worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Heiward folder sizes" };
					worker.Start();
				}
			}
			if (background) signal.Set();
			return views;
		}

		/// <summary>Measures every folder asked for and still wanted, here and now: the page's thread does it in the background.</summary>
		internal void Drain() {
			while (Next() is { } folder) MeasureAsked(folder);
		}

		/// <summary>
		/// What Heiward moved to the Recycle Bin or deleted: the folders above each path, measured before, are measured again
		/// when the page asks next.
		/// </summary>
		public void Forget(IEnumerable<string> paths) {
			var gone = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
			if (gone.Count == 0) return;
			lock (gate) {
				// Their subfolders stay listed, each beside it keeping its size: a folder that went shows as measured again.
				foreach (string folder in sizes.Keys.Where(f => gone.Any(p => ListingPlan.IsUnder(p, f))).ToList()) sizes.Remove(folder);
			}
		}

		/// <summary>
		/// The totals of <paramref name="folder"/>, measured now (and kept, with its subfolders' and theirs): for a test,
		/// or the thread of its own.
		/// </summary>
		internal FolderTotals Measure(string folder, CancellationToken ct = default) => Measure(Key(folder), depth: 1, () => ct.ThrowIfCancellationRequested());

		static string Key(string folder) {
			string full = Path.GetFullPath(folder);
			return Path.GetPathRoot(full) is { } root && root.Length >= full.Length ? root : Path.TrimEndingDirectorySeparator(full);
		}

		FolderSizeView View(string folder) => sizes.TryGetValue(folder, out var s)
			? new FolderSizeView(folder, s.Totals.Bytes, s.Totals.Files, s.Totals.Unreadable, s.Totals.Denied, s.Totals.Link, s.MeasuredUtc)
			: new FolderSizeView(folder, null, null, 0, false, false, null);

		/// <summary>Under the lock: the folder's totals if they're recent enough to stand.</summary>
		FolderTotals? Fresh(string folder, DateTime now) =>
			sizes.TryGetValue(folder, out var s) && now - s.MeasuredUtc < Keep && s.MeasuredUtc <= now.AddMinutes(1) ? s.Totals : null;

		FolderTotals? FreshNow(string folder) {
			DateTime now = clock();
			lock (gate) return Fresh(folder, now);
		}

		void Record(string folder, FolderTotals totals) {
			lock (gate) {
				sizes[folder] = (totals, clock());
				if (sizes.Count <= MaxEntries) return;
				// The oldest go: measured longest ago, they're the first a page would measure again anyway.
				foreach (string old in sizes.OrderBy(s => s.Value.MeasuredUtc).Take(MaxEntries / 5).Select(s => s.Key).ToList()) {
					sizes.Remove(old);
					subfolders.Remove(old);
				}
			}
		}

		/// <summary>The next folder to measure: the first asked about lately; those the page stopped asking about are let go.</summary>
		string? Next() {
			DateTime now = clock();
			lock (gate) {
				queue.RemoveAll(q => !StillWanted(q, now));
				measuring = queue.Count > 0 ? queue[0] : null;
				if (measuring != null) queue.RemoveAt(0);
				return measuring;
			}
		}

		bool StillWanted(string folder, DateTime now) => asked.TryGetValue(folder, out DateTime at) && now - at < Wanted;

		/// <summary>Under the lock: the queue in the order the page asked, stably.</summary>
		void Order() {
			var ordered = queue.OrderBy(Rank).ToList();
			queue.Clear();
			queue.AddRange(ordered);
		}

		int Rank(string folder) => rank.GetValueOrDefault(folder, int.MaxValue);

		/// <summary>Stops a folder's measuring for one the page asked for ahead of it.</summary>
		sealed class GiveWay : Exception;

		void MeasureAsked(string folder) {
			try {
				Measure(folder, depth: 1, () => {
					DateTime now = clock();
					bool wanted, giveWay;
					lock (gate) {
						wanted = StillWanted(folder, now);
						giveWay = queue.Count > 0 && Rank(queue[0]) < Rank(folder);
					}
					if (!wanted) throw new OperationCanceledException();
					if (giveWay) throw new GiveWay();
				});
			}
			catch (OperationCanceledException) { /* the page moved on: what's measured stays */ }
			catch (GiveWay) {
				// What it measured so far stays: its subfolders done aren't measured again when it goes on.
				lock (gate) {
					if (!queue.Contains(folder, StringComparer.OrdinalIgnoreCase)) queue.Add(folder);
					Order();
				}
			}
			catch (Exception) {
				// Anything else: the page shows it as a folder Heiward couldn't read, and the thread goes on. Written nowhere,
				// as nothing of the sizes is.
				Record(folder, FolderTotals.DeniedTotals);
			}
			finally {
				lock (gate) measuring = null;
			}
		}

		void Work() {
			FolderListing.BackgroundMode();
			while (true) {
				if (Next() is { } folder) MeasureAsked(folder);
				else signal.WaitOne(TimeSpan.FromMinutes(5));
			}
		}

		/// <summary>
		/// Measures <paramref name="folder"/>, keeping its totals and its subfolders', and with <paramref name="depth"/> 1 their
		/// subfolders' too. Subfolders measured lately are taken as they are; the rest are walked, never through a link.
		/// <paramref name="check"/> runs for each folder listed, and throws to stop.
		/// </summary>
		FolderTotals Measure(string folder, int depth, Action check) {
			check();
			List<DirEntry>? entries = FolderListing.List(folder);
			if (entries == null) {
				Record(folder, FolderTotals.DeniedTotals);
				lock (gate) subfolders[folder] = new();
				return FolderTotals.DeniedTotals;
			}
			var total = new FolderTotals(0, 0, 0);
			var subs = new List<(string Path, bool Link)>();
			foreach (DirEntry e in entries) {
				if (e.IsFolder) subs.Add((Path.Join(folder, e.Name), e.IsLink));
				else total = total with { Bytes = total.Bytes + (e.IsLink ? 0 : e.Allocated), Files = total.Files + 1 };
			}
			subs.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
			lock (gate) subfolders[folder] = subs.Select(s => s.Path).ToList();
			foreach (var (sub, link) in subs) {
				FolderTotals t;
				if (link) Record(sub, t = FolderTotals.LinkTotals);
				else if (FreshNow(sub) is { } known) t = known;
				else if (depth > 0) t = Measure(sub, depth - 1, check);
				else Record(sub, t = Sum(sub, check));
				total = total.With(t);
			}
			Record(folder, total);
			return total;
		}

		/// <summary>Everything below <paramref name="folder"/>, folder by folder, without keeping what's below it.</summary>
		FolderTotals Sum(string folder, Action check) {
			var total = new FolderTotals(0, 0, 0);
			var stack = new Stack<string>();
			stack.Push(folder);
			bool top = true;
			while (stack.Count > 0) {
				check();
				string dir = stack.Pop();
				List<DirEntry>? entries = FolderListing.List(dir);
				if (entries == null) {
					if (top) return FolderTotals.DeniedTotals;
					total = total with { Unreadable = total.Unreadable + 1 };
					continue;
				}
				top = false;
				foreach (DirEntry e in entries) {
					if (!e.IsFolder) {
						total = total with { Bytes = total.Bytes + (e.IsLink ? 0 : e.Allocated), Files = total.Files + 1 };
						continue;
					}
					if (e.IsLink) continue;
					string sub = Path.Join(dir, e.Name);
					if (FreshNow(sub) is { } known) total = total.With(known);
					else stack.Push(sub);
				}
			}
			return total;
		}
	}

	/// <summary>One entry of a folder's listing, as Windows gives it (FILE_FULL_DIR_INFO).</summary>
	/// <param name="Allocated">Its size on disk (AllocationSize).</param>
	/// <param name="Length">Its logical size (EndOfFile).</param>
	/// <param name="ReparseTag">Its reparse point's tag when it has one: Windows gives it in the EaSize field then.</param>
	readonly record struct DirEntry(string Name, long Allocated, long Length, FileAttributes Attributes, uint ReparseTag) {
		/// <summary>Microsoft's name surrogate bit: a reparse point that stands for another place.</summary>
		const uint NameSurrogate = 0x20000000;

		public bool IsFolder => (Attributes & FileAttributes.Directory) != 0;

		/// <summary>
		/// A link to another place: a junction, a symbolic link, a drive mounted in a folder (each a name surrogate). A OneDrive
		/// folder or file is a reparse point too, but holds its own files: it isn't one.
		/// </summary>
		public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0 && (ReparseTag & NameSurrogate) != 0;
	}

	/// <summary>A folder's entries as Windows lists them, with each file's size on disk: one handle, a few calls per folder.</summary>
	static class FolderListing {
		const uint ListDirectory = 0x0001, Synchronize = 0x00100000;
		const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
		const int FileFullDirectoryInfo = 14;
		const int NoMoreFiles = 18, FileNotFound = 2, PathNotFound = 3;

		[ThreadStatic] static byte[]? buffer;

		/// <summary>
		/// The entries of <paramref name="folder"/>, "." and ".." left out: null when Windows won't list it (access denied),
		/// empty when it's gone. The folder is opened as itself, never through a reparse point: a OneDrive folder's files are
		/// listed as they are on disk, and nothing is fetched from the cloud for it.
		/// </summary>
		public static List<DirEntry>? List(string folder) {
			using SafeFileHandle h = CreateFileW(Long(folder), ListDirectory | Synchronize, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
				FileMode.Open, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
			if (h.IsInvalid) {
				int error = Marshal.GetLastPInvokeError();
				return error is FileNotFound or PathNotFound ? new() : null;
			}
			var entries = new List<DirEntry>();
			byte[] buf = buffer ??= new byte[64 * 1024];
			while (GetFileInformationByHandleEx(h, FileFullDirectoryInfo, buf, (uint)buf.Length)) {
				for (int at = 0; ; ) {
					var span = buf.AsSpan(at);
					uint next = BinaryPrimitives.ReadUInt32LittleEndian(span);
					long length = BinaryPrimitives.ReadInt64LittleEndian(span[40..]);
					long allocated = BinaryPrimitives.ReadInt64LittleEndian(span[48..]);
					var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(span[56..]);
					int nameBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[60..]);
					uint ea = BinaryPrimitives.ReadUInt32LittleEndian(span[64..]);
					string name = new(MemoryMarshal.Cast<byte, char>(span.Slice(68, nameBytes)));
					if (name is not ("." or ".."))
						entries.Add(new DirEntry(name, allocated, length, attributes, (attributes & FileAttributes.ReparsePoint) != 0 ? ea : 0));
					if (next == 0) break;
					at += (int)next;
				}
			}
			int last = Marshal.GetLastPInvokeError();
			// Anything but the end of the listing midway: what was listed counts, as for a folder that changed meanwhile.
			if (last != NoMoreFiles && entries.Count == 0 && last is not (FileNotFound or PathNotFound)) return null;
			return entries;
		}

		/// <summary>The path as Windows takes it without the 260-character limit.</summary>
		internal static string Long(string path) =>
			path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
			: path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
			: @"\\?\" + path;

		const int BackgroundBegin = 0x00010000;

		/// <summary>This thread's disk and memory work in Windows' background mode: it gives way to everything else.</summary>
		public static void BackgroundMode() {
			try { SetThreadPriority(GetCurrentThread(), BackgroundBegin); }
			catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { }
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern SafeFileHandle CreateFileW(string fileName, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, [Out] byte[] info, uint size);

		[DllImport("kernel32.dll")]
		static extern IntPtr GetCurrentThread();

		[DllImport("kernel32.dll")]
		static extern bool SetThreadPriority(IntPtr thread, int priority);
	}
}
