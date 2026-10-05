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

using System.Diagnostics.CodeAnalysis;

namespace HEI.Agent {
	/// <summary>An employee of the manor who looks after part of a developer's housekeeping in Heiward's place.</summary>
	/// <param name="Name">"Reeve", "the Steward": as a sentence says it.</param>
	/// <param name="Url">Its page.</param>
	/// <param name="Note">What it does, as the page says it: "Reeve's worktree-tidy job removes merged worktrees and branches".</param>
	sealed record ManorRole(string Name, string Url, string Note);

	/// <summary>
	/// Who keeps a developer's git state and pull requests. Standalone, Heiward does it all, as it always has. At a manor
	/// (Manor installed, as <see cref="Manor.Load()"/> has it: its settings.json and app folder), its other employees own
	/// them, and Heiward leaves them alone:
	/// <list type="bullet">
	/// <item><see cref="Worktrees"/>: Reeve, when it's installed too (%USERPROFILE%\.reeve\app, or REEVE_HOME's app). Its
	/// worktree-tidy job removes merged, clean worktrees and deletes merged local branches. Heiward still measures the
	/// worktrees and shows the space they take, read-only: no ticks, no automatic cleanup, no Prune. What a removal that
	/// stopped partway left (no .git any more, so no worktree git knows) is disk, not git state, and stays Heiward's.</item>
	/// <item><see cref="PullRequests"/>: the Steward, when it's installed too (%USERPROFILE%\.steward\app, or STEWARD_HOME's
	/// app). It merges, catches up and releases the pull requests; Heiward no longer asks hosts for them or shows them.</item>
	/// </list>
	/// A manor without Reeve, or without the Steward, leaves that part with Heiward, so nothing is left unowned. Build
	/// outputs, caches, temp files, crash dumps, emulator images and bin/obj with no project are disk: always Heiward's.
	/// Read fresh for each page load and poll, each scan's housekeeping and each command, as <see cref="DevMode"/> is.
	/// </summary>
	sealed record ManorRoles(ManorRole? Worktrees, ManorRole? PullRequests) {
		public static readonly ManorRoles Standalone = new(null, null);

		public const string ReeveUrl = "http://reeve.localhost:18383/", StewardUrl = "http://steward.localhost:19494/";
		public const string WorktreesNote = "Reeve's worktree-tidy job removes merged worktrees and branches";
		public const string PullRequestsNote = "The Steward merges pull requests";
		/// <summary>A worktree's tag on the page, and why it can't be cleaned here.</summary>
		public const string WorktreeKept = "Reeve looks after it";

		/// <summary>Now: Manor's folder, Reeve's and the Steward's, read fresh.</summary>
		public static ManorRoles Now() => Of(Manor.Load(), Installed("REEVE_HOME", ".reeve"), Installed("STEWARD_HOME", ".steward"));

		/// <param name="manor">The manor; null when Manor isn't installed (then Heiward keeps everything).</param>
		/// <param name="reeve">Reeve is installed.</param>
		/// <param name="steward">The Steward is installed.</param>
		public static ManorRoles Of(Manor? manor, bool reeve, bool steward) => manor == null ? Standalone : new ManorRoles(
			reeve ? new ManorRole("Reeve", ReeveUrl, WorktreesNote) : null,
			steward ? new ManorRole("the Steward", StewardUrl, PullRequestsNote) : null);

		/// <summary>
		/// An agent of the manor is installed: its home (<paramref name="envVar"/> as a full path, else
		/// %USERPROFILE%\<paramref name="dotFolder"/>) has its app folder, as Manor's own staff list has it.
		/// </summary>
		internal static bool Installed(string envVar, string dotFolder) {
			string? home = Environment.GetEnvironmentVariable(envVar);
			try {
				home = string.IsNullOrWhiteSpace(home)
					? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dotFolder)
					: Path.GetFullPath(home);
				return Directory.Exists(Path.Combine(home, "app"));
			}
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) {
				return false;
			}
		}

		/// <summary>
		/// The developer report as Heiward acts on it: with Reeve keeping git state, its worktrees are read-only (never
		/// ticked, never cleaned, here or automatically: <see cref="WorktreeKept"/>), what removals left behind aside, and
		/// it has no merged branches to prune. Otherwise the report as it is. The saved report is left whole, so it's right
		/// again as soon as Reeve or Manor is gone.
		/// </summary>
		[return: NotNullIfNotNull(nameof(report))]
		public DevReport? View(DevReport? report) {
			if (report == null || Worktrees == null) return report;
			return new DevReport {
				ScannedAtUtc = report.ScannedAtUtc,
				DurationSec = report.DurationSec,
				StaleDays = report.StaleDays,
				Build = report.Build,
				Sources = report.Sources,
				Repositories = new(),
				Categories = report.Categories.Select(c => c.Key != DevScanner.Worktrees ? c
					: c with { Items = c.Items.Select(i => i.Leftover ? i : i with { Suggested = false, Blocked = WorktreeKept }).ToList() }).ToList(),
			};
		}

		/// <summary>For the page: who keeps what, with their pages; null for what Heiward keeps.</summary>
		public object Describe() => new {
			worktrees = Worktrees == null ? null : new { name = Worktrees.Name, url = Worktrees.Url, note = Worktrees.Note },
			pullRequests = PullRequests == null ? null : new { name = PullRequests.Name, url = PullRequests.Url, note = PullRequests.Note },
		};
	}
}
