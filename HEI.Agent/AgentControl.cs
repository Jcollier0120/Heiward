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
	/// "Stop scanning for a while", from the review page or <c>hei pause</c>: scheduled scans skip themselves
	/// until <see cref="UntilUtc"/>, or until the user resumes when it's null. Scan now still works. A pause
	/// that has run out is gone.
	/// </summary>
	sealed record AgentPause(DateTime? UntilUtc, DateTime SinceUtc) {
		/// <summary>The longest pause with an end: a week.</summary>
		public const int MaxMinutes = 7 * 24 * 60;

		public static AgentPause? Load(DateTime nowUtc) {
			try {
				if (!File.Exists(AgentPaths.Paused)) return null;
				var pause = JsonSerializer.Deserialize<AgentPause>(File.ReadAllText(AgentPaths.Paused), AgentConfig.Json);
				if (pause?.UntilUtc is { } until && until <= nowUtc) {
					File.Delete(AgentPaths.Paused);
					return null;
				}
				return pause;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return null;
			}
		}

		public static AgentPause? Load() => Load(DateTime.UtcNow);

		/// <param name="minutes">How long; null: until the user resumes.</param>
		public static AgentPause Start(int? minutes, DateTime nowUtc) {
			var pause = new AgentPause(minutes is int m ? nowUtc.AddMinutes(Math.Clamp(m, 1, MaxMinutes)) : null, nowUtc);
			AgentPaths.WriteAtomic(AgentPaths.Paused, JsonSerializer.Serialize(pause, AgentConfig.Json));
			AgentPaths.AppendLog(pause.UntilUtc is { } until ? $"scans paused until {until.ToLocalTime():g}" : "scans paused until resumed");
			return pause;
		}

		public static void Resume() {
			try {
				if (!File.Exists(AgentPaths.Paused)) return;
				File.Delete(AgentPaths.Paused);
				AgentPaths.AppendLog("scans resumed");
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		/// <summary>"until 15:30", "until tomorrow 08:00", "until you resume".</summary>
		public string Describe(DateTime nowUtc) {
			if (UntilUtc is not { } until) return "until you resume";
			DateTime local = until.ToLocalTime(), today = nowUtc.ToLocalTime().Date;
			return local.Date == today ? $"until {local:t}" : local.Date == today.AddDays(1) ? $"until tomorrow {local:t}" : $"until {local:ddd d MMM t}";
		}
	}

	/// <summary>
	/// A new install's first scan waits for the person: nothing scans until they have opened Heiward's page, seen its
	/// tour, and pressed Scan now there, or chosen on the tour's last step (or the page's banner) to let it scan by
	/// itself. Until then the install starts no scan, and scheduled scans skip themselves. An update, or a PC where a
	/// scan has finished before, doesn't wait.
	/// </summary>
	static class FirstScanWait {
		public static bool Waiting => File.Exists(AgentPaths.FirstScanWaits);

		/// <summary>Whether an install's first scan waits: not an update (<paramref name="update"/>), and no scan finished before.</summary>
		public static bool ForInstall(bool update, bool scannedBefore) => !update && !scannedBefore;

		public static void Begin() {
			AgentPaths.WriteAtomic(AgentPaths.FirstScanWaits, DateTime.UtcNow.ToString("O"));
			AgentPaths.AppendLog("first scan: waits for the person (Scan now, or Scan by itself, on the review page)");
		}

		/// <param name="how">What ended the wait, for the log.</param>
		public static void End(string how) {
			try {
				if (!File.Exists(AgentPaths.FirstScanWaits)) return;
				File.Delete(AgentPaths.FirstScanWaits);
				AgentPaths.AppendLog("first scan: no longer waits: " + how);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}
	}

	/// <summary>
	/// The page's tour (wwwroot/tour.js) opens by itself the first time the page is opened after a new install, from the
	/// Store or GitHub, and not again once it's done or skipped. Heiward keeps that here, tied to the install, and not in
	/// the browser: the page opens in an Edge app window on http://heiward.localhost:&lt;port&gt;/, and Edge keeps that
	/// page's storage in its own profile, which uninstalling Heiward leaves as it was, so a PC that had Heiward before
	/// (from GitHub, Castellan's copy or the Store) never saw the tour after installing it again. The install says whether
	/// it's due (<see cref="ForInstall"/>); the tour's end removes the file, the only write it costs. Reading it is all a
	/// page load does.
	/// </summary>
	static class PageTour {
		public static bool Due => File.Exists(AgentPaths.TourDue);

		/// <summary>
		/// Whether an install opens the tour: a new one, which replaces no copy (the install's "Installed"), as one after an
		/// uninstall is, since uninstalling removes what marks a copy (Apps &amp; Features' entry, the Store version's
		/// setup.txt); or one whose first scan waits for the person (<see cref="FirstScanWait"/>), since the setup's Done says
		/// the tour asks about it. Not an update: what the tour's end said stays.
		/// </summary>
		/// <param name="replaces">The version of the copy the install replaces, "" when it isn't known; null: none.</param>
		public static bool ForInstall(string? replaces, bool firstScanWaits) => replaces == null || firstScanWaits;

		public static void Begin() {
			AgentPaths.WriteAtomic(AgentPaths.TourDue, DateTime.UtcNow.ToString("O"));
			AgentPaths.AppendLog("tour: opens by itself the next time the page is opened");
		}

		/// <summary>The tour was done or skipped: it doesn't open by itself again. Nothing is written when it wasn't due.</summary>
		public static void End() {
			try { File.Delete(AgentPaths.TourDue); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}
	}

	/// <summary>
	/// Stopping a scan that's running in another process: the page (or <c>hei stop</c>) leaves a request, and the
	/// scan checks for one every second and stops the way Ctrl+C would, without writing a report.
	/// </summary>
	static class ScanStop {
		public static void Request() {
			Directory.CreateDirectory(AgentPaths.Home);
			File.WriteAllText(AgentPaths.StopScan, DateTime.UtcNow.ToString("O"));
			AgentPaths.AppendLog("scan: asked to stop");
		}

		/// <summary>A request made since the scan started (an older one is a leftover).</summary>
		public static bool Requested(DateTime scanStartedUtc) {
			try { return File.Exists(AgentPaths.StopScan) && File.GetLastWriteTimeUtc(AgentPaths.StopScan) >= scanStartedUtc; }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
		}

		public static void Clear() {
			try { File.Delete(AgentPaths.StopScan); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		/// <summary>Cancels <paramref name="stop"/> once a stop is requested; ends with <paramref name="ct"/>.</summary>
		public static async Task WatchAsync(DateTime scanStartedUtc, CancellationTokenSource stop, CancellationToken ct) {
			while (!ct.IsCancellationRequested) {
				try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
				catch (OperationCanceledException) { return; }
				if (!Requested(scanStartedUtc)) continue;
				AgentPaths.AppendLog("scan stopped by the user");
				stop.Cancel();
				return;
			}
		}
	}
}
