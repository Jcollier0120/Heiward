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

namespace HEI.Agent.Tests;

/// <summary>
/// Developer mode runs git only where the repository's own settings can't make it run a program or reach another computer
/// (a folder unzipped from the internet with a .git inside), with git's file watcher and hooks off; and the Windows
/// sign-in goes only to the servers the user named.
/// </summary>
public sealed class SafeGitTests : IDisposable {
	readonly string root = RealTemp.Folder("hei-safegit-");

	public SafeGitTests() => Directory.CreateDirectory(root);

	public void Dispose() {
		try { Directory.Delete(root, true); } catch { }
	}

	[Theory]
	[InlineData("[core]\n\tfsmonitor = C:/evil.exe\n", "core.fsmonitor")]
	[InlineData("[core]\n\tFSMonitor = true\n", "core.fsmonitor")]
	[InlineData("[core] fsmonitor = x\n", "core.fsmonitor")]                      // on the section's own line
	[InlineData("[core]\nfsmonitor\n", "core.fsmonitor")]                         // a key alone is true
	[InlineData("[core]\n\tsshCommand = ssh -oProxyCommand=calc\n", "core.sshCommand")]
	[InlineData("[core]\n\thooksPath = .husky\n", "core.hooksPath")]
	[InlineData("[core]\n\taskPass = x\n", "core.askPass")]
	[InlineData("[core]\n\tgitProxy = x\n", "core.gitProxy")]
	[InlineData("[credential]\n\thelper = !calc\n", "credential.helper")]
	[InlineData("[credential \"https://example.com\"]\n\thelper = x\n", "credential.helper")]
	[InlineData("[remote \"origin\"]\n\turl = x\n\tuploadpack = calc\n", "remote.uploadpack")]
	[InlineData("[remote \"origin\"]\n\treceivepack = calc\n", "remote.receivepack")]
	[InlineData("[filter \"lfs\"]\n\tclean = calc\n", "a filter")]
	[InlineData("[filter.lfs]\n\tsmudge = calc\n", "a filter")]                   // the older spelling
	[InlineData("[ filter \"x\" ]\n\tprocess = calc\n", "a filter")]
	[InlineData("[include]\n\tpath = elsewhere\n", "include")]
	[InlineData("[includeIf \"gitdir:C:/\"]\n\tpath = elsewhere\n", "includeIf")]
	[InlineData("[core]\r\n\tfsmonitor = x\r\n", "core.fsmonitor")]             // Windows line ends
	[InlineData("[core]\n\teditor = notepad \\\n\tfsmonitor = x\n", "core.fsmonitor")] // a continued line can't hide a key
	[InlineData("[remote \"origin\"]\n\turl = \\\\\\\\server\\\\share\\\\repo\n", "a path on another computer")]
	[InlineData("[remote \"origin\"]\n\turl = //server/share/repo\n", "a path on another computer")]
	[InlineData("[core]\n\tworktree = \"\\\\\\\\server\\\\share\"\n", "a path on another computer")]
	[InlineData("[remote \"origin\"]\n\turl = file://server/share/repo\n", "a path on another computer")]
	[InlineData("[remote \"origin\"]\n\turl = /\\\\server/share/repo\n", "a path on another computer")]
	[InlineData("[url \"//server/share/\"]\n\tinsteadOf = https://github.com/\n", "a path on another computer")]
	[InlineData("[url \"\\\\\\\\server\\\\share\\\\\"]\n\tinsteadOf = https://github.com/\n", "a path on another computer")]
	public void ASettingThatCanRunAProgram_OrReachAnotherComputer_IsFound(string config, string setting) =>
		Assert.Equal(setting, Git.RiskySetting(config));

