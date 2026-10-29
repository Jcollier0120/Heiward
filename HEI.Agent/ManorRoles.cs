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
using System.Globalization;
using System.Text.Json;

namespace HEI.Agent {
	/// <summary>An employee of the manor who looks after part of a developer's housekeeping in Heiward's place.</summary>
	/// <param name="Name">"Reeve", "the Steward": as a sentence says it.</param>
	/// <param name="Url">Its page.</param>
	/// <param name="Note">What it does, as the page says it: "Reeve's worktree-tidy job removes merged worktrees and branches".</param>
	sealed record ManorRole(string Name, string Url, string Note);

	/// <summary>A worktree Reeve's tidy round keeps for a reason only you can clear (<see cref="ManorRoles.KeptForYou"/>).</summary>
	/// <param name="Why">Reeve's reason, in plain words: "has changes that aren't committed".</param>
	/// <param name="TouchedUtc">When it was last touched, as Reeve found it.</param>
	sealed record KeptWork(string Why, DateTime TouchedUtc);

	/// <summary>
	/// Who keeps a developer's git state and pull requests. Standalone, Heiward does it all, as it always has. At a manor
	/// (Manor installed, as <see cref="Manor.Load()"/> has it: its settings.json and app folder), its other employees own
	/// them, and Heiward leaves them alone:
	/// <list type="bullet">
	/// <item><see cref="Worktrees"/>: Reeve, when it's installed too (%USERPROFILE%\.reeve\app, or REEVE_HOME's app). Its
	/// worktree-tidy job removes merged, clean worktrees and deletes merged local branches. Heiward still measures the
	/// worktrees and shows the space they take, read-only, those its last round went through (<see cref="LookedAfter"/>):
	/// no ticks, no automatic cleanup, no Prune. Of those, the ones it keeps for a reason only the user can clear
	/// (<see cref="KeptForYou"/>: uncommitted changes, a merge under way) are the user's again, with Reeve's reason. A
	/// worktree its round doesn't go through is Heiward's, never ticked for the user. What a removal that stopped
	/// partway left (no .git any more, so no worktree git knows) is disk, not git state, and stays Heiward's.</item>
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
		/// <summary>A worktree Reeve keeps for the user (<see cref="KeptForYou"/>): its tag on the page, before Reeve's reason.</summary>
		public const string KeptForYouTag = "Reeve keeps it";

		/// <summary>"Reeve keeps it: has changes that aren't committed".</summary>
		public static string Tag(KeptWork kept) => KeptForYouTag + ": " + kept.Why;

		/// <summary>
		/// The worktrees Reeve's last full worktree-tidy round went through, whatever it decided for each, by their real
		/// paths (<see cref="RealPath"/>) and as Reeve wrote them, ignoring case. Null when Reeve doesn't say (an older
		/// Reeve, no round yet, a file that can't be read): then it looks after them all, as it was taken to before it said.
		/// </summary>
		public IReadOnlySet<string>? LookedAfter { get; init; }

		/// <summary>
		/// Of the worktrees Reeve's last round went through, those it keeps for a reason that won't clear without the user
		/// (uncommitted changes, commits only on this PC, nothing on origin to check the work against, git-ignored files
		/// that aren't build output, a merge or rebase under way), keyed as <see cref="LookedAfter"/> is, with its reason:
		/// the user's again. Null when Reeve doesn't say (an older Reeve, a file that can't be read): then they stay
		/// Reeve's, as they were before it said.
		/// </summary>
		public IReadOnlyDictionary<string, KeptWork>? KeptForYou { get; init; }

		/// <summary>Now: Manor's folder, Reeve's (with its last tidy round) and the Steward's, read fresh.</summary>
		public static ManorRoles Now() {
			string? reeve = Home("REEVE_HOME", ".reeve") is { } home && Directory.Exists(Path.Combine(home, "app")) ? home : null;
			Round round = reeve != null ? RoundIn(reeve) : Round.Unsaid;
			return Of(Manor.Load(), reeve != null, Installed("STEWARD_HOME", ".steward"), round.LookedAfter, round.KeptForYou);
		}

