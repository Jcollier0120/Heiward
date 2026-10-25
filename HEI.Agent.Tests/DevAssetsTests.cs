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

using System.Diagnostics;

namespace HEI.Agent.Tests;

/// <summary>
/// Developer mode: what counts as a build output, and deletion that never follows a link. Standalone throughout: Manor,
/// Reeve and the Steward have homes of their own here with nothing installed, so a PC with the real ones installed (where
/// Reeve keeps the worktrees, <see cref="ManorRoles"/>) runs these as CI does.
/// </summary>
[Collection(AgentHomeCollection.Name)] // MANOR_HOME, REEVE_HOME and STEWARD_HOME are process-wide
public sealed class DevAssetsTests : IDisposable {
	static readonly string[] Vars = ["MANOR_HOME", "REEVE_HOME", "STEWARD_HOME"];
	readonly string root = RealTemp.Folder("hei-dev-");
	readonly string homes = RealTemp.Folder("hei-dev-homes-");
	readonly Dictionary<string, string?> saved = Vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
	readonly List<string> links = new(); // junctions made outside root (JunctionFromAnotherDrive)

	public DevAssetsTests() {
		Directory.CreateDirectory(root);
		// Never created: none of them is installed.
		foreach (string v in Vars) Environment.SetEnvironmentVariable(v, Path.Combine(homes, v));
	}

	public void Dispose() {
		foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
		foreach (string link in links) try { Directory.Delete(link); } catch { } // the junction only, never what it points to
		// Junctions first, so cleanup can't reach through one.
		foreach (string d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
			if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);
		try { Directory.Delete(root, true); } catch { }
	}

	string Dir(params string[] parts) {
		string p = Path.Combine(new[] { root }.Concat(parts).ToArray());
		Directory.CreateDirectory(p);
		return p;
	}

