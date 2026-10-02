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

namespace HEI.Agent.Tests;

/// <summary>Which host a remote's pull requests are on, what each waits for, and what a repository uses.</summary>
public sealed class PullRequestsTests : IDisposable {
	readonly string temp = Path.Combine(Path.GetTempPath(), "hei-vcs-" + Guid.NewGuid().ToString("N"));

	public void Dispose() { try { Directory.Delete(temp, true); } catch { } }

	[Theory]
	// GitHub, over https, ssh and scp-style, and GitHub Enterprise.
	[InlineData("https://github.com/Jcollier0120/Heiward.git", "github", "github.com", "https://api.github.com", "Jcollier0120", "Heiward", "https://github.com/Jcollier0120/Heiward")]
	[InlineData("git@github.com:owner/repo.git", "github", "github.com", "https://api.github.com", "owner", "repo", "https://github.com/owner/repo")]
	[InlineData("ssh://git@github.com/owner/repo", "github", "github.com", "https://api.github.com", "owner", "repo", "https://github.com/owner/repo")]
	[InlineData("https://github.example.com/team/app.git", "github", "github.example.com", "https://github.example.com/api", "team", "app", "https://github.example.com/team/app")]
	// Azure DevOps: dev.azure.com, the old visualstudio.com names, ssh, and a project's own repository.
	[InlineData("https://org@dev.azure.com/org/Fabrikam/_git/Web", "azure", "dev.azure.com", "https://dev.azure.com/org", "Fabrikam", "Web", "https://dev.azure.com/org/Fabrikam/_git/Web")]
	[InlineData("https://dev.azure.com/org/_git/Fabrikam", "azure", "dev.azure.com", "https://dev.azure.com/org", "Fabrikam", "Fabrikam", "https://dev.azure.com/org/Fabrikam/_git/Fabrikam")]
	[InlineData("https://org.visualstudio.com/DefaultCollection/Fabrikam/_git/Web", "azure", "org.visualstudio.com", "https://org.visualstudio.com", "Fabrikam", "Web", "https://org.visualstudio.com/Fabrikam/_git/Web")]
	[InlineData("git@ssh.dev.azure.com:v3/org/Fabrikam/Web", "azure", "dev.azure.com", "https://dev.azure.com/org", "Fabrikam", "Web", "https://dev.azure.com/org/Fabrikam/_git/Web")]
	[InlineData("org@vs-ssh.visualstudio.com:v3/org/Fabrikam/Web", "azure", "org.visualstudio.com", "https://org.visualstudio.com", "Fabrikam", "Web", "https://org.visualstudio.com/Fabrikam/_git/Web")]
	// Azure DevOps Server and TFS, with a port, a virtual folder, and ssh.
	[InlineData("http://tfs:8080/tfs/DefaultCollection/Fabrikam/_git/Web", "azure-server", "tfs:8080", "http://tfs:8080/tfs/DefaultCollection", "Fabrikam", "Web", "http://tfs:8080/tfs/DefaultCollection/Fabrikam/_git/Web")]
	[InlineData("https://devops.corp.example/Main/Fabrikam/_git/Web", "azure-server", "devops.corp.example", "https://devops.corp.example/Main", "Fabrikam", "Web", "https://devops.corp.example/Main/Fabrikam/_git/Web")]
	[InlineData("https://devops.corp.example/Main/_git/Fabrikam", "azure-server", "devops.corp.example", "https://devops.corp.example/Main", "Fabrikam", "Fabrikam", "https://devops.corp.example/Main/Fabrikam/_git/Fabrikam")]
	[InlineData("ssh://devops.corp.example:22/Main/Fabrikam/_git/Web", "azure-server", "devops.corp.example", "https://devops.corp.example/Main", "Fabrikam", "Web", "https://devops.corp.example/Main/Fabrikam/_git/Web")]
	// GitLab, with subgroups and a self-managed server.
	[InlineData("https://gitlab.com/group/sub/app.git", "gitlab", "gitlab.com", "https://gitlab.com/api/v4", "group/sub", "app", "https://gitlab.com/group/sub/app")]
	[InlineData("git@gitlab.example.com:team/app.git", "gitlab", "gitlab.example.com", "https://gitlab.example.com/api/v4", "team", "app", "https://gitlab.example.com/team/app")]
	// Bitbucket Cloud, and Server over https and ssh.
	[InlineData("https://someone@bitbucket.org/workspace/app.git", "bitbucket", "bitbucket.org", "https://api.bitbucket.org/2.0", "workspace", "app", "https://bitbucket.org/workspace/app")]
	[InlineData("https://git.example.com/bitbucket/scm/PROJ/app.git", "bitbucket-server", "git.example.com", "https://git.example.com/bitbucket", "PROJ", "app", "https://git.example.com/bitbucket/projects/PROJ/repos/app")]
	[InlineData("ssh://git@bitbucket.example.com:7999/PROJ/app.git", "bitbucket-server", "bitbucket.example.com", "https://bitbucket.example.com", "PROJ", "app", "https://bitbucket.example.com/projects/PROJ/repos/app")]
	// Gitea and Forgejo.
	[InlineData("https://codeberg.org/owner/app.git", "gitea", "codeberg.org", "https://codeberg.org/api/v1", "owner", "app", "https://codeberg.org/owner/app")]
	public void Remote_SaysWhereItsPullRequestsAre(string remote, string kind, string host, string api, string owner, string name, string web) {
		PullSource s = Assert.IsType<PullSource>(PullSource.Parse(remote));
		Assert.Equal((kind, host, api, owner, name, web), (s.Kind, s.Host, s.Base, s.Owner, s.Name, s.Web));
	}

