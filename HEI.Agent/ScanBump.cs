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

using System.Text.Json;

namespace HEI.Agent {
	/// <summary>
	/// "Scan now instead of at 18:00", on a drive's page: the next scheduled scan, brought forward. The scan starts at once
	/// (<c>scan --bump</c>), and once it has gone through, the scheduled scan it stood in for skips itself
	/// (<see cref="ScanWhenDue.BumpedSkip"/>); the one after runs as usual. A bumped scan that fails or is stopped skips
	/// nothing: the scheduled one still comes. The scan task keeps its times, so no other timer is involved: the scheduled
	/// scan decides, as it does for a pause or the battery.
	/// </summary>
	/// <param name="FirstSlotUtc">The first scheduled scan skipped: the next one when the bump was asked for.</param>
	/// <param name="LastSlotUtc">The last one skipped: a later bump, before the first was skipped, skips its next one too.</param>
	/// <param name="ScannedUtc">When the latest bumped scan started.</param>
	sealed record ScanBump(DateTime FirstSlotUtc, DateTime LastSlotUtc, DateTime ScannedUtc) {
		/// <summary>
		/// A scheduled run this close to a slot is that slot's: Task Scheduler starts a run within moments of its time, a
		/// little late more often than early, and the page's time for it comes from Task Scheduler's minutes.
		/// </summary>
		internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

		static string FilePath => Path.Combine(AgentPaths.Home, "scan-bump.json");

		static TimeSpan Every(int everyMinutes) => TimeSpan.FromMinutes(Math.Max(15, everyMinutes));

		/// <summary>
		/// Whether the scheduled scan at <paramref name="runUtc"/> is one this bump stands in for: from the first slot (less
		/// <see cref="Grace"/>) until the run after the last slot is due (less the same).
		/// </summary>
		public bool Covers(DateTime runUtc, int everyMinutes) =>
			everyMinutes > 0 && runUtc >= FirstSlotUtc - Grace && runUtc < LastSlotUtc + Every(everyMinutes) - Grace;

		/// <summary>Past every run it stands in for: nothing left to skip.</summary>
		public bool Spent(DateTime nowUtc, int everyMinutes) => everyMinutes <= 0 || nowUtc >= LastSlotUtc + Every(everyMinutes) - Grace;

		/// <summary>The next scheduled scan once the ones this bump stands in for are skipped: <paramref name="nextUtc"/>, or the one after.</summary>
		public DateTime After(DateTime nextUtc, int everyMinutes) {
			var every = Every(everyMinutes);
			while (Covers(nextUtc, everyMinutes)) nextUtc += every;
			return nextUtc;
		}

		/// <summary>
		/// After the bumped scan went through (<see cref="LastScan.Ok"/>): the scheduled scan at <paramref name="slotUtc"/> is
		/// skipped, and any a bump before it stood in for and that hasn't come yet.
		/// </summary>
		public static ScanBump Record(DateTime slotUtc, DateTime scannedUtc, int everyMinutes) {
			ScanBump bump = Load() is { } kept && !kept.Spent(scannedUtc, everyMinutes)
				? new ScanBump(Min(kept.FirstSlotUtc, slotUtc), Max(kept.LastSlotUtc, slotUtc), scannedUtc)
				: new ScanBump(slotUtc, slotUtc, scannedUtc);
			try {
				AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(bump, AgentConfig.Json));
				AgentPaths.AppendLog($"scan brought forward went through: the scheduled scan at {slotUtc.ToLocalTime():g} skips itself");
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				AgentPaths.AppendLog("scan-bump.json not written, so the next scheduled scan runs: " + e.Message);
			}
			return bump;
		}

		public static ScanBump? Load() {
			try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ScanBump>(File.ReadAllText(FilePath), AgentConfig.Json) : null; }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
		}

		/// <summary>
		/// For the scheduled scan starting at <paramref name="nowUtc"/>: why it skips itself, a bump having scanned in its
		/// place, or null when it runs. A bump with nothing left to skip is let go.
		/// </summary>
		public static string? Take(DateTime nowUtc, int everyMinutes) {
			ScanBump? bump = Load();
			string? skip = ScanWhenDue.BumpedSkip(bump, nowUtc, everyMinutes);
			if (bump != null && bump.Spent(nowUtc + (skip != null ? Every(everyMinutes) : TimeSpan.Zero), everyMinutes)) Forget();
			return skip;
		}

		static void Forget() {
			try { File.Delete(FilePath); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
		static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
	}

	/// <summary>
	/// When a scan last read each drive (and each folder scanned apart), for the drive's page: "Last scanned 2 h ago". A
	/// scan that went through writes it once, for the roots it read; a drive left alone (scanned only when asked) keeps
	/// its time. Missing before this was kept: the page falls back on the last scan's time.
	/// </summary>
	static class DriveScans {
		static string FilePath => Path.Combine(AgentPaths.Home, "drive-scans.json");

		static readonly StringComparer Roots = StringComparer.OrdinalIgnoreCase;

		/// <summary>"C:\" for a drive (its root keeps its separator), "D:\Photos" for a folder.</summary>
		static string Key(string root) => Path.TrimEndingDirectorySeparator(root);

		/// <summary>The roots read, as the scan names them, by when it started.</summary>
		/// <param name="before">
		/// Each root's "Last scanned" as the page gave it before the file was there (<see cref="Before"/>): it starts the file, so
		/// a root the scan didn't read (Scan this drive now on another, a folder's scan) keeps its time rather than "never".
		/// </param>
		public static void Record(IEnumerable<string> read, DateTime startedUtc, IReadOnlyDictionary<string, DateTime>? before = null) {
			try {
				var times = Load() ?? new Dictionary<string, DateTime>(before ?? new Dictionary<string, DateTime>(), Roots);
				foreach (string root in read) times[Key(root)] = startedUtc;
				AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(times.OrderBy(t => t.Key, Roots).ToDictionary(t => t.Key, t => t.Value), AgentConfig.Json));
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				AgentPaths.AppendLog("drive-scans.json not written: " + e.Message);
			}
		}

		/// <summary>Each root's last read; null when no scan has kept them yet.</summary>
		public static Dictionary<string, DateTime>? Load() {
			try {
				if (!File.Exists(FilePath)) return null;
				var read = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(FilePath), AgentConfig.Json);
				return read == null ? null : new Dictionary<string, DateTime>(read.Select(r => KeyValuePair.Create(Key(r.Key), r.Value)), Roots);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
		}

		/// <summary>
		/// Each of <paramref name="roots"/>' "Last scanned" as the page gives it while the file isn't there (scans before kept
		/// none, <see cref="ExplorerView.Drives"/>): the last scan's (<paramref name="index"/>), for each drive it listed. Null
		/// with no last scan. Once the file is there, it says it all, and <see cref="Record"/> leaves this aside.
		/// </summary>
		public static Dictionary<string, DateTime>? Before(ScanIndex? index, IEnumerable<string> roots) {
			if (index == null) return null;
			var times = new Dictionary<string, DateTime>(Roots);
			foreach (string root in roots)
				if (index.Drives.ContainsKey(ScanIndex.DriveOf(root))) times[Key(root)] = DateTime.SpecifyKind(index.ScannedAtUtc, DateTimeKind.Utc);
			return times;
		}

		/// <summary><paramref name="root"/>'s last read in <paramref name="times"/>.</summary>
		public static DateTime? Of(IReadOnlyDictionary<string, DateTime>? times, string root) =>
			times != null && times.TryGetValue(Key(root), out DateTime at) ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null;
	}
}
