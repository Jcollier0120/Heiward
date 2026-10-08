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
	/// What automatic cleanup will do with one thing. <see cref="DueUtc"/>: from when it's cleaned (by the
	/// first scan after, or for developer items the first daily check after); null when it waits for the
	/// user, and <see cref="Reason"/> says why (or <see cref="Held"/>: the user said "leave it").
	/// </summary>
	/// <param name="FolderPair">Set when the whole-folder brake holds it: what the user can allow.</param>
	/// <param name="Count">Files (a set), or merged branches (a repository), that would go.</param>
	sealed record AutoPlanEntry(DateTime? DueUtc, string? Reason, bool Held = false, string? FolderPair = null, int Count = 0, long Bytes = 0);

	/// <summary>One automatic cleanup: what went, and what couldn't.</summary>
	/// <param name="GameItems">What games left behind that went to the Recycle Bin (game mode), and <paramref name="GameBytes"/> their size.</param>
	sealed record AutoRun(DateTime AtUtc, int Files, long FileBytes, int DevItems, long DevBytes, int Branches, List<string> Problems,
		int GameItems = 0, long GameBytes = 0) {
		public bool DidSomething => Files > 0 || DevItems > 0 || Branches > 0 || GameItems > 0;

		/// <summary>"12 copies (340 MB) to the Recycle Bin · 2.1 GB of developer leftovers · 5 merged branches" (the console gets "; ").</summary>
		public string Describe(string separator = " · ") {
			var parts = new List<string>();
			if (Files > 0) parts.Add($"{Files} {(Files == 1 ? "copy" : "copies")} ({Format.Bytes(FileBytes)}) to the Recycle Bin");
			if (DevItems > 0) parts.Add($"{Format.Bytes(DevBytes)} of developer leftovers");
			if (Branches > 0) parts.Add($"{Branches} merged {(Branches == 1 ? "branch" : "branches")}");
			if (GameItems > 0) parts.Add($"{Format.Bytes(GameBytes)} of game leftovers to the Recycle Bin");
			if (parts.Count == 0) parts.Add("nothing cleaned");
			if (Problems.Count > 0) parts.Add($"{Problems.Count} left alone (see the review page)");
			return string.Join(separator, parts);
		}
	}

	sealed record AutoFailure(string Reason, DateTime AtUtc);

	/// <summary>
	/// auto-clean.json: when each thing was first listed, when each half was turned on, what the user held
	/// back, and the last runs. Changed only under <see cref="CleanLock"/>: the page and scans both write it.
	/// </summary>
	sealed class AutoCleanState {
		public DateTime? DuplicatesSinceUtc { get; set; }
		public DateTime? DeveloperSinceUtc { get; set; }
		public DateTime? GamesSinceUtc { get; set; }
		/// <summary>"g:{set}", "d:{developer item}", "b:{repository}:{branch}", "m:{game item}" → when first listed.</summary>
		public Dictionary<string, DateTime> FirstSeenUtc { get; set; } = new();
		/// <summary>"g:{set}", "d:{developer item}", "b:{repository}", "m:{game item}": the user said "leave it".</summary>
		public HashSet<string> Held { get; set; } = new();
		/// <summary>Folder pairs (<see cref="AutoCleaner.FolderPair"/>) the user let past the whole-folder brake.</summary>
		public HashSet<string> AllowedFolderPairs { get; set; } = new();
		/// <summary>"g:{set}" → why automatic cleanup couldn't move it. Tried again a day later.</summary>
		public Dictionary<string, AutoFailure> Failed { get; set; } = new();
		/// <summary>The last runs, newest first.</summary>
		public List<AutoRun> Runs { get; set; } = new();

		public static string FilePath => Path.Combine(AgentPaths.Home, "auto-clean.json");

		public static AutoCleanState Load() {
			try {
				if (File.Exists(FilePath))
					return JsonSerializer.Deserialize<AutoCleanState>(File.ReadAllText(FilePath), AgentConfig.Json) ?? new();
			}
			catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
				AgentPaths.AppendLog($"auto-clean.json unreadable: {e.Message}");
			}
			return new();
		}

		public void Save() => AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(this, AgentConfig.Json));

		/// <summary>Loads, changes and saves it under the cleanup lock.</summary>
		public static AutoCleanState Update(Action<AutoCleanState> change) {
			using (CleanLock.Acquire()) {
				AutoCleanState s = Load();
				change(s);
				s.Save();
				return s;
			}
		}
	}

	/// <summary>What automatic cleanup would do now, thing by thing.</summary>
	sealed class AutoPlan {
		/// <summary>By set key; only sets not decided on the page.</summary>
		public Dictionary<string, AutoPlanEntry> Groups { get; } = new();
		/// <summary>By developer item id.</summary>
		public Dictionary<string, AutoPlanEntry> DevItems { get; } = new();
		/// <summary>By repository id: the first of its merged branches due, and how many are merged.</summary>
		public Dictionary<string, AutoPlanEntry> Repos { get; } = new();
		/// <summary>By game item id (game mode).</summary>
		public Dictionary<string, AutoPlanEntry> GameItems { get; } = new();
		/// <summary>Due now: each set's files to move.</summary>
		internal Dictionary<string, List<string>> GroupTargets { get; } = new();
		/// <summary>Due now: each repository's branches to delete.</summary>
		internal Dictionary<string, List<string>> BranchesDue { get; } = new();
	}

	/// <summary>
	/// Automatic cleanup. After each scan it cleans what the review page would tick for the user, once that
	/// has been listed for <see cref="AutoCleanConfig.AfterDays"/> days (counted from when automatic cleanup
	/// was turned on, at the earliest), through the same guarded actions as the page's buttons. It's stricter
	/// than the page, since nobody looks first:
	/// <list type="bullet">
	/// <item>Duplicates: only plain copies of photos (identical, or the same picture pixel for pixel), and
	/// videos only when byte-for-byte identical. They go to the Recycle Bin, and never when the copy is in a
	/// cloud-synced folder (that deletes it on every device), when the kept file changed since the scan, or
	/// when the set looks like part of a copy of a whole folder: <see cref="WholeFolderSets"/> or more sets
	/// between the same two folders are most likely a backup.</item>
	/// <item>Developer leftovers: only right after the daily developer check, so "untouched for 30 days" is
	/// today's answer; only items the page ticks, of the kinds the user picked; never package caches or
	/// emulators.</item>
	/// <item>Whatever the user decided on the page, or held back with "leave it", stays as it is.</item>
	/// </list>
	/// </summary>
	static class AutoCleaner {
		public const string Branches = "branches", Temp = "temp", BuildOutputs = "buildOutputs", Worktrees = "worktrees", SystemImages = "systemImages";
		/// <summary>The developer kinds automatic cleanup can take, safest first.</summary>
		public static readonly string[] DeveloperKinds = { Branches, Temp, BuildOutputs, Worktrees, SystemImages };

		public const string GameLeftovers = "leftovers", GameCaches = "caches", GameDumps = "dumps", GameShaders = "shaders";
		/// <summary>
		/// What games leave behind that automatic cleanup can take (to the Recycle Bin), safest first. Shader caches only of games
		/// no longer installed: those of installed games make their next launch stutter.
		/// </summary>
		public static readonly string[] GameKinds = { GameDumps, GameCaches, GameLeftovers, GameShaders };

		/// <summary>This many sets or more with copies between the same two folders look like a copy of the whole folder.</summary>
		internal const int WholeFolderSets = 20;
		/// <summary>A set automatic cleanup couldn't move is tried again after this.</summary>
		static readonly TimeSpan RetryAfter = TimeSpan.FromDays(1);
		const int RunsKept = 20;

		/// <summary>
		/// The automatic-cleanup kind of a developer item; null for package caches, emulators and build outputs with no
		/// project beside them, which it never takes.
		/// </summary>
		internal static string? KindOf(DevItem item) => item.Kind switch {
			DevScanner.Projects => BuildOutputs,
			DevScanner.Worktrees => Worktrees,
			"temp" or "dumps" => Temp,
			"sysimage" => SystemImages,
			_ => null,
		};

		/// <summary>
		/// The automatic-cleanup kind of a game item; null for what it never takes: shader caches of installed games and the
		/// graphics drivers', paused downloads, and what Heiward only points to (a game installed twice, one not played).
		/// </summary>
		internal static string? KindOf(GameItem item) => item.Info ? null : item.Kind switch {
			"orphan" or "workshop" => GameLeftovers,
			"cache" or "download" => GameCaches,
			"dump" or "crash" or "wer" => GameDumps,
			"shader" when item.Game == null => GameShaders,
			_ => null,
		};

		internal static string GameLabel(string kind) => kind switch {
			GameLeftovers => "leftovers of uninstalled games",
			GameCaches => "launchers' download caches",
			GameDumps => "crash dumps and reports",
			GameShaders => "shader caches of uninstalled games",
			_ => kind,
		};

		static string Label(string kind) => kind switch {
			Branches => "merged branches",
			Temp => "temp files and crash dumps",
			BuildOutputs => "build outputs",
			Worktrees => "worktrees",
			SystemImages => "emulator system images",
			_ => kind,
		};

		/// <summary>Turned on: the clock starts now. Turned off: it stops.</summary>
		public static void SyncSince(AutoCleanConfig cfg, AutoCleanState s, DateTime now) {
			s.DuplicatesSinceUtc = cfg.Duplicates ? s.DuplicatesSinceUtc ?? now : null;
			s.DeveloperSinceUtc = cfg.Developer ? s.DeveloperSinceUtc ?? now : null;
			s.GamesSinceUtc = cfg.Games ? s.GamesSinceUtc ?? now : null;
		}

		/// <summary>
		/// Notes when each set, ticked developer item and merged branch was first listed, and forgets what's
		/// gone: a developer item that stops being ticked (worked on again, or blocked) starts over.
		/// </summary>
		public static void Observe(AutoCleanState s, Report? report, DevReport? dev, DateTime now, GameReport? games = null) {
			var groups = (report?.Groups ?? new()).Select(g => "g:" + g.Key).ToHashSet();
			var items = dev?.Categories.SelectMany(c => c.Items).ToList() ?? new();
			var repos = dev?.Repositories ?? new();
			var gameItems = games?.Items.ToList() ?? new();
			var listed = new HashSet<string>(groups);
			foreach (DevItem i in items)
				if (i.Suggested && i.Blocked == null) listed.Add("d:" + i.Id);
			foreach (GameItem i in gameItems)
				if (i.Suggested && i.Blocked == null) listed.Add("m:" + i.Id);
			foreach (RepoBranches r in repos)
				foreach (string b in r.Merged) listed.Add($"b:{r.Id}:{b}");
			foreach (string k in listed) s.FirstSeenUtc.TryAdd(k, now);
			foreach (string k in s.FirstSeenUtc.Keys.Where(k => !listed.Contains(k)).ToList()) s.FirstSeenUtc.Remove(k);

			var exists = new HashSet<string>(groups);
			foreach (DevItem i in items) exists.Add("d:" + i.Id);
			foreach (RepoBranches r in repos) exists.Add("b:" + r.Id);
			foreach (GameItem i in gameItems) exists.Add("m:" + i.Id);
			s.Held.RemoveWhere(k => !exists.Contains(k));
			foreach (var (k, f) in s.Failed.ToList())
				if (!groups.Contains(k) || now - f.AtUtc >= RetryAfter) s.Failed.Remove(k);
		}

		public static AutoPlan Plan(AgentConfig cfg, Report? report, DevReport? dev, IReadOnlyDictionary<string, Decision> decisions, AutoCleanState s, DateTime now,
			GameReport? games = null) {
			var plan = new AutoPlan();
			AutoCleanConfig auto = cfg.AutoClean;
			DateTime Due(string key, DateTime? since) {
				DateTime start = s.FirstSeenUtc.TryGetValue(key, out DateTime seen) ? seen : now;
				if (since is { } on && on > start) start = on;
				return start.AddDays(Math.Clamp(auto.AfterDays, 0, AutoCleanConfig.MaxAfterDays));
			}

			if (auto.Duplicates && report != null) {
				var pending = report.Groups.Where(g => !decisions.ContainsKey(g.Key)).ToList();
				var wholeFolders = WholeFolderPairs(pending);
				foreach (ReportGroup g in pending) {
					string key = "g:" + g.Key;
					var (targets, reason) = Targets(g);
					// Cleaning up would wake a drive scanned only when asked: the set waits for you instead.
					if (reason == null && g.Items.Any(i => cfg.IsOnRequest(i.Path)))
						reason = "It's on a drive scanned only when you ask, so it waits for you";
					string? pair = null;
					if (reason == null) {
						string keepFolder = Keeper(g).Folder;
						pair = targets.Select(i => FolderPair(keepFolder, i.Folder)?.Key)
							.FirstOrDefault(p => p != null && wholeFolders.ContainsKey(p) && !s.AllowedFolderPairs.Contains(p));
						if (pair != null) reason = wholeFolders[pair];
					}
					if (reason == null && s.Failed.TryGetValue(key, out AutoFailure? failed))
						reason = $"Automatic cleanup couldn't move it ({failed.Reason}); it tries again a day later";
					bool held = s.Held.Contains(key);
					DateTime? due = reason == null && !held ? Due(key, s.DuplicatesSinceUtc) : null;
					plan.Groups[g.Key] = new AutoPlanEntry(due, reason, held, pair, targets.Count, targets.Sum(i => i.Size));
					if (due <= now) plan.GroupTargets[g.Key] = targets.Select(i => i.Path).ToList();
				}
			}

			if (auto.Developer && dev != null) {
				foreach (DevItem i in dev.Categories.SelectMany(c => c.Items)) {
					string key = "d:" + i.Id;
					string? kind = KindOf(i);
					string? reason =
						kind == null ? i.Kind switch {
							DevScanner.Caches => "Package caches are always your call",
							DevScanner.Orphans => "Build outputs with no project beside them are always your call",
							_ => "Emulators hold apps and data: always your call",
						} :
						!auto.DeveloperKinds.Contains(kind) ? $"Automatic cleanup is off for {Label(kind)}" :
						i.Blocked ?? (i.Suggested ? null : kind is BuildOutputs or Worktrees ? $"Used in the last {cfg.StaleProjectDays} days" : "Not ticked for you");
					bool held = s.Held.Contains(key);
					DateTime? due = reason == null && !held ? Due(key, s.DeveloperSinceUtc) : null;
					plan.DevItems[i.Id] = new AutoPlanEntry(due, reason, held, Count: i.Paths.Count, Bytes: i.Bytes);
				}
				foreach (RepoBranches r in dev.Repositories.Where(r => r.Merged.Count > 0)) {
					if (!auto.DeveloperKinds.Contains(Branches)) {
						plan.Repos[r.Id] = new AutoPlanEntry(null, $"Automatic cleanup is off for {Label(Branches)}", Count: r.Merged.Count);
						continue;
					}
					bool held = s.Held.Contains("b:" + r.Id);
					var dues = r.Merged.Select(b => (Branch: b, Due: Due($"b:{r.Id}:{b}", s.DeveloperSinceUtc))).ToList();
					plan.Repos[r.Id] = new AutoPlanEntry(held ? null : dues.Min(d => d.Due), null, held, Count: r.Merged.Count);
					var dueNow = held ? new() : dues.Where(d => d.Due <= now).Select(d => d.Branch).ToList();
					if (dueNow.Count > 0) plan.BranchesDue[r.Id] = dueNow;
				}
			}

			if (auto.Games && games != null) {
				foreach (GameItem i in games.Items.Where(i => !i.Info)) {
					string key = "m:" + i.Id;
					string? kind = KindOf(i);
					string? reason =
						kind == null ? (i.Kind == "paused" ? "A paused download is always your call"
							: "Shader caches of installed games are always your call: they rebuild, and the next launch may stutter") :
						!auto.GameKinds.Contains(kind) ? $"Automatic cleanup is off for {GameLabel(kind)}" :
						i.Blocked ?? (i.Suggested ? null : "Not ticked for you");
					bool held = s.Held.Contains(key);
					DateTime? due = reason == null && !held ? Due(key, s.GamesSinceUtc) : null;
					plan.GameItems[i.Id] = new AutoPlanEntry(due, reason, held, Count: i.Paths.Count, Bytes: i.Bytes);
				}
			}
			return plan;
		}

		static ReportItem Keeper(ReportGroup g) => g.Items.First(i => i.Keep);

		/// <summary>The files of a set automatic cleanup would move, or why it moves none.</summary>
		internal static (List<ReportItem> Targets, string? Reason) Targets(ReportGroup g) {
			var copies = g.Items.Where(i => i.Suggested && !i.Keep).ToList();
			if (copies.Count == 0)
				return (copies, "Edits and look-alikes are always your call");
			if (g.Media == "video") {
				// The pixel-level threshold is calibrated on photos only.
				copies = copies.Where(i => i.Relation == "identical").ToList();
				if (copies.Count == 0)
					return (copies, "A video is cleaned automatically only when it's byte-for-byte identical");
			}
			var local = copies.Where(i => !i.Synced).ToList();
			if (local.Count == 0)
				return (local, "It's in a cloud-synced folder: deleting it here deletes it on every device");
			return (local, null);
		}

		/// <summary>
		/// Two folders with their common tail cut off, so a mirrored tree is one pair: C:\Photos\2024 and
		/// D:\Backup\Photos\2024 give C:\ and D:\Backup. The key ignores which side holds the kept file.
		/// Null for the same folder ("IMG_1 (1).jpg" beside IMG_1.jpg is the everyday kind of copy).
		/// </summary>
		internal static (string Key, string A, string B)? FolderPair(string keepFolder, string copyFolder) {
			string[] a = keepFolder.TrimEnd('\\').Split('\\'), b = copyFolder.TrimEnd('\\').Split('\\');
			int common = 0;
			while (common < a.Length && common < b.Length && a[^(common + 1)].Equals(b[^(common + 1)], StringComparison.OrdinalIgnoreCase))
				common++;
			string ra = Root(a[..(a.Length - common)]), rb = Root(b[..(b.Length - common)]);
			if (ra.Equals(rb, StringComparison.OrdinalIgnoreCase)) return null;
			if (string.Compare(ra, rb, StringComparison.OrdinalIgnoreCase) > 0) (ra, rb) = (rb, ra);
			return ((ra + "|" + rb).ToLowerInvariant(), ra, rb);

			static string Root(string[] parts) => parts.Length == 1 && parts[0].EndsWith(':') ? parts[0] + "\\" : string.Join('\\', parts);
		}

		/// <summary>Folder pairs with copies of <see cref="WholeFolderSets"/> or more sets, and what the page says about them.</summary>
		static Dictionary<string, string> WholeFolderPairs(List<ReportGroup> groups) {
			var sets = new Dictionary<string, (int Count, string A, string B)>();
			foreach (ReportGroup g in groups) {
				string keepFolder = Keeper(g).Folder;
				var pairs = Targets(g).Targets.Select(i => FolderPair(keepFolder, i.Folder)).Where(p => p != null).Select(p => p!.Value).DistinctBy(p => p.Key);
				foreach (var p in pairs)
					sets[p.Key] = sets.TryGetValue(p.Key, out var c) ? (c.Count + 1, c.A, c.B) : (1, p.A, p.B);
			}
			return sets.Where(kv => kv.Value.Count >= WholeFolderSets).ToDictionary(kv => kv.Key,
				kv => $"{kv.Value.Count} sets have copies in both {kv.Value.A} and {kv.Value.B}: that looks like a copy of a whole folder (a backup?), so they wait for you");
		}

		/// <summary>Why the kept file is no longer what the scan saw (then its copy may be the only original left), or null.</summary>
		static string? KeptFileProblem(ReportItem keep) {
			var fi = new FileInfo(keep.Path);
			if (!fi.Exists) return "the kept file is gone";
			if (fi.Length != keep.Size || Math.Abs((fi.LastWriteTimeUtc - keep.ModifiedUtc).TotalSeconds) > 2) return "the kept file changed since the scan";
			return null;
		}

		/// <summary>
		/// Cleans what's due. <paramref name="devChecked"/>: the developer check just ran, so its list is
		/// today's (developer items are cleaned only then). Updates <paramref name="s"/>: first-listed times,
		/// failures, and the run. Null when automatic cleanup is off.
		/// </summary>
		/// <param name="games">The games check's list, with game mode on; <paramref name="gamesChecked"/>: it just ran, so it's today's (game items are cleaned only then).</param>
		internal static AutoRun? Run(AgentConfig cfg, Report? report, DevReport? dev, IReadOnlyDictionary<string, Decision> decisions,
			AutoCleanState s, DateTime now, bool devChecked, IAutoActions actions, GameReport? games = null, bool gamesChecked = false) {
			SyncSince(cfg.AutoClean, s, now);
			Observe(s, report, dev, now, games);
			if (!cfg.AutoClean.Duplicates && !cfg.AutoClean.Developer && !cfg.AutoClean.Games) return null;
			AutoPlan plan = Plan(cfg, report, dev, decisions, s, now, games);

			int files = 0, devItems = 0, branches = 0;
			long fileBytes = 0, devBytes = 0;
			var problems = new List<string>();
			foreach (var (key, paths) in plan.GroupTargets) {
				ReportGroup g = report!.Groups.First(x => x.Key == key);
				ReportItem keep = Keeper(g);
				if (KeptFileProblem(keep) is { } why) {
					problems.Add($"{keep.Name}: {why}");
					s.Failed["g:" + key] = new AutoFailure(why, now);
					continue;
				}
				RecycleResult r = actions.Recycle(g, paths);
				files += r.Recycled.Count;
				fileBytes += r.RecycledBytes;
				problems.AddRange(r.Failed.Select(f => $"{Path.GetFileName(f.Path)}: {f.Reason}"));
				if (r.Recycled.Count == 0 && r.Failed.Count > 0)
					s.Failed["g:" + key] = new AutoFailure(r.Failed[0].Reason, now);
			}
			if (devChecked && dev != null) {
				foreach (DevItem i in dev.Categories.SelectMany(c => c.Items).ToList()) {
					if (!plan.DevItems.TryGetValue(i.Id, out AutoPlanEntry? e) || !(e.DueUtc <= now)) continue;
					CleanResult r = actions.Clean(i);
					if (r.FreedBytes > 0) {
						devItems++;
						devBytes += r.FreedBytes;
					}
					if (r.Error != null) problems.Add($"{i.Name}: {r.Error}");
					else if (r.Describe(withPath: false) is { } left) problems.Add($"{i.Name}: {left}");
				}
				foreach (var (repoId, due) in plan.BranchesDue) {
					RepoBranches repo = dev.Repositories.First(r => r.Id == repoId);
					PruneResult r = actions.Prune(repo, due);
					branches += r.Deleted.Count;
					if (r.Error != null) problems.Add($"{repo.Name}: {r.Error}");
				}
			}
			int gameItems = 0;
			long gameBytes = 0;
			if (gamesChecked && games != null) {
				foreach (GameItem i in games.Items.ToList()) {
					if (!plan.GameItems.TryGetValue(i.Id, out AutoPlanEntry? e) || !(e.DueUtc <= now)) continue;
					CleanResult r = actions.RemoveGame(i);
					if (r.FreedBytes > 0) {
						gameItems++;
						gameBytes += r.FreedBytes;
					}
					if (r.Error != null) problems.Add($"{i.Name}: {r.Error}");
					else if (r.Describe(withPath: false) is { } left) problems.Add($"{i.Name}: {left}");
				}
			}
			var run = new AutoRun(now, files, fileBytes, devItems, devBytes, branches, problems, gameItems, gameBytes);
			if (run.DidSomething || problems.Count > 0) {
				s.Runs.Insert(0, run);
				if (s.Runs.Count > RunsKept) s.Runs.RemoveRange(RunsKept, s.Runs.Count - RunsKept);
			}
			return run;
		}

		/// <summary>
		/// After a scan: runs automatic cleanup on the saved reports, holding the cleanup lock throughout. Developer
		/// leftovers only with developer mode on (<paramref name="devMode"/>: Manor's Developer options, or Heiward's own switch),
		/// and never worktrees or merged branches where Reeve keeps them (<see cref="ManorRoles"/>).
		/// </summary>
		/// <param name="gameMode">What games leave behind only with game mode on, right after the daily games check (<paramref name="gamesChecked"/>).</param>
		public static AutoRun? RunAndSave(AgentConfig cfg, DevMode devMode, bool devChecked, IAutoActions actions, GameMode? gameMode = null, bool gamesChecked = false) {
			ManorRoles roles = ManorRoles.Now();
			bool on = cfg.AutoClean.Duplicates || cfg.AutoClean.Developer || cfg.AutoClean.Games;
			if (!on && !File.Exists(AutoCleanState.FilePath)) return null; // never turned on: nothing to keep
			using (CleanLock.Acquire(TimeSpan.FromMinutes(10))) {
				AutoCleanState s = AutoCleanState.Load();
				AutoRun? run = Run(cfg, Report.Load(), devMode.On ? roles.View(DevReport.Load()) : null, DecisionStore.Load(), s, DateTime.UtcNow, devChecked, actions,
					gameMode is { On: true } ? GameReport.Load() : null, gamesChecked);
				s.Save();
				if (run != null && (run.DidSomething || run.Problems.Count > 0))
					AgentPaths.AppendLog("automatic cleanup: " + run.Describe() + string.Concat(run.Problems.Select(p => Environment.NewLine + "    left alone: " + p)));
				return run;
			}
		}

		/// <summary>
		/// The sets that need the user: all of them with automatic cleanup of duplicates off; otherwise
		/// those it won't take (look-alikes, synced copies, held back).
		/// </summary>
		public static List<ReportGroup> WaitingForUser(AgentConfig cfg, Report report, IReadOnlyDictionary<string, Decision> decisions, AutoCleanState s, DateTime now) {
			var pending = report.Groups.Where(g => !decisions.ContainsKey(g.Key)).ToList();
			if (!cfg.AutoClean.Duplicates) return pending;
			AutoPlan plan = Plan(cfg, report, null, decisions, s, now);
			return pending.Where(g => !(plan.Groups.TryGetValue(g.Key, out AutoPlanEntry? e) && e.DueUtc != null)).ToList();
		}

		/// <summary>
		/// A set waiting for the user that calls for them: a notification, the page opening at sign-in.
		/// Duplicates do; look-alikes don't (Heiward calls out duplicates), they're on the page whenever it's opened.
		/// </summary>
		public static bool Announced(ReportGroup g) => g.Kind != "similar";
	}

	/// <summary>The cleaning actions, for the page's buttons and automatic cleanup alike.</summary>
	interface IAutoActions {
		RecycleResult Recycle(ReportGroup group, IReadOnlyCollection<string> paths);
		CleanResult Clean(DevItem item);
		/// <param name="branches">Only these of its merged branches; null for all.</param>
		PruneResult Prune(RepoBranches repo, IReadOnlyCollection<string>? branches);
		/// <summary>Moves what a game left behind to the Recycle Bin (game mode).</summary>
		CleanResult RemoveGame(GameItem item) => new(0, 0, "Not available here");
	}

	/// <summary>
	/// Each action under <see cref="CleanLock"/>, recorded in decisions.json (marked automatic when it
	/// was), with the saved report brought up to date.
	/// </summary>
	sealed class CleanupActions : IAutoActions {
		readonly AgentConfig cfg;
		readonly bool automatic;

		public CleanupActions(AgentConfig cfg, bool automatic) {
			this.cfg = cfg;
			this.automatic = automatic;
		}

		public RecycleResult Recycle(ReportGroup group, IReadOnlyCollection<string> paths) => Recycle(group, paths, null, null);

		/// <param name="batch">Part of one cleanup of a whole folder (<paramref name="folder"/>): one History row for all its sets.</param>
		public RecycleResult Recycle(ReportGroup group, IReadOnlyCollection<string> paths, string? batch, string? folder) {
			using (CleanLock.Acquire()) {
				RecycleResult result = Recycler.Recycle(group, paths);
				if (result.Recycled.Count > 0)
					DecisionStore.Record(cfg, group.Key, new Decision("recycled", DateTime.UtcNow, result.Recycled, result.RecycledBytes, automatic, batch, folder));
				return result;
			}
		}

		public CleanResult Clean(DevItem item) {
			CleanResult result;
			using (CleanLock.Acquire()) {
				result = DevCleaner.Clean(item, cfg);
				if (result.FreedBytes > 0)
					DecisionStore.Record(cfg, "dev:" + item.Id, new Decision("dev-cleaned", DateTime.UtcNow, new() { item.Name }, result.FreedBytes, automatic));
				// Gone, or what's left (files in use) re-measured.
				if (result.Error == null)
					DevReport.Update(item.Id, result.LeftInUse == 0 ? null : item with { Bytes = Math.Max(0, item.Bytes - result.FreedBytes), Suggested = false });
			}
			// Without a history, the log keeps no names either.
			AgentPaths.AppendLog($"developer clean{(automatic ? " (automatic)" : "")}: {item.Kind}{(cfg.KeepHistory ? " " + item.Location : "")}: freed {Format.Bytes(result.FreedBytes)}" +
				(result.Describe(cfg.KeepHistory) is { } left ? ", " + left : "") + (result.Error != null ? $", {result.Error}" : ""));
			return result;
		}

		public CleanResult RemoveGame(GameItem item) {
			CleanResult result;
			using (CleanLock.Acquire()) {
				result = GameCleaner.Remove(item, GamePlaces.Current(cfg));
				if (result.FreedBytes > 0)
					DecisionStore.Record(cfg, "game:" + item.Id, new Decision("game-recycled", DateTime.UtcNow, new() { item.Name }, result.FreedBytes, automatic));
				// Gone, or what's left (in use, refused) kept for another try, unticked.
				if (result.Error == null)
					GameReport.Update(item.Id, result.LeftInUse == 0 ? null : item with { Bytes = Math.Max(0, item.Bytes - result.FreedBytes), Suggested = false });
			}
			// Without a history, the log keeps no names either.
			AgentPaths.AppendLog($"game clean{(automatic ? " (automatic)" : "")}: {item.Kind}{(cfg.KeepHistory ? " " + item.Location : "")}: " +
				$"{Format.Bytes(result.FreedBytes)} to the Recycle Bin" +
				(result.Describe(cfg.KeepHistory) is { } left ? ", " + left : "") + (result.Error != null ? $", {result.Error}" : ""));
			return result;
		}

		public PruneResult Prune(RepoBranches repo, IReadOnlyCollection<string>? branches) {
			// At a manor with Reeve, merged branches are Reeve's to delete (ManorRoles): read fresh, whatever the list said.
			if (ManorRoles.Now().Worktrees is { } reeve) return new PruneResult(new(), new(), false, reeve.Note + ": Heiward leaves branches to it");
			PruneResult result;
			using (CleanLock.Acquire()) {
				result = BranchPruner.Prune(repo.Path, branches);
				if (result.Deleted.Count > 0)
					DecisionStore.Record(cfg, $"branches:{repo.Id}:{DateTime.UtcNow.Ticks}",
						new Decision("branches-pruned", DateTime.UtcNow, new[] { repo.Name }.Concat(result.Deleted).ToList(), 0, automatic));
				if (BranchPruner.Inspect(repo.Path) is { } now) DevReport.UpdateRepository(now);
			}
			// With a history, the repository and each branch by name, one per line.
			string counts = $"deleted {result.Deleted.Count}, kept {result.Kept.Count}" + (result.Fetched ? "" : "; fetch failed");
			AgentPaths.AppendLog(cfg.KeepHistory
				? $"pruned branches{(automatic ? " (automatic)" : "")} in {repo.Path}: {counts}" +
					string.Concat(result.Deleted.Select(b => Environment.NewLine + "    deleted: " + b)) +
					string.Concat(result.Kept.Select(k => Environment.NewLine + $"    kept: {k.Branch} ({k.Reason})"))
				: $"pruned branches{(automatic ? " (automatic)" : "")}: {counts}");
			return result;
		}
	}

	/// <summary>
	/// One cleanup at a time, across the review page and scans (clean.lock): two cleanups of one set at
	/// once could each keep the file the other removes. Re-entrant on a thread, since automatic cleanup
	/// holds it for its whole run and calls the same actions as the page.
	/// </summary>
	static class CleanLock {
		[ThreadStatic] static int depth;
		static string LockPath => Path.Combine(AgentPaths.Home, "clean.lock");

		/// <exception cref="TimeoutException">Another cleanup held it for longer than <paramref name="wait"/> (default: two minutes).</exception>
		public static IDisposable Acquire(TimeSpan? wait = null) {
			if (depth > 0) {
				depth++;
				return new Holder(null);
			}
			Directory.CreateDirectory(AgentPaths.Home);
			DateTime until = DateTime.UtcNow + (wait ?? TimeSpan.FromMinutes(2));
			while (true) {
				try {
					var held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
					depth = 1;
					return new Holder(held);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					if (DateTime.UtcNow >= until) throw new TimeoutException("Another cleanup is running; try again in a minute.");
					Thread.Sleep(100);
				}
			}
		}

		sealed class Holder : IDisposable {
			FileStream? held;
			bool released;

			public Holder(FileStream? held) => this.held = held;

			public void Dispose() {
				if (released) return;
				released = true;
				depth--;
				held?.Dispose();
				held = null;
			}
		}
	}
}