	[Theory]
	[InlineData("https://git.example.com/team/app.git")] // a git server Heiward doesn't know
	[InlineData(@"C:\repos\app")]
	[InlineData("/srv/git/app.git")]
	[InlineData("https://dev.azure.com/org/_git")]
	public void UnknownRemote_HasNoPullRequests(string remote) => Assert.Null(PullSource.Parse(remote));

	[Fact]
	public void AzureDevOps_IsAskedForItsOrganizationsSignIn() =>
		Assert.Equal("org", PullSource.Parse("https://dev.azure.com/org/Fabrikam/_git/Web")!.SignInPath);

	[Theory]
	[InlineData(false, null, "MERGEABLE", "CLEAN", "SUCCESS", "merge")]
	[InlineData(false, "APPROVED", "MERGEABLE", "CLEAN", null, "merge")]
	[InlineData(false, "REVIEW_REQUIRED", "MERGEABLE", "BLOCKED", "SUCCESS", "review")]
	[InlineData(false, "CHANGES_REQUESTED", "MERGEABLE", "BLOCKED", "SUCCESS", "changes")]
	[InlineData(false, "APPROVED", "MERGEABLE", "UNSTABLE", "FAILURE", "checks")]
	[InlineData(false, "APPROVED", "CONFLICTING", "DIRTY", "SUCCESS", "conflicts")]
	[InlineData(false, "APPROVED", "MERGEABLE", "BLOCKED", "PENDING", "running")]
	[InlineData(false, "APPROVED", "MERGEABLE", "BEHIND", "SUCCESS", "behind")]
	[InlineData(true, null, "MERGEABLE", "DRAFT", "SUCCESS", "draft")]
	public void GitHub_SaysWhatAPullRequestWaitsFor(bool draft, string? review, string mergeable, string state, string? checks, string waits) =>
		Assert.Equal(waits, PullRequests.GitHubWaits(draft, review, mergeable, state, checks));

