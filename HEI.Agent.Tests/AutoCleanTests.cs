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

namespace HEI.Agent.Tests;

/// <summary>
/// Automatic cleanup: what it takes, what it always leaves for the user, and when. Runs use stand-in
/// actions, so nothing here moves or deletes a file.
/// </summary>
[Collection(AgentHomeCollection.Name)] // one test points HEIWARD_HOME at its own folder
public sealed class AutoCleanTests : IDisposable {
	static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
	static readonly Dictionary<string, Decision> NoDecisions = new();
	readonly string dir = Path.Combine(Path.GetTempPath(), "heiward-auto-tests-" + Guid.NewGuid().ToString("N"));

	public AutoCleanTests() => Directory.CreateDirectory(dir);
	public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

	static ReportItem Item(string path, string relation, bool synced = false, long size = 1000, DateTime? modified = null) {
		bool suggested = relation is "identical" or "smaller" or "compressed" or "resaved";
		return new ReportItem(path, Path.GetFileName(path), Path.GetDirectoryName(path)!, size, 100, 100, "jpg", 0, 0, 0,
			modified ?? Now.AddDays(-100), 100, false, relation, relation == "keep", suggested, synced);
	}

	static ReportGroup Group(string key, params ReportItem[] items) => Group(key, "image", items);

	static ReportGroup Group(string key, string media, params ReportItem[] items) =>
		new(key, items.Any(i => i.Suggested) ? "copies" : "similar", media, items.First(i => i.Keep).Path, "test",
			items.Where(i => i.Suggested).Sum(i => i.Size), 100, items.ToList());

	static Report ReportOf(IEnumerable<ReportGroup> groups) => new(Report.CurrentVersion, Now, 1, "NPU", 10, new(), new(), new(), groups.ToList());
	static Report ReportOf(params ReportGroup[] groups) => ReportOf(groups.AsEnumerable());

	static AgentConfig Config(bool duplicates = true, bool developer = false, int days = 3, params string[] kinds) => new() {
		AutoClean = new AutoCleanConfig {
			Duplicates = duplicates, Developer = developer, AfterDays = days,
			DeveloperKinds = kinds.Length > 0 ? kinds.ToList() : AutoCleaner.DeveloperKinds.ToList(),
		},
	};

	/// <summary>Turned on long ago, with each key first listed at the given time.</summary>
	static AutoCleanState State(params (string Key, DateTime At)[] seen) {
		var s = new AutoCleanState { DuplicatesSinceUtc = Now.AddDays(-60), DeveloperSinceUtc = Now.AddDays(-60) };
		foreach (var (k, at) in seen) s.FirstSeenUtc[k] = at;
		return s;
	}

	static ReportGroup PlainCopy(string key, string keepFolder = @"C:\Pictures", string copyFolder = @"C:\Pictures") =>
		Group(key, Item($@"{keepFolder}\{key}.jpg", "keep"), Item($@"{copyFolder}\{key} (1).jpg", "identical"));

	// ------------------------------------------------------------------ settings

	[Fact]
	public void It_is_off_until_turned_on() {
		var auto = new AgentConfig().AutoClean;
		Assert.False(auto.Duplicates);
		Assert.False(auto.Developer);
		Assert.Equal(3, auto.AfterDays);
		Assert.Equal(AutoCleaner.DeveloperKinds, auto.DeveloperKinds);
	}

	[Fact]
	public void Settings_read_from_json_are_made_valid() {
		var cfg = JsonSerializer.Deserialize<AgentConfig>("""{"autoClean":{"duplicates":true,"developerKinds":["temp","everything"],"afterDays":500}}""", AgentConfig.Json)!;
		AutoCleanConfig next = cfg.AutoClean.Normalized();
		Assert.True(next.Duplicates);
		Assert.Equal(new[] { "temp" }, next.DeveloperKinds);
		Assert.Equal(AutoCleanConfig.MaxAfterDays, next.AfterDays);
		Assert.False(JsonSerializer.Deserialize<AgentConfig>("""{"autoClean":null}""", AgentConfig.Json)!.AutoClean.Duplicates);
		Assert.False(JsonSerializer.Deserialize<AgentConfig>("{}", AgentConfig.Json)!.AutoClean.Developer);
	}

	// ------------------------------------------------------------------ duplicates

