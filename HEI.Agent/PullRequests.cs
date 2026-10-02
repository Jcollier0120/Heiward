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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HEI.Agent {
	/// <summary>An open pull request (GitLab's merge request), as the developer page shows it.</summary>
	/// <param name="Ref">How its host writes it: "#45", "!12".</param>
	/// <param name="Waits">
	/// What it waits for, most pressing first on the page: "merge" (nothing: approved or needing no review, checks passed),
	/// "review", "changes" (changes requested), "checks" (failing), "conflicts", "behind" (the rules want it up to date with its
	/// base), "blocked" (other rules), "running" (checks not done), "draft".
	/// </param>
	/// <param name="Yours">The signed-in account opened it.</param>
	/// <param name="AsksYou">The signed-in account is asked to review it.</param>
	sealed record PullRequest(string Ref, string Title, string Url, string Author, bool Yours, bool AsksYou, string Head, string Base,
		DateTime? UpdatedUtc, string Waits);

	/// <param name="Host">Where it's hosted, as people call it: "GitHub", "Azure DevOps", "GitLab" …</param>
	/// <param name="Web">Its pull requests' page there.</param>
	/// <param name="Total">Its open pull requests; <paramref name="Pulls"/> holds the 20 updated last.</param>
	/// <param name="Error">Why they couldn't be read: no sign-in, no access, no answer.</param>
	sealed record RepoPulls(string Host, string Web, int Total, List<PullRequest> Pulls, string? Error);

	/// <param name="Repos">By the repository's folder, lower case, without a trailing backslash.</param>
	sealed record PullsView(Dictionary<string, RepoPulls> Repos, DateTime FetchedUtc);

	/// <summary>Where a git remote's pull requests live, read from its URL.</summary>
	/// <param name="Kind">github, azure (Azure DevOps), azure-server (Azure DevOps Server, TFS), gitlab, bitbucket, bitbucket-server, gitea (and Forgejo).</param>
	/// <param name="Host">The server whose sign-in is asked for, with its port when it isn't the usual one.</param>
	/// <param name="Base">Where its API starts: https://api.github.com, https://dev.azure.com/org, https://tfs:8080/tfs/DefaultCollection, https://gitlab.com/api/v4 …</param>
	/// <param name="Owner">GitHub's or Gitea's owner, GitLab's namespace, Bitbucket's workspace or project key, Azure's project.</param>
	/// <param name="Web">The repository's page.</param>
	/// <param name="SignInPath">The path git's credential helpers may keep a sign-in under (Azure DevOps keeps one per organization).</param>
	sealed record PullSource(string Kind, string Host, string Base, string Owner, string Name, string Web, string? SignInPath = null) {
		public string Label => Kind switch {
			"github" => "GitHub", "azure" => "Azure DevOps", "azure-server" => "Azure DevOps Server", "gitlab" => "GitLab",
			"bitbucket" => "Bitbucket", "bitbucket-server" => "Bitbucket Server", _ => "Gitea",
		};

		/// <summary>The repository's list of open pull requests on its host.</summary>
		public string PullsWeb => Web + Kind switch {
			"github" or "gitea" => "/pulls", "azure" or "azure-server" => "/pullrequests", "gitlab" => "/-/merge_requests", _ => "/pull-requests",
		};

		static string Esc(string s) => Uri.EscapeDataString(s);

		/// <summary>
		/// The host of a remote: https, ssh or scp-style (git@host:owner/repo). Null for a host that isn't one of these, by
		/// its name (github.com, dev.azure.com, gitlab.com, bitbucket.org, codeberg.org, or a server named after its product)
		/// or its URL's shape (Azure DevOps Server's /_git/, Bitbucket Server's /scm/).
		/// </summary>
		public static PullSource? Parse(string remote) {
			string url = remote.Trim();
			Match scp = Regex.Match(url, @"^(?:[^@/\s]+@)?([^:/\s]+):(?!//)(.+)$");
			if (!url.Contains("://") && scp.Success) url = "ssh://" + scp.Groups[1].Value + "/" + scp.Groups[2].Value.TrimStart('/');
			if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http" or "ssh" or "git")) return null;
			string host = uri.Host.ToLowerInvariant();
			bool web = uri.Scheme is "https" or "http";
			string hostPort = web && !uri.IsDefaultPort ? $"{host}:{uri.Port}" : host;
			string root = (uri.Scheme == "http" ? "http://" : "https://") + hostPort;
			List<string> parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
			if (parts.Count == 0) return null;
			parts[^1] = Regex.Replace(parts[^1], @"\.git$", "", RegexOptions.IgnoreCase);

			// Azure DevOps over ssh: v3/organization/project/repository.
			if (host is "ssh.dev.azure.com" or "vs-ssh.visualstudio.com" && parts.Count == 4 && parts[0] == "v3") {
				bool dev = host == "ssh.dev.azure.com";
				string org = parts[1];
				string b = dev ? "https://dev.azure.com/" + Esc(org) : $"https://{org}.visualstudio.com";
				return new PullSource("azure", dev ? "dev.azure.com" : $"{org}.visualstudio.com", b, parts[2], parts[3],
					$"{b}/{Esc(parts[2])}/_git/{Esc(parts[3])}", dev ? org : null);
			}
			// Azure DevOps, Azure DevOps Server and TFS over https: …/project/_git/repository, or …/_git/repository for the
			// project's own repository, which has the project's name.
			int git = parts.FindIndex(p => p.Equals("_git", StringComparison.OrdinalIgnoreCase));
			if (git >= 0 && git == parts.Count - 2) {
				string repo = parts[^1];
				if (host == "dev.azure.com") {
					if (git == 0) return null;
					string project = git == 1 ? repo : parts[git - 1];
					string b = "https://dev.azure.com/" + Esc(parts[0]);
					return new PullSource("azure", host, b, project, repo, $"{b}/{Esc(project)}/_git/{Esc(repo)}", parts[0]);
				}
				if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal)) {
					string project = git == 0 || parts[git - 1].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase) && git == 1 ? repo : parts[git - 1];
					string b = "https://" + host;
					return new PullSource("azure", host, b, project, repo, $"{b}/{Esc(project)}/_git/{Esc(repo)}");
				}
				if (git == 0) return null; // a server's URL names its collection
				// git == 1: collection/_git/repository; otherwise …/collection/project/_git/repository.
				string serverProject = git == 1 ? repo : parts[git - 1];
				string collection = string.Join('/', parts.Take(git == 1 ? 1 : git - 1).Select(Esc));
				string server = $"{root}/{collection}";
				return new PullSource("azure-server", hostPort, server, serverProject, repo, $"{server}/{Esc(serverProject)}/_git/{Esc(repo)}");
			}
			if (host == "bitbucket.org" && parts.Count == 2)
				return new PullSource("bitbucket", host, "https://api.bitbucket.org/2.0", parts[0], parts[1], $"https://bitbucket.org/{Esc(parts[0])}/{Esc(parts[1])}");
			// Bitbucket Server and Data Center: https://host/scm/PROJECT/repo, or ssh://git@host:7999/PROJECT/repo.
			int scm = parts.FindIndex(p => p.Equals("scm", StringComparison.OrdinalIgnoreCase));
			if (web && scm >= 0 && scm == parts.Count - 3 || !web && parts.Count == 2 && (host.Contains("bitbucket") || host.Contains("stash"))) {
				string prefix = web ? string.Concat(parts.Take(scm).Select(p => "/" + Esc(p))) : "";
				string b = root + prefix;
				return new PullSource("bitbucket-server", hostPort, b, parts[^2], parts[^1], $"{b}/projects/{Esc(parts[^2])}/repos/{Esc(parts[^1])}");
			}
			if ((host == "github.com" || host.Contains("github")) && parts.Count == 2)
				return new PullSource("github", host, host == "github.com" ? "https://api.github.com" : root + "/api", parts[0], parts[1],
					$"{(host == "github.com" ? "https://github.com" : root)}/{Esc(parts[0])}/{Esc(parts[1])}");
			if ((host == "gitlab.com" || host.Contains("gitlab")) && parts.Count >= 2) {
				string ns = string.Join('/', parts.Take(parts.Count - 1));
				return new PullSource("gitlab", hostPort, root + "/api/v4", ns, parts[^1], $"{root}/{string.Join('/', parts.Select(Esc))}");
			}
			if ((host == "codeberg.org" || host.Contains("gitea") || host.Contains("forgejo")) && parts.Count == 2)
				return new PullSource("gitea", hostPort, root + "/api/v1", parts[0], parts[1], $"{root}/{Esc(parts[0])}/{Esc(parts[1])}");
			return null;
		}
	}

	/// <summary>
	/// The developer page's open pull requests, asked of each repository's host when the page wants them, and kept two
	/// minutes. Sign-ins come from the host's own command-line tool (gh, az), Windows' sign-in for a server on this
	/// network, or the one git keeps for the host (Git Credential Manager), and only ever go to that host. Nothing is
	/// asked interactively, nor written anywhere.
	/// </summary>
	static class PullRequests {
		static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);
		static readonly SemaphoreSlim gate = new(1, 1);
		static (string Key, PullsView View, DateTime At)? cached;
		const int Shown = 20;

		internal static string KeyOf(string path) => path.TrimEnd('\\', '/').ToLowerInvariant();

		/// <param name="again">Ask again even if the last answer is fresh (the page's Refresh).</param>
		public static async Task<PullsView> GetAsync(IReadOnlyList<RepoSource> sources, bool again, CancellationToken ct) {
			var wanted = sources.Where(s => s.Vcs == "git" && s.Remote != null)
				.Select(s => (Key: KeyOf(s.Path), Source: PullSource.Parse(s.Remote!)))
				.Where(x => x.Source != null).Select(x => (x.Key, Source: x.Source!)).ToList();
			string key = string.Join('|', wanted.Select(w => w.Key + ">" + w.Source.Web));
			await gate.WaitAsync(ct);
			try {
				if (!again && cached is { } c && c.Key == key && DateTime.UtcNow - c.At < Fresh) return c.View;
				var view = new PullsView(await FetchAsync(wanted, ct), DateTime.UtcNow);
				cached = (key, view, DateTime.UtcNow);
				return view;
			}
			finally { gate.Release(); }
		}

		static async Task<Dictionary<string, RepoPulls>> FetchAsync(List<(string Key, PullSource Source)> wanted, CancellationToken ct) {
			var signIns = new SignIns();
			var results = new ConcurrentDictionary<string, RepoPulls>();
			var work = new List<Task>();
			// GitHub: one query per server for all its repositories.
			foreach (var server in wanted.Where(w => w.Source.Kind == "github").GroupBy(w => w.Source.Host))
				work.Add(Task.Run(async () => {
					foreach (var (k, r) in await GitHubAsync(server.ToList(), signIns, ct)) results[k] = r;
				}, ct));
			using var limit = new SemaphoreSlim(6);
			foreach (var (k, s) in wanted.Where(w => w.Source.Kind != "github"))
				work.Add(Task.Run(async () => {
					await limit.WaitAsync(ct);
					try { results[k] = await Guarded(s, () => OneAsync(s, signIns, ct)); }
					finally { limit.Release(); }
				}, ct));
			await Task.WhenAll(work);
			return new Dictionary<string, RepoPulls>(results);
		}

		static async Task<RepoPulls> Guarded(PullSource s, Func<Task<RepoPulls>> read) {
			try { return await read(); }
			catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) {
				return Failed(s, e is TaskCanceledException ? $"{s.Label} didn't answer in time." : $"{s.Label} couldn't be read: {e.Message}");
			}
		}

		static RepoPulls Failed(PullSource s, string error) => new(s.Label, s.PullsWeb, 0, new(), error);

		static string NoSignIn(PullSource s) => s.Kind switch {
			"github" => "Sign in with the GitHub CLI (gh auth login), or with git (Git Credential Manager), to see its pull requests.",
			"azure" => "Sign in to Azure DevOps with git (Git Credential Manager) or the Azure CLI (az login) to see its pull requests.",
			_ => $"Sign in to {s.Host} with git (Git Credential Manager) to see its pull requests.",
		};

		static Task<RepoPulls> OneAsync(PullSource s, SignIns signIns, CancellationToken ct) => s.Kind switch {
			"azure" or "azure-server" => AzureAsync(s, signIns, ct),
			"gitlab" => GitLabAsync(s, signIns, ct),
			"bitbucket" => BitbucketAsync(s, signIns, ct),
			"bitbucket-server" => BitbucketServerAsync(s, signIns, ct),
			_ => GiteaAsync(s, signIns, ct),
		};

		// ------------------------------------------------------------------ GitHub

		const string GitHubFragment = @"