	void Touch(string path, int bytes = 10) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, new byte[bytes]);
	}

	static void Junction(string link, string target) {
		using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
		p.StandardOutput.ReadToEnd();
		p.WaitForExit();
		Assert.Equal(0, p.ExitCode);
	}

	[Fact]
	public void BuildOutputs_AreRecognisedOnlyBesideTheirProjectFile() {
		string app = Dir("repo", "app");
		Touch(Path.Combine(app, "package.json"));
		Dir("repo", "app", "node_modules", "left-pad");
		Touch(Path.Combine(app, "App.csproj"));
		Dir("repo", "app", "bin");
		Dir("repo", "app", "obj");
		Dir("repo", "photos", "build");      // no build.gradle beside it: somebody's own folder
		Dir("repo", "notes", "node_modules"); // no package.json beside it
		Dir("repo", "android", "app", "build");
		Touch(Path.Combine(root, "repo", "android", "app", "build.gradle"));
		Dir("repo", "nested", ".git");        // another repository: its own item
		Dir("repo", "nested", "node_modules");
		Touch(Path.Combine(root, "repo", "nested", "package.json"));

		var found = DevScanner.FindBuildOutputs(Path.Combine(root, "repo"))
			.Select(o => Path.GetRelativePath(Path.Combine(root, "repo"), o.Path)).Order().ToList();
		Assert.Equal(new[] { @"android\app\build", @"app\bin", @"app\node_modules", @"app\obj" }, found);
	}

	[Fact]
	public void SafeDelete_RemovesAJunctionButNotWhatItPointsTo() {
		// pnpm's node_modules link into a shared store; a delete that followed them would empty the store.
		string store = Dir("store", "pkg");
		Touch(Path.Combine(store, "index.js"), 100);
		string nodeModules = Dir("repo", "node_modules");
		Touch(Path.Combine(nodeModules, "own.js"), 50);
		Junction(Path.Combine(nodeModules, "pkg"), store);

		var result = SafeDelete.Tree(nodeModules);
		Assert.False(Directory.Exists(nodeModules));
		Assert.True(File.Exists(Path.Combine(store, "index.js")), "the junction's target was deleted");
		Assert.Equal(50, result.Bytes); // the link's target isn't counted either
		Assert.Equal(0, result.Left);
	}

	[Fact]
	public void Measure_DoesNotCountThroughJunctions() {
		string store = Dir("store");
		Touch(Path.Combine(store, "big.bin"), 1000);
		string modules = Dir("m");
		Touch(Path.Combine(modules, "a.js"), 10);
		Junction(Path.Combine(modules, "linked"), store);
		Assert.Equal(10, DevScanner.Measure(modules).Bytes);
	}

	[Fact]
	public void SafeDelete_KeepRoot_EmptiesACacheButKeepsTheFolder_AndClearsReadOnly() {
		string cache = Dir("cache");
		Touch(Path.Combine(cache, "a", "b.bin"), 20);
		Touch(Path.Combine(cache, "ro.bin"), 5);
		File.SetAttributes(Path.Combine(cache, "ro.bin"), FileAttributes.ReadOnly); // git objects and caches are read-only
		var result = SafeDelete.Tree(cache, keepRoot: true);
		Assert.True(Directory.Exists(cache));
		Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
		Assert.Equal(25, result.Bytes);
	}

	[Fact]
	public void SafeDelete_LeavesFilesInUse_AndSaysSo() {
		string dir = Dir("busy");
		string open = Path.Combine(dir, "open.log");
		Touch(open, 10);
		Touch(Path.Combine(dir, "free.log"), 10);
		using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.None)) {
			var result = SafeDelete.Tree(dir);
			Assert.Equal(1, result.Left);
			Assert.Equal(10, result.Bytes);
		}
		Assert.True(File.Exists(open));
		Assert.True(Directory.Exists(dir)); // not removed while something inside remains
	}

	[Fact]
	public void SafeDelete_SeveralFoldersAtOnce_SaysWhatsLeftInTheFoldersOrder() {
		string dir = Dir("wide");
		for (int i = 0; i < 40; i++)
			for (int j = 0; j < 20; j++) Touch(Path.Combine(dir, $"f{i:00}", "sub", $"{j}.js"), 10);
		string first = Path.Combine(dir, "f07", "sub", "3.js"), second = Path.Combine(dir, "f31", "sub", "12.js");
		using (new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.None))
		using (new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.None)) {
			SafeDelete.Result r = SafeDelete.Tree(dir);
			Assert.Equal(2, r.Left);
			Assert.Equal(new[] { first, second }, r.LeftPaths);
			Assert.Equal(40 * 20 * 10 - 20, r.Bytes);
		}
		Assert.Equal(new[] { first, second }, Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order());
		Assert.Equal(new[] { "f07", "f31" }, Directory.EnumerateDirectories(dir).Select(Path.GetFileName).Order());
	}

	/// <summary>
	/// A cleaning of Temp re-checks the entries it listed, each on its own just before it goes: it used to walk all of Temp
	/// again first, which on a developer's PC is millions of files and took minutes.
	/// </summary>
	[Fact]
	public void Clean_LooksAgainAtEachTempEntryItListed_AndAtNothingElse() {
		string temp = Dir("temp");
		string oldFile = Path.Combine(temp, "old.tmp"), oldFolder = Path.Combine(temp, "old-folder"), touched = Path.Combine(temp, "touched");
		string unlisted = Path.Combine(temp, "unlisted.tmp"), claude = Path.Combine(temp, "claude"), elsewhere = Path.Combine(Dir("elsewhere"), "old.tmp");
		Touch(oldFile, 7);
		Touch(Path.Combine(oldFolder, "a.bin"), 20);
		Touch(Path.Combine(touched, "a.bin"), 30);
		Touch(unlisted, 40);
		Touch(Path.Combine(claude, "x.txt"), 50);
		Touch(elsewhere, 60);
		DateTime old = DateTime.UtcNow.AddDays(-30);
		foreach (string f in new[] { oldFile, Path.Combine(oldFolder, "a.bin"), Path.Combine(touched, "a.bin"), unlisted, Path.Combine(claude, "x.txt"), elsewhere })
			File.SetLastWriteTimeUtc(f, old);
		foreach (string d in new[] { oldFolder, touched, claude }) Directory.SetLastWriteTimeUtc(d, old);
		// Since the list was made, something wrote in one of its folders.
		Touch(Path.Combine(touched, "new.bin"));
		var item = new DevItem("id", "temp", "Temp folder", temp,
			new() { oldFile, oldFolder, touched, claude, elsewhere, Path.Combine(temp, "gone.tmp") }, 0, null, true, null, "");

		CleanResult r = DevCleaner.Clean(item, new AgentConfig());
		Assert.Null(r.Error);
		Assert.Equal(27, r.FreedBytes);
		Assert.Equal(0, r.LeftInUse);
		Assert.False(File.Exists(oldFile));
		Assert.False(Directory.Exists(oldFolder));
		Assert.True(File.Exists(Path.Combine(touched, "a.bin")));
		Assert.True(File.Exists(unlisted));                       // not on the list
		Assert.True(File.Exists(Path.Combine(claude, "x.txt"))); // Claude Code's, judged a session at a time
		Assert.True(File.Exists(elsewhere));                     // not in Temp
	}

	[Fact]
	public void SafeDelete_RefusesADriveRoot() =>
		Assert.Throws<InvalidOperationException>(() => SafeDelete.Tree(Path.GetPathRoot(root)!));

	[Fact]
	public void LastUsed_ReadsGitsFiles_AndIgnoresBuildOutputs() {
		string repo = Dir("proj");
		string git = Dir("proj", ".git");
		Touch(Path.Combine(git, "index"));
		Touch(Path.Combine(repo, "README.md"));
		Dir("proj", "node_modules");
		DateTime old = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		foreach (string p in new[] { Path.Combine(git, "index"), Path.Combine(repo, "README.md"), git })
			if (File.Exists(p)) File.SetLastWriteTimeUtc(p, old); else Directory.SetLastWriteTimeUtc(p, old);
		// node_modules was just (re)installed: that isn't working on the project.
		Assert.Equal(old, DevScanner.LastUsed(repo, git));
	}

	[Fact]
	public void OldTempEntries_TakesOnlyWhatsUntouchedThroughout() {
		string temp = Dir("temp");
		string oldFile = Path.Combine(temp, "old.tmp");
		Touch(oldFile, 7);
		File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
		string mixed = Dir("temp", "mixed");
		Touch(Path.Combine(mixed, "old.bin"));
		File.SetLastWriteTimeUtc(Path.Combine(mixed, "old.bin"), DateTime.UtcNow.AddDays(-30));
		Touch(Path.Combine(mixed, "fresh.bin")); // a folder still in use
		Touch(Path.Combine(temp, "new.tmp"));
		var old = DevScanner.OldTempEntries(temp, 7);
		Assert.Equal(new[] { oldFile }, old.Select(o => o.Path));
	}

	/// <summary>Temp's entries are measured several at once (millions of files took the check minutes), and listed in the folder's order.</summary>
	[Fact]
	public void OldTempEntries_MeasuredSeveralAtOnce_KeepTheFoldersOrder_AndStopWhenAsked() {
		string temp = Dir("temp");
		DateTime old = DateTime.UtcNow.AddDays(-30);
		var folders = new List<string>();
		for (int i = 0; i < 60; i++) {
			string d = Path.Combine(temp, $"e{i:00}");
			Touch(Path.Combine(d, "a.bin"), i + 1);
			File.SetLastWriteTimeUtc(Path.Combine(d, "a.bin"), old);
			Directory.SetLastWriteTimeUtc(d, old);
			folders.Add(d);
		}
		var found = DevScanner.OldTempEntries(temp, 7);
		Assert.Equal(folders, found.Select(e => e.Path));
		Assert.Equal(Enumerable.Range(1, 60).Select(i => (long)i), found.Select(e => e.Bytes));
		// Stop scan: nothing more is measured, and the check isn't saved.
		Assert.Empty(DevScanner.OldTempEntries(temp, 7, new CancellationToken(true)));
	}

	[Fact]
	public void FindRepositories_StopsAtARepository_AndSkipsExemptFolders() {
		Dir("a", "repo1", ".git");
		Dir("a", "repo1", "inner", ".git"); // not descended into
		Dir("a", "node_modules", "pkg", ".git");
		Dir("b", "deep", "repo2", ".git");
		var rules = ScanScope.ExclusionRules(new AgentConfig { ScanAllDrives = false });
		var repos = DevScanner.FindRepositories(new[] { root }, rules, default).Select(r => Path.GetRelativePath(root, r)).Order().ToList();
		Assert.Equal(new[] { @"a\repo1", @"b\deep\repo2" }, repos);
	}

	[Fact]
	public void FindRepositories_LeavesOutTempFolders() {
		Dir("work", "app", ".git");
		Dir("tmp", "npu-checkout-files-x", "origin", ".git"); // a test's leftover
		var rules = ScanScope.ExclusionRules(new AgentConfig { ScanAllDrives = false }).Concat(ScanScope.TempFolders(Path.Combine(root, "tmp"))).ToList();
		var repos = DevScanner.FindRepositories(new[] { root }, rules, default).Select(r => Path.GetRelativePath(root, r)).ToList();
		Assert.Equal(new[] { @"work\app" }, repos);
	}

	[Fact]
	public void TempFolders_NeverTheProfileOrADrive() {
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		foreach (string temp in new[] { profile, Path.GetDirectoryName(profile)!, @"C:\" })
			Assert.Equal(new[] { @"?:\Temp", @"?:\Tmp" }, ScanScope.TempFolders(temp).Select(r => r.Pattern));
		Assert.Contains(ScanScope.TempFolders(@"C:\Work\Temp\").Select(r => r.Pattern), p => p == @"C:\Work\Temp");
	}

	[Theory]
	[InlineData(@"~\.npu-agent\checkouts\gamernexus", ".npu-agent")] // a tool's jobs read this one
	[InlineData(@"%L\SomeTool\wt\main", "app data")]
	[InlineData(@"C:\Projects\GamerNexus\.claude\worktrees\great-goodall", null)] // an AI session's: the user's
	[InlineData(@"~\source\repos\app-wt", null)]
	[InlineData(@"C:\g57", null)]
	public void Worktrees_InsideAToolsHome_AreKeptForTheTool(string path, string? tool) {
		string p = path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
			.Replace("%L", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
		Assert.Equal(tool, DevScanner.ToolHome(p));
	}

	static string G(string dir, params string[] args) {
		var psi = new ProcessStartInfo(Git.Exe!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
		foreach (string a in new[] { "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "init.defaultBranch=main", "-c", "commit.gpgsign=false" }.Concat(args))
			psi.ArgumentList.Add(a);
		using var p = Process.Start(psi)!;
		string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
		p.WaitForExit();
		Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {output}");
		return output;
	}

	[Fact]
	public void PruneBranches_DeletesOnlyWhatsMergedOnTheRemote_AndKeepsCheckedOutOnes() {
		if (Git.Exe == null) return; // needs git
		string remote = Dir("remote.git");
		G(remote, "init", "--bare");
		string seed = Dir("seed");
		G(seed, "init");
		File.WriteAllText(Path.Combine(seed, "a.txt"), "1");
		G(seed, "add", ".");
		G(seed, "commit", "-m", "one");
		G(seed, "remote", "add", "origin", remote);
		G(seed, "push", "-u", "origin", "main");

		string repo = Path.Combine(root, "repo");
		G(root, "clone", remote, repo);
		G(repo, "branch", "open-work");             // made before the merges below: not in main
		G(repo, "checkout", "-b", "feature-merged");
		File.WriteAllText(Path.Combine(repo, "b.txt"), "2");
		G(repo, "add", ".");
		G(repo, "commit", "-m", "feature");
		G(repo, "checkout", "-b", "wt-merged");
		G(repo, "checkout", "main");
		G(repo, "merge", "--ff-only", "feature-merged");
		G(repo, "push", "origin", "main");
		G(repo, "checkout", "open-work");           // so feature-merged isn't merged into HEAD: git -d alone would refuse
		File.WriteAllText(Path.Combine(repo, "c.txt"), "3");
		G(repo, "add", ".");
		G(repo, "commit", "-m", "open");
		G(repo, "worktree", "add", Path.Combine(root, "wt"), "wt-merged");

		RepoBranches before = BranchPruner.Inspect(repo)!;
		Assert.Equal("origin/main", before.Default);
		Assert.Equal(new[] { "feature-merged" }, before.Merged);
		Assert.Equal(new[] { "wt-merged" }, before.CheckedOut);

		PruneResult result = BranchPruner.Prune(repo);
		Assert.True(result.Fetched);
		Assert.Equal(new[] { "feature-merged" }, result.Deleted);
		Assert.Contains(result.Kept, k => k.Branch == "wt-merged");
		string branches = G(repo, "branch", "--format=%(refname:short)");
		Assert.DoesNotContain("feature-merged", branches);
		foreach (string kept in new[] { "main", "open-work", "wt-merged" }) Assert.Contains(kept, branches);
		Assert.Contains("feature", G(repo, "log", "--oneline", "origin/main")); // the remote is untouched
		G(repo, "worktree", "remove", Path.Combine(root, "wt"));
	}

	/// <summary>A clone whose main is pushed, node_modules ignored, and a worktree of it where Claude Code keeps them.</summary>
	(string Repo, string Worktree, DevItem Item) AgentWorktree(string name) {
		string remote = Dir("remote.git");
		G(remote, "init", "--bare");
		string seed = Dir("seed");
		G(seed, "init");
		File.WriteAllText(Path.Combine(seed, ".gitignore"), "node_modules/\n");
		G(seed, "add", ".");
		G(seed, "commit", "-m", "one");
		G(seed, "remote", "add", "origin", remote);
		G(seed, "push", "-u", "origin", "main");
		string repo = Path.Combine(root, "repo");
		G(root, "clone", remote, repo);
		string wt = Path.Combine(repo, ".claude", "worktrees", name);
		G(repo, "worktree", "add", "-b", name, wt, "origin/main");
		return (repo, wt, new DevItem("id", DevScanner.Worktrees, name, wt, new() { wt }, 0, null, true, null, "", repo));
	}

	[Fact]
	public void Clean_RemovesAWorktreeDeeperThanWindowsPathLimit() {
		if (Git.Exe == null) return; // needs git
		var (repo, wt, item) = AgentWorktree("deep");
		// node_modules nests deep: git alone stops here with "Filename too long", halfway through.
		string deep = Path.Combine(wt, "node_modules", new string('a', 100), new string('b', 100), "index.js");
		Touch(deep, 20);
		Assert.True(deep.Length > 260);

		CleanResult result = DevCleaner.Clean(item, new AgentConfig());
		Assert.Null(result.Error);
		Assert.Equal(0, result.LeftInUse);
		Assert.False(Directory.Exists(wt));
		Assert.DoesNotContain("/" + Path.GetFileName(wt), G(repo, "worktree", "list"));
	}

	/// <summary>Git deletes a worktree a file at a time: the build output git ignores goes first, several folders at once.</summary>
	[Fact]
	public void Clean_DeletesAWorktreesIgnoredBuildOutputItself_ThenGitRemovesTheRest() {
		if (Git.Exe == null) return; // needs git
		var (repo, wt, item) = AgentWorktree("built");
		File.AppendAllText(Path.Combine(repo, ".git", "info", "exclude"), "\nbuild/\nnotes/\n");
		Touch(Path.Combine(wt, "node_modules", "pkg", "x.js"), 40);
		Touch(Path.Combine(wt, "web", "node_modules", "y.js"), 30);
		Touch(Path.Combine(wt, "build", "app.js"), 20);
		Touch(Path.Combine(wt, "notes", "todo.txt"));
		Assert.Equal(new[] { Path.Combine(wt, "build"), Path.Combine(wt, "node_modules"), Path.Combine(wt, "web", "node_modules") },
			DevScanner.RebuiltIgnored(wt).Order(StringComparer.OrdinalIgnoreCase));
		Directory.Delete(Path.Combine(wt, "notes"), true); // a build doesn't make it again: it would keep the worktree

		CleanResult result = DevCleaner.Clean(item, new AgentConfig());
		Assert.Null(result.Error);
		Assert.True(result.FreedBytes >= 90);
		Assert.False(Directory.Exists(wt));
		Assert.DoesNotContain("/" + Path.GetFileName(wt), G(repo, "worktree", "list"));
	}

	[Fact]
	public void Clean_FinishesARemovalThatGitStoppedPartway() {
		if (Git.Exe == null) return; // needs git
		var (repo, wt, item) = AgentWorktree("busy");
		string open = Path.Combine(wt, "node_modules", "open.log");
		Touch(open, 10);
		Touch(Path.Combine(wt, "node_modules", "z-free.log"), 30);
		using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.None)) {
			// Git checks there's nothing uncommitted, lets go of the worktree and stops at the file in use.
			CleanResult result = DevCleaner.Clean(item, new AgentConfig());
			Assert.Null(result.Error);
			Assert.Equal(1, result.LeftInUse);
			Assert.True(result.FreedBytes >= 30);
		}
		Assert.DoesNotContain("/" + Path.GetFileName(wt), G(repo, "worktree", "list"));
		Assert.Equal(new[] { open }, Directory.EnumerateFiles(wt, "*", SearchOption.AllDirectories));
	}

	[Fact]
	public void LeftoversOfAnUnfinishedRemoval_AreListedAsWorktrees_AndCleaned() {
		if (Git.Exe == null) return; // needs git
		var (repo, wt, item) = AgentWorktree("half");
		Touch(Path.Combine(wt, "node_modules", "pkg", "x.js"), 40);
		Touch(Path.Combine(wt, "package.json"));
		string live = Path.Combine(repo, ".claude", "worktrees", "live");
		G(repo, "worktree", "add", "-b", "live", live, "origin/main");
		string notes = Dir("repo", ".claude", "notes"); // not where worktrees go
		// What a removal stopped partway leaves: git let go of the worktree, and its .git went first.
		File.Delete(Path.Combine(wt, ".git"));
		G(repo, "worktree", "prune");

		Assert.True(DevScanner.IsLeftoverWorktree(repo, wt));
		Assert.False(DevScanner.IsLeftoverWorktree(repo, live));
		Assert.False(DevScanner.IsLeftoverWorktree(repo, notes));
		DevItem listed = Assert.Single(DevScanner.WorktreeItems(repo, DateTime.UtcNow.AddDays(-30), default), i => i.Location == wt);
		Assert.True(listed.Suggested);
		Assert.Null(listed.Blocked);
		Assert.StartsWith("left over", listed.Detail);
		// Its node_modules is the leftover's, not a build output of the repository too.
		Assert.Empty(DevScanner.FindBuildOutputs(repo));

		CleanResult result = DevCleaner.Clean(item, new AgentConfig());
		Assert.Null(result.Error);
		Assert.True(result.FreedBytes >= 40);
		Assert.False(Directory.Exists(wt));
		Assert.True(Directory.Exists(live));
		G(repo, "worktree", "remove", live);
	}

	[Fact]
	public void DevProjects_AreMadeConsistent() {
		var saved = DevProject.Normalize(new[] {
			new DevProject("  Suite  ", new() { @"C:\Projects\App\", @"C:\Projects\Api", @"c:\projects\app" }),
			new DevProject("suite", new() { @"C:\Projects\Other", @"C:\Projects\More" }),   // same name: dropped
			new DevProject("Solo", new() { @"C:\Projects\Api", @"C:\Projects\Tool" }),      // Api is taken: one left, dropped
			new DevProject("Loose", new() { "relative\\path", @"C:\Projects\X", @"C:\Projects\Y" }),
			new DevProject("", new() { @"C:\A", @"C:\B" }),
		});
		Assert.Equal(new[] { "Suite", "Loose" }, saved.Select(p => p.Name));
		Assert.Equal(new[] { @"C:\Projects\App", @"C:\Projects\Api" }, saved[0].Repos);
		Assert.Equal(new[] { @"C:\Projects\X", @"C:\Projects\Y" }, saved[1].Repos);
	}

	[Fact]
	public void Clean_ReChecksAProjectFolderBeforeDeleting() {
		string app = Dir("p", "app");
		Touch(Path.Combine(app, "package.json"));
		Touch(Path.Combine(app, "node_modules", "x.js"), 30);
		var item = new DevItem("id", DevScanner.Projects, "p", Path.Combine(root, "p"), new() { Path.Combine(app, "node_modules") }, 30, null, true, null, "");
		// Between the check and the click, package.json went away: the folder isn't a build output any more.
		File.Delete(Path.Combine(app, "package.json"));
		Assert.Equal(0, DevCleaner.Clean(item, new AgentConfig()).FreedBytes);
		Assert.True(Directory.Exists(Path.Combine(app, "node_modules")));

		Touch(Path.Combine(app, "package.json"));
		Assert.Equal(30, DevCleaner.Clean(item, new AgentConfig()).FreedBytes);
		Assert.False(Directory.Exists(Path.Combine(app, "node_modules")));
	}

	[Fact]
	public void SafeDelete_SaysWhatIsLeft_Why_AndWhatHoldsIt() {
		string dir = Dir("held");
		string open = Path.Combine(dir, "open.log");
		Touch(open, 10);
		using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.None)) {
			SafeDelete.Result r = SafeDelete.Tree(dir);
			Assert.Equal(1, r.Left);
			Assert.Equal(new[] { open }, r.LeftPaths);
			Assert.Contains("being used by another process", r.Reason);
			Assert.DoesNotContain(open, r.Reason); // the path is in LeftPaths, and a log without history keeps no names

			CleanResult c = CleanResult.Of(r, dir, new FolderUse(DateTime.UtcNow, claudeHome: Dir("no-claude"), processes: () => new()));
			Assert.Equal(1, c.LeftInUse);
			Assert.Equal(open, c.LeftPath);
			// Windows' Restart Manager names the process with the file open: this one.
			Assert.Contains($"(process {Environment.ProcessId}) has a file open", c.HeldBy);
			Assert.Contains(open, c.Describe(withPath: true));
			Assert.DoesNotContain(open, c.Describe(withPath: false));
			Assert.StartsWith("1 left in use (", c.Describe(withPath: false));
		}
		Assert.Null(CleanResult.Of(SafeDelete.Tree(dir), dir, new FolderUse(DateTime.UtcNow, claudeHome: Dir("no-claude"), processes: () => new())).Describe(true));
	}

	[Fact]
	public void SafeDelete_RemovesPathsLongerThanWindowsLimit_AndNamesItsUsualPathsCantSay() {
		string leftover = Dir("leftover");
		string deep = SafeDelete.Extended(Path.Combine(leftover, new string('a', 120), new string('b', 120), new string('c', 40)));
		Directory.CreateDirectory(deep);
		File.WriteAllBytes(Path.Combine(deep, "index.js"), new byte[20]);
		Assert.True(deep.Length > 300);
		// What git or WSL can leave: names ending in a dot or a space, which a path without \\?\ loses.
		File.WriteAllBytes(SafeDelete.Extended(leftover) + @"\trailing dot.", new byte[5]);
		Directory.CreateDirectory(SafeDelete.Extended(leftover) + @"\trailing space \inner");

		SafeDelete.Result r = SafeDelete.Tree(leftover);
		Assert.Equal(0, r.Left);
		Assert.Null(r.Reason);
		Assert.Equal(25, r.Bytes);
		Assert.False(Directory.Exists(leftover));
	}

	[Theory]
	[InlineData(@"C:\Projects\x", @"\\?\C:\Projects\x")]
	[InlineData(@"\\server\share\x", @"\\?\UNC\server\share\x")]
	[InlineData(@"\\?\C:\x", @"\\?\C:\x")]
	public void SafeDelete_UsesExtendedPaths_AndSaysThemAsUsual(string path, string extended) {
		Assert.Equal(extended, SafeDelete.Extended(path));
		Assert.Equal(path.StartsWith(@"\\?\") ? @"C:\x" : path, SafeDelete.Plain(extended));
	}

	[Fact]
	public void ALeftoverAProcessWorksIn_IsntOffered_AndCleaningItSaysWhatHoldsIt() {
		if (Git.Exe == null || !Environment.Is64BitProcess) return; // needs git
		var (repo, wt, item) = AgentWorktree("held");
		File.Delete(Path.Combine(wt, ".git"));
		G(repo, "worktree", "prune");
		string noClaude = Dir("no-claude");
		// A shell left working in it: Windows won't remove the folder, so it was offered, and failed, every day.
		using var shell = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { WorkingDirectory = wt, CreateNoWindow = true, UseShellExecute = false })!;
		try {
			Assert.Equal(wt, FolderUseTests.WorkingFolderOnceStarted(shell), StringComparer.OrdinalIgnoreCase);
			DevItem listed = Assert.Single(DevScanner.WorktreeItems(repo, DateTime.UtcNow.AddDays(-30), default, new FolderUse(DateTime.UtcNow, claudeHome: noClaude)), i => i.Location == wt);
			Assert.False(listed.Suggested);
			Assert.StartsWith("In use: ", listed.Blocked);
			Assert.Contains("works in it", listed.Blocked);
			// Cleaning checks again, and leaves it.
			Assert.StartsWith("In use: ", DevCleaner.Clean(item, new AgentConfig()).Error);
			Assert.True(Directory.Exists(wt));

			// What a deletion makes of it: the folder left, with Windows' reason and the process that holds it.
			SafeDelete.Result r = SafeDelete.Tree(wt);
			Assert.Equal(1, r.Left);
			Assert.Equal(new[] { wt }, r.LeftPaths);
			Assert.NotNull(r.Reason);
			CleanResult c = CleanResult.Of(r, wt, new FolderUse(DateTime.UtcNow, claudeHome: noClaude));
			Assert.Contains("works in it", c.HeldBy);
		}
		finally {
			try { shell.Kill(entireProcessTree: true); } catch { }
			shell.WaitForExit();
		}
		// Let go of, it's offered again.
		DevItem free = Assert.Single(DevScanner.WorktreeItems(repo, DateTime.UtcNow.AddDays(-30), default, new FolderUse(DateTime.UtcNow, claudeHome: noClaude)), i => i.Location == wt);
		Assert.Null(free.Blocked);
		Assert.True(free.Suggested);
	}

	[Fact]
	public void AWorktreeAClaudeCodeSessionUsedToday_IsntOffered() {
		if (Git.Exe == null) return; // needs git
		var (repo, wt, _) = AgentWorktree("recent");
		string claude = Dir("claude");
		string transcripts = Path.Combine(claude, "projects", ClaudeSessions.Slug(wt));
		Touch(Path.Combine(transcripts, Guid.NewGuid() + ".jsonl"));
		DevItem listed = Assert.Single(DevScanner.WorktreeItems(repo, DateTime.UtcNow.AddDays(1), default, new FolderUse(DateTime.UtcNow, claudeHome: claude, processes: () => new())));
		Assert.False(listed.Suggested);
		Assert.StartsWith("A Claude Code session used it", listed.Blocked);

		// Two days on, the session doesn't keep it. (Something else may: the temp folder is in app data, a tool's home.)
		foreach (string t in Directory.EnumerateFiles(transcripts)) File.SetLastWriteTimeUtc(t, DateTime.UtcNow.AddDays(-2));
		listed = Assert.Single(DevScanner.WorktreeItems(repo, DateTime.UtcNow.AddDays(1), default, new FolderUse(DateTime.UtcNow, claudeHome: claude, processes: () => new())));
		Assert.False(listed.Blocked?.StartsWith("A Claude Code session") ?? false);
		Assert.Equal(listed.Blocked == null, listed.Suggested);
		G(repo, "worktree", "remove", wt);
	}

	/// <summary>
	/// Git lists a worktree by its real path (E:\worktrees\x on a PC with Dev Drives); the session working in it was started
	/// through a junction (C:\worktrees\x), and Claude Code names its transcripts, and the session's folder, after that.
	/// Compared as written, the worktree in use was offered for cleanup.
	/// </summary>
	[Fact]
	public void AWorktreeUsedThroughAJunction_IsntOffered() {
		if (Git.Exe == null) return; // needs git
		var (repo, wt, _) = AgentWorktree("linked");
		string link = Path.Combine(root, "worktrees");
		Junction(link, Path.GetDirectoryName(wt)!);
		string viaLink = Path.Combine(link, "linked");
		string claude = Dir("claude");
		string transcripts = Path.Combine(claude, "projects", ClaudeSessions.Slug(viaLink));
		Directory.CreateDirectory(transcripts);
		File.WriteAllText(Path.Combine(transcripts, Guid.NewGuid() + ".jsonl"), "{\"type\":\"user\",\"cwd\":" + System.Text.Json.JsonSerializer.Serialize(viaLink) + "}\n");
		DevItem listed = Assert.Single(DevScanner.WorktreeItems(repo, DateTime.UtcNow.AddDays(1), default, new FolderUse(DateTime.UtcNow, claudeHome: claude, processes: () => new())));
		Assert.False(listed.Suggested);
		Assert.StartsWith("A Claude Code session used it", listed.Blocked);

		// A program working in it through the junction holds it too.
		listed = Assert.Single(DevScanner.WorktreeItems(repo, DateTime.UtcNow.AddDays(1), default,
			new FolderUse(DateTime.UtcNow, claudeHome: Dir("no-claude"), processes: () => new() { new WorkingFolder(4242, "Code", Path.Combine(viaLink, "src")) })));
		Assert.False(listed.Suggested);
		Assert.Equal("In use: Code (process 4242) works in it", listed.Blocked);
		G(repo, "worktree", "remove", wt);
	}

	[Fact]
	public void ScheduledTasks_NameTheirFolders_ByTheirRealPaths() {
		string real = Dir("tasks", "tool");
		string link = Path.Combine(root, "tasks-link");
		Junction(link, Path.Combine(root, "tasks"));
		string text = $"\"Name\",\"{Path.Combine(link, "tool", "run.cmd")} --now\",\"{Path.Combine(link, "tool")}\"\r\n\"Other\",\"C:\\Windows\\System32\\cmd.exe /c exit\",\"N/A\"";
		List<string> paths = DevScanner.TaskPaths(text);
		Assert.Contains(Path.Combine(real, "run.cmd"), paths, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(real, paths, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(paths, p => p.StartsWith(@"C:\Windows\System32\cmd.exe", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void BuildOutputs_WithNoProjectBeside_AreListedForReview_WhenGitKeepsNothingInThem() {
		if (Git.Exe == null) return; // needs git
		string repo = Dir("orphans");
		G(repo, "init");
		File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\nobj/\n");
		// A project as it is: its bin is its build output.
		Touch(Path.Combine(repo, "HEI.Core", "HEI.Core.csproj"));
		Touch(Path.Combine(repo, "HEI.Core", "bin", "Debug", "HEI.Core.dll"), 2 << 20);
		// The same project before it was renamed: its bin and obj stayed behind.
		Touch(Path.Combine(repo, "VDF.Core", "bin", "Release", "VDF.Core.dll"), 2 << 20);
		Touch(Path.Combine(repo, "VDF.Core", "obj", "project.assets.json"), 1000);
		// A build's bin that git keeps: not Heiward's to offer.
		Touch(Path.Combine(repo, "Vendor", "bin", "Release", "tool.dll"), 2 << 20);
		// A bin that merely has the name: scripts, no Debug or Release in it.
		Touch(Path.Combine(repo, "scripts", "bin", "run.cmd"), 2 << 20);
		G(repo, "add", ".gitignore", "HEI.Core/HEI.Core.csproj");
		G(repo, "add", "-f", "Vendor/bin/Release/tool.dll");
		G(repo, "commit", "-m", "one");

		var (outputs, orphans) = DevScanner.WalkBuildOutputs(repo);
		Assert.Equal(new[] { @"HEI.Core\bin" }, outputs.Select(o => Path.GetRelativePath(repo, o.Path)));
		Assert.Equal(new[] { @"VDF.Core\bin", @"VDF.Core\obj", @"Vendor\bin" }.Order(StringComparer.OrdinalIgnoreCase),
			orphans.Select(o => Path.GetRelativePath(repo, o)).Order(StringComparer.OrdinalIgnoreCase));

		DevItem item = Assert.Single(DevScanner.OrphanItems(repo, orphans));
		Assert.Equal(DevScanner.Orphans, item.Kind);
		Assert.Equal("VDF.Core", item.Name);
		Assert.Equal(repo, item.Repo);
		Assert.Equal(new[] { Path.Combine(repo, "VDF.Core", "bin"), Path.Combine(repo, "VDF.Core", "obj") }, item.Paths);
		Assert.False(item.Suggested); // low confidence: never ticked
		Assert.Null(item.Blocked);
		Assert.StartsWith("bin, obj · no project file beside them", item.Detail);

		// Never cleaned automatically, whatever kinds automatic cleanup takes.
		Assert.Null(AutoCleaner.KindOf(item));
		var cfg = new AgentConfig { AutoClean = new AutoCleanConfig { Developer = true, AfterDays = 0, DeveloperKinds = AutoCleaner.DeveloperKinds.ToList() } };
		var dev = new DevReport { ScannedAtUtc = DateTime.UtcNow, Categories = { new DevCategory(DevScanner.Orphans, "", "", new() { item }) } };
		AutoPlanEntry planned = AutoCleaner.Plan(cfg, null, dev, new Dictionary<string, Decision>(),
			new AutoCleanState { DeveloperSinceUtc = DateTime.UtcNow.AddDays(-60) }, DateTime.UtcNow).DevItems[item.Id];
		Assert.Null(planned.DueUtc);
		Assert.Contains("always your call", planned.Reason);

		// Cleaning checks again: with a project file back beside it, it's that project's, and left alone.
		Touch(Path.Combine(repo, "VDF.Core", "VDF.Core.csproj"));
		Assert.Equal(0, DevCleaner.Clean(item, new AgentConfig()).FreedBytes);
		File.Delete(Path.Combine(repo, "VDF.Core", "VDF.Core.csproj"));
		CleanResult cleaned = DevCleaner.Clean(item, new AgentConfig());
		Assert.Equal((2 << 20) + 1000, cleaned.FreedBytes);
		Assert.Equal(0, cleaned.LeftInUse);
		Assert.False(Directory.Exists(Path.Combine(repo, "VDF.Core", "bin")));
		Assert.False(Directory.Exists(Path.Combine(repo, "VDF.Core", "obj")));
		Assert.True(File.Exists(Path.Combine(repo, "Vendor", "bin", "Release", "tool.dll")));
		Assert.True(File.Exists(Path.Combine(repo, "HEI.Core", "bin", "Debug", "HEI.Core.dll")));
	}

	[Fact]
	public void AWorktreeWithIgnoredFilesABuildDoesntMake_IsKept() {
		if (Git.Exe == null) return; // needs git
		var (repo, wt, item) = AgentWorktree("env");
		File.AppendAllText(Path.Combine(repo, ".git", "info", "exclude"), "\n.env\n*.tsbuildinfo\n");
		Touch(Path.Combine(wt, "node_modules", "pkg", "x.js"));
		Touch(Path.Combine(wt, "app.tsbuildinfo"));
		Assert.Empty(DevScanner.KeptIgnored(wt)!);

		// git worktree remove would delete the .env with the rest.
		File.WriteAllText(Path.Combine(wt, ".env"), "TOKEN=1");
		Assert.Equal(new[] { ".env" }, DevScanner.KeptIgnored(wt));
		Assert.Equal("Files git ignores that aren't build output, such as .env: kept", DevScanner.WorktreeBlocker(wt));
		Assert.Equal("Files git ignores that aren't build output, such as .env: kept", DevCleaner.Clean(item, new AgentConfig()).Error);
		Assert.True(File.Exists(Path.Combine(wt, ".env")));
	}

	[Fact]
	public void Clean_KeepsABuildOutputHoldingADatabase_OrFilesGitTracks() {
		string app = Dir("p", "App");
		Touch(Path.Combine(app, "App.csproj"));
		Touch(Path.Combine(app, "bin", "Debug", "App.dll"), 30);
		Touch(Path.Combine(app, "bin", "Debug", "site.db"), 30);
		Touch(Path.Combine(app, "obj", "project.assets.json"), 20);
		var item = new DevItem("id", DevScanner.Projects, "p", Path.Combine(root, "p"),
			new() { Path.Combine(app, "bin"), Path.Combine(app, "obj") }, 80, null, true, null, "");
		// The obj folder goes; the bin folder holds a database a build doesn't make again, and stays.
		Assert.Equal(20, DevCleaner.Clean(item, new AgentConfig()).FreedBytes);
		Assert.True(File.Exists(Path.Combine(app, "bin", "Debug", "site.db")));
		Assert.Equal(@"App\bin kept: it holds a database, Debug\site.db",
			DevCleaner.Clean(item with { Paths = new() { Path.Combine(app, "bin") } }, new AgentConfig()).Error);

		if (Git.Exe == null) return; // the rest needs git
		string repo = Dir("tracked");
		G(repo, "init");
		Touch(Path.Combine(repo, "Tool.csproj"));
		Touch(Path.Combine(repo, "bin", "run.cmd"));
		G(repo, "add", ".");
		G(repo, "commit", "-m", "one");
		Assert.Equal("git keeps files in it", DevScanner.BuildOutputKeeps(repo, Path.Combine(repo, "bin")));
		Assert.Null(DevScanner.BuildOutputKeeps(repo, Dir("tracked", "obj")));
	}

	// ------------------------------------------------------------------ drives scanned only when asked

	/// <summary>Settings where the drive the tests' folder is really on is scanned only when asked.</summary>
	AgentConfig OnRequestHere() => new() { OnRequestDrives = new() { RealPath.VolumeOf(root)! } };

	/// <summary>
	/// A junction to <paramref name="target"/> written with another drive's letter (as C:\DevData is the drive E:), where this
	/// PC has a folder on another drive to make it in; null where it hasn't (one drive, as on CI).
	/// </summary>
	string? JunctionFromAnotherDrive(string target) {
		string? drive = RealPath.VolumeOf(target);
		foreach (string place in new[] { Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }) {
			if (place.Length == 0 || string.Equals(AgentConfig.DriveOf(place), drive, StringComparison.OrdinalIgnoreCase)) continue;
			string link = Path.Combine(place, "hei-dev-link-" + Guid.NewGuid().ToString("N"));
			links.Add(link);
			return RealTemp.Junction(link, target);
		}
		return null;
	}

	[Fact]
	public void ADriveScannedOnlyWhenAsked_RestsUntilItsAskedFor_ByTheDriveItReallyIsOn() {
		AgentConfig cfg = OnRequestHere();
		string drive = cfg.OnRequestDrives[0];
		Assert.True(DevScanner.Resting(cfg, null)(root));
		Assert.Equal(drive, cfg.OnRequestDriveOf(Path.Combine(root, "x")));
		// Scan this drive now reads it; back to automatic, every check does.
		Assert.False(DevScanner.Resting(cfg, new[] { drive.ToLowerInvariant() })(root));
		Assert.False(DevScanner.Resting(new AgentConfig(), null)(root));
		Assert.False(DevScanner.Resting(new AgentConfig { OnRequestDrives = new() { @"Q:\" } }, null)(root));
		// Through a junction written with another drive's letter, by where it really is.
		Dir("target", "x");
		if (JunctionFromAnotherDrive(Path.Combine(root, "target")) is { } link) {
			Assert.True(DevScanner.Resting(cfg, null)(Path.Combine(link, "x")));
			Assert.Equal(drive, cfg.OnRequestDriveOf(Path.Combine(link, "x")));
			Assert.False(DevScanner.Resting(cfg, new[] { drive })(Path.Combine(link, "x")));
		}
	}

	/// <summary>
	/// %TEMP% on a drive scanned only when asked, as C:\DevData\Temp is E:\Temp: neither its old files nor the crash dumps
	/// there are read, as written or through a junction. The last check's stay (<see cref="ItemsOnADriveScannedOnlyWhenAsked_StayAsLastMeasured"/>).
	/// </summary>
	[Fact]
	public void Temp_OnADriveScannedOnlyWhenAsked_IsntRead() {
		string temp = Dir("temp");
		string old = Path.Combine(temp, "old.bin");
		Touch(old, 2 << 20);
		File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
		string dumps = Dir("dumps");
		Touch(Path.Combine(dumps, "app.dmp"), 100);
		Assert.Equal(new[] { "temp", "dumps" }, DevScanner.TempItems(7, _ => false, temp, dumps).Select(i => i.Kind));

		Func<string, bool> resting = DevScanner.Resting(OnRequestHere(), null);
		Assert.Empty(DevScanner.TempItems(7, resting, temp, dumps));
		string viaLink = Path.Combine(root, "temp-link");
		Junction(viaLink, temp);
		Assert.Empty(DevScanner.TempItems(7, resting, viaLink, dumps));
		if (JunctionFromAnotherDrive(temp) is { } elsewhere) Assert.Empty(DevScanner.TempItems(7, resting, elsewhere, dumps));
		// Asked for, it's read.
		Assert.Equal(2, DevScanner.TempItems(7, DevScanner.Resting(OnRequestHere(), OnRequestHere().OnRequestDrives), temp, dumps).Count());
	}

	/// <summary>
	/// A worktree on a drive scanned only when asked (E:\worktrees, of a repository on another drive) isn't measured or asked
	/// of git. Its repository's leftovers and checked-out branches are still told apart, from the repository's own records.
	/// </summary>
	[Fact]
	public void AWorktreeOnADriveScannedOnlyWhenAsked_IsntLookedAt() {
		if (Git.Exe == null) return; // needs git
		var (repo, wt, _) = AgentWorktree("resting");
		string half = Path.Combine(repo, ".claude", "worktrees", "half");
		G(repo, "worktree", "add", "-b", "half", half, "origin/main");
		File.Delete(Path.Combine(half, ".git"));
		G(repo, "worktree", "prune");
		DateTime staleBefore = DateTime.UtcNow.AddDays(1);
		var noOne = new FolderUse(DateTime.UtcNow, claudeHome: Dir("no-claude"), processes: () => new());
		Assert.Equal(2, DevScanner.WorktreeItems(repo, staleBefore, default, noOne).Count());

		// The worktree's drive alone rests.
		string real = RealPath.Of(wt);
		Func<string, bool> resting = p => string.Equals(RealPath.Of(p), real, StringComparison.OrdinalIgnoreCase);
		DevItem left = Assert.Single(DevScanner.WorktreeItems(repo, staleBefore, default, noOne, resting));
		Assert.Equal(half, left.Location);
		Assert.True(left.Leftover);
		RepoBranches branches = BranchPruner.Inspect(repo, resting)!;
		Assert.Contains("resting", branches.CheckedOut);
		Assert.DoesNotContain("resting", branches.Merged);
		Assert.Contains("half", branches.Merged);

		// The repository's drive too: nothing of it is read.
		Assert.Empty(DevScanner.WorktreeItems(repo, staleBefore, default, noOne, DevScanner.Resting(OnRequestHere(), null)));
		G(repo, "worktree", "remove", wt);
	}

	/// <summary>
	/// What the last check found on a drive scanned only when asked stays listed as it was, marked with the drive and when it
	/// was measured, until Scan this drive now reads it again or the drive goes back to automatic. A worktree is on its
	/// repository's drive too: git status reads the repository.
	/// </summary>
	[Fact]
	public void ItemsOnADriveScannedOnlyWhenAsked_StayAsLastMeasured() {
		var cfg = new AgentConfig { OnRequestDrives = new() { @"Q:\" } };
		DateTime then = DateTime.UtcNow.AddDays(-3);
		static DevItem Item(string id, string kind, string location, string? repo = null) =>
			new(id, kind, id, location, new() { location }, 5 << 20, null, true, null, "", repo);
		DevItem tree = Item("t1", DevScanner.Worktrees, @"Q:\worktrees\app-x");
		var previous = new DevReport {
			ScannedAtUtc = then,
			Categories = new() {
				new DevCategory(DevScanner.Worktrees, "", "", new() {
					tree,
					Item("t2", DevScanner.Worktrees, @"C:\hei-none\app-y", @"Q:\src\app"), // of a repository there
					Item("t3", DevScanner.Worktrees, @"C:\hei-none\app-z"), // this check would have found it: it's gone
				}),
				new DevCategory(DevScanner.Temp, "", "", new() { Item("tmp", "temp", @"Q:\Temp") }),
			},
			Repositories = new() {
				new RepoBranches("r1", "app", @"Q:\src\app", "origin/main", 3, new() { "done" }, new(), null),
				new RepoBranches("r2", "web", @"C:\hei-none\web", "origin/main", 1, new(), new(), null),
			},
			Sources = new() { new RepoSource(@"Q:\src\app", "git", null) },
		};
		static DevReport Fresh() => new() {
			ScannedAtUtc = DateTime.UtcNow,
			Categories = new() { new DevCategory(DevScanner.Worktrees, "", "", new()), new DevCategory(DevScanner.Temp, "", "", new()) },
		};

		DevReport now = Fresh();
		DevScanner.KeepResting(now, previous, cfg, DevScanner.Resting(cfg, null));
		Assert.Equal(new[] { "t1", "t2" }, now.Categories[0].Items.Select(i => i.Id).Order());
		Assert.Equal("tmp", Assert.Single(now.Categories[1].Items).Id);
		Assert.All(now.Categories.SelectMany(c => c.Items), i => {
			Assert.Equal(@"Q:\", i.OnRequest);
			Assert.Equal(then, i.MeasuredUtc);
		});
		Assert.Equal("r1", Assert.Single(now.Repositories).Id);
		Assert.Single(now.Sources);

		// Kept again, it's still as first measured.
		DevReport next = Fresh();
		DevScanner.KeepResting(next, now, cfg, DevScanner.Resting(cfg, null));
		Assert.All(next.Categories.SelectMany(c => c.Items), i => Assert.Equal(then, i.MeasuredUtc));

		// Scan this drive now read it: what it found is this check's, and what it didn't find is gone.
		DevReport read = Fresh();
		read.Categories[0].Items.Add(tree with { Bytes = 1 });
		DevScanner.KeepResting(read, previous, cfg, DevScanner.Resting(cfg, new[] { @"q:" }));
		DevItem found = Assert.Single(read.Categories[0].Items);
		Assert.Equal(1, found.Bytes);
		Assert.Null(found.OnRequest);
		Assert.Empty(read.Categories[1].Items);
		Assert.Empty(read.Repositories);

		// Back to automatic: the next check measures it.
		DevReport automatic = Fresh();
		DevScanner.KeepResting(automatic, previous, new AgentConfig(), DevScanner.Resting(new AgentConfig(), null));
		Assert.All(automatic.Categories, c => Assert.Empty(c.Items));
	}

	/// <summary>
	/// A cache is where its tool's setting puts it (a Dev Drive, say: npm's cache= in .npmrc, NUGET_PACKAGES); the folder it
	/// used before is listed, ticked, as nothing uses it, unless it's a link to the new one. On a drive left unread, neither is read.
	/// </summary>
	[Fact]
	public void Caches_AreWhereTheirSettingsPutThem_AndTheOldFolderIsOffered() {
		string standard = Dir("default-cache"), moved = Dir("moved-cache");
		Touch(Path.Combine(standard, "a.tgz"), 3 << 20);
		Touch(Path.Combine(moved, "b.tgz"), 2 << 20);
		var tools = new[] { ("npm", new[] { new DevScanner.CachePlace(standard, "TEST_CACHE") }, Array.Empty<string>()) };
		string? Setting(string name) => name == "TEST_CACHE" ? moved : null;

		Assert.Equal((moved, standard), DevScanner.Locate(tools[0].Item2[0], Setting));
		Assert.Equal((standard, (string?)null), DevScanner.Locate(tools[0].Item2[0], _ => null));
		Assert.Null(DevScanner.Locate(tools[0].Item2[0], _ => standard + "\\").Old); // set to where it is anyway

		var items = DevScanner.CacheItems(_ => false, Setting, tools).ToList();
		Assert.Equal(2, items.Count);
		DevItem now = items[0], old = items[1];
		Assert.Equal(moved, now.Location);
		Assert.False(now.Suggested);
		Assert.Equal(standard, old.Location);
		Assert.Equal("npm, old location", old.Name);
		Assert.True(old.Suggested);
		Assert.Contains("nothing uses this one", old.Detail);
		Assert.NotEqual(now.Id, old.Id);

		// On a drive scanned only when asked: not read, either of them.
		Assert.Empty(DevScanner.CacheItems(DevScanner.Resting(OnRequestHere(), null), Setting, tools));
		Assert.Equal(new[] { standard }, DevScanner.CacheItems(p => p == moved, Setting, tools).Select(i => i.Location));

		// The old folder made a link to the new one: it's the cache itself.
		Directory.Delete(standard, true);
		Junction(standard, moved);
		Assert.Equal(new[] { moved }, DevScanner.CacheItems(_ => false, Setting, tools).Select(i => i.Location));
	}

	/// <summary>
	/// Emulators on a drive scanned only when asked aren't read, and as one there may use any system image, no image is ticked;
	/// the system images' own folder there isn't read either.
	/// </summary>
	[Fact]
	public void Emulators_OnADriveScannedOnlyWhenAsked_ArentRead_AndKeepTheirImages() {
		string sdk = Dir("sdk");
		string image = Dir("sdk", "system-images", "android-34", "google_apis", "x86_64");
		Touch(Path.Combine(image, "system.img"), 100);
		Dir("sdk", "system-images", "android-33", "google_apis", "x86_64");
		string avdHome = Dir("avd"), avd = Dir("avds", "pixel.avd");
		File.WriteAllText(Path.Combine(avdHome, "pixel.ini"), "path=" + avd + "\n");
		File.WriteAllText(Path.Combine(avd, "config.ini"), @"image.sysdir.1=system-images\android-34\google_apis\x86_64\" + "\n");
		DateTime staleBefore = DateTime.UtcNow.AddDays(-30);
		bool running = DevScanner.EmulatorRunning();

		// Nothing rests: the emulator, and the image it doesn't use, ticked.
		var items = DevScanner.AndroidItems(staleBefore, _ => false, avdHome, sdk).ToList();
		Assert.Equal(new[] { "avd", "sysimage" }, items.Select(i => i.Kind));
		Assert.Contains("android-33", items[1].Name);
		Assert.Equal(!running, items[1].Suggested);

		// The emulator's folder rests, or where emulators are listed: an image may be in use.
		foreach (Func<string, bool> resting in new Func<string, bool>[] { p => p.StartsWith(Path.Combine(root, "avds"), StringComparison.OrdinalIgnoreCase), p => p == avdHome }) {
			items = DevScanner.AndroidItems(staleBefore, resting, avdHome, sdk).ToList();
			Assert.All(items, i => Assert.Equal("sysimage", i.Kind));
			Assert.Equal(2, items.Count);
			Assert.All(items, i => Assert.False(i.Suggested));
			if (!running) Assert.All(items, i => Assert.Equal("An emulator on a drive scanned only when you ask may use it", i.Blocked));
		}

		// The SDK's drive rests.
		Assert.DoesNotContain(DevScanner.AndroidItems(staleBefore, p => p.StartsWith(sdk, StringComparison.OrdinalIgnoreCase), avdHome, sdk), i => i.Kind == "sysimage");
	}
}