	[Fact]
	public void A_plain_copy_goes_once_it_has_been_listed_long_enough() {
		ReportGroup g = PlainCopy("a");
		AutoPlan plan = AutoCleaner.Plan(Config(), ReportOf(g), null, NoDecisions, State(("g:a", Now.AddDays(-4))), Now);
		Assert.Equal(Now.AddDays(-1), plan.Groups["a"].DueUtc);
		Assert.Equal(new[] { @"C:\Pictures\a (1).jpg" }, plan.GroupTargets["a"]);

		plan = AutoCleaner.Plan(Config(), ReportOf(g), null, NoDecisions, State(("g:a", Now.AddDays(-2))), Now);
		Assert.Equal(Now.AddDays(1), plan.Groups["a"].DueUtc);
		Assert.Empty(plan.GroupTargets);
	}

	[Fact]
	public void The_wait_counts_from_when_it_was_turned_on() {
		AutoCleanState s = State(("g:a", Now.AddDays(-30)));
		s.DuplicatesSinceUtc = Now.AddDays(-1);
		AutoPlan plan = AutoCleaner.Plan(Config(), ReportOf(PlainCopy("a")), null, NoDecisions, s, Now);
		Assert.Equal(Now.AddDays(2), plan.Groups["a"].DueUtc);
		Assert.Empty(plan.GroupTargets);
	}

	[Fact]
	public void Edits_and_look_alikes_always_wait() {
		ReportGroup g = Group("a", Item(@"C:\P\a.jpg", "keep"), Item(@"C:\P\a-edited.jpg", "edited"), Item(@"C:\P\b.jpg", "variant"));
		AutoPlanEntry e = AutoCleaner.Plan(Config(), ReportOf(g), null, NoDecisions, State(("g:a", Now.AddDays(-30))), Now).Groups["a"];
		Assert.Null(e.DueUtc);
		Assert.Contains("look-alikes", e.Reason);
	}

	[Fact]
	public void A_copy_in_a_synced_folder_stays_and_the_others_go() {
		ReportGroup mixed = Group("a", Item(@"C:\P\a.jpg", "keep"), Item(@"C:\iCloud\a.jpg", "identical", synced: true), Item(@"C:\P\a (1).jpg", "identical"));
		ReportGroup synced = Group("b", Item(@"C:\P\b.jpg", "keep"), Item(@"C:\iCloud\b.jpg", "identical", synced: true));
		AutoPlan plan = AutoCleaner.Plan(Config(), ReportOf(mixed, synced), null, NoDecisions, State(("g:a", Now.AddDays(-9)), ("g:b", Now.AddDays(-9))), Now);
		Assert.Equal(new[] { @"C:\P\a (1).jpg" }, plan.GroupTargets["a"]);
		Assert.Null(plan.Groups["b"].DueUtc);
		Assert.Contains("cloud", plan.Groups["b"].Reason);
	}

	[Fact]
	public void A_video_goes_only_when_byte_for_byte_identical() {
		ReportGroup resaved = Group("a", "video", Item(@"C:\V\a.mp4", "keep"), Item(@"C:\V\a-small.mp4", "compressed"));
		ReportGroup same = Group("b", "video", Item(@"C:\V\b.mp4", "keep"), Item(@"C:\V\b (1).mp4", "identical"));
		AutoPlan plan = AutoCleaner.Plan(Config(), ReportOf(resaved, same), null, NoDecisions, State(("g:a", Now.AddDays(-9)), ("g:b", Now.AddDays(-9))), Now);
		Assert.Null(plan.Groups["a"].DueUtc);
		Assert.Contains("identical", plan.Groups["a"].Reason);
		Assert.Equal(new[] { @"C:\V\b (1).mp4" }, plan.GroupTargets["b"]);
	}

	[Fact]
	public void A_set_the_user_held_back_or_decided_on_stays() {
		AutoCleanState s = State(("g:a", Now.AddDays(-9)), ("g:b", Now.AddDays(-9)));
		s.Held.Add("g:a");
		var decisions = new Dictionary<string, Decision> { ["b"] = new("kept", Now, new(), 0) };
		AutoPlan plan = AutoCleaner.Plan(Config(), ReportOf(PlainCopy("a"), PlainCopy("b")), null, decisions, s, Now);
		Assert.True(plan.Groups["a"].Held);
		Assert.Null(plan.Groups["a"].DueUtc);
		Assert.False(plan.Groups.ContainsKey("b"));
		Assert.Empty(plan.GroupTargets);
	}

