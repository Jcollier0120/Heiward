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

namespace HEI.Agent.Estate {
	/// <summary>
	/// What Heiward 1.11.0 to 1.11.5 kept here of every PC it scanned for: a sparse stand-in of each of that PC's files under
	/// <c>remote\&lt;pc&gt;\mirror</c>, never removed (670,803 files on one PC, 3.1 TB as Explorer counts them, about 1.4 GB on
	/// its disk with their records). A scan keeps none now (<see cref="RemoteMirror"/>); these go by themselves, from the page:
	/// <list type="bullet">
	/// <item>Only Heiward's own: a PC's <c>mirror</c> folder in its home under <c>remote</c>, and the <c>mirror.old-…</c> folders
	/// a scan moved aside. Nothing else there (its database, its listing, what it found), and never through a link.</item>
	/// <item>Never under a scan: a <c>mirror</c> folder is first moved aside whole, an instant, while no scan for that PC can
	/// start (<see cref="Gate"/>) and none runs (its scan lock is free); it's deleted where no scan looks.</item>
	/// <item>Gently: on a thread of its own in Windows' background mode (the lowest disk priority), <see cref="Batch"/> files
	/// at a time with a rest between, waiting while a game is played.</item>
	/// <item>Safe to cut off: what's left is there the next time, and goes then (the page's next start, or the end of the
	/// next scan for another PC).</item>
	/// </list>
	/// </summary>
	static class OldMirrors {
		/// <summary>How many files and folders go between rests.</summary>
		internal static int Batch { get; set; } = 500;
		/// <summary>The rest between batches.</summary>
		internal static TimeSpan Rest { get; set; } = TimeSpan.FromMilliseconds(200);
		/// <summary>How long after the page is up the first sweep waits: the page's own start goes first.</summary>
		static readonly TimeSpan StartAfter = TimeSpan.FromMinutes(2);
		/// <summary>While a game is played, how often it's looked at again.</summary>
		static readonly TimeSpan GameWait = TimeSpan.FromSeconds(30);

		/// <summary>Held while a PC's mirror is moved aside, and while a scan for another PC is started (<see cref="ScanForOthers"/>).</summary>
		internal static readonly object Gate = new();

		static readonly AutoResetEvent again = new(false);
		static int started;

		/// <summary>The folder of the scans for other PCs: one home a PC (<see cref="RemoteScanner.HomeFor"/>).</summary>
		static string RemoteRoot => Path.Combine(AgentPaths.Home, "remote");

		/// <summary>The sweeps, from the page's start until it stops: one a while after it's up, one after each scan for another PC.</summary>
		public static void Start(CancellationToken ct) {
			if (Interlocked.Exchange(ref started, 1) == 1) return;
			new Thread(() => Run(ct)) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Heiward: old remote-scan files" }.Start();
		}

		/// <summary>A sweep soon: a scan for another PC ended (it may have moved its mirror aside, or left one).</summary>
		public static void Kick() => again.Set();

		static void Run(CancellationToken ct) {
			FolderListing.BackgroundMode();
			if (WaitHandle.WaitAny([ct.WaitHandle, again], StartAfter) == 0) return;
			while (!ct.IsCancellationRequested) {
				try { Sweep(RemoteRoot, ScanForOthers.ScansIn, ct, () => ScanForOthers.GameNow() != null, AgentPaths.AppendLog); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					AgentPaths.AppendLog("clearing old remote-scan files: " + e.Message);
				}
				if (WaitHandle.WaitAny([ct.WaitHandle, again]) == 0) return;
			}
		}

		/// <summary>What a sweep did: files and folders deleted, entries it couldn't delete (in use), and mirrors left where a scan runs.</summary>
		internal readonly record struct Swept(int Files, int Folders, int Left, int Busy);

		/// <summary>
		/// One sweep of <paramref name="remoteRoot"/>: each PC's <c>mirror</c> moved aside where no scan for it runs
		/// (<paramref name="scanning"/>, and its scan lock), then every <c>mirror.old-…</c> deleted. Stops between two batches
		/// once <paramref name="ct"/> is cancelled; waits while <paramref name="hold"/> says so.
		/// </summary>
		internal static Swept Sweep(string remoteRoot, Func<string, bool> scanning, CancellationToken ct, Func<bool>? hold = null, Action<string>? log = null) {
			var root = new DirectoryInfo(remoteRoot);
			if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return default;
			var sweeper = new Sweeper(ct, hold);
			int busy = 0;
			foreach (DirectoryInfo home in Listed(root)) {
				if (ct.IsCancellationRequested) break;
				if (home.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // a link: not Heiward's own
				string mirror = Path.Combine(home.FullName, "mirror");
				if ((Directory.Exists(mirror) || File.Exists(RemoteMirror.EmptyMark(mirror))) && !MoveAside(home.FullName, mirror, scanning)) busy++;
				foreach (DirectoryInfo old in Listed(home).Where(d => d.Name.StartsWith("mirror" + RemoteMirror.OldSuffix, StringComparison.OrdinalIgnoreCase))) {
					if (ct.IsCancellationRequested) break;
					var timer = Stopwatch.StartNew();
					(int files, int folders, int left) = (sweeper.Files, sweeper.Folders, sweeper.Left);
					bool whole = sweeper.Tree(old);
					files = sweeper.Files - files;
					folders = sweeper.Folders - folders;
					left = sweeper.Left - left;
					if (files + folders > 0 || left > 0)
						log?.Invoke($"cleared old files of the scans for another PC ({home.Name}): {files:N0} files and {folders:N0} folders in {timer.Elapsed.TotalSeconds:N0} s" +
							(!whole ? ", the rest at its next round" : left > 0 ? $", {left:N0} in use left for its next round" : ""));
				}
			}
			return new Swept(sweeper.Files, sweeper.Folders, sweeper.Left, busy);
		}

		/// <summary>A folder's folders, listed whole first (none is deleted while it's being read); none for one that can't be read.</summary>
		static List<DirectoryInfo> Listed(DirectoryInfo dir) {
			try { return dir.EnumerateDirectories().ToList(); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
		}

		/// <summary>A PC's mirror moved aside, unless a scan for it runs or is starting: false then, or when it couldn't be moved.</summary>
		static bool MoveAside(string home, string mirror, Func<string, bool> scanning) {
			lock (Gate) {
				if (scanning(home)) return false;
				using FileStream? held = TryLock(Path.Combine(home, Path.GetFileName(AgentPaths.ScanLock)));
				if (held == null) return false; // a scan for it holds its lock
				return RemoteMirror.SetAside(mirror);
			}
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
