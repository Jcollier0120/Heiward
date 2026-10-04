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

/// <summary>Developer mode: what counts as a build output, and deletion that never follows a link.</summary>
public sealed class DevAssetsTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "hei-dev-" + Guid.NewGuid().ToString("N"));

	public DevAssetsTests() => Directory.CreateDirectory(root);

	public void Dispose() {
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
}