	[Theory]
	[InlineData("")]
	[InlineData("[core]\n\trepositoryformatversion = 0\n\tfilemode = false\n\tbare = false\n\tlogallrefupdates = true\n\tsymlinks = false\n\tignorecase = true\n")]
	[InlineData("[remote \"origin\"]\n\turl = https://github.com/someone/app.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n")]
	[InlineData("[branch \"main\"]\n\tremote = origin\n\tmerge = refs/heads/main\n")]
	[InlineData("[remote \"origin\"]\n\turl = git@github.com:someone/app.git\n")]
	[InlineData("[user]\n\tname = Someone\n\temail = someone@example.com\n")]
	[InlineData("[lfs]\n\trepositoryformatversion = 0\n")]
	[InlineData("[core]\n\t# fsmonitor = x\n")]                                  // a comment
	[InlineData("[branch \"fsmonitor\"]\n\tremote = origin\n")]                  // a branch's name
	[InlineData("[remote \"origin\"]\n\turl = C:/Projects/app\n")]
	[InlineData("[remote \"origin\"]\n\turl = file:///C:/Projects/app.git\n")]
	[InlineData("[url \"https://github.com/\"]\n\tinsteadOf = gh:\n")]
	public void AnOrdinaryRepositorysSettings_AreFine(string config) => Assert.Null(Git.RiskySetting(config));

	[Fact]
	public void EveryGit_HasItsFileWatcherAndHooksOff() {
		var args = Git.SafeArgs().ToList();
		Assert.Contains("core.fsmonitor=false", args);
		Assert.Contains("protocol.ext.allow=never", args);
		string hooks = Assert.Single(args, a => a.StartsWith("core.hooksPath=", StringComparison.Ordinal))["core.hooksPath=".Length..];
		Assert.False(Directory.Exists(hooks)); // a folder that isn't there: no hook runs
		for (int i = 0; i < args.Count; i += 2) Assert.Equal("-c", args[i]);
	}

	[Theory]
	[InlineData(@"\\server\share\wt\.git", true)]
	[InlineData(@"//server/share/wt/.git", true)]
	[InlineData(@"\\?\UNC\server\share", true)]
	[InlineData(@"\\.\pipe\x", true)]
	[InlineData(@"C:\Projects\app\.git", false)]
	[InlineData(@"C:/Projects/app/.git", false)]
	[InlineData(@"..\app\.git", false)]
	public void APathOnAnotherComputer_IsKnown(string path, bool other) => Assert.Equal(other, DevScanner.OnAnotherComputer(path));

