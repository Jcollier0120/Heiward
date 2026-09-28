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

namespace VDF.Agent.Tests;

/// <summary>Developer mode: what counts as a build output, and deletion that never follows a link.</summary>
public sealed class DevAssetsTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "vdf-dev-" + Guid.NewGuid().ToString("N"));

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
	[InlineData(@"~\.npu-agent\checkouts\gamernexus", ".npu-agent")] // npu-agent's jobs read this one
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
}
