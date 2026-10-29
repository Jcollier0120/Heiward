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

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace HEI.Agent.Tests;

/// <summary>
/// At a manor, Heiward leaves git state to Reeve (worktrees and merged branches) and pull requests to the Steward, each
/// when it's installed: the worktrees are measured and shown read-only, no branch is pruned, no host is asked for pull
/// requests. Standalone, or at a manor without them, Heiward does it all as before. Every home here is a fixture: nothing
/// reads the real Manor, Reeve or Steward, or Heiward's own settings.
/// </summary>
[Collection(AgentHomeCollection.Name)] // MANOR_HOME, REEVE_HOME, STEWARD_HOME and HEIWARD_HOME are process-wide
public sealed class ManorRolesTests : IDisposable {
	static readonly string[] Vars = ["MANOR_HOME", "REEVE_HOME", "STEWARD_HOME", "HEIWARD_HOME"];
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-roles-" + Guid.NewGuid().ToString("N"));
	readonly Dictionary<string, string?> saved = Vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
	readonly string manor, reeve, steward;

	public ManorRolesTests() {
		manor = Path.Combine(dir, "manor");
		reeve = Path.Combine(dir, "reeve");
		steward = Path.Combine(dir, "steward");
		Directory.CreateDirectory(Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "heiward"));
		// Nothing installed in any of them until a test says so.
		Environment.SetEnvironmentVariable("MANOR_HOME", manor);
		Environment.SetEnvironmentVariable("REEVE_HOME", reeve);
		Environment.SetEnvironmentVariable("STEWARD_HOME", steward);
	}

	public void Dispose() {
		foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
		try { Directory.Delete(dir, true); } catch { }
	}

	void InstallManor() {
		Directory.CreateDirectory(Path.Combine(manor, "app"));
		File.WriteAllText(Path.Combine(manor, "settings.json"), "{\"name\":\"The Hall\"}");
	}

	static void Install(string home) => Directory.CreateDirectory(Path.Combine(home, "app"));

	void InstallAll() {
		InstallManor();
		Install(reeve);
		Install(steward);
	}

	// ---- Who keeps what

	[Fact]
	public void Standalone_HeiwardKeepsEverything() {
		Assert.Equal(ManorRoles.Standalone, ManorRoles.Now());
		// Reeve and the Steward installed without Manor: Heiward is standalone still.
		Install(reeve);
		Install(steward);
		Assert.Equal(ManorRoles.Standalone, ManorRoles.Now());
		Assert.Null(ManorRoles.Now().Worktrees);
		Assert.Null(ManorRoles.Now().PullRequests);
	}

	[Fact]
	public void AtAManor_EachPartGoesToWhoeverIsInstalled_NothingLeftUnowned() {
		InstallManor();
		Assert.Equal(new ManorRoles(null, null), ManorRoles.Now()); // a manor without Reeve or the Steward: still Heiward's

		Install(reeve);
		ManorRoles roles = ManorRoles.Now();
		Assert.Equal(new ManorRole("Reeve", "http://reeve.localhost:18383/", "Reeve's worktree-tidy job removes merged worktrees and branches"), roles.Worktrees);
		Assert.Null(roles.PullRequests);

		Install(steward);
		Assert.Equal(new ManorRole("the Steward", "http://steward.localhost:19494/", "The Steward merges pull requests"), ManorRoles.Now().PullRequests);

		// Manor's settings without its app folder: Manor isn't installed, so Heiward keeps everything again.
		Directory.Delete(Path.Combine(manor, "app"));
		Assert.Equal(ManorRoles.Standalone, ManorRoles.Now());
	}

	[Fact]
	public void AnAgentsHomeWithoutItsAppFolder_IsntInstalled() {
		InstallManor();
		Directory.CreateDirectory(reeve);
		File.WriteAllText(Path.Combine(reeve, "config.json"), "{}");
		Directory.CreateDirectory(Path.Combine(steward, "kits")); // the kit cache every agent shares, not the Steward
		Assert.Equal(new ManorRoles(null, null), ManorRoles.Now());
	}

	// ---- The developer report as Heiward acts on it

	static DevItem Worktree(string name, bool leftover = false) =>
		new(name, DevScanner.Worktrees, name, @"C:\r\.claude\worktrees\" + name, [@"C:\r\.claude\worktrees\" + name], 40 << 20,
			DateTime.UtcNow.AddDays(-60), Suggested: true, Blocked: null, "branch x · worktree of r", @"C:\r", Leftover: leftover);

	static readonly DevItem Temp = new("tmp", "temp", "Temp folder", @"C:\t", [@"C:\t\a"], 5 << 20, null, true, null, "");

	static DevReport Report() => new() {
		ScannedAtUtc = DateTime.UtcNow, Build = AppBuild.Current, StaleDays = 30,
		Categories = [
			new DevCategory(DevScanner.Worktrees, "Git worktrees", "", [Worktree("wt"), Worktree("left", leftover: true)]),
			new DevCategory(DevScanner.Temp, "Temp", "", [Temp]),
		],
		Repositories = [new RepoBranches("repo1", "r", @"C:\r", "origin/main", 4, ["done-1", "done-2"], [], null)],
		Sources = [new RepoSource(@"C:\r", "git", "https://github.com/someone/r.git")],
	};

	[Fact]
	public void View_WithReeve_WorktreesReadOnly_NoBranches_RestAsItWas() {
		DevReport report = Report();
		DevReport view = ManorRoles.Of(new Manor("The Hall", 18585, null), reeve: true, steward: false).View(report);

		DevItem wt = view.Categories[0].Items[0];
		Assert.False(wt.Suggested);
		Assert.Equal(ManorRoles.WorktreeKept, wt.Blocked);
		Assert.Equal(40 << 20, wt.Bytes); // still measured
		Assert.Same(report.Categories[0].Items[1], view.Categories[0].Items[1]); // what a removal left: disk, still Heiward's
		Assert.Same(report.Categories[1], view.Categories[1]);
		Assert.Empty(view.Repositories);
		Assert.Equal(report.Sources, view.Sources);
		Assert.Equal(report.ScannedAtUtc, view.ScannedAtUtc);

		// The saved report itself is left whole.
		Assert.True(report.Categories[0].Items[0].Suggested);
		Assert.Single(report.Repositories);
	}

	/// <summary>Reeve's last worktree-tidy round as it writes it: its job data, jobs\data\worktree-tidy.json in its home.</summary>
	void TidyRound(string json) {
		Directory.CreateDirectory(Path.Combine(reeve, "jobs", "data"));
		File.WriteAllText(Path.Combine(reeve, "jobs", "data", "worktree-tidy.json"), json);
	}

	static string Round(params string[] lookedAfter) =>
		JsonSerializer.Serialize(new { worktrees = 3, keptWork = new[] { "C:\\r: kept" }, dryRun = false, lookedAfter });

	static (DevItem Wt, DevItem Mine, DevItem Left) Worktrees(DevReport view) =>
		(view.Categories[0].Items.Single(i => i.Id == "wt"), view.Categories[0].Items.Single(i => i.Id == "mine"), view.Categories[0].Items.Single(i => i.Id == "left"));

	static DevReport ReportWithMine() {
		DevReport report = Report();
		report.Categories[0] = report.Categories[0] with { Items = [.. report.Categories[0].Items, Worktree("mine")] };
		return report;
	}

	[Fact]
	public void View_ReeveLooksAfterOnlyTheWorktreesItsRoundWentThrough_TheOthersAreHeiwards() {
		InstallManor();
		Install(reeve);
		// Reeve's paths are real ones, sorted, any case; a trailing separator is the same folder.
		TidyRound(Round(@"C:\elsewhere\x", @"C:\R\.CLAUDE\worktrees\WT\"));
		var (wt, mine, left) = Worktrees(ManorRoles.Now().View(ReportWithMine()));

		Assert.False(wt.Suggested);
		Assert.Equal(ManorRoles.WorktreeKept, wt.Blocked);
		// Not in its round: Heiward's, as without Reeve, but never ticked for the user.
		Assert.False(mine.Suggested);
		Assert.Null(mine.Blocked);
		Assert.Equal(40 << 20, mine.Bytes);
		Assert.True(left.Suggested); // what a removal left: disk, still Heiward's
		Assert.Null(left.Blocked);

		// Read fresh: the next round goes through it too.
		TidyRound(Round(@"C:\r\.claude\worktrees\mine", @"C:\r\.claude\worktrees\wt"));
		Assert.Equal(ManorRoles.WorktreeKept, Worktrees(ManorRoles.Now().View(ReportWithMine())).Mine.Blocked);
		// And an empty round went through none of them.
		TidyRound(Round());
		var (wt2, mine2, _) = Worktrees(ManorRoles.Now().View(ReportWithMine()));
		Assert.Null(wt2.Blocked);
		Assert.Null(mine2.Blocked);
	}

	[Fact]
	public void View_OneFolderUnderTwoNames_IsTheSameWorktree() {
		string real = Path.Combine(dir, "real"), wtFolder = Path.Combine(real, "wt");
		Directory.CreateDirectory(wtFolder);
		// As C:\worktrees is E:\worktrees: a junction, made as mklink /J does (no rights needed).
		string link = Path.Combine(dir, "link");
		using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") {
			CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
		})!) {
			p.StandardOutput.ReadToEnd();
			p.WaitForExit();
			Assert.Equal(0, p.ExitCode);
		}
		try {
			InstallManor();
			Install(reeve);
			DevItem AsHeiwardHasIt(string location) => Worktree("wt") with { Location = location, Paths = [location] };
			DevReport ReportAt(string location) => new() { ScannedAtUtc = DateTime.UtcNow, Build = AppBuild.Current, Categories = [new DevCategory(DevScanner.Worktrees, "", "", [AsHeiwardHasIt(location)])] };

			// Heiward has it through the junction, Reeve by its real path.
			TidyRound(Round(RealPath.Of(wtFolder)));
			Assert.Equal(ManorRoles.WorktreeKept, ManorRoles.Now().View(ReportAt(Path.Combine(link, "wt"))).Categories[0].Items[0].Blocked);
			// And the other way round: a Reeve that wrote it through the junction.
			TidyRound(Round(Path.Combine(link, "wt")));
			Assert.Equal(ManorRoles.WorktreeKept, ManorRoles.Now().View(ReportAt(wtFolder)).Categories[0].Items[0].Blocked);
			// A folder beside it isn't it.
			Assert.Null(ManorRoles.Now().View(ReportAt(Path.Combine(link, "wt2"))).Categories[0].Items[0].Blocked);
		}
		finally {
			Directory.Delete(link); // the junction only, never what it points to
		}
	}

	[Fact]
	public void View_ReeveThatDoesntSayWhichItWentThrough_LooksAfterThemAll() {
		InstallManor();
		Install(reeve);
		void AllReeves() {
			var (wt, mine, left) = Worktrees(ManorRoles.Now().View(ReportWithMine()));
			Assert.Equal(ManorRoles.WorktreeKept, wt.Blocked);
			Assert.Equal(ManorRoles.WorktreeKept, mine.Blocked);
			Assert.False(mine.Suggested);
			Assert.Null(left.Blocked);
		}
		AllReeves(); // no round yet
		Assert.Null(ManorRoles.Now().LookedAfter);
		// An older Reeve: its job data without the field.
		TidyRound("{\"worktrees\":3,\"keptWork\":[],\"dryRun\":false}");
		AllReeves();
		Assert.Null(ManorRoles.Now().LookedAfter);
		// A broken file is no answer either, never an error.
		foreach (string broken in new[] { "{\"lookedAfter\":[\"C:\\\\r\\\\.claude\\\\worktrees\\\\wt\"", "", "[]", "{\"lookedAfter\":\"C:\\\\x\"}", "{\"lookedAfter\":[1]}", "{\"lookedAfter\":[\"relative\\\\wt\"]}" }) {
			TidyRound(broken);
			AllReeves();
		}
	}

	// ---- The worktrees Reeve keeps for you: the user's again

	const string Uncommitted = "has changes that aren't committed";
	static readonly DateTime Touched = new(2026, 9, 27, 14, 3, 12, DateTimeKind.Utc);

	/// <summary>A round as Reeve writes it with keptForYou: its entries' JSON as given, so a test can break one.</summary>
	static string RoundKeeping(string[] lookedAfter, string keptForYou) =>
		$"{{\"worktrees\":3,\"keptWork\":[\"C:\\\\r: kept\"],\"dryRun\":false,\"lookedAfter\":{JsonSerializer.Serialize(lookedAfter)},\"keptForYou\":{keptForYou}}}";

	static string Kept(string path, string why = Uncommitted, string touched = "2026-09-27T14:03:12.000Z") =>
		JsonSerializer.Serialize(new { path, why, touched });

	const string WtPath = @"C:\r\.claude\worktrees\wt", MinePath = @"C:\r\.claude\worktrees\mine";

	[Fact]
	public void KeptForYou_IsRead_ByRealPathIgnoringCase_AndIsntReevesToLookAfter() {
		InstallManor();
		Install(reeve);
		// Reeve's paths are lookedAfter's; any case, a trailing separator, is the same folder.
		TidyRound(RoundKeeping([WtPath, MinePath], $"[{Kept(@"C:\R\.CLAUDE\worktrees\MINE\")}]"));
		ManorRoles roles = ManorRoles.Now();

		Assert.Equal(new KeptWork(Uncommitted, Touched), roles.KeptFor(MinePath));
		Assert.Equal(DateTimeKind.Utc, roles.KeptFor(MinePath)!.TouchedUtc.Kind);
		Assert.False(roles.LooksAfter(MinePath)); // the user's again
		Assert.Null(roles.KeptFor(WtPath));
		Assert.True(roles.LooksAfter(WtPath)); // the rest of its round stays Reeve's
		Assert.Equal(roles, ManorRoles.Now()); // two reads of one file are the same roles

		// None kept: [] is every worktree its round went through, as Reeve's.
		TidyRound(RoundKeeping([WtPath, MinePath], "[]"));
		Assert.NotNull(ManorRoles.Now().KeptForYou);
		Assert.True(ManorRoles.Now().LooksAfter(MinePath));
		Assert.NotEqual(roles, ManorRoles.Now());
	}

	[Fact]
	public void View_TagsAndTicks_EachKindOfWorktree() {
		InstallManor();
		Install(reeve);
		DevReport report = Report();
		report.Categories[0] = report.Categories[0] with {
			Items = [
				.. report.Categories[0].Items,
				Worktree("mine") with { Suggested = false, Blocked = "Uncommitted changes", HoldsWork = true },
				Worktree("busy") with { Suggested = false, Blocked = "A Claude Code session works in it" },
				Worktree("own"),
			],
		};
		TidyRound(RoundKeeping([WtPath, MinePath, @"C:\r\.claude\worktrees\busy"],
			$"[{Kept(MinePath)}, {Kept(@"C:\r\.claude\worktrees\busy", "a git merge is under way in it")}]"));
		List<DevItem> items = ManorRoles.Now().View(report).Categories[0].Items;
		DevItem Of(string id) => items.Single(i => i.Id == id);

		// Reeve's: tagged, no tick, never cleaned.
		Assert.False(Of("wt").Suggested);
		Assert.Equal(ManorRoles.WorktreeKept, Of("wt").Blocked);
		Assert.Null(Of("wt").KeptForYou);
		// Kept for you: its reason and when it was touched, no tag of Reeve's, never ticked for you, but yours to tick.
		Assert.False(Of("mine").Suggested);
		Assert.Null(Of("mine").Blocked);
		Assert.Equal(Uncommitted, Of("mine").KeptForYou);
		Assert.Equal(Touched, Of("mine").KeptTouchedUtc);
		Assert.Equal(40 << 20, Of("mine").Bytes); // still measured
		// Kept for you, but in use: Reeve's reason beside it, and still no tick until it's let go.
		Assert.Equal("A Claude Code session works in it", Of("busy").Blocked);
		Assert.Equal("a git merge is under way in it", Of("busy").KeptForYou);
		// Not in its round: Heiward's, never ticked for you.
		Assert.False(Of("own").Suggested);
		Assert.Null(Of("own").Blocked);
		Assert.Null(Of("own").KeptForYou);
		// What a removal left: disk, Heiward's as ever.
		Assert.True(Of("left").Suggested);
		Assert.Null(Of("left").Blocked);
	}

	[Fact]
	public void KeptForYou_MissingOrBroken_LeavesThemReevesAsBefore() {
		InstallManor();
		Install(reeve);
		void AsBefore() {
			DevItem mine = Worktrees(ManorRoles.Now().View(ReportWithMine())).Mine;
			Assert.Equal(ManorRoles.WorktreeKept, mine.Blocked);
			Assert.Null(mine.KeptForYou);
		}

		// An older Reeve: lookedAfter without keptForYou. What its round went through stays Reeve's.
		TidyRound(Round(WtPath, MinePath));
		Assert.Null(ManorRoles.Now().KeptForYou);
		Assert.NotNull(ManorRoles.Now().LookedAfter);
		AsBefore();

		// Anything in it that isn't what Reeve writes: nothing the file says is believed, lookedAfter included.
		foreach (string kept in new[] {
			"\"" + MinePath.Replace(@"\", @"\\") + "\"", "null", "{}", "[1]", "[null]",
			$"[{Kept(@"relative\mine")}]", $"[{Kept(MinePath, why: "  ")}]", $"[{Kept(MinePath, touched: "lately")}]",
			$"[{JsonSerializer.Serialize(new { path = MinePath, why = Uncommitted })}]",
			$"[{JsonSerializer.Serialize(new { path = MinePath, why = 5, touched = "2026-09-27T14:03:12.000Z" })}]",
			$"[{Kept(MinePath)}, {Kept("")}]",
		}) {
			TidyRound(RoundKeeping([WtPath], kept));
			Assert.Null(ManorRoles.Now().KeptForYou);
			Assert.Null(ManorRoles.Now().LookedAfter);
			AsBefore();
		}
		// Cut short as it was written.
		TidyRound(RoundKeeping([WtPath, MinePath], $"[{Kept(MinePath)}]")[..^3]);
		Assert.Null(ManorRoles.Now().KeptForYou);
		AsBefore();
	}

	[Fact]
	public void AutomaticCleanup_NeverTakesAWorktreeReeveKeepsForYou() {
		InstallManor();
		Install(reeve);
		TidyRound(RoundKeeping([WtPath], $"[{Kept(WtPath)}]"));
		var (cleaned, pruned) = RunAutomaticCleanup();
		Assert.Equal(new[] { "left", "tmp" }, cleaned.Order());
		Assert.Empty(pruned);

		// It says why, where automatic cleanup says why it waits.
		var cfg = new AgentConfig { DeveloperMode = "on", AutoClean = new AutoCleanConfig { Developer = true } };
		AutoPlan plan = AutoCleaner.Plan(cfg, null, ManorRoles.Now().View(Report()), new Dictionary<string, Decision>(), new AutoCleanState(), DateTime.UtcNow);
		Assert.Equal("It holds work Reeve keeps for you: only you remove it", plan.DevItems["wt"].Reason);
		Assert.Null(plan.DevItems["wt"].DueUtc);
	}

	static string G(string dir, params string[] args) {
		var psi = new System.Diagnostics.ProcessStartInfo(Git.Exe!) {
			UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir,
		};
		foreach (string a in new[] { "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "init.defaultBranch=main", "-c", "commit.gpgsign=false" }.Concat(args))
			psi.ArgumentList.Add(a);
		using var p = System.Diagnostics.Process.Start(psi)!;
		Task<string> error = p.StandardError.ReadToEndAsync();
		string output = p.StandardOutput.ReadToEnd();
		p.WaitForExit();
		Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {output}{error.Result}");
		return output;
	}

	[Fact]
	public void TheActions_RemoveAWorktreeReeveKeepsForYou_OnlyByHand_WorkToTheRecycleBin() {
		if (Git.Exe == null) return; // needs git
		string repo = Path.Combine(dir, "repo");
		Directory.CreateDirectory(repo);
		G(repo, "init");
		File.WriteAllText(Path.Combine(repo, ".gitignore"), "node_modules/\n");
		File.WriteAllText(Path.Combine(repo, "a.txt"), "one");
		G(repo, "add", ".");
		G(repo, "commit", "-m", "one");
		string wt = Path.Combine(dir, "wt");
		G(repo, "worktree", "add", "-b", "kept", wt);
		File.WriteAllText(Path.Combine(wt, "a.txt"), "two, not committed");
		Directory.CreateDirectory(Path.Combine(wt, "node_modules", "pkg"));
		File.WriteAllText(Path.Combine(wt, "node_modules", "pkg", "x.js"), new string('x', 4096));
		string record = Path.Combine(repo, ".git", "worktrees", "wt");
		Assert.True(Directory.Exists(record));
		var item = new DevItem("w", DevScanner.Worktrees, "wt", wt, [wt], 1, null, false, null, "", repo, HoldsWork: true);
		InstallManor();
		Install(reeve);
		TidyRound(RoundKeeping([RealPath.Of(wt)], $"[{Kept(RealPath.Of(wt))}]"));

		// Never automatically: nothing is touched.
		CleanResult auto = new CleanupActions(new AgentConfig(), automatic: true).Clean(item);
		Assert.Equal("Reeve keeps it: has changes that aren't committed: only you remove it, never automatic cleanup", auto.Error);
		Assert.Equal(0, auto.FreedBytes);
		Assert.Equal("two, not committed", File.ReadAllText(Path.Combine(wt, "a.txt")));

		// By hand: build output for good, the folder and git's record of it to the Recycle Bin, the branch kept.
		CleanResult byHand = new CleanupActions(new AgentConfig(), automatic: false).Clean(item);
		Assert.Null(byHand.Error);
		Assert.Equal(0, byHand.LeftInUse);
		Assert.True(byHand.FreedBytes >= 4096);
		Assert.False(Directory.Exists(wt));
		Assert.False(Directory.Exists(record));
		Assert.Contains("kept", G(repo, "branch", "--list", "kept"));
		Assert.Single(G(repo, "worktree", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries)); // the clone's own, alone
	}

	[Fact]
	public void TheActions_LeaveAWorktreeReeveNoLongerKeepsForYou_ToReeve() {
		string repo = Path.Combine(dir, "repo"), wt = Path.Combine(dir, "wt");
		Directory.CreateDirectory(repo);
		Directory.CreateDirectory(wt);
		File.WriteAllText(Path.Combine(wt, "file.txt"), "x");
		var item = new DevItem("w", DevScanner.Worktrees, "wt", wt, [wt], 1, null, false, null, "", repo, HoldsWork: true);
		InstallManor();
		Install(reeve);
		// The page listed it as kept for you; Reeve's next round looks after it again (the work was committed, say).
		TidyRound(RoundKeeping([wt], "[]"));
		Assert.Equal("Reeve's worktree-tidy job removes merged worktrees and branches: Heiward leaves worktrees to it",
			new CleanupActions(new AgentConfig(), automatic: false).Clean(item).Error);
		Assert.True(File.Exists(Path.Combine(wt, "file.txt")));
	}

	[Fact]
	public void TheActions_CleanAWorktreeReevesRoundDidntGoThrough() {
		string repo = Path.Combine(dir, "repo"), wt = Path.Combine(dir, "wt");
		Directory.CreateDirectory(repo);
		Directory.CreateDirectory(wt);
		File.WriteAllText(Path.Combine(wt, "file.txt"), "x");
		var item = new DevItem("w", DevScanner.Worktrees, "wt", wt, [wt], 1, null, false, null, "", repo);
		InstallManor();
		Install(reeve);
		var actions = new CleanupActions(new AgentConfig(), automatic: false);

		TidyRound(Round(Path.Combine(dir, "another")));
		CleanResult clean = actions.Clean(item);
		// Past Reeve: here git is what stops it, as it is without Reeve (the repository isn't one).
		Assert.DoesNotContain("Reeve", clean.Error ?? "");
		Assert.True(File.Exists(Path.Combine(wt, "file.txt")));

		TidyRound(Round(wt));
		Assert.Equal("Reeve's worktree-tidy job removes merged worktrees and branches: Heiward leaves worktrees to it", actions.Clean(item).Error);
	}

	[Fact]
	public void AutomaticCleanup_NeverTakesAWorktreeReevesRoundDidntGoThrough() {
		InstallManor();
		Install(reeve);
		TidyRound(Round(@"C:\elsewhere"));
		var (cleaned, pruned) = RunAutomaticCleanup();
		Assert.Equal(new[] { "left", "tmp" }, cleaned.Order()); // wt is Heiward's now, but never ticked: the user cleans it
		Assert.Empty(pruned);
	}

	[Fact]
	public void View_StandaloneOrWithoutReeve_IsTheReportItself() {
		DevReport report = Report();
		Assert.Same(report, ManorRoles.Standalone.View(report));
		Assert.Same(report, ManorRoles.Of(new Manor("The Hall", 18585, null), reeve: false, steward: true).View(report));
		Assert.Null(ManorRoles.Standalone.View(null));
	}

	// ---- Automatic cleanup, and the actions themselves

	/// <summary>Cleans nothing: says what it would have.</summary>
	sealed class Recorder : IAutoActions {
		public readonly List<string> Cleaned = new(), Pruned = new();
		public RecycleResult Recycle(ReportGroup group, IReadOnlyCollection<string> paths) => new(new(), new(), 0);
		public CleanResult Clean(DevItem item) {
			Cleaned.Add(item.Id);
			return new CleanResult(item.Bytes, 0, null);
		}
		public PruneResult Prune(RepoBranches repo, IReadOnlyCollection<string>? branches) {
			Pruned.AddRange(branches ?? []);
			return new PruneResult([.. branches ?? []], new(), true, null);
		}
	}

	(List<string> Cleaned, List<string> Pruned) RunAutomaticCleanup() {
		DateTime now = DateTime.UtcNow;
		Report().Save();
		var cfg = new AgentConfig { DeveloperMode = "on", AutoClean = new AutoCleanConfig { Developer = true } };
		var s = new AutoCleanState { DeveloperSinceUtc = now.AddDays(-60) };
		foreach (string k in new[] { "d:wt", "d:left", "d:tmp", "b:repo1:done-1", "b:repo1:done-2" }) s.FirstSeenUtc[k] = now.AddDays(-9);
		s.Save();
		var actions = new Recorder();
		AutoCleaner.RunAndSave(cfg, DevMode.Now(cfg), devChecked: true, actions);
		return (actions.Cleaned, actions.Pruned);
	}

	[Fact]
	public void AutomaticCleanup_Standalone_TakesWorktreesAndBranches() {
		var (cleaned, pruned) = RunAutomaticCleanup();
		Assert.Equal(new[] { "left", "tmp", "wt" }, cleaned.Order());
		Assert.Equal(new[] { "done-1", "done-2" }, pruned.Order());
	}

	[Fact]
	public void AutomaticCleanup_AtAManorWithReeve_LeavesWorktreesAndBranches() {
		InstallManor();
		Install(reeve);
		var (cleaned, pruned) = RunAutomaticCleanup();
		Assert.Equal(new[] { "left", "tmp" }, cleaned.Order());
		Assert.Empty(pruned);
	}

	[Fact]
	public void AutomaticCleanup_AtAManorWithoutReeve_StillTakesThem() {
		InstallManor();
		Install(steward);
		var (cleaned, pruned) = RunAutomaticCleanup();
		Assert.Equal(new[] { "left", "tmp", "wt" }, cleaned.Order());
		Assert.Equal(new[] { "done-1", "done-2" }, pruned.Order());
	}

	[Fact]
	public void TheActions_RefuseWorktreesAndBranches_WhereReeveKeepsThem() {
		// A worktree folder that's still there, in a repository that isn't one: only the roles can stop it here.
		string repo = Path.Combine(dir, "repo"), wt = Path.Combine(dir, "wt");
		Directory.CreateDirectory(repo);
		Directory.CreateDirectory(wt);
		File.WriteAllText(Path.Combine(wt, "file.txt"), "x");
		var item = new DevItem("w", DevScanner.Worktrees, "wt", wt, [wt], 1, null, true, null, "", repo);
		InstallManor();
		Install(reeve);

		var actions = new CleanupActions(new AgentConfig(), automatic: false);
		CleanResult clean = actions.Clean(item);
		Assert.Equal(0, clean.FreedBytes);
		Assert.Equal("Reeve's worktree-tidy job removes merged worktrees and branches: Heiward leaves worktrees to it", clean.Error);
		Assert.True(File.Exists(Path.Combine(wt, "file.txt")));

		PruneResult prune = actions.Prune(new RepoBranches("r", "repo", repo, "origin/main", 2, ["done"], [], null), null);
		Assert.Empty(prune.Deleted);
		Assert.Equal("Reeve's worktree-tidy job removes merged worktrees and branches: Heiward leaves branches to it", prune.Error);
	}

	[Fact]
	public void HeiDevPruneBranches_IsReevesAtAManor() {
		InstallManor();
		Install(reeve);
		var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "hei.exe")) {
			UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
		};
		foreach (string a in new[] { "dev", "--prune-branches", dir }) psi.ArgumentList.Add(a);
		foreach (string v in Vars) psi.Environment[v] = Environment.GetEnvironmentVariable(v);
		using var p = System.Diagnostics.Process.Start(psi)!;
		Task<string> error = p.StandardError.ReadToEndAsync();
		p.StandardOutput.ReadToEnd();
		Assert.True(p.WaitForExit(60_000));
		Assert.Equal(1, p.ExitCode);
		Assert.Equal("Reeve's worktree-tidy job removes merged worktrees and branches: Heiward leaves branches to it (http://reeve.localhost:18383/).", error.Result.Trim());
	}

	// ---- The review page

	/// <summary>The review page on a free port with developer mode on, until disposed.</summary>
	sealed class Page : IAsyncDisposable {
		readonly int port;
		readonly Task<int> server;
		readonly string token;
		public readonly HttpClient Http;

		Page(int port, Task<int> server, HttpClient http, string token) => (this.port, this.server, Http, this.token) = (port, server, http, token);

		public static async Task<Page> StartAsync() {
			var cfg = new AgentConfig { DeveloperMode = "on", ScanEveryMinutes = 0 };
			using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
				probe.Start();
				cfg.Port = ((IPEndPoint)probe.LocalEndpoint).Port;
			}
			cfg.Save();
			Task<int> server = ReviewServer.RunAsync(cfg, openBrowser: false, CancellationToken.None);
			for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(cfg.Port); i++) await Task.Delay(250);
			var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{cfg.Port}/"), Timeout = TimeSpan.FromSeconds(30) };
			return new Page(cfg.Port, server, http, ReviewServer.TokenIn(await http.GetStringAsync("/"))!);
		}

		public async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path) {
			using HttpResponseMessage answer = await Http.GetAsync(path);
			return (answer.StatusCode, JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.Clone());
		}

		public async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path) {
			using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) };
			request.Headers.Add("X-Agent-Token", token);
			using HttpResponseMessage answer = await Http.SendAsync(request);
			return (answer.StatusCode, JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.Clone());
		}

		public async ValueTask DisposeAsync() {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
			Http.Dispose();
		}
	}

	[Fact]
	public async Task ThePage_Standalone_OffersWorktreesBranchesAndPullRequests() {
		Report().Save();
		await using Page page = await Page.StartAsync();

		var (status, dev) = await page.GetAsync("/api/dev");
		Assert.Equal(HttpStatusCode.OK, status);
		JsonElement wt = dev.GetProperty("report").GetProperty("categories")[0].GetProperty("items")[0];
		Assert.True(wt.GetProperty("suggested").GetBoolean());
		Assert.Equal(JsonValueKind.Null, wt.GetProperty("blocked").ValueKind);
		Assert.Equal(1, dev.GetProperty("report").GetProperty("repositories").GetArrayLength());
		Assert.Equal(JsonValueKind.Null, dev.GetProperty("roles").GetProperty("worktrees").ValueKind);
		Assert.Equal(JsonValueKind.Null, dev.GetProperty("roles").GetProperty("pullRequests").ValueKind);

		Assert.Equal(HttpStatusCode.OK, (await page.GetAsync("/api/dev/pulls")).Status);
		var (_, state) = await page.GetAsync("/api/state");
		Assert.Equal(JsonValueKind.Null, state.GetProperty("dev").GetProperty("roles").GetProperty("worktrees").ValueKind);
		Assert.Equal((40 << 20) * 2 + (5 << 20), state.GetProperty("dev").GetProperty("suggestedBytes").GetInt64()); // wt, left and tmp ticked
	}

	[Fact]
	public async Task ThePage_AtAManor_ShowsWorktreesReadOnly_AndLeavesBranchesAndPullRequests() {
		Report().Save();
		InstallAll();
		await using Page page = await Page.StartAsync();

		var (status, dev) = await page.GetAsync("/api/dev");
		Assert.Equal(HttpStatusCode.OK, status);
		JsonElement items = dev.GetProperty("report").GetProperty("categories")[0].GetProperty("items");
		Assert.False(items[0].GetProperty("suggested").GetBoolean());
		Assert.Equal("Reeve looks after it", items[0].GetProperty("blocked").GetString());
		Assert.Equal(40 << 20, items[0].GetProperty("bytes").GetInt64());
		Assert.True(items[1].GetProperty("leftover").GetBoolean());
		Assert.True(items[1].GetProperty("suggested").GetBoolean());
		Assert.Equal(0, dev.GetProperty("report").GetProperty("repositories").GetArrayLength());
		JsonElement roles = dev.GetProperty("roles");
		Assert.Equal("Reeve", roles.GetProperty("worktrees").GetProperty("name").GetString());
		Assert.Equal("http://reeve.localhost:18383/", roles.GetProperty("worktrees").GetProperty("url").GetString());
		Assert.Equal("Reeve looks after it", roles.GetProperty("worktrees").GetProperty("kept").GetString()); // the page tells its worktrees apart by it
		Assert.Equal("The Steward merges pull requests", roles.GetProperty("pullRequests").GetProperty("note").GetString());

		// No host is asked for pull requests, no worktree is cleaned and no branch pruned here.
		var (pullStatus, pulls) = await page.GetAsync("/api/dev/pulls");
		Assert.Equal(HttpStatusCode.Conflict, pullStatus);
		Assert.Equal("The Steward merges pull requests: Heiward leaves pull requests to it.", pulls.GetProperty("error").GetString());
		var (cleanStatus, clean) = await page.PostAsync("/api/dev/items/wt/clean");
		Assert.Equal(HttpStatusCode.Conflict, cleanStatus);
		Assert.Equal("Reeve looks after it", clean.GetProperty("error").GetString());
		var (pruneStatus, prune) = await page.PostAsync("/api/dev/repos/repo1/prune");
		Assert.Equal(HttpStatusCode.Conflict, pruneStatus);
		Assert.Equal("Reeve's worktree-tidy job removes merged worktrees and branches: Heiward leaves branches to it.", prune.GetProperty("error").GetString());

		// The page's poll says who looks after what, and ticks only what's Heiward's (the leftover and the temp files).
		var (_, state) = await page.GetAsync("/api/state");
		JsonElement summary = state.GetProperty("dev");
		Assert.Equal("Reeve's worktree-tidy job removes merged worktrees and branches", summary.GetProperty("roles").GetProperty("worktrees").GetProperty("note").GetString());
		Assert.Equal("the Steward", summary.GetProperty("roles").GetProperty("pullRequests").GetProperty("name").GetString());
		Assert.Equal((40 << 20) + (5 << 20), summary.GetProperty("suggestedBytes").GetInt64());
		Assert.Equal((40 << 20) * 2 + (5 << 20), summary.GetProperty("totalBytes").GetInt64()); // every worktree still measured
	}

	[Fact]
	public async Task ThePage_AtAManor_GivesAWorktreeReeveKeepsForYouBack_WithItsReason() {
		Report().Save();
		InstallAll();
		TidyRound(RoundKeeping([WtPath], $"[{Kept(WtPath)}]"));
		await using Page page = await Page.StartAsync();

		var (_, dev) = await page.GetAsync("/api/dev");
		JsonElement wt = dev.GetProperty("report").GetProperty("categories")[0].GetProperty("items")[0];
		Assert.False(wt.GetProperty("suggested").GetBoolean());
		Assert.Equal(JsonValueKind.Null, wt.GetProperty("blocked").ValueKind); // yours to tick
		Assert.Equal("has changes that aren't committed", wt.GetProperty("keptForYou").GetString());
		Assert.Equal(Touched, wt.GetProperty("keptTouchedUtc").GetDateTime().ToUniversalTime());
		Assert.Equal("Reeve keeps it", dev.GetProperty("roles").GetProperty("worktrees").GetProperty("keptForYou").GetString()); // its tag, before the reason

		// Not ticked for you: only the leftover and the temp files are.
		var (_, state) = await page.GetAsync("/api/state");
		Assert.Equal((40 << 20) + (5 << 20), state.GetProperty("dev").GetProperty("suggestedBytes").GetInt64());

		// Cleaning it from the page goes past the page's guard, to the removal itself (this one is no worktree git knows).
		var (cleanStatus, clean) = await page.PostAsync("/api/dev/items/wt/clean");
		Assert.Equal(HttpStatusCode.OK, cleanStatus);
		Assert.Equal("Not a worktree git knows any more; check again", clean.GetProperty("error").GetString());
	}

	static string Asset(string name) {
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/" + name)!;
		using var reader = new StreamReader(s);
		return reader.ReadToEnd();
	}

	[Fact]
	public void ThePagesWords_ForAWorktreeReeveKeepsForYou() {
		string script = Asset("app.js");
		// Its tag: "Reeve keeps it: has changes that aren't committed", and beside it how long it's gone untouched.
		Assert.Contains("const keptTag = (i) => ((reeveRole() || {}).keptForYou || 'Reeve keeps it') + ': ' + i.keptForYou;", script);
		Assert.Contains("name.append(el('span', 'tag kept', keptTag(i)))", script);
		Assert.Contains("return 'Untouched for ' + (days === 1 ? 'a day' : days < 60 ? days + ' days' : Math.round(days / 30) + ' months');", script);
		// Its section says what it is, and that it's never ticked for you.
		Assert.Contains("'Those tagged \"' + reeve.keptForYou + '\" hold work ' + reeve.name + '\\'s tidy won\\'t throw away, so ' +", script);
		Assert.Contains("'they\\'re yours again. None is ticked for you; removing one asks first and says what goes with it.'", script);
		Assert.Contains("const many = open.filter((i) => !i.keptForYou);", script); // Select all leaves it out
		// Removing it asks first, and says what goes with it and where it can be got back.
		Assert.Contains("') with the work in ' + (kept.length === 1 ? 'it' : 'them') + '?'), ...keptLoss(kept)];", script);
		Assert.Contains("one ? reeve + ' keeps ' + kept[0].name + ' for you: ' + kept[0].keptForYou + '.'", script);
		Assert.Contains("' uncommitted changes, the files git ignores and any merge or rebase under way; only build output is deleted for good. ' +", script);
		Assert.Contains("(one ? 'Its branch and commits stay' : 'Their branches and commits stay') + ' in the repository.',", script);
		Assert.Contains(".tag.kept {", Asset("app.css"));
	}
}
