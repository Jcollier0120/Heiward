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

		static readonly object gate = new();
		static (long At, int Count) last = (-1, 0);

		/// <summary>
		/// The ping's needsYou, as Manor reads it from its staff (kind, title, detail, url): one item while sets wait.
		/// Counted at most once a minute: Manor asks every few seconds, and reading the report isn't free.
		/// </summary>
		internal static object[] NeedsYou(int port) {
			int count;
			lock (gate) {
				if (last.At < 0 || Environment.TickCount64 - last.At > 60_000) {
					try { last = (Environment.TickCount64, Count()); }
					catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { last = (Environment.TickCount64, 0); }
				}
				count = last.Count;
			}
			return count == 0 ? [] : [Item(count, ReviewServer.PageUrl(port))];
		}

		internal static object Item(int count, string url) => new {
			id = "review",
			kind = "review",
			title = count == 1 ? "A set of copies waits for your review" : $"{count:N0} sets of copies wait for your review",
			detail = new[] { "Heiward leaves these for you to decide. Open its page to choose which copy to keep." },
			url,
		};
	}
}