	[Fact]
	public void AzureDevOps_SaysWhatAPullRequestWaitsFor() {
		Assert.Equal("merge", PullRequests.AzureWaits(false, "succeeded", new[] { (10, true), (0, false) }));
		Assert.Equal("review", PullRequests.AzureWaits(false, "succeeded", new[] { (0, true) }));
		Assert.Equal("review", PullRequests.AzureWaits(false, "succeeded", Array.Empty<(int, bool)>())); // nobody approved yet
		Assert.Equal("changes", PullRequests.AzureWaits(false, "succeeded", new[] { (10, false), (-5, false) }));
		Assert.Equal("conflicts", PullRequests.AzureWaits(false, "conflicts", new[] { (10, true) }));
		Assert.Equal("blocked", PullRequests.AzureWaits(false, "rejectedByPolicy", new[] { (10, true) }));
		Assert.Equal("draft", PullRequests.AzureWaits(true, "succeeded", new[] { (10, true) }));
	}

	[Fact]
	public void GitLabAndTheOthers_SayWhatAPullRequestWaitsFor() {
		Assert.Equal("merge", PullRequests.GitLabWaits(false, false, "mergeable", false));
		Assert.Equal("review", PullRequests.GitLabWaits(false, false, "not_approved", true));
		Assert.Equal("checks", PullRequests.GitLabWaits(false, false, "ci_must_pass", false));
		Assert.Equal("conflicts", PullRequests.GitLabWaits(false, true, "mergeable", false));
		Assert.Equal("draft", PullRequests.GitLabWaits(false, false, "draft_status", false));
		Assert.Equal("review", PullRequests.GitLabWaits(false, false, null, true)); // GitLab before 15.6
		Assert.Equal("merge", PullRequests.ReviewWaits(false, false, false, true));
		Assert.Equal("review", PullRequests.ReviewWaits(false, false, false, false));
		Assert.Equal("changes", PullRequests.ReviewWaits(false, false, true, true));
	}

	[Fact]
	public void GitConfig_GivesOrigin_NotUpstream() {
		const string config = "[core]\n\tbare = false\n[remote \"upstream\"]\n\turl = https://github.com/someone/original.git\n" +
			"[remote \"origin\"]\n\turl = https://github.com/me/fork.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n";
		Assert.Equal("https://github.com/me/fork.git", VersionControl.GitRemote(config));
		Assert.Equal("https://example.com/only.git", VersionControl.GitRemote("[remote \"mirror\"]\n url = https://example.com/only.git\n"));
		Assert.Null(VersionControl.GitRemote("[core]\n\tbare = false\n"));
	}

	[Fact]
	public void Hgrc_GivesTheDefaultPath() =>
		Assert.Equal("https://hg.example.com/app", VersionControl.HgDefault("[ui]\nusername = me\n[paths]\ndefault = https://hg.example.com/app\ndefault-push = ssh://x\n"));

	[Theory]
	[InlineData("https://me:ghp_secret@github.com/me/app.git", "https://github.com/me/app.git")]
	[InlineData("https://org@dev.azure.com/org/P/_git/R", "https://dev.azure.com/org/P/_git/R")]
	[InlineData("git@github.com:me/app.git", "git@github.com:me/app.git")]
	public void ARemotesPassword_IsNeverKept(string remote, string kept) => Assert.Equal(kept, VersionControl.WithoutPassword(remote));

	[Fact]
	public void VersionControl_IsToldByItsMarker() {
		string Repo(string name, string marker, bool file = false) {
			string dir = Path.Combine(temp, name);
			Directory.CreateDirectory(dir);
			if (file) File.WriteAllText(Path.Combine(dir, marker), "");
			else Directory.CreateDirectory(Path.Combine(dir, marker));
			return dir;
		}
		Assert.Equal("git", VersionControl.Of(Repo("a", ".git")));
		Assert.Equal("tfvc", VersionControl.Of(Repo("b", "$tf")));
		Assert.Equal("svn", VersionControl.Of(Repo("c", ".svn")));
		Assert.Equal("hg", VersionControl.Of(Repo("d", ".hg")));
		Assert.Equal("plastic", VersionControl.Of(Repo("e", ".plastic")));
		Assert.Equal("fossil", VersionControl.Of(Repo("f", "_FOSSIL_", file: true)));
		Assert.Equal("perforce", VersionControl.Of(Repo("g", ".p4config", file: true)));
		Assert.Null(VersionControl.Of(Repo("h", "src")));
	}