	[Fact]
	public void Copies_of_a_whole_folder_wait_as_a_likely_backup_until_allowed() {
		ReportGroup[] Mirror(int n) => Enumerable.Range(0, n)
			.Select(i => PlainCopy("m" + i, $@"C:\Users\me\Pictures\2024\{i % 12:00}", $@"D:\Backup\Pictures\2024\{i % 12:00}")).ToArray();
		AutoCleanState Seen(int n) => State(Enumerable.Range(0, n).Select(i => ("g:m" + i, Now.AddDays(-9))).ToArray());

		AutoPlan plan = AutoCleaner.Plan(Config(), ReportOf(Mirror(AutoCleaner.WholeFolderSets)), null, NoDecisions, Seen(AutoCleaner.WholeFolderSets), Now);
		Assert.Empty(plan.GroupTargets);
		AutoPlanEntry e = plan.Groups["m0"];
		Assert.Contains("backup", e.Reason);
		Assert.Contains(@"D:\Backup", e.Reason);
		Assert.NotNull(e.FolderPair);

		AutoCleanState allowed = Seen(AutoCleaner.WholeFolderSets);
		allowed.AllowedFolderPairs.Add(e.FolderPair!);
		Assert.Equal(AutoCleaner.WholeFolderSets, AutoCleaner.Plan(Config(), ReportOf(Mirror(AutoCleaner.WholeFolderSets)), null, NoDecisions, allowed, Now).GroupTargets.Count);

		int fewer = AutoCleaner.WholeFolderSets - 1;
		Assert.Equal(fewer, AutoCleaner.Plan(Config(), ReportOf(Mirror(fewer)), null, NoDecisions, Seen(fewer), Now).GroupTargets.Count);
	}

	[Fact]
	public void Copies_beside_their_originals_never_look_like_a_backup() {
		var groups = Enumerable.Range(0, 50).Select(i => PlainCopy("s" + i)).ToArray();
		var s = State(groups.Select(g => ("g:" + g.Key, Now.AddDays(-9))).ToArray());
		Assert.Equal(50, AutoCleaner.Plan(Config(), ReportOf(groups), null, NoDecisions, s, Now).GroupTargets.Count);
	}