fragment pulls on Repository {
  pullRequests(states: OPEN, first: 20, orderBy: {field: UPDATED_AT, direction: DESC}) {
    totalCount
    nodes {
      number title url isDraft reviewDecision mergeable mergeStateStatus updatedAt headRefName baseRefName
      author { login }
      reviewRequests(first: 20) { nodes { requestedReviewer { ... on User { login } } } }
      commits(last: 1) { nodes { commit { statusCheckRollup { state } } } }
    }
  }
}";

		static async Task<Dictionary<string, RepoPulls>> GitHubAsync(List<(string Key, PullSource Source)> repos, SignIns signIns, CancellationToken ct) {
			var result = new Dictionary<string, RepoPulls>();
			PullSource first = repos[0].Source;
			try {
				Credential? signIn = await signIns.For(first);
				if (signIn == null) {
					foreach (var (k, s) in repos) result[k] = Failed(s, NoSignIn(s));
					return result;
				}
				var query = new StringBuilder("query {\n  viewer { login }\n");
				for (int i = 0; i < repos.Count; i++)
					query.Append($"  r{i}: repository(owner: {JsonSerializer.Serialize(repos[i].Source.Owner)}, name: {JsonSerializer.Serialize(repos[i].Source.Name)}) {{ ...pulls }}\n");
				query.Append('}').Append(GitHubFragment);
				using var request = new HttpRequestMessage(HttpMethod.Post, first.Base + "/graphql") {
					Content = new StringContent(JsonSerializer.Serialize(new { query = query.ToString() }), Encoding.UTF8, "application/json"),
				};
				request.Headers.Accept.ParseAdd("application/vnd.github.merge-info-preview+json");
				var (status, json, _) = await SendAsync(Http, request, signIn, ct);
				if (status != HttpStatusCode.OK || json == null) {
					string why = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? NoSignIn(first) : $"GitHub answered {(int)status}.";
					foreach (var (k, s) in repos) result[k] = Failed(s, why);
					return result;
				}
				using (json) {
					JsonElement data = Get(json.RootElement, "data") ?? default;
					string? viewer = Str(data, "viewer", "login");
					for (int i = 0; i < repos.Count; i++) {
						var (k, s) = repos[i];
						if (data.ValueKind != JsonValueKind.Object || Get(data, "r" + i) is not { ValueKind: JsonValueKind.Object } repo) {
							result[k] = Failed(s, "GitHub doesn't show this account that repository.");
							continue;
						}
						var pulls = Items(repo, "pullRequests", "nodes").Select(p => GitHubPull(p, viewer)).ToList();
						result[k] = new RepoPulls(s.Label, s.PullsWeb, Int(repo, "pullRequests", "totalCount") ?? pulls.Count, pulls, null);
					}
				}
			}
			catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) {
				foreach (var (k, s) in repos) result[k] = Failed(s, e is TaskCanceledException ? "GitHub didn't answer in time." : "GitHub couldn't be read: " + e.Message);
			}
			return result;
		}

		static PullRequest GitHubPull(JsonElement p, string? viewer) {
			string author = Str(p, "author", "login") ?? "ghost";
			string? checks = Items(p, "commits", "nodes").Select(n => Str(n, "commit", "statusCheckRollup", "state")).LastOrDefault();
			bool asks = viewer != null && Items(p, "reviewRequests", "nodes").Any(n => Str(n, "requestedReviewer", "login") == viewer);
			return new PullRequest("#" + Int(p, "number"), Str(p, "title") ?? "", Str(p, "url") ?? "", author, author == viewer, asks,
				Str(p, "headRefName") ?? "", Str(p, "baseRefName") ?? "", Date(p, "updatedAt"),
				GitHubWaits(Bool(p, "isDraft"), Str(p, "reviewDecision"), Str(p, "mergeable"), Str(p, "mergeStateStatus"), checks));
		}

		internal static string GitHubWaits(bool draft, string? review, string? mergeable, string? mergeState, string? checks) =>
			draft ? "draft"
			: mergeable == "CONFLICTING" || mergeState == "DIRTY" ? "conflicts"
			: checks is "FAILURE" or "ERROR" ? "checks"
			: review == "CHANGES_REQUESTED" ? "changes"
			: checks is "PENDING" or "EXPECTED" ? "running"
			: review == "REVIEW_REQUIRED" ? "review"
			: mergeState == "BEHIND" ? "behind"
			: mergeState == "BLOCKED" ? "blocked"
			: "merge";

		// ------------------------------------------------------------------ Azure DevOps, Azure DevOps Server, TFS

		static async Task<RepoPulls> AzureAsync(PullSource s, SignIns signIns, CancellationToken ct) {
			string pullsPath = $"/{Uri.EscapeDataString(s.Owner)}/_apis/git/repositories/{Uri.EscapeDataString(s.Name)}/pullrequests?searchCriteria.status=active&$top=50&api-version=";
			// A server on this network takes Windows' own sign-in; anything else, or a refusal, the sign-in git keeps.
			bool windows = s.Kind == "azure-server" && OnThisNetwork(s.Host);
			Credential? signIn = windows ? null : await signIns.For(s);
			if (!windows && signIn == null) return Failed(s, NoSignIn(s));
			HttpClient http = windows ? WindowsSignIn : Http;
			// Azure DevOps takes the newest API; a server takes what its version knows (TFS 2018 stops at 4.1).
			string[] versions = s.Kind == "azure" ? new[] { "7.1" } : new[] { "6.0", "5.0", "4.1" };
			foreach (string version in versions) {
				var (status, json, _) = await GetAsync(http, s.Base + pullsPath + version, signIn, ct);
				if (status == HttpStatusCode.Unauthorized && windows) {
					json?.Dispose();
					windows = false;
					http = Http;
					signIn = await signIns.For(s);
					if (signIn == null) return Failed(s, NoSignIn(s));
					(status, json, _) = await GetAsync(http, s.Base + pullsPath + version, signIn, ct);
				}
				if (status == HttpStatusCode.BadRequest && version != versions[^1]) { json?.Dispose(); continue; }
				if (status != HttpStatusCode.OK || json == null)
					return Failed(s, status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation
						? NoSignIn(s) : status == HttpStatusCode.NotFound ? $"{s.Label} doesn't show this account that repository." : $"{s.Label} answered {(int)status}.");
				using (json) {
					string? me = await AzureViewerAsync(s, http, signIn, ct);
					var pulls = Items(json.RootElement, "value").Select(p => AzurePull(p, s, me)).ToList();
					return new RepoPulls(s.Label, s.PullsWeb, pulls.Count, pulls.Take(Shown).ToList(), null);
				}
			}
			return Failed(s, $"{s.Label} answered no version of its API Heiward knows.");
		}

		static async Task<string?> AzureViewerAsync(PullSource s, HttpClient http, Credential? signIn, CancellationToken ct) {
			var (status, json, _) = await GetAsync(http, s.Base + "/_apis/connectionData", signIn, ct);
			using (json) return status == HttpStatusCode.OK && json != null ? Str(json.RootElement, "authenticatedUser", "id") : null;
		}

		internal static PullRequest AzurePull(JsonElement p, PullSource s, string? me) {
			string id = Int(p, "pullRequestId")?.ToString() ?? "";
			var reviewers = Items(p, "reviewers").Select(r => (Id: Str(r, "id"), Vote: Int(r, "vote") ?? 0, Required: Bool(r, "isRequired"))).ToList();
			string web = Str(p, "repository", "webUrl") ?? s.Web;
			return new PullRequest("!" + id, Str(p, "title") ?? "", $"{web}/pullrequest/{id}", Str(p, "createdBy", "displayName") ?? "",
				me != null && Str(p, "createdBy", "id") == me, me != null && reviewers.Any(r => r.Id == me && r.Vote == 0),
				Branch(Str(p, "sourceRefName")), Branch(Str(p, "targetRefName")), Date(p, "creationDate"),
				AzureWaits(Bool(p, "isDraft"), Str(p, "mergeStatus"), reviewers.Select(r => (r.Vote, r.Required))));
		}

		/// <param name="reviewers">Votes: 10 approved, 5 approved with suggestions, 0 none yet, -5 waiting for the author, -10 rejected.</param>
		internal static string AzureWaits(bool draft, string? mergeStatus, IEnumerable<(int Vote, bool Required)> reviewers) {
			var r = reviewers.ToList();
			return draft ? "draft"
				: mergeStatus == "conflicts" ? "conflicts"
				: r.Any(x => x.Vote < 0) ? "changes"
				: mergeStatus is "rejectedByPolicy" or "failure" ? "blocked"
				: mergeStatus == "queued" ? "running"
				: r.Any(x => x.Required && x.Vote < 5) || !r.Any(x => x.Vote >= 5) ? "review"
				: "merge";
		}

		static string Branch(string? refName) => refName?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? refName["refs/heads/".Length..] : refName ?? "";

		// ------------------------------------------------------------------ GitLab

		static async Task<RepoPulls> GitLabAsync(PullSource s, SignIns signIns, CancellationToken ct) {
			Credential? signIn = await signIns.For(s); // a public project answers without one
			string project = Uri.EscapeDataString(s.Owner + "/" + s.Name);
			var (status, json, headers) = await GetAsync(Http, $"{s.Base}/projects/{project}/merge_requests?state=opened&per_page={Shown}&order_by=updated_at", signIn, ct);
			if (status != HttpStatusCode.OK || json == null) return Failed(s, Refused(s, status, signIn));
			using (json) {
				string? me = null;
				if (signIn != null) {
					var (us, user, _) = await GetAsync(Http, s.Base + "/user", signIn, ct);
					using (user) me = us == HttpStatusCode.OK && user != null ? Str(user.RootElement, "username") : null;
				}
				var pulls = Items(json.RootElement).Select(p => {
					string author = Str(p, "author", "username") ?? "";
					var reviewers = Items(p, "reviewers").Select(r => Str(r, "username")).ToList();
					return new PullRequest("!" + Int(p, "iid"), Str(p, "title") ?? "", Str(p, "web_url") ?? s.Web, Str(p, "author", "name") ?? author,
						me != null && author == me, me != null && reviewers.Contains(me), Str(p, "source_branch") ?? "", Str(p, "target_branch") ?? "",
						Date(p, "updated_at"), GitLabWaits(Bool(p, "draft") || Bool(p, "work_in_progress"), Bool(p, "has_conflicts"),
							Str(p, "detailed_merge_status"), reviewers.Count > 0));
				}).ToList();
				return new RepoPulls(s.Label, s.PullsWeb, Total(headers, "X-Total") ?? pulls.Count, pulls, null);
			}
		}

		internal static string GitLabWaits(bool draft, bool conflicts, string? detailed, bool hasReviewers) =>
			draft || detailed == "draft_status" ? "draft"
			: conflicts || detailed == "conflict" ? "conflicts"
			: detailed switch {
				"mergeable" => "merge",
				"not_approved" => "review",
				"ci_must_pass" => "checks",
				"ci_still_running" or "checking" or "unchecked" or "preparing" or "approvals_syncing" => "running",
				"discussions_not_resolved" or "requested_changes" => "changes",
				"need_rebase" => "behind",
				null => hasReviewers ? "review" : "merge", // GitLab before 15.6
				_ => "blocked",
			};

		// ------------------------------------------------------------------ Bitbucket Cloud and Server

		static async Task<RepoPulls> BitbucketAsync(PullSource s, SignIns signIns, CancellationToken ct) {
			Credential? signIn = await signIns.For(s);
			string url = $"{s.Base}/repositories/{Uri.EscapeDataString(s.Owner)}/{Uri.EscapeDataString(s.Name)}/pullrequests?state=OPEN&pagelen={Shown}" +
				"&fields=size,values.id,values.title,values.draft,values.updated_on,values.links.html.href,values.author.display_name,values.author.uuid," +
				"values.source.branch.name,values.destination.branch.name,values.participants.state,values.participants.approved,values.reviewers.uuid";
			var (status, json, _) = await GetAsync(Http, url, signIn, ct);
			if (status == HttpStatusCode.Unauthorized && signIn is { Scheme: "Basic", Secret: { } token })
				(status, json, _) = await GetAsync(Http, url, signIn = signIn with { Scheme = "Bearer", Value = token }, ct); // a sign-in from Git Credential Manager's OAuth
			// Bitbucket fails a page of 20 for some repositories ("Either a cset or a repo and commit hash must be provided"); 10 goes through.
			if (status == HttpStatusCode.BadRequest) {
				json?.Dispose();
				(status, json, _) = await GetAsync(Http, url.Replace($"pagelen={Shown}", "pagelen=10"), signIn, ct);
			}
			if (status != HttpStatusCode.OK || json == null) return Failed(s, Refused(s, status, signIn));
			using (json) {
				string? me = null;
				if (signIn != null) {
					var (us, user, _) = await GetAsync(Http, s.Base + "/user", signIn, ct);
					using (user) me = us == HttpStatusCode.OK && user != null ? Str(user.RootElement, "uuid") : null;
				}
				var pulls = Items(json.RootElement, "values").Select(p => {
					var states = Items(p, "participants").Select(x => (State: Str(x, "state"), Approved: Bool(x, "approved"))).ToList();
					return new PullRequest("#" + Int(p, "id"), Str(p, "title") ?? "", Str(p, "links", "html", "href") ?? s.Web, Str(p, "author", "display_name") ?? "",
						me != null && Str(p, "author", "uuid") == me, me != null && Items(p, "reviewers").Any(r => Str(r, "uuid") == me),
						Str(p, "source", "branch", "name") ?? "", Str(p, "destination", "branch", "name") ?? "", Date(p, "updated_on"),
						ReviewWaits(Bool(p, "draft"), false, states.Any(x => x.State == "changes_requested"), states.Any(x => x.Approved)));
				}).ToList();
				return new RepoPulls(s.Label, s.PullsWeb, Int(json.RootElement, "size") ?? pulls.Count, pulls, null);
			}
		}

		static async Task<RepoPulls> BitbucketServerAsync(PullSource s, SignIns signIns, CancellationToken ct) {
			Credential? signIn = await signIns.For(s);
			string url = $"{s.Base}/rest/api/1.0/projects/{Uri.EscapeDataString(s.Owner)}/repos/{Uri.EscapeDataString(s.Name)}/pull-requests?state=OPEN&order=NEWEST&limit={Shown}";
			var (status, json, _) = await GetAsync(Http, url, signIn, ct);
			if (status == HttpStatusCode.Unauthorized && signIn is { Scheme: "Basic", Secret: { } token })
				(status, json, _) = await GetAsync(Http, url, signIn = signIn with { Scheme = "Bearer", Value = token }, ct); // an HTTP access token
			if (status != HttpStatusCode.OK || json == null) return Failed(s, Refused(s, status, signIn));
			using (json) {
				var pulls = Items(json.RootElement, "values").Select(p => BitbucketServerPull(p, s, signIn?.User)).ToList();
				return new RepoPulls(s.Label, s.PullsWeb, pulls.Count, pulls, null);
			}
		}

		internal static PullRequest BitbucketServerPull(JsonElement p, PullSource s, string? me) {
			var reviews = Items(p, "reviewers").Select(r => (Name: Str(r, "user", "name"), Status: Str(r, "status"))).ToList();
			string author = Str(p, "author", "user", "name") ?? "";
			long? updated = Long(p, "updatedDate");
			return new PullRequest("#" + Int(p, "id"), Str(p, "title") ?? "", Items(p, "links", "self").Select(l => Str(l, "href")).FirstOrDefault() ?? s.Web,
				Str(p, "author", "user", "displayName") ?? author, me != null && author.Equals(me, StringComparison.OrdinalIgnoreCase),
				me != null && reviews.Any(r => r.Name?.Equals(me, StringComparison.OrdinalIgnoreCase) == true && r.Status == "UNAPPROVED"),
				Str(p, "fromRef", "displayId") ?? "", Str(p, "toRef", "displayId") ?? "",
				updated is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null,
				ReviewWaits(Bool(p, "draft"), Str(p, "properties", "mergeResult", "outcome") == "CONFLICTED",
					reviews.Any(r => r.Status == "NEEDS_WORK"), reviews.Any(r => r.Status == "APPROVED")));
		}

		/// <summary>Hosts that say only who approved and who asked for changes: Bitbucket, Gitea.</summary>
		internal static string ReviewWaits(bool draft, bool conflicts, bool changesAsked, bool approved) =>
			draft ? "draft" : conflicts ? "conflicts" : changesAsked ? "changes" : approved ? "merge" : "review";

		// ------------------------------------------------------------------ Gitea and Forgejo

		static async Task<RepoPulls> GiteaAsync(PullSource s, SignIns signIns, CancellationToken ct) {
			Credential? signIn = await signIns.For(s); // a public repository answers without one
			string url = $"{s.Base}/repos/{Uri.EscapeDataString(s.Owner)}/{Uri.EscapeDataString(s.Name)}/pulls?state=open&sort=recentupdate&limit={Shown}";
			var (status, json, headers) = await GetAsync(Http, url, signIn, ct);
			if (status != HttpStatusCode.OK || json == null) return Failed(s, Refused(s, status, signIn));
			using (json) {
				string? me = null;
				if (signIn != null) {
					var (us, user, _) = await GetAsync(Http, s.Base + "/user", signIn, ct);
					using (user) me = us == HttpStatusCode.OK && user != null ? Str(user.RootElement, "login") : null;
				}
				var pulls = Items(json.RootElement).Select(p => {
					string author = Str(p, "user", "login") ?? "";
					var asked = Items(p, "requested_reviewers").Select(r => Str(r, "login")).ToList();
					bool draft = Bool(p, "draft") || Regex.IsMatch(Str(p, "title") ?? "", @"^\s*(\[?WIP\]?:?|Draft:)", RegexOptions.IgnoreCase);
					// A fork's branch is only refs/pull/N/head here: its label says owner/branch.
					string head = Str(p, "head", "ref") ?? "";
					if (head.StartsWith("refs/pull/", StringComparison.Ordinal)) head = Str(p, "head", "label") ?? head;
					return new PullRequest("#" + Int(p, "number"), Str(p, "title") ?? "", Str(p, "html_url") ?? s.Web, author,
						me != null && author == me, me != null && asked.Contains(me), head, Str(p, "base", "ref") ?? "",
						Date(p, "updated_at"), ReviewWaits(draft, Get(p, "mergeable")?.ValueKind == JsonValueKind.False, false, asked.Count == 0));
				}).ToList();
				return new RepoPulls(s.Label, s.PullsWeb, Total(headers, "X-Total-Count") ?? pulls.Count, pulls, null);
			}
		}

		static string Refused(PullSource s, HttpStatusCode status, Credential? signIn) =>
			status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || status == HttpStatusCode.NotFound && signIn == null ? NoSignIn(s)
			: status == HttpStatusCode.NotFound ? $"{s.Label} doesn't show this account that repository."
			: $"{s.Label} answered {(int)status}.";

		// ------------------------------------------------------------------ sign-ins

		/// <param name="Scheme">"Bearer" or "Basic".</param>
		/// <param name="Value">What follows the scheme in the Authorization header.</param>
		/// <param name="User">Who it signs in, when the host said.</param>
		/// <param name="Secret">The token alone, to try it as a bearer token when the host refuses it in Basic.</param>
		sealed record Credential(string Scheme, string Value, string? User = null, string? Secret = null) {
			public override string ToString() => $"{Scheme} sign-in for {User ?? "someone"}"; // never the token
		}

		/// <summary>Each host's sign-in, asked for once per fetch.</summary>
		sealed class SignIns {
			readonly ConcurrentDictionary<string, Lazy<Task<Credential?>>> byHost = new();

			public Task<Credential?> For(PullSource s) =>
				byHost.GetOrAdd(s.Kind + "|" + s.Host + "|" + s.SignInPath, _ => new Lazy<Task<Credential?>>(() => Task.Run(() => Find(s)))).Value;
		}

		static Credential? Find(PullSource s) {
			if (s.Kind == "github" && Tools.Gh != null && Tools.Run(Tools.Gh, "auth", "token", "--hostname", s.Host) is { Length: > 0 } ghToken)
				return new Credential("Bearer", ghToken);
			if (GitSignIn(s.Host, s.Kind == "azure" ? s.SignInPath : null) is var (user, secret)) {
				return s.Kind switch {
					"github" or "gitlab" => new Credential("Bearer", secret, user),
					// Git Credential Manager keeps Azure DevOps' OAuth access tokens (a JWT); a personal access token goes in Basic.
					"azure" or "azure-server" when secret.StartsWith("eyJ", StringComparison.Ordinal) && secret.Count(ch => ch == '.') == 2 => new Credential("Bearer", secret, user),
					_ => new Credential("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + secret)), user, secret),
				};
			}
			if (s.Kind == "azure" && Tools.AzureToken() is { Length: > 0 } azToken) return new Credential("Bearer", azToken);
			return null;
		}

		/// <summary>
		/// The sign-in git keeps for a host (git credential fill), never asking for one: Git Credential Manager and git's own
		/// prompts are turned off, so with nothing kept this fails at once instead of opening a window.
		/// </summary>
		static (string User, string Secret)? GitSignIn(string host, string? path) {
			if (Git.Exe == null) return null;
			var psi = new ProcessStartInfo(Git.Exe) {
				UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
			};
			foreach (string a in new[] { "-c", "credential.interactive=false", "-c", "core.askPass=", "credential", "fill" }) psi.ArgumentList.Add(a);
			psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
			psi.Environment["GCM_INTERACTIVE"] = "never";
			psi.Environment["GCM_GUI_PROMPT"] = "false";
			psi.Environment["GIT_ASKPASS"] = "";
			psi.Environment["SSH_ASKPASS"] = "";
			try {
				using var p = Process.Start(psi)!;
				p.StandardInput.Write($"protocol=https\nhost={host}\n" + (path != null ? $"path={path}\n" : "") + "\n");
				p.StandardInput.Close();
				Task<string> err = p.StandardError.ReadToEndAsync();
				Task<string> output = p.StandardOutput.ReadToEndAsync();
				if (!p.WaitForExit(20_000)) {
					try { p.Kill(true); } catch { }
					return null;
				}
				if (p.ExitCode != 0) return null;
				string? user = null, secret = null;
				foreach (string line in output.Result.Split('\n')) {
					if (line.StartsWith("username=", StringComparison.Ordinal)) user = line["username=".Length..].TrimEnd('\r');
					else if (line.StartsWith("password=", StringComparison.Ordinal)) secret = line["password=".Length..].TrimEnd('\r');
				}
				_ = err.Result;
				return string.IsNullOrEmpty(secret) ? null : (user ?? "", secret);
			}
			catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) {
				return null;
			}
		}

		/// <summary>
		/// A host Windows' own sign-in may go to: a name without dots, or one whose every address is on this network
		/// (private, link-local, loopback). A repository's remote could name any server, and Windows' sign-in sent to one
		/// outside could be replayed.
		/// </summary>
		internal static bool OnThisNetwork(string hostPort) {
			string host = hostPort.Split(':')[0];
			if (!host.Contains('.')) return true;
			try {
				IPAddress[] addresses = Dns.GetHostAddresses(host);
				return addresses.Length > 0 && addresses.All(IsPrivate);
			}
			catch (Exception e) when (e is SocketException or ArgumentException) { return false; }
		}

		internal static bool IsPrivate(IPAddress a) {
			if (IPAddress.IsLoopback(a)) return true;
			if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
			if (a.AddressFamily == AddressFamily.InterNetwork) {
				byte[] b = a.GetAddressBytes();
				return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
			}
			return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal;
		}

		// ------------------------------------------------------------------ HTTP

		static readonly HttpClient Http = Client(windows: false);
		static readonly HttpClient WindowsSignIn = Client(windows: true);

		// No redirects: a sign-in must not follow one to another host (Azure DevOps redirects a refused one to its sign-in page).
		static HttpClient Client(bool windows) {
			var handler = new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
			if (windows) handler.Credentials = CredentialCache.DefaultCredentials;
			var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
			http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Heiward", "1"));
			http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
			return http;
		}

		static Task<(HttpStatusCode Status, JsonDocument? Json, HttpResponseHeaders? Headers)> GetAsync(HttpClient http, string url, Credential? signIn, CancellationToken ct) =>
			SendAsync(http, new HttpRequestMessage(HttpMethod.Get, url), signIn, ct);

		/// <summary>The answer's status, and its JSON when it's JSON (Azure DevOps answers a refused sign-in with a web page).</summary>
		static async Task<(HttpStatusCode Status, JsonDocument? Json, HttpResponseHeaders? Headers)> SendAsync(HttpClient http, HttpRequestMessage request, Credential? signIn, CancellationToken ct) {
			using (request) {
				if (signIn != null) request.Headers.Authorization = new AuthenticationHeaderValue(signIn.Scheme, signIn.Value);
				using HttpResponseMessage response = await http.SendAsync(request, ct);
				if (response.Content.Headers.ContentType?.MediaType?.Contains("json") != true)
					return (response.StatusCode == HttpStatusCode.OK ? HttpStatusCode.NonAuthoritativeInformation : response.StatusCode, null, response.Headers);
				await using Stream body = await response.Content.ReadAsStreamAsync(ct);
				return (response.StatusCode, await JsonDocument.ParseAsync(body, cancellationToken: ct), response.Headers);
			}
		}

		static int? Total(HttpResponseHeaders? headers, string name) =>
			headers != null && headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out int n) ? n : null;

		// ------------------------------------------------------------------ JSON

		static JsonElement? Get(JsonElement e, params string[] path) {
			foreach (string p in path) {
				if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
			}
			return e;
		}

		static string? Str(JsonElement e, params string[] path) => Get(e, path) is { } v ? v.ValueKind switch {
			JsonValueKind.String => v.GetString(),
			JsonValueKind.Number => v.GetRawText(),
			_ => null,
		} : null;

		static int? Int(JsonElement e, params string[] path) => Get(e, path) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int n) ? n : null;
		static long? Long(JsonElement e, params string[] path) => Get(e, path) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out long n) ? n : null;
		static bool Bool(JsonElement e, params string[] path) => Get(e, path) is { ValueKind: JsonValueKind.True };
		static DateTime? Date(JsonElement e, params string[] path) => Str(e, path) is { } s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime d) ? d : null;

		static IEnumerable<JsonElement> Items(JsonElement e, params string[] path) =>
			(path.Length == 0 ? e : Get(e, path)) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();
	}

	/// <summary>The hosts' own command-line tools, when they're installed: the GitHub CLI and the Azure CLI.</summary>
	static class Tools {
		public static readonly string? Gh = Find("gh.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"));
		static readonly string? Az = Find("az.cmd",
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"));

		static string? Find(string name, params string[] also) {
			foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
				try {
					string p = Path.Combine(dir.Trim(), name);
					if (File.Exists(p)) return p;
				}
				catch (ArgumentException) { }
			}
			return also.FirstOrDefault(File.Exists);
		}

		/// <summary>The tool's output, trimmed, when it succeeds within 20 seconds; null otherwise. It's never asked to prompt.</summary>
		public static string? Run(string exe, params string[] args) {
			var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
			foreach (string a in args) psi.ArgumentList.Add(a);
			return Run(psi);
		}

		/// <summary>An Azure DevOps access token from the Azure CLI (az login), for the organizations its account can see.</summary>
		public static string? AzureToken() {
			if (Az == null) return null;
			// az is a batch file: cmd runs it. The arguments are fixed.
			var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") {
				Arguments = $"/d /s /c \"\"{Az}\" account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv\"",
				UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
			};
			return Run(psi);
		}

		static string? Run(ProcessStartInfo psi) {
			psi.Environment["GH_PROMPT_DISABLED"] = "1";
			psi.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
			psi.Environment["NO_COLOR"] = "1";
			try {
				using var p = Process.Start(psi)!;
				Task<string> err = p.StandardError.ReadToEndAsync();
				Task<string> output = p.StandardOutput.ReadToEndAsync();
				if (!p.WaitForExit(20_000)) {
					try { p.Kill(true); } catch { }
					return null;
				}
				_ = err.Result;
				return p.ExitCode == 0 ? output.Result.Trim() : null;
			}
			catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) {
				return null;
			}
		}
	}
}
