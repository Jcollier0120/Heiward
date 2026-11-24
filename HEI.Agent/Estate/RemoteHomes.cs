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

using System.Diagnostics;
using System.Text.Json;

namespace HEI.Agent.Estate {
	/// <summary>
	/// The homes of the scans this PC runs for other PCs (<c>remote\&lt;pc&gt;</c>, <see cref="RemoteScanner.HomeFor"/>): a
	/// home is there only while a scan runs in it, and until the PC it's for has taken back its records
	/// (<see cref="ScanRecords"/>); then nothing of it stays. From the page, in the background:
	/// <list type="bullet">
	/// <item>A home whose PC hasn't taken its records back (a scan that stopped with Heiward here, or one from before this
	/// version, which kept them all here) is offered to it again (<c>scan/take</c>): it takes them, and the home goes. Where
	/// it can't be reached, or runs an older Heiward, it's asked again every <see cref="RetryEvery"/>, and this PC's page
	/// says so (<see cref="Waiting"/>). One for a PC that has left the Estate goes after <see cref="KeptFor"/>.</item>
	/// <item>A home that holds nothing to take back goes.</item>
	/// <item>What Heiward 1.11.0 to 1.11.5 kept of every PC it scanned for, a sparse stand-in of each of its files under
	/// <c>mirror</c> (670,803 files on one PC, 3.1 TB as Explorer counts them, about 1.4 GB on its disk with their records),
	/// goes: moved aside whole first, then deleted.</item>
	/// <item>Only Heiward's own: the homes under <c>remote</c>, and nothing through a link. Never under a scan: a home, or
	/// its stand-ins, is moved aside whole, an instant, while no scan for that PC can start (<see cref="Gate"/>) and none
	/// runs (its scan lock is free), and deleted where no scan looks.</item>
	/// <item>Gently: on a thread of its own in Windows' background mode (the lowest disk priority), <see cref="Batch"/> files
	/// at a time with a rest between, waiting while a game is played. Safe to cut off: what's left goes the next time (the
	/// page's next start, or the end of the next scan for another PC).</item>
	/// </list>
	/// </summary>
	static class RemoteHomes {
		/// <summary>How many files and folders go between rests.</summary>
		internal static int Batch { get; set; } = 500;
		/// <summary>The rest between batches.</summary>
		internal static TimeSpan Rest { get; set; } = TimeSpan.FromMilliseconds(200);
		/// <summary>How long after the page is up the first sweep waits: the page's own start goes first.</summary>
		static readonly TimeSpan StartAfter = TimeSpan.FromMinutes(2);
		/// <summary>While a game is played, how often it's looked at again.</summary>
		static readonly TimeSpan GameWait = TimeSpan.FromSeconds(30);
		/// <summary>How often a PC that hasn't taken its records back is asked again.</summary>
		internal static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(30);
		/// <summary>How long records are kept for a PC that has left the Estate (it can't take them back).</summary>
		internal static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);
		/// <summary>The start of a home's name once it's been let go: deleted where no scan looks.</summary>
		internal const string Gone = ".gone-";

		/// <summary>Held while a PC's home, or a part of it, is moved aside, and while a scan for another PC is started (<see cref="ScanForOthers"/>).</summary>
		internal static readonly object Gate = new();

		static readonly AutoResetEvent again = new(false);
		static int started;
		static volatile IReadOnlyList<Waits> waiting = [];

		/// <summary>The folder of the scans for other PCs: one home a PC (<see cref="RemoteScanner.HomeFor"/>).</summary>
		static string RemoteRoot => Path.Combine(AgentPaths.Home, "remote");

		/// <summary>In a home, whose it is (its PC's id and name, for this page), written as its scan starts.</summary>
		internal static string TargetFile(string home) => Path.Combine(home, "target.json");

		/// <summary>A PC this PC holds records of its scans for, which it hasn't taken back: why (<c>older</c>: its Heiward is; <c>away</c>: it can't be reached).</summary>
		internal readonly record struct Waits(string Id, string Name, string Why);