		/// <param name="manor">The manor; null when Manor isn't installed (then Heiward keeps everything).</param>
		/// <param name="reeve">Reeve is installed.</param>
		/// <param name="steward">The Steward is installed.</param>
		/// <param name="lookedAfter">The worktrees Reeve's last round went through (<see cref="LookedAfter"/>); null for all of them.</param>
		/// <param name="keptForYou">Those of them it keeps for the user (<see cref="KeptForYou"/>); null for none.</param>
		public static ManorRoles Of(Manor? manor, bool reeve, bool steward, IReadOnlySet<string>? lookedAfter = null,
			IReadOnlyDictionary<string, KeptWork>? keptForYou = null) => manor == null ? Standalone : new ManorRoles(
			reeve ? new ManorRole("Reeve", ReeveUrl, WorktreesNote) : null,
			steward ? new ManorRole("the Steward", StewardUrl, PullRequestsNote) : null) {
			LookedAfter = reeve ? lookedAfter : null,
			KeptForYou = reeve ? keptForYou : null,
		};

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

		/// <summary>What Reeve's last tidy round says (<see cref="RoundIn"/>): <see cref="LookedAfter"/> and <see cref="KeptForYou"/>.</summary>
		internal sealed record Round(IReadOnlySet<string>? LookedAfter, IReadOnlyDictionary<string, KeptWork>? KeptForYou) {
			/// <summary>Reeve doesn't say: it looks after them all, and keeps none for the user.</summary>
			public static readonly Round Unsaid = new(null, null);
		}

		/// <summary>The last file read (<see cref="RoundIn"/>) and what it says: resolved again only when it changes.</summary>
		static (string Text, Round Round)? lastRound;

