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

namespace HEI.Agent {
	/// <summary>
	/// Whether starting the review page starts a scan: only when one is due by the schedule, the last scan
	/// plus the interval between scans. Opening, reloading or restarting the page between scans shows the
	/// last scan's results and starts nothing; Scan now is always there.
	/// <para>
	/// Before, a new build of Heiward rescanned every time the page started, until a rescan finished: a
	/// rescan stopped halfway (or a page restarted while one ran) meant another scan at the next open.
	/// </para>
	/// </summary>
	static class ScanWhenDue {
		/// <summary>
		/// Why a scan is due now, for the log; null when none is. <paramref name="lastScanUtc"/>: the last
		/// report, whichever build made it. <paramref name="lastStartUtc"/>: the last scan started, finished
		/// or not, so a scan stopped halfway isn't started again at the next open.
		/// </summary>
		public static string? Due(DateTime? lastScanUtc, DateTime? lastStartUtc, int everyMinutes, DateTime nowUtc) {
			if (everyMinutes <= 0) return null; // scans only when you press Scan now
			DateTime? last = Latest(lastScanUtc, lastStartUtc);
			if (last == null) return null; // no scan yet: the setup or Scan now starts the first
			// As the scheduled task runs them (Scheduler: every 15 minutes at the most often).
			var every = TimeSpan.FromMinutes(Math.Max(15, everyMinutes));
			TimeSpan since = nowUtc - last.Value;
			return since >= every ? $"the last scan started {Span(since)} ago, and scans run every {Span(every)}" : null;
		}

		/// <summary>When the next scan is due by the same rule, for the page; null when scans run only when asked.</summary>
		public static DateTime? NextUtc(DateTime? lastScanUtc, DateTime? lastStartUtc, int everyMinutes) {
			if (everyMinutes <= 0) return null;
			return Latest(lastScanUtc, lastStartUtc)?.AddMinutes(Math.Max(15, everyMinutes));
		}

		static DateTime? Latest(DateTime? a, DateTime? b) => a == null ? b : b == null ? a : a > b ? a : b;

		static string Span(TimeSpan t) =>
			t.TotalMinutes < 90 ? $"{Math.Round(t.TotalMinutes)} minutes"
			: t.TotalHours < 36 ? $"{Math.Round(t.TotalHours)} hours"
			: $"{Math.Round(t.TotalDays)} days";
	}
}