		/// <summary>What scans here found for other PCs and holds for them, as the last sweep found it, for this page.</summary>
		internal static IReadOnlyList<Waits> Waiting => waiting;

		/// <summary>The sweeps, from the page's start until it stops: one a while after it's up, one after each scan for another PC, and again while a PC hasn't taken its records back.</summary>
		public static void Start(CancellationToken ct) {
			if (Interlocked.Exchange(ref started, 1) == 1) return;
			new Thread(() => Run(ct)) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Heiward: the homes of scans for other PCs" }.Start();
		}

		/// <summary>A sweep soon: a scan for another PC ended, or a home was let go.</summary>
		public static void Kick() => again.Set();

		static void Run(CancellationToken ct) {
			FolderListing.BackgroundMode();
			if (WaitHandle.WaitAny([ct.WaitHandle, again], StartAfter) == 0) return;
			while (!ct.IsCancellationRequested) {
				Swept swept = default;
				try { swept = Sweep(RemoteRoot, ScanForOthers.ScansIn, ct, () => ScanForOthers.GameNow() != null, AgentPaths.AppendLog, id => Offer(id, ct)); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					AgentPaths.AppendLog("looking after the homes of scans for other PCs: " + e.Message);
				}
				if (WaitHandle.WaitAny([ct.WaitHandle, again], swept.Waiting > 0 ? RetryEvery : Timeout.InfiniteTimeSpan) == 0) return;
			}
		}

		/// <summary>What offering a PC its records back came to.</summary>
		internal enum Offered {
			/// <summary>It takes them now, and the home goes once it has.</summary>
			Taking,
			/// <summary>Its Heiward is older: it takes them once it's updated.</summary>
			Older,
			/// <summary>It can't be reached now.</summary>
			Away,
			/// <summary>It isn't on the Estate any more.</summary>
			Left,
		}

		/// <summary>A PC offered its records back (<c>GET …/scan/take</c>): it asks for them itself (<c>scan/kept</c>).</summary>
		static Offered Offer(string id, CancellationToken ct) {
			try {
				EstateLink.EstateView estate = EstateLink.ReadEstateAsync(ct).GetAwaiter().GetResult();
				EstateLink.EstatePc? pc = estate.Pcs.FirstOrDefault(p => p.Id == id);
				if (estate.On == true && pc == null) return Offered.Left;
				if (pc is not { Here: true }) return Offered.Away;
				var (status, json) = EstateLink.GetJsonAsync(EstateLink.PeerPath(id, "scan/take"), TimeSpan.FromSeconds(15), ct, limit: 64 * 1024).GetAwaiter().GetResult();
				json?.Dispose();
				return status is 200 or 202 ? Offered.Taking : status == 404 ? Offered.Older : Offered.Away;
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException or EstateMissing or IOException) {
				return Offered.Away;
			}
		}

		/// <summary>What a sweep did: files and folders deleted, entries it couldn't delete (in use), homes left where a scan runs, and PCs that haven't taken their records back.</summary>
		internal readonly record struct Swept(int Files, int Folders, int Left, int Busy, int Waiting = 0);

