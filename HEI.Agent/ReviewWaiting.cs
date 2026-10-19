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
	/// The sets of copies that wait for the person: what opens the page at sign-in when Heiward is on its own, and what
	/// Manor's page lists when it's at a manor (the ping's needsYou), so the page itself never opens there.
	/// </summary>
	static class ReviewWaiting {
		/// <summary>
		/// Sets that wait for the user and call for them (<see cref="AutoCleaner.Announced"/>: not look-alikes). With
		/// automatic cleanup of duplicates on and a <paramref name="stamp"/> (when the page last opened at sign-in), only
		/// those listed since then: the sign-in page shouldn't open every day for sets the user leaves for later.
		/// </summary>
		internal static int Count(string? stamp = null) {
			var report = Report.Load();
			if (report == null) return 0;
			var cfg = AgentConfig.Load();
			var s = AutoCleanState.Load();
			var waiting = AutoCleaner.WaitingForUser(cfg, report, DecisionStore.Load(), s, DateTime.UtcNow).Where(AutoCleaner.Announced).ToList();
			if (stamp == null || !cfg.AutoClean.Duplicates || !File.Exists(stamp)) return waiting.Count;
			DateTime lastOpened = File.GetLastWriteTimeUtc(stamp);
			return waiting.Count(g => !s.FirstSeenUtc.TryGetValue("g:" + g.Key, out DateTime seen) || seen > lastOpened);
		}

		/// <summary>What the last report holds for the person, as the ping gives it (needsYou, and its summary's count).</summary>
		/// <param name="HasReport">A report from this build: before one, <paramref name="Stale"/> says whether an older build's is there.</param>
		/// <param name="ToReview">Sets with no decision yet, as <c>hei status</c> counts them.</param>
		/// <param name="Waiting">Those that call for the person (<see cref="Count"/>).</param>
		internal readonly record struct Counts(bool HasReport, bool Stale, int ToReview, int Waiting);

		static readonly object gate = new();
		static (string Inputs, long At, Counts Counts)? last;

		/// <summary>How long <see cref="Now"/>'s counts stand while the files they're made from stay the same: sets come due for automatic cleanup with time.</summary>
		internal const int KeptMinutes = 30;

		/// <summary>
		/// The counts now. Counted again only when one of the files they're made from changes (the report, the decisions,
		/// automatic cleanup's state, the settings), or after <see cref="KeptMinutes"/>: Manor's ping asks every round, and
		/// reading the report isn't free.
		/// </summary>
		internal static Counts Now() {
			string inputs = Inputs();
			lock (gate) {
				if (last is { } l && l.Inputs == inputs && Environment.TickCount64 - l.At < KeptMinutes * 60_000L) return l.Counts;
				Counts counts;
				try { counts = CountNow(); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { counts = default; }
				last = (inputs, Environment.TickCount64, counts);
				return counts;
			}
		}

		/// <summary>Each file the counts are made from, when it was written and how long it is ("-": it isn't there).</summary>
		static string Inputs() => string.Join("|", new[] { AgentPaths.Report, AgentPaths.Decisions, AutoCleanState.FilePath, AgentPaths.Config }.Select(f => {
			var file = new FileInfo(f);
			return file.Exists ? $"{f}:{file.LastWriteTimeUtc.Ticks}:{file.Length}" : f + ":-";
		}));

		static Counts CountNow() {
			var report = Report.Load();
			if (report == null) return new Counts(false, Report.IsStale(), 0, 0);
			var cfg = AgentConfig.Load();
			var decisions = DecisionStore.Load();
			int waiting = AutoCleaner.WaitingForUser(cfg, report, decisions, AutoCleanState.Load(), DateTime.UtcNow).Count(AutoCleaner.Announced);
			return new Counts(true, false, report.Groups.Count(g => !decisions.ContainsKey(g.Key)), waiting);
		}

		/// <summary>
		/// The ping's needsYou, as Manor reads it from its staff (kind, title, detail, url): one item while a new install's
		/// first scan waits for the person (<see cref="FirstScanWait"/>), else one while sets wait (<see cref="Now"/>).
		/// </summary>
		internal static object[] NeedsYou(int port) {
			if (FirstScanWait.Waiting) return [FirstScanItem(ReviewServer.PageUrl(port))];
			int count = Now().Waiting;
			return count == 0 ? [] : [Item(count, ReviewServer.PageUrl(port))];
		}

		internal static object FirstScanItem(string url) => new {
			id = "first-scan",
			kind = "first-scan",
			title = "Heiward's first scan waits for you",
			detail = new[] { "It scans nothing until you say. Open its page: press Scan now, or let it scan by itself on its schedule." },
			url,
		};

		internal static object Item(int count, string url) => new {
			id = "review",
			kind = "review",
			title = count == 1 ? "A set of copies waits for your review" : $"{count:N0} sets of copies wait for your review",
			detail = new[] { "Heiward leaves these for you to decide. Open its page to choose which copy to keep." },
			url,
		};
	}
}