	string Meta(string gitdir) {
		string meta = Path.Combine(root, "repo", ".git", "worktrees", Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(meta);
		File.WriteAllText(Path.Combine(meta, "gitdir"), gitdir + "\n");
		return meta;
	}

	[Fact]
	public void AWorktreeRecordOnAnotherComputer_IsIgnored() {
		Assert.Null(DevScanner.WorktreePath(Meta(@"\\server\share\wt\.git")));
		Assert.Null(DevScanner.WorktreePath(Meta("//server/share/wt/.git")));
		string wt = Path.Combine(root, "wt");
		Assert.Equal(wt, DevScanner.WorktreePath(Meta(Path.Combine(wt, ".git"))));
	}

	[Fact]
	public void ADotGitFile_PointingToAnotherComputer_IsntFollowed() {
		string repo = Path.Combine(root, "pointing");
		Directory.CreateDirectory(repo);
		File.WriteAllText(Path.Combine(repo, ".git"), @"gitdir: \\server\share\repo.git" + "\n");
		Assert.Null(DevScanner.GitDirOf(repo));
		Assert.Equal("Not checked with git: its .git points to another computer, or can't be read", Git.Refusal(repo));
	}

	string Repo(string name, string config = "[core]\n\tbare = false\n") {
		string repo = Path.Combine(root, name);
		Directory.CreateDirectory(Path.Combine(repo, ".git", "objects", "info"));
		File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
		File.WriteAllText(Path.Combine(repo, ".git", "config"), config);
		return repo;
	}

	[Fact]
	public void ARepositoryWithRiskySettings_IsSkipped_AndSaysWhy() {
		Assert.Null(Git.Refusal(Repo("plain")));
		Assert.Equal("Not checked with git: its git settings set core.fsmonitor, which can run a program or reach another computer",
			Git.Refusal(Repo("watched", "[core]\n\tfsmonitor = run.exe\n")));
		Assert.Equal("Not checked with git: it has no .git of its own", Git.Refusal(Path.Combine(root, "plain", "src")));

		// Nothing runs there: git isn't started.
		string risky = Repo("filtered", "[filter \"x\"]\n\tclean = calc\n");
		if (Git.Exe != null) Assert.Equal((-1, Git.Refusal(risky)!), Git.Run(risky, "status"));
	}

	[Fact]
	public void ASettingInAnotherOfTheRepositorysFiles_IsFoundToo() {
		string worktreeOnly = Repo("worktree-config");
		File.WriteAllText(Path.Combine(worktreeOnly, ".git", "config.worktree"), "[core]\n\thooksPath = hooks\n");
		Assert.Contains("core.hooksPath", Git.Refusal(worktreeOnly));

		// A submodule's settings: git status reads them as well.
		string withSubmodule = Repo("submodules");
		string module = Path.Combine(withSubmodule, ".git", "modules", "lib", "modules", "inner");
		Directory.CreateDirectory(module);
		File.WriteAllText(Path.Combine(withSubmodule, ".git", "modules", "lib", "HEAD"), "ref: refs/heads/main\n");
		File.WriteAllText(Path.Combine(withSubmodule, ".git", "modules", "lib", "config"), "[core]\n\tbare = false\n");
		Assert.Null(Git.Refusal(withSubmodule));
		File.WriteAllText(Path.Combine(module, "HEAD"), "ref: refs/heads/main\n");
		File.WriteAllText(Path.Combine(module, "config"), "[credential]\n\thelper = x\n");
		Assert.Contains("credential.helper", Git.Refusal(withSubmodule));
	}

	[Fact]
	public void ARepositoryWhoseObjectsOrCommonGitAreOnAnotherComputer_IsSkipped() {
		string borrowing = Repo("alternates");
		File.WriteAllText(Path.Combine(borrowing, ".git", "objects", "info", "alternates"), @"\\server\share\objects" + "\n");
		Assert.Equal("Not checked with git: its objects are on another computer", Git.Refusal(borrowing));

		// A worktree whose repository's .git is on another computer.
		string wt = Path.Combine(root, "far-worktree");
		string gitDir = Path.Combine(root, "far-meta");
		Directory.CreateDirectory(wt);
		Directory.CreateDirectory(gitDir);
		File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + gitDir + "\n");
		File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/x\n");
		File.WriteAllText(Path.Combine(gitDir, "commondir"), @"\\server\share\repo\.git" + "\n");
		Assert.Equal("Not checked with git: its .git points to another computer", Git.Refusal(wt));
	}

	[Theory]
	[InlineData("tfs:8080", true)]
	[InlineData("TFS:8080", true)]
	[InlineData("tfs", false)]                      // port 80: not the one named
	[InlineData("devops.contoso.local", true)]
	[InlineData("devops.contoso.local:443", true)]
	[InlineData("tfs:9090", false)]                 // another port than the one named
	[InlineData("evil.example.com", false)]
	[InlineData("tfs.evil.example.com", false)]
	[InlineData("10.0.0.5", false)]                 // on this network, but not named
	[InlineData("", false)]
	public void TheWindowsSignIn_GoesOnlyToTheServersNamed(string host, bool named) =>
		Assert.Equal(named, PullRequests.Named(host, ["tfs:8080", "https://devops.contoso.local/"]));

	[Fact]
	public void AServerNamedWithoutAPort_IsNamedOnEveryPort() {
		Assert.True(PullRequests.Named("tfs:8080", ["TFS"]));
		Assert.True(PullRequests.Named("tfs", [" tfs "]));
		Assert.False(PullRequests.Named("tfs2:8080", ["tfs"]));
	}

	[Fact]
	public void WithNoServerNamed_TheWindowsSignInGoesNowhere() {
		Assert.False(PullRequests.Named("tfs:8080", null));
		Assert.False(PullRequests.Named("tfs:8080", []));
		Assert.False(PullRequests.Named("localhost", []));
	}
}