		/// <summary>
		/// One sweep of <paramref name="remoteRoot"/>, each home where no scan runs (<paramref name="scanning"/>, and its scan
		/// lock): its stand-ins moved aside and deleted; its records, if any, offered back to its PC (<paramref name="offer"/>;
		/// none: kept as they are), else the home let go; then every home let go deleted. Stops between two batches once
		/// <paramref name="ct"/> is cancelled; waits while <paramref name="hold"/> says so.
		/// </summary>
		internal static Swept Sweep(string remoteRoot, Func<string, bool> scanning, CancellationToken ct, Func<bool>? hold = null, Action<string>? log = null,
			Func<string, Offered>? offer = null, DateTime? nowUtc = null) {
			var root = new DirectoryInfo(remoteRoot);
			if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) {
				waiting = [];
				return default;
			}
			var sweeper = new Sweeper(ct, hold);
			int busy = 0;
			var waits = new List<Waits>();
			foreach (DirectoryInfo home in Listed(root)) {
				if (ct.IsCancellationRequested) break;
				if (home.Attributes.HasFlag(FileAttributes.ReparsePoint) || home.Name.StartsWith(Gone, StringComparison.Ordinal)) continue;
				string mirror = Path.Combine(home.FullName, "mirror");
				if ((Directory.Exists(mirror) || File.Exists(RemoteMirror.EmptyMark(mirror))) && !MoveAside(home.FullName, () => RemoteMirror.SetAside(mirror), scanning)) {
					busy++;
					continue;
				}
				foreach (DirectoryInfo old in Listed(home).Where(d => d.Name.StartsWith("mirror" + RemoteMirror.OldSuffix, StringComparison.OrdinalIgnoreCase))) {
					if (ct.IsCancellationRequested) break;
					Delete(old, $"old files of the scans for another PC ({home.Name})", sweeper, log);
				}
				if (ct.IsCancellationRequested) break;
				// Its records: offered back to its PC, which takes them and lets the home go; none, and there's nothing to keep.
				ScanRecords.Manifest records = ScanRecords.Of(home.FullName);
				if (records.Files.Count == 0) {
					if (!MoveAside(home.FullName, () => Retire(home.FullName), scanning)) busy++;
					continue;
				}
				lock (Gate) {
					if (InUse(home.FullName, scanning)) {
						busy++;
						continue;
					}
				}
				Offered said = offer?.Invoke(home.Name) ?? Offered.Away;
				DateTime newest = records.Files.Max(f => new DateTime(f.Ticks, DateTimeKind.Utc));
				if (said == Offered.Left && (nowUtc ?? DateTime.UtcNow) - newest > KeptFor) {
					log?.Invoke($"what scans here found for {NameOf(home.FullName)} goes: that PC left the Estate, and didn't take it back in {KeptFor.TotalDays:N0} days");
					if (!MoveAside(home.FullName, () => Retire(home.FullName), scanning)) busy++;
					continue;
				}
				if (said != Offered.Taking) waits.Add(new Waits(home.Name, NameOf(home.FullName), said == Offered.Older ? "older" : "away"));
			}
			// Homes let go: deleted, where no scan looks.
			foreach (DirectoryInfo gone in Listed(root).Where(d => d.Name.StartsWith(Gone, StringComparison.Ordinal) && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))) {
				if (ct.IsCancellationRequested) break;
				Delete(gone, "what was kept of a scan for another PC, once that PC had it", sweeper, log);
			}
			waiting = waits;
			return new Swept(sweeper.Files, sweeper.Folders, sweeper.Left, busy, waits.Count);
		}

		/// <summary>The PC a home is for, by name: as its scan wrote it, else its id.</summary>
		static string NameOf(string home) {
			try {
				string path = TargetFile(home);
				if (File.Exists(path)) {
					using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
					if (EstateLink.Str(doc.RootElement, "name") is { Length: > 0 } name) return name;
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
			return Path.GetFileName(home);
		}

		/// <summary>A tree deleted a batch at a time, said in the log once it's done (or cut off).</summary>
		static void Delete(DirectoryInfo tree, string what, Sweeper sweeper, Action<string>? log) {
			var timer = Stopwatch.StartNew();
			(int files, int folders, int left) = (sweeper.Files, sweeper.Folders, sweeper.Left);
			bool whole = sweeper.Tree(tree);
			files = sweeper.Files - files;
			folders = sweeper.Folders - folders;
			left = sweeper.Left - left;
			if (files + folders > 0 || left > 0)
				log?.Invoke($"cleared {what}: {files:N0} files and {folders:N0} folders in {timer.Elapsed.TotalSeconds:N0} s" +
					(!whole ? ", the rest at its next round" : left > 0 ? $", {left:N0} in use left for its next round" : ""));
		}

		/// <summary>A folder's folders, listed whole first (none is deleted while it's being read); none for one that can't be read.</summary>
		static List<DirectoryInfo> Listed(DirectoryInfo dir) {
			try { return dir.EnumerateDirectories().ToList(); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
		}

		/// <summary>A part of a PC's home moved aside (<paramref name="move"/>), unless a scan for it runs or is starting: false then, or when it couldn't be moved.</summary>
		static bool MoveAside(string home, Func<bool> move, Func<string, bool> scanning) {
			lock (Gate) {
				if (InUse(home, scanning)) return false;
				return move();
			}
		}

		/// <summary>Whether a scan for that PC runs in <paramref name="home"/> or is starting there: the page's (<paramref name="scanning"/>), or any holding its scan lock.</summary>
		internal static bool InUse(string home, Func<string, bool> scanning) {
			if (scanning(home)) return true;
			using FileStream? held = TryLock(Path.Combine(home, Path.GetFileName(AgentPaths.ScanLock)));
			return held == null;
		}

		/// <summary>
		/// A home let go: moved aside whole (an instant), to be deleted gently where no scan looks. Under <see cref="Gate"/>, by
		/// a caller that has seen no scan runs in it. False when it couldn't be moved (something in it is open): it's tried again.
		/// </summary>
		internal static bool Retire(string home) {
			lock (Gate) {
				if (!Directory.Exists(home)) return true;
				string gone = Path.Combine(Path.GetDirectoryName(home)!, Gone + Path.GetFileName(home) + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture));
				try { Directory.Move(home, gone); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
			}
			Kick();
			return true;
		}

		/// <summary>The scan lock of a PC's home, as its scan takes it (<see cref="AgentScanner.TryLock"/>); null while a scan holds it.</summary>
		static FileStream? TryLock(string path) {
			try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
		}

		/// <summary>Deletes trees a batch at a time: a folder's files, then its folders, then itself; a link is removed itself, never followed.</summary>
		sealed class Sweeper(CancellationToken ct, Func<bool>? hold) {
			public int Files, Folders, Left;
			int sinceRest;

			/// <summary>All of <paramref name="dir"/> deleted that can be; false when it was cut off.</summary>
			public bool Tree(DirectoryInfo dir) {
				List<FileSystemInfo> entries;
				try { entries = dir.EnumerateFileSystemInfos().ToList(); }
				catch (DirectoryNotFoundException) { return true; }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Left++;
					return true;
				}
				foreach (FileSystemInfo e in entries) {
					if (ct.IsCancellationRequested) return false;
					if (e is DirectoryInfo sub && !e.Attributes.HasFlag(FileAttributes.ReparsePoint)) {
						if (!Tree(sub)) return false;
						continue;
					}
					if (Remove(e)) {
						if (e is DirectoryInfo) Folders++;
						else Files++;
					}
					else Left++;
					if (++sinceRest >= Batch && !Rested()) return false;
				}
				if (Remove(dir)) Folders++;
				else Left++;
				return ++sinceRest < Batch || Rested();
			}

			/// <summary>The rest between batches, and the wait while a game is played; false once cut off.</summary>
			bool Rested() {
				sinceRest = 0;
				if (ct.WaitHandle.WaitOne(Rest)) return false;
				while (hold?.Invoke() == true)
					if (ct.WaitHandle.WaitOne(GameWait)) return false;
				return true;
			}

			/// <summary>A file, a link, or a folder emptied: deleted (one gone already counts as deleted).</summary>
			static bool Remove(FileSystemInfo e) {
				try {
					if (e is DirectoryInfo d) d.Delete(); // empty, or a link: never what it links to
					else {
						if (e.Attributes.HasFlag(FileAttributes.ReadOnly)) e.Attributes &= ~FileAttributes.ReadOnly;
						e.Delete();
					}
					return true;
				}
				catch (Exception x) when (x is FileNotFoundException or DirectoryNotFoundException) {
					return true;
				}
				catch (Exception x) when (x is IOException or UnauthorizedAccessException) {
					return false;
				}
			}
		}
	}
}
