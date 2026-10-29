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
using System.Text.Json;

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
	/// worktrees and shows the space they take, read-only, those its last round went through (<see cref="LookedAfter"/>):
	/// no ticks, no automatic cleanup, no Prune. A worktree its round doesn't go through is Heiward's, never ticked for
	/// the user. What a removal that stopped partway left (no .git any more, so no worktree git knows) is disk, not git
	/// state, and stays Heiward's.</item>
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

		/// <summary>
		/// The worktrees Reeve's last full worktree-tidy round went through, whatever it decided for each, by their real
		/// paths (<see cref="RealPath"/>) and as Reeve wrote them, ignoring case. Null when Reeve doesn't say (an older
		/// Reeve, no round yet, a file that can't be read): then it looks after them all, as it was taken to before it said.
		/// </summary>
		public IReadOnlySet<string>? LookedAfter { get; init; }

		/// <summary>Now: Manor's folder, Reeve's (with its last tidy round) and the Steward's, read fresh.</summary>
		public static ManorRoles Now() {
			string? reeve = Home("REEVE_HOME", ".reeve") is { } home && Directory.Exists(Path.Combine(home, "app")) ? home : null;
			return Of(Manor.Load(), reeve != null, Installed("STEWARD_HOME", ".steward"), reeve != null ? LookedAfterIn(reeve) : null);
		}

		/// <param name="manor">The manor; null when Manor isn't installed (then Heiward keeps everything).</param>
		/// <param name="reeve">Reeve is installed.</param>
		/// <param name="steward">The Steward is installed.</param>
		/// <param name="lookedAfter">The worktrees Reeve's last round went through (<see cref="LookedAfter"/>); null for all of them.</param>
		public static ManorRoles Of(Manor? manor, bool reeve, bool steward, IReadOnlySet<string>? lookedAfter = null) => manor == null ? Standalone : new ManorRoles(
			reeve ? new ManorRole("Reeve", ReeveUrl, WorktreesNote) : null,
			steward ? new ManorRole("the Steward", StewardUrl, PullRequestsNote) : null) { LookedAfter = reeve ? lookedAfter : null };

		/// <summary>
		/// An agent of the manor is installed: its home (<see cref="Home"/>) has its app folder, as Manor's own staff list has it.
		/// </summary>
		internal static bool Installed(string envVar, string dotFolder) =>
			Home(envVar, dotFolder) is { } home && Directory.Exists(Path.Combine(home, "app"));

		/// <summary>An agent's home: <paramref name="envVar"/> as a full path, else %USERPROFILE%\<paramref name="dotFolder"/>; null when it can't be read.</summary>
		static string? Home(string envVar, string dotFolder) {
			string? home = Environment.GetEnvironmentVariable(envVar);
			try {
				return string.IsNullOrWhiteSpace(home)
					? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dotFolder)
					: Path.GetFullPath(home);
			}
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) {
				return null;
			}
		}

		/// <summary>The last file read (<see cref="LookedAfterIn"/>) and what it says: resolved again only when it changes.</summary>
		sealed record Round(string Text, IReadOnlySet<string>? Paths);
		static Round? lastRound;

		/// <summary>
		/// The worktrees Reeve's last full tidy round went through (<see cref="LookedAfter"/>): its job data,
		/// jobs\data\worktree-tidy.json in <paramref name="reeveHome"/>, under lookedAfter. Null when the file or the
		/// field is missing, or isn't what Reeve writes: nothing a broken file says is believed.
		/// </summary>
		internal static IReadOnlySet<string>? LookedAfterIn(string reeveHome) {
			string text;
			try { text = File.ReadAllText(Path.Combine(reeveHome, "jobs", "data", "worktree-tidy.json")); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) {
				return null;
			}
			if (lastRound is { } last && last.Text == text) return last.Paths;
			IReadOnlySet<string>? paths = Parse(text);
			lastRound = new Round(text, paths);
			return paths;

			static IReadOnlySet<string>? Parse(string text) {
				try {
					using JsonDocument doc = JsonDocument.Parse(text);
					if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("lookedAfter", out JsonElement list)
						|| list.ValueKind != JsonValueKind.Array) return null;
					var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (JsonElement entry in list.EnumerateArray()) {
						if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { Length: > 0 } path || !Path.IsPathFullyQualified(path)) return null;
						// Reeve writes real paths; resolved here too, so one written another way still matches.
						paths.Add(AsWritten(path));
						paths.Add(RealPath.Of(path));
					}
					return paths;
				}
				catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException or PathTooLongException) {
					return null;
				}
			}
		}

		/// <summary><paramref name="path"/> full, without a trailing separator, as written: no drive is read.</summary>
		static string AsWritten(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

		/// <summary>
		/// Reeve looks after the worktree at <paramref name="path"/>: it's installed at a manor, and its last tidy round went
		/// through it (by its real path), or Reeve doesn't say which its round went through (<see cref="LookedAfter"/>).
		/// </summary>
		/// <param name="resolve">False for a path on a drive the developer check left unread: matched as written (the check
		/// wrote it by its real path), so that drive isn't read.</param>
		public bool LooksAfter(string path, bool resolve = true) {
			if (Worktrees == null) return false;
			if (LookedAfter == null) return true;
			try { return LookedAfter.Contains(AsWritten(path)) || resolve && LookedAfter.Contains(RealPath.Of(path)); }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
		}

		/// <summary>
		/// The developer report as Heiward acts on it: with Reeve keeping git state, the worktrees it looks after are
		/// read-only (never ticked, never cleaned, here or automatically: <see cref="WorktreeKept"/>); those its round
		/// doesn't go through are Heiward's, never ticked for the user; what removals left behind is Heiward's as ever.
		/// It has no merged branches to prune. Otherwise the report as it is. The saved report is left whole, so it's
		/// right again as soon as Reeve or Manor is gone.
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
					: c with {
						Items = c.Items.Select(i => i.Leftover ? i
							: LooksAfter(i.Location, resolve: i.OnRequest == null) ? i with { Suggested = false, Blocked = WorktreeKept }
							: i with { Suggested = false }).ToList(),
					}).ToList(),
			};
		}

		// Reeve's list compared as a set: two reads of one file are the same roles.
		public bool Equals(ManorRoles? other) => other is not null && Worktrees == other.Worktrees && PullRequests == other.PullRequests
			&& (LookedAfter == null ? other.LookedAfter == null : other.LookedAfter != null && LookedAfter.SetEquals(other.LookedAfter));

		public override int GetHashCode() => HashCode.Combine(Worktrees, PullRequests, LookedAfter?.Count);

		/// <summary>For the page: who keeps what, with their pages (and the tag of the worktrees Reeve looks after); null for what Heiward keeps.</summary>
		public object Describe() => new {
			worktrees = Worktrees == null ? null : new { name = Worktrees.Name, url = Worktrees.Url, note = Worktrees.Note, kept = WorktreeKept },
			pullRequests = PullRequests == null ? null : new { name = PullRequests.Name, url = PullRequests.Url, note = PullRequests.Note },
		};
	}
}
