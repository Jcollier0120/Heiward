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