	[Fact]
	public void FolderPair_cuts_the_common_tail_and_ignores_direction() {
		var p = AutoCleaner.FolderPair(@"C:\Photos\2024\June", @"D:\Backup\Photos\2024\June")!.Value;
		Assert.Equal(@"C:\", p.A);
		Assert.Equal(@"D:\Backup", p.B);
		Assert.Equal(p.Key, AutoCleaner.FolderPair(@"D:\Backup\Photos\2024\May", @"C:\Photos\2024\May")!.Value.Key);
		Assert.Null(AutoCleaner.FolderPair(@"C:\Photos", @"c:\photos\"));
	}

	// ------------------------------------------------------------------ developer leftovers

	static DevItem Dev(string id, string kind, bool suggested = true, string? blocked = null) =>
		new(id, kind, id, @"C:\x\" + id, new() { @"C:\x\" + id }, 5 << 20, null, suggested, blocked, "");

	static DevReport DevOf(IEnumerable<DevItem> items, params RepoBranches[] repos) => new() {
		ScannedAtUtc = Now,
		Categories = new() { new DevCategory("all", "all", "", items.ToList()) },
		Repositories = repos.ToList(),
	};

	[Fact]
	public void Developer_items_go_by_kind_and_tick_and_never_caches_or_emulators() {
		DevReport dev = DevOf(new[] {
			Dev("build", DevScanner.Projects), Dev("tree", DevScanner.Worktrees, suggested: false), Dev("cache", DevScanner.Caches, suggested: false),
			Dev("avd", "avd", suggested: false), Dev("tmp", "temp"), Dev("img", "sysimage", suggested: false, blocked: "An emulator is running"),
		});
		var s = State(dev.Categories[0].Items.Select(i => ("d:" + i.Id, Now.AddDays(-9))).ToArray());
		AutoPlan plan = AutoCleaner.Plan(Config(duplicates: false, developer: true, kinds: new[] { AutoCleaner.BuildOutputs, AutoCleaner.Worktrees, AutoCleaner.SystemImages }),
			null, dev, NoDecisions, s, Now);
		Assert.NotNull(plan.DevItems["build"].DueUtc);
		Assert.Contains("Used in the last", plan.DevItems["tree"].Reason);
		Assert.Contains("caches", plan.DevItems["cache"].Reason);
		Assert.Contains("Emulators", plan.DevItems["avd"].Reason);
		Assert.Contains("off for temp files", plan.DevItems["tmp"].Reason);
		Assert.Equal("An emulator is running", plan.DevItems["img"].Reason);
	}

	[Fact]
	public void Merged_branches_go_one_by_one_as_each_comes_due() {
		var repo = new RepoBranches("r1", "app", @"C:\src\app", "origin/main", 5, new() { "old", "new" }, new(), null);
		var s = State(("b:r1:old", Now.AddDays(-5)), ("b:r1:new", Now.AddDays(-1)));
		AutoPlan plan = AutoCleaner.Plan(Config(duplicates: false, developer: true), null, DevOf(Array.Empty<DevItem>(), repo), NoDecisions, s, Now);
		Assert.Equal(new[] { "old" }, plan.BranchesDue["r1"]);
		Assert.Equal(Now.AddDays(-2), plan.Repos["r1"].DueUtc);

		s.Held.Add("b:r1");
		plan = AutoCleaner.Plan(Config(duplicates: false, developer: true), null, DevOf(Array.Empty<DevItem>(), repo), NoDecisions, s, Now);
		Assert.Empty(plan.BranchesDue);
		Assert.True(plan.Repos["r1"].Held);
	}

	// ------------------------------------------------------------------ bookkeeping and runs

	[Fact]
	public void Observe_keeps_first_listed_times_and_forgets_what_is_gone() {
		var s = State(("g:stay", Now.AddDays(-5)), ("g:gone", Now.AddDays(-5)), ("d:idle", Now.AddDays(-5)));
		s.Held.Add("g:gone");
		s.Held.Add("g:stay");
		s.Failed["g:stay"] = new AutoFailure("too large", Now.AddDays(-2));
		var dev = DevOf(new[] { Dev("idle", DevScanner.Projects, suggested: false), Dev("fresh", "temp") });
		AutoCleaner.Observe(s, ReportOf(PlainCopy("stay"), PlainCopy("new")), dev, Now);
		Assert.Equal(Now.AddDays(-5), s.FirstSeenUtc["g:stay"]);
		Assert.Equal(Now, s.FirstSeenUtc["g:new"]);
		Assert.Equal(Now, s.FirstSeenUtc["d:fresh"]);
		Assert.False(s.FirstSeenUtc.ContainsKey("g:gone"));
		Assert.False(s.FirstSeenUtc.ContainsKey("d:idle")); // worked on again: its clock starts over when it's ticked again
		Assert.Equal(new[] { "g:stay" }, s.Held);
		Assert.Empty(s.Failed); // a day later, it's tried again
	}

	sealed class FakeActions : IAutoActions {
		public readonly List<(string Key, List<string> Paths)> Recycled = new();
		public readonly List<string> Cleaned = new();
		public readonly List<(string Repo, List<string> Branches)> Pruned = new();

		public RecycleResult Recycle(ReportGroup group, IReadOnlyCollection<string> paths) {
			Recycled.Add((group.Key, paths.ToList()));
			return new RecycleResult(paths.ToList(), new(), group.Items.Where(i => paths.Contains(i.Path)).Sum(i => i.Size));
		}

		public CleanResult Clean(DevItem item) {
			Cleaned.Add(item.Id);
			return new CleanResult(item.Bytes, 0, null);
		}

		public PruneResult Prune(RepoBranches repo, IReadOnlyCollection<string>? branches) {
			var due = branches!.ToList();
			Pruned.Add((repo.Id, due));
			return new PruneResult(due, new(), true, null);
		}
	}

	/// <summary>A set whose kept file exists on disk, as the scan saw it (automatic cleanup checks it).</summary>
	ReportGroup OnDisk(string key) {
		string keep = Path.Combine(dir, key + ".jpg");
		File.WriteAllBytes(keep, new byte[1000]);
		var fi = new FileInfo(keep);
		return Group(key, Item(keep, "keep", modified: fi.LastWriteTimeUtc), Item(Path.Combine(dir, key + " (1).jpg"), "identical"));
	}

	[Fact]
	public void A_run_moves_due_copies_and_cleans_developer_items_only_after_the_daily_check() {
		ReportGroup due = OnDisk("due"), later = OnDisk("later");
		var repo = new RepoBranches("r1", "app", @"C:\src\app", "origin/main", 3, new() { "done" }, new(), null);
		DevReport dev = DevOf(new[] { Dev("tmp", "temp") }, repo);
		AutoCleanState S() => State(("g:due", Now.AddDays(-4)), ("g:later", Now.AddDays(-1)), ("d:tmp", Now.AddDays(-4)), ("b:r1:done", Now.AddDays(-4)));
		AgentConfig cfg = Config(duplicates: true, developer: true);

		var actions = new FakeActions();
		AutoCleanState s = S();
		AutoRun run = AutoCleaner.Run(cfg, ReportOf(due, later), dev, NoDecisions, s, Now, devChecked: false, actions)!;
		Assert.Equal(new[] { "due" }, actions.Recycled.Select(r => r.Key));
		Assert.Equal(new[] { due.Items[1].Path }, actions.Recycled[0].Paths);
		Assert.Empty(actions.Cleaned);
		Assert.Empty(actions.Pruned);
		Assert.Equal(1, run.Files);
		Assert.Single(s.Runs);

		actions = new FakeActions();
		run = AutoCleaner.Run(cfg, ReportOf(due, later), dev, NoDecisions, S(), Now, devChecked: true, actions)!;
		Assert.Equal(new[] { "tmp" }, actions.Cleaned);
		Assert.Equal(new[] { "done" }, Assert.Single(actions.Pruned).Branches);
		Assert.Equal(1, run.Branches);
		Assert.Equal(5L << 20, run.DevBytes);
	}

	[Fact]
	public void A_run_leaves_a_set_whose_kept_file_changed_and_says_why() {
		ReportGroup g = OnDisk("edited");
		File.WriteAllBytes(g.Items[0].Path, new byte[2000]); // the kept photo was edited after the scan
		var actions = new FakeActions();
		AutoCleanState s = State(("g:edited", Now.AddDays(-9)));
		AutoRun run = AutoCleaner.Run(Config(), ReportOf(g), null, NoDecisions, s, Now, devChecked: false, actions)!;
		Assert.Empty(actions.Recycled);
		Assert.Contains("changed since the scan", Assert.Single(run.Problems));
		Assert.Contains("couldn't move it", AutoCleaner.Plan(Config(), ReportOf(g), null, NoDecisions, s, Now).Groups["edited"].Reason);
	}

	[Fact]
	public void Nothing_runs_while_it_is_off() {
		var actions = new FakeActions();
		AutoRun? run = AutoCleaner.Run(Config(duplicates: false), ReportOf(OnDisk("a")), null, NoDecisions, State(("g:a", Now.AddDays(-9))), Now, devChecked: true, actions);
		Assert.Null(run);
		Assert.Empty(actions.Recycled);
	}

	[Fact]
	public void Only_sets_it_wont_take_wait_for_the_user() {
		ReportGroup copy = PlainCopy("copy"), lookalike = Group("look", Item(@"C:\P\x.jpg", "keep"), Item(@"C:\P\y.jpg", "variant"));
		Report report = ReportOf(copy, lookalike);
		AutoCleanState s = State(("g:copy", Now), ("g:look", Now));
		Assert.Equal(2, AutoCleaner.WaitingForUser(Config(duplicates: false), report, NoDecisions, s, Now).Count);
		Assert.Equal(new[] { "look" }, AutoCleaner.WaitingForUser(Config(), report, NoDecisions, s, Now).Select(g => g.Key));
	}

	[Fact]
	public void The_cleanup_lock_is_one_at_a_time_and_reentrant_on_a_thread() {
		string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
		try {
			using (CleanLock.Acquire()) {
				using (CleanLock.Acquire()) { } // automatic cleanup calls the page's actions while it holds the lock
				Exception? other = null;
				var t = new Thread(() => { try { using (CleanLock.Acquire(TimeSpan.FromMilliseconds(300))) { } } catch (Exception e) { other = e; } });
				t.Start();
				t.Join();
				Assert.IsType<TimeoutException>(other);
			}
			Exception? after = null;
			var t2 = new Thread(() => { try { using (CleanLock.Acquire(TimeSpan.FromSeconds(5))) { } } catch (Exception e) { after = e; } });
			t2.Start();
			t2.Join();
			Assert.Null(after);
		}
		finally {
			Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		}
	}
}
