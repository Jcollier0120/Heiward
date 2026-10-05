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
}