		/// <summary>
		/// What Reeve's last full tidy round says, from its job data, jobs\data\worktree-tidy.json in <paramref name="reeveHome"/>:
		/// the worktrees it went through (lookedAfter, <see cref="LookedAfter"/>) and those of them it keeps for the user
		/// (keptForYou, <see cref="KeptForYou"/>). Each null when its field is missing; both when the file is, or anything in
		/// it isn't what Reeve writes: nothing a broken file says is believed.
		/// </summary>
		internal static Round RoundIn(string reeveHome) {
			string text;
			try { text = File.ReadAllText(Path.Combine(reeveHome, "jobs", "data", "worktree-tidy.json")); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) {
				return Round.Unsaid;
			}
			if (lastRound is { } last && last.Text == text) return last.Round;
			Round round = Parse(text);
			lastRound = (text, round);
			return round;

			static Round Parse(string text) {
				try {
					using JsonDocument doc = JsonDocument.Parse(text);
					JsonElement root = doc.RootElement;
					if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("lookedAfter", out JsonElement list)
						|| list.ValueKind != JsonValueKind.Array) return Round.Unsaid;
					var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (JsonElement entry in list.EnumerateArray()) {
						if (!IsFullPath(entry, out string? path)) return Round.Unsaid;
						// Reeve writes real paths; resolved here too, so one written another way still matches.
						paths.Add(AsWritten(path));
						paths.Add(RealPath.Of(path));
					}
					// An older Reeve doesn't write keptForYou: it keeps none for the user.
					if (!root.TryGetProperty("keptForYou", out JsonElement keptList)) return new Round(paths, null);
					if (keptList.ValueKind != JsonValueKind.Array) return Round.Unsaid;
					var kept = new Dictionary<string, KeptWork>(StringComparer.OrdinalIgnoreCase);
					foreach (JsonElement entry in keptList.EnumerateArray()) {
						if (entry.ValueKind != JsonValueKind.Object
							|| !entry.TryGetProperty("path", out JsonElement p) || !IsFullPath(p, out string? path)
							|| !entry.TryGetProperty("why", out JsonElement w) || w.ValueKind != JsonValueKind.String || w.GetString()!.Trim() is not { Length: > 0 } why
							|| !entry.TryGetProperty("touched", out JsonElement t) || t.ValueKind != JsonValueKind.String
							|| !DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime touched))
							return Round.Unsaid;
						var work = new KeptWork(why, touched);
						kept[AsWritten(path)] = work;
						kept[RealPath.Of(path)] = work;
					}
					return new Round(paths, kept);
				}
				catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException or PathTooLongException) {
					return Round.Unsaid;
				}
			}

			static bool IsFullPath(JsonElement entry, [NotNullWhen(true)] out string? path) {
				path = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
				return path is { Length: > 0 } && Path.IsPathFullyQualified(path);
			}
		}

		/// <summary><paramref name="path"/> full, without a trailing separator, as written: no drive is read.</summary>
		static string AsWritten(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

		/// <summary>
		/// Reeve looks after the worktree at <paramref name="path"/>: it's installed at a manor, and its last tidy round went
		/// through it (by its real path), or Reeve doesn't say which its round went through (<see cref="LookedAfter"/>). Not
		/// one it keeps for the user (<see cref="KeptFor"/>): that one is the user's.
		/// </summary>
		/// <param name="resolve">False for a path on a drive the developer check left unread: matched as written (the check
		/// wrote it by its real path), so that drive isn't read.</param>
		public bool LooksAfter(string path, bool resolve = true) {
			if (Worktrees == null || KeptFor(path, resolve) != null) return false;
			if (LookedAfter == null) return true;
			try { return LookedAfter.Contains(AsWritten(path)) || resolve && LookedAfter.Contains(RealPath.Of(path)); }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
		}

		/// <summary>
		/// Why Reeve keeps the worktree at <paramref name="path"/> for the user, and when it was last touched (<see cref="KeptForYou"/>),
		/// matched as <see cref="LooksAfter"/> matches; null when it doesn't.
		/// </summary>
		public KeptWork? KeptFor(string path, bool resolve = true) {
			if (Worktrees == null || KeptForYou == null) return null;
			try { return KeptForYou.GetValueOrDefault(AsWritten(path)) ?? (resolve ? KeptForYou.GetValueOrDefault(RealPath.Of(path)) : null); }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
		}

		/// <summary>
		/// The developer report as Heiward acts on it: with Reeve keeping git state, the worktrees it looks after are
		/// read-only (never ticked, never cleaned, here or automatically: <see cref="WorktreeKept"/>); those it keeps for the
		/// user hold work, so they're the user's to remove, never ticked for them and never cleaned automatically, with
		/// Reeve's reason (<see cref="DevItem.KeptForYou"/>): what blocked them for that work alone no longer does, though
		/// being in use still does. Those its round doesn't go through are Heiward's, never ticked for the user; what
		/// removals left behind is Heiward's as ever. It has no merged branches to prune. Otherwise the report as it is.
		/// The saved report is left whole, so it's right again as soon as Reeve or Manor is gone.
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
							: KeptFor(i.Location, resolve: i.OnRequest == null) is { } kept
								? i with { Suggested = false, Blocked = i.HoldsWork ? null : i.Blocked, KeptForYou = kept.Why, KeptTouchedUtc = kept.TouchedUtc }
							: LooksAfter(i.Location, resolve: i.OnRequest == null) ? i with { Suggested = false, Blocked = WorktreeKept }
							: i with { Suggested = false }).ToList(),
					}).ToList(),
			};
		}

		// Reeve's lists compared as sets: two reads of one file are the same roles.
		public bool Equals(ManorRoles? other) => other is not null && Worktrees == other.Worktrees && PullRequests == other.PullRequests
			&& (LookedAfter == null ? other.LookedAfter == null : other.LookedAfter != null && LookedAfter.SetEquals(other.LookedAfter))
			&& (KeptForYou == null ? other.KeptForYou == null
				: other.KeptForYou != null && KeptForYou.Count == other.KeptForYou.Count
					&& KeptForYou.All(k => other.KeptForYou.TryGetValue(k.Key, out KeptWork? o) && o == k.Value));

		public override int GetHashCode() => HashCode.Combine(Worktrees, PullRequests, LookedAfter?.Count, KeptForYou?.Count);

		/// <summary>
		/// For the page: who keeps what, with their pages (and the tags of the worktrees Reeve looks after, and of those it
		/// keeps for the user); null for what Heiward keeps.
		/// </summary>
		public object Describe() => new {
			worktrees = Worktrees == null ? null : new { name = Worktrees.Name, url = Worktrees.Url, note = Worktrees.Note, kept = WorktreeKept, keptForYou = KeptForYouTag },
			pullRequests = PullRequests == null ? null : new { name = PullRequests.Name, url = PullRequests.Url, note = PullRequests.Note },
		};
	}
}
