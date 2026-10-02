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
	/// <summary>The review page's address, and whether it answers now.</summary>
	sealed record StatusPage(string Url, bool Up);

	/// <summary>
	/// <c>hei status --json</c>: where Heiward stands, as one JSON object for scripts and other tools. The
	/// property names are a contract (AgentStatusTests holds them to it); times are UTC ("…Z") or null.
	/// </summary>
	/// <param name="Running">On duty: scheduled scans aren't paused by <c>hei pause</c>. Says nothing about whether a scan task is registered (<paramref name="Scheduled"/>).</param>
	/// <param name="StoppedSince">When the pause began; null when not paused.</param>
	/// <param name="PausedUntil">When a timed pause ends; null when not paused, or paused until resumed.</param>
	/// <param name="Scheduled">settings.json asks for scheduled scans and the scan task is registered and enabled.</param>
	/// <param name="NextScan">The scan task's next run as Task Scheduler gives it (<see cref="Scheduler.NextRun"/>).</param>
	/// <param name="Scanning">A scan is running now.</param>
	/// <param name="LastScan">When this build's last report was made (<see cref="Report.Load"/>).</param>
	/// <param name="ToReview">Sets in that report with no decision yet, as <c>hei status</c> counts them.</param>
	/// <param name="Summary">The same in one short sentence, for people.</param>
	sealed record AgentStatus(string App, bool Running, DateTime? StoppedSince, DateTime? PausedUntil, bool Scheduled, string? NextScan,
		bool Scanning, DateTime? LastScan, int ToReview, StatusPage Page, string Summary) {

		/// <summary>
		/// The status now. The two slow questions run side by side: Task Scheduler for the next run, and the
		/// review page's ping (two seconds at most).
		/// </summary>
		public static async Task<AgentStatus> NowAsync(AgentConfig cfg) {
			Task<bool> pageUp = ReviewServer.IsUpAsync(cfg.Port);
			string? nextRun = Scheduler.NextRun();
			return Build(cfg, DateTime.UtcNow, await pageUp, nextRun, DevBuild.Current);
		}

		/// <param name="pageUp">The review page answers (<see cref="ReviewServer.IsUpAsync"/>).</param>
		/// <param name="nextRun">What <see cref="Scheduler.NextRun"/> gave.</param>
		/// <param name="devBuild">A development build (<see cref="DevBuild"/>), which has no scan task.</param>
		public static AgentStatus Build(AgentConfig cfg, DateTime nowUtc, bool pageUp, string? nextRun, bool devBuild = false) {
			AgentPause? pause = AgentPause.Load(nowUtc);
			Report? report = Report.Load();
			int toReview = 0;
			if (report != null) {
				var decisions = DecisionStore.Load();
				toReview = report.Groups.Count(g => !decisions.ContainsKey(g.Key));
			}
			bool scheduled = cfg.ScanEveryMinutes > 0 && nextRun != null;
			bool scanning = AgentScanner.IsRunning();
			string summary = Summarize(cfg, nowUtc, pause, scheduled, nextRun, scanning, report, toReview, devBuild);
			return new AgentStatus("heiward", pause == null, Utc(pause?.SinceUtc), Utc(pause?.UntilUtc), scheduled, nextRun,
				scanning, Utc(report?.ScannedAtUtc), toReview, new StatusPage(ReviewServer.PageUrl(cfg.Port), pageUp), summary);
		}

		/// <summary>One line, for a pipe: camelCase, nulls written out, anything but ASCII escaped.</summary>
		public string ToJson() => JsonSerializer.Serialize(this, Json);

		static readonly JsonSerializerOptions Json = new(AgentConfig.Json) { WriteIndented = false };

		/// <summary>"Scans every hour, next at 15:00. 3 sets to review.", "Paused until you resume. Nothing to review."</summary>
		static string Summarize(AgentConfig cfg, DateTime nowUtc, AgentPause? pause, bool scheduled, string? nextRun, bool scanning, Report? report, int toReview, bool devBuild) {
			string schedule = Scheduler.Describe(cfg);
			string duty = pause != null ? "Paused " + pause.Describe(nowUtc)
				: cfg.ScanEveryMinutes > 0 && !scheduled && devBuild ? "No scheduled scans: a development build has none"
				: cfg.ScanEveryMinutes > 0 && !scheduled ? "No scheduled scans: the scan task is missing or turned off"
				: char.ToUpperInvariant(schedule[0]) + schedule[1..] + (scheduled && NextAt(nextRun, nowUtc) is { } at ? ", next " + at : "");
			string review = report == null ? (Report.IsStale() ? "Heiward was updated: the next scan lists the sets again" : "No scan yet")
				: toReview == 0 ? "Nothing to review"
				: toReview == 1 ? "1 set to review"
				: $"{toReview} sets to review";
			return (scanning ? "Scanning now. " : "") + duty + ". " + review + ".";
		}

		/// <summary>
		/// "at 15:00", "tomorrow at 08:00", "on Fri 3 Oct at 08:00", from Task Scheduler's local time; null when it
		/// isn't a time (Task Scheduler's own wording, in another language than this process's).
		/// </summary>
		static string? NextAt(string? nextRun, DateTime nowUtc) {
			if (!DateTime.TryParse(nextRun, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTime next)) return null;
			DateTime today = nowUtc.ToLocalTime().Date;
			return next.Date == today ? $"at {next:t}" : next.Date == today.AddDays(1) ? $"tomorrow at {next:t}" : $"on {next:ddd d MMM} at {next:t}";
		}

		/// <summary>As UTC, so the JSON says so: a time read back without its zone was written as UTC.</summary>
		static DateTime? Utc(DateTime? t) => t is not { } v ? null : v.Kind switch {
			DateTimeKind.Local => v.ToUniversalTime(),
			DateTimeKind.Unspecified => DateTime.SpecifyKind(v, DateTimeKind.Utc),
			_ => v,
		};
	}
}
