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

using System.Globalization;
using System.Text.Json;

namespace HEI.Agent {
	/// <summary>
	/// How the last scan ended, written by the scan as it lets go of scan.lock (<see cref="AgentScanner.RunAsync"/>):
	/// every scan that ran, whoever started it. A scheduled scan that skipped itself (paused, on battery) or found
	/// another running never ran, and leaves the last one standing.
	/// </summary>
	/// <param name="Ok">
	/// It went through: a fresh report, or nothing new to scan. Not when it failed, found none of its folders, or was
	/// stopped (Stop scan, <c>hei stop</c>, a pause).
	/// </param>
	/// <param name="ExitCode">The scan's: 0 when it went through, 130 when stopped, 2 when none of its folders exist; null when it failed with an error.</param>
	/// <param name="Error">That error's message.</param>
	sealed record LastScan(DateTime StartedUtc, DateTime EndedUtc, bool Ok, int? ExitCode, string? Error = null) {
		public static LastScan Record(DateTime startedUtc, DateTime endedUtc, int? exitCode, Exception? error) {
			bool stopped = exitCode == 130 || error is OperationCanceledException;
			var scan = new LastScan(startedUtc, endedUtc, exitCode == 0 && error == null, stopped ? 130 : exitCode,
				error == null || stopped ? null : error.Message);
			try { AgentPaths.WriteAtomic(AgentPaths.LastScan, JsonSerializer.Serialize(scan, AgentConfig.Json)); }
			catch (Exception e) { AgentPaths.AppendLog("last-scan.json not written: " + e.Message); }
			return scan;
		}

		public static LastScan? Load() {
			try { return File.Exists(AgentPaths.LastScan) ? JsonSerializer.Deserialize<LastScan>(File.ReadAllText(AgentPaths.LastScan), AgentConfig.Json) : null; }
			catch { return null; }
		}
	}

	/// <summary>
	/// Heiward's scans at a glance, as /api/ping gives them for Manor's employee cards: the same four fields the
	/// Steward's kit gives the other agents. Manor polls every few seconds, so this reads only small files Heiward
	/// keeps anyway; the one question for Task Scheduler is <see cref="Scheduler.NextRun"/>'s, asked at most once a
	/// minute. Times are UTC.
	/// </summary>
	/// <param name="LastRunAt">When the last scan ended (<see cref="LastScan"/>). Before any scan wrote that: when the last report was written, then null.</param>
	/// <param name="LastRunOk">Whether that scan went through (<see cref="LastScan.Ok"/>); null when unknown (only a report to go by).</param>
	/// <param name="NextRunAt">The scan task's next run, from Task Scheduler; null while paused, with scans only on request, with no scan task (or a development build), or when Task Scheduler's wording isn't a time.</param>
	/// <param name="RunningSince">When the scan under way started; null when none is.</param>
	sealed record RunTimes(DateTime? LastRunAt, bool? LastRunOk, DateTime? NextRunAt, DateTime? RunningSince) {
		public static RunTimes Now(AgentConfig cfg) {
			DateTime now = DateTime.UtcNow;
			LastScan? last = LastScan.Load();
			DateTime? reportAt = null;
			if (last == null)
				try { reportAt = File.Exists(AgentPaths.Report) ? File.GetLastWriteTimeUtc(AgentPaths.Report) : null; }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			bool paused = AgentPause.Load(now) != null;
			// A development build has no scan task: the installed copy's runs the installed copy.
			int every = DevBuild.Current ? 0 : cfg.ScanEveryMinutes;
			string? nextRun = paused || every <= 0 ? null : Scheduler.NextRun();
			return Build(now, last, reportAt, AgentScanner.IsRunning(), AgentScanner.LastStartedUtc(), paused, every, nextRun);
		}

		/// <param name="reportAt">When report.json was last written: only used without <paramref name="last"/>.</param>
		/// <param name="scanning">A scan holds scan.lock (<see cref="AgentScanner.IsRunning"/>).</param>
		/// <param name="lastStartedUtc">When the last scan started (<see cref="AgentScanner.LastStartedUtc"/>).</param>
		/// <param name="everyMinutes">Minutes between scheduled scans; 0: only when asked.</param>
		/// <param name="nextRun">What <see cref="Scheduler.NextRun"/> gave: Task Scheduler's local time, in this PC's format.</param>
		internal static RunTimes Build(DateTime nowUtc, LastScan? last, DateTime? reportAt, bool scanning, DateTime? lastStartedUtc,
			bool paused, int everyMinutes, string? nextRun) {
			DateTime? lastRunAt = last != null ? Utc(last.EndedUtc) : Utc(reportAt);
			bool? lastRunOk = last?.Ok;
			// The scan holding the lock wrote its start just after taking it; until then the file is the last scan's.
			DateTime? runningSince = scanning && lastStartedUtc is { } s && (last == null || s > Utc(last.StartedUtc)) ? Utc(s) : null;
			DateTime? nextRunAt = paused || everyMinutes <= 0 ? null : NextUtc(nextRun, nowUtc, everyMinutes);
			return new RunTimes(lastRunAt, lastRunOk, nextRunAt, runningSince);
		}

		/// <summary>
		/// Task Scheduler's next run as UTC. A time already past (the answer is up to a minute old, or the PC slept
		/// through it) moves on by the scan interval, as the task repeats.
		/// </summary>
		static DateTime? NextUtc(string? nextRun, DateTime nowUtc, int everyMinutes) {
			if (!DateTime.TryParse(nextRun, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTime local)) return null;
			DateTime next = local.ToUniversalTime();
			var every = TimeSpan.FromMinutes(Math.Max(15, everyMinutes));
			if (next <= nowUtc) next += every * Math.Floor((nowUtc - next) / every + 1);
			return next;
		}

		/// <summary>As UTC, so the JSON says so: a time read back without its zone was written as UTC.</summary>
		static DateTime? Utc(DateTime? t) => t is not { } v ? null : v.Kind switch {
			DateTimeKind.Local => v.ToUniversalTime(),
			DateTimeKind.Unspecified => DateTime.SpecifyKind(v, DateTimeKind.Utc),
			_ => v,
		};
	}
}