	[Theory]
	[InlineData("10.1.2.3", true)]
	[InlineData("172.20.0.5", true)]
	[InlineData("192.168.1.10", true)]
	[InlineData("127.0.0.1", true)]
	[InlineData("fd12:3456::1", true)]
	[InlineData("8.8.8.8", false)]
	[InlineData("172.32.0.1", false)]
	[InlineData("2606:4700::1111", false)]
	public void WindowsSignIn_GoesOnlyToThisNetwork(string address, bool mine) => Assert.Equal(mine, PullRequests.IsPrivate(IPAddress.Parse(address)));

	[Fact]
	public void ANameWithoutDots_IsOnThisNetwork() => Assert.True(PullRequests.OnThisNetwork("tfs:8080"));

	// As Azure DevOps and Azure DevOps Server answer GET …/pullrequests (trimmed).
	[Fact]
	public void AzureDevOpsPullRequest_IsRead() {
		PullSource s = PullSource.Parse("http://tfs:8080/tfs/DefaultCollection/Fabrikam/_git/Web")!;
		using var json = System.Text.Json.JsonDocument.Parse("""
			{ "pullRequestId": 22, "title": "Add the help page", "isDraft": false, "mergeStatus": "succeeded",
			  "creationDate": "2026-09-30T08:15:00.123Z", "sourceRefName": "refs/heads/help", "targetRefName": "refs/heads/main",
			  "createdBy": { "id": "a1", "displayName": "Ana Lee" },
			  "repository": { "webUrl": "http://tfs:8080/tfs/DefaultCollection/Fabrikam/_git/Web" },
			  "reviewers": [ { "id": "me", "vote": 0, "isRequired": true }, { "id": "b2", "vote": 10, "isRequired": false } ] }
			""");
		PullRequest p = PullRequests.AzurePull(json.RootElement, s, "me");
		Assert.Equal(("!22", "Add the help page", "help", "main", "Ana Lee"), (p.Ref, p.Title, p.Head, p.Base, p.Author));
		Assert.Equal("http://tfs:8080/tfs/DefaultCollection/Fabrikam/_git/Web/pullrequest/22", p.Url);
		Assert.True(p.AsksYou);
		Assert.False(p.Yours);
		Assert.Equal("review", p.Waits); // a required reviewer hasn't voted
		Assert.Equal(new DateTime(2026, 9, 30, 8, 15, 0, 123, DateTimeKind.Utc), p.UpdatedUtc);
	}

	// As Bitbucket Server and Data Center answer GET …/pull-requests (trimmed).
	[Fact]
	public void BitbucketServerPullRequest_IsRead() {
		PullSource s = PullSource.Parse("https://git.example.com/scm/PROJ/app.git")!;
		using var json = System.Text.Json.JsonDocument.Parse("""
			{ "id": 7, "title": "Retry the upload", "updatedDate": 1790000000000, "draft": false,
			  "author": { "user": { "name": "jdoe", "displayName": "Jo Doe" } },
			  "fromRef": { "displayId": "retry" }, "toRef": { "displayId": "master" },
			  "reviewers": [ { "user": { "name": "me" }, "status": "APPROVED" } ],
			  "properties": { "mergeResult": { "outcome": "CLEAN" } },
			  "links": { "self": [ { "href": "https://git.example.com/projects/PROJ/repos/app/pull-requests/7" } ] } }
			""");
		PullRequest p = PullRequests.BitbucketServerPull(json.RootElement, s, "me");
		Assert.Equal(("#7", "retry", "master", "Jo Doe", "merge"), (p.Ref, p.Head, p.Base, p.Author, p.Waits));
		Assert.Equal("https://git.example.com/projects/PROJ/repos/app/pull-requests/7", p.Url);
		Assert.False(p.AsksYou); // already approved
	}
}
