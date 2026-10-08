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
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HEI.Agent.Tests;

/// <summary>
/// Game mode's switch: Heiward's own, off until it's turned on, with the seam for Manor (its settings' "gameMode") that
/// Developer options have; the Games area's requests refused while it's off; and automatic cleanup of the safe kinds only.
/// Nothing here reads the real Manor's settings or Heiward's.
/// </summary>
[Collection(AgentHomeCollection.Name)] // MANOR_HOME and HEIWARD_HOME are process-wide
public sealed class GameModeTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-gamemode-" + Guid.NewGuid().ToString("N"));
	readonly string? manorHome = Environment.GetEnvironmentVariable("MANOR_HOME");
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	readonly string home, manor;

	public GameModeTests() {
		home = Path.Combine(dir, "heiward");
		manor = Path.Combine(dir, "manor");
		Directory.CreateDirectory(home);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		Environment.SetEnvironmentVariable("MANOR_HOME", manor); // no Manor until a test installs it
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("MANOR_HOME", manorHome);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	string ManorSettings => Path.Combine(manor, "settings.json");

	void InstallManor(string settings) {
		Directory.CreateDirectory(Path.Combine(manor, "app"));
		File.WriteAllText(ManorSettings, settings);
	}

	static AgentConfig Own(bool on) => new() { GameMode = on ? "on" : "off" };

	static Manor TheHall(bool? gameMode) => new("The Hall", 19000, null, null, gameMode);

	// ---- The switch

	[Fact]
	public void IsOff_UntilTurnedOn() {
		Assert.False(new AgentConfig().GameModeOn);
		Assert.False(AgentConfig.FromJson("{}").GameModeOn);
		Assert.True(AgentConfig.FromJson("""{ "gameMode": "on" }""").GameModeOn);
		Assert.False(AgentConfig.FromJson("""{ "gameMode": "off" }""").GameModeOn);
		Assert.False(GameMode.Now(new AgentConfig()).On);
	}

	[Fact]
	public void IsKeptInSettings_BesideDeveloperMode() {
		new AgentConfig { GameMode = "on", DeveloperMode = "off" }.Save();
		Assert.Contains("\"gameMode\": \"on\"", File.ReadAllText(AgentPaths.Config));
		AgentConfig back = AgentConfig.Load();
		Assert.True(back.GameModeOn);
		Assert.False(back.DeveloperModeOn);
	}

	[Theory]
	[InlineData("{\"gameMode\":true}", true)]
	[InlineData("{\"gameMode\":false}", false)]
	[InlineData("{\"gameMode\":\"on\"}", null)]
	[InlineData("{\"gameMode\":1}", null)]
	[InlineData("{}", null)]
	public void ManorsSeam_OnlyTrueOrFalse(string json, bool? on) => Assert.Equal(on, Manor.FromJson(json).GameMode);

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public void Manor_DecidesOnceItSays_WhateverHeiwardsOwnSwitchSays(bool own, bool manors) {
		GameMode mode = GameMode.Of(Own(own), TheHall(manors));
		Assert.Equal(manors, mode.On);
		Assert.True(mode.ByManor);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void WithoutManor_OrManorNotSaying_HeiwardsOwnSwitchDecides(bool own) {
		Assert.Equal(new GameMode(own, null), GameMode.Of(Own(own), null));
		Assert.Equal(new GameMode(own, null), GameMode.Of(Own(own), TheHall(null)));
		// Manor's Developer options are another switch: they don't turn game mode on or off.
		Assert.Equal(new GameMode(own, null), GameMode.Of(Own(own), new Manor("The Hall", 19000, null, !own)));
	}

	[Fact]
	public void Now_ReadsManorFresh() {
		InstallManor("{\"name\":\"The Hall\",\"port\":19000,\"developerOptions\":false}");
		Assert.Equal(new GameMode(true, null), GameMode.Now(Own(true))); // Manor doesn't pass it yet
		File.WriteAllText(ManorSettings, "{\"name\":\"The Hall\",\"port\":19000,\"gameMode\":false}");
		GameMode off = GameMode.Now(Own(true));
		Assert.False(off.On);
		Assert.Equal("The Hall turns this off", off.ManorNote);
		Assert.Equal("Game mode is off: The Hall turns it off. Change it in The Hall.", off.PageOffText);
		Assert.Equal("Game mode is off: turn it on in Settings.", GameMode.Of(Own(false), null).PageOffText);
		Assert.Equal("off (The Hall's)", off.Describe());
	}

	[Fact]
	public void TheDailyGamesCheck_OnlyWithGameModeOn_OnceADay() {
		Assert.False(GameScan.Due(GameMode.Of(Own(false), null)));
		Assert.True(GameScan.Due(GameMode.Of(Own(true), null)));
		new GameReport { ScannedAtUtc = DateTime.UtcNow, Build = AppBuild.Current }.Save();
		Assert.False(GameScan.Due(GameMode.Of(Own(true), null)));
		new GameReport { ScannedAtUtc = DateTime.UtcNow.AddDays(-1), Build = AppBuild.Current }.Save();
		Assert.True(GameScan.Due(GameMode.Of(Own(true), null)));
	}

	// ---- Automatic cleanup: only the safe kinds, only if turned on, only right after the daily check

	/// <summary>Removes nothing: says what it would have.</summary>
	sealed class Recorder : IAutoActions {
		public readonly List<string> Removed = new();
		public RecycleResult Recycle(ReportGroup group, IReadOnlyCollection<string> paths) => new(new(), new(), 0);
		public CleanResult Clean(DevItem item) => new(0, 0, null);
		public PruneResult Prune(RepoBranches repo, IReadOnlyCollection<string>? branches) => new(new(), new(), true, null);
		public CleanResult RemoveGame(GameItem item) {
			Removed.Add(item.Id);
			return new CleanResult(item.Bytes, 0, null);
		}
	}

	static GameItem Item(string id, string kind, bool suggested = true, string? game = null, string? blocked = null, bool info = false) =>
		new(id, kind, id, @"C:\x\" + id, [@"C:\x\" + id], 5 << 20, null, suggested, blocked, "", "", Game: game, Info: info);

	static GameReport Games(params GameItem[] items) => new() {
		ScannedAtUtc = DateTime.UtcNow, Build = AppBuild.Current, Categories = { new GameCategory("all", "All", "", items.ToList()) },
	};

	[Fact]
	public void AutomaticCleanup_TakesOnlyTheSafeKinds() {
		DateTime now = DateTime.UtcNow;
		GameReport games = Games(
			Item("orphan", "orphan"), Item("workshop", "workshop"), Item("cache", "cache"), Item("download", "download"),
			Item("dump", "dump"), Item("crash", "crash"), Item("wer", "wer"), Item("shader-gone", "shader"),
			Item("shader-installed", "shader", suggested: false, game: "g1"), Item("gpu", "gpu-shader", suggested: false),
			Item("paused", "paused", suggested: false), Item("busy", "cache", suggested: false, blocked: "Steam is running: close it first"),
			Item("twice", "twice", suggested: false, info: true), Item("idle", "idle", suggested: false, info: true));
		var cfg = new AgentConfig { AutoClean = new AutoCleanConfig { Games = true, AfterDays = 3 } };
		var s = new AutoCleanState { GamesSinceUtc = now.AddDays(-30) };
		foreach (GameItem i in games.Items) s.FirstSeenUtc["m:" + i.Id] = now.AddDays(-9);

		var actions = new Recorder();
		AutoRun run = AutoCleaner.Run(cfg, null, null, new Dictionary<string, Decision>(), s, now, false, actions, games, gamesChecked: true)!;
		Assert.Equal(["orphan", "workshop", "cache", "download", "dump", "crash", "wer", "shader-gone"], actions.Removed);
		Assert.Equal(8, run.GameItems);
		Assert.Contains("of game leftovers to the Recycle Bin", run.Describe());

		AutoPlan plan = AutoCleaner.Plan(cfg, null, null, new Dictionary<string, Decision>(), s, now, games);
		Assert.Contains("stutter", plan.GameItems["shader-installed"].Reason);
		Assert.Contains("stutter", plan.GameItems["gpu"].Reason);
		Assert.Equal("A paused download is always your call", plan.GameItems["paused"].Reason);
		Assert.Equal("Steam is running: close it first", plan.GameItems["busy"].Reason);
		Assert.False(plan.GameItems.ContainsKey("twice")); // what Heiward only points to isn't cleanup's at all
	}

	[Fact]
	public void AutomaticCleanup_OffUntilTurnedOn_OnlyTheKindsTicked_OnlyAfterTheDailyCheck() {
		DateTime now = DateTime.UtcNow;
		GameReport games = Games(Item("dump", "dump"), Item("cache", "cache"));
		var s = new AutoCleanState { GamesSinceUtc = now.AddDays(-30) };
		foreach (GameItem i in games.Items) s.FirstSeenUtc["m:" + i.Id] = now.AddDays(-9);
		var none = new Dictionary<string, Decision>();

		var actions = new Recorder();
		Assert.Null(AutoCleaner.Run(new AgentConfig(), null, null, none, new AutoCleanState(), now, false, actions, games, gamesChecked: true));
		Assert.Empty(actions.Removed);

		var cfg = new AgentConfig { AutoClean = new AutoCleanConfig { Games = true, GameKinds = ["dumps"] }.Normalized() };
		AutoCleaner.Run(cfg, null, null, none, s, now, false, actions, games, gamesChecked: false);
		Assert.Empty(actions.Removed); // the list isn't today's
		AutoCleaner.Run(cfg, null, null, none, s, now, false, actions, games, gamesChecked: true);
		Assert.Equal(["dump"], actions.Removed);
		Assert.Equal("Automatic cleanup is off for launchers' download caches",
			AutoCleaner.Plan(cfg, null, null, none, s, now, games).GameItems["cache"].Reason);

		// "Leave it" holds one back.
		s.Held.Add("m:dump");
		actions.Removed.Clear();
		AutoCleaner.Run(cfg, null, null, none, s, now, false, actions, games, gamesChecked: true);
		Assert.Empty(actions.Removed);
	}

	[Fact]
	public void AutomaticCleanup_GameKinds_KnownOnly() {
		var cfg = new AutoCleanConfig { GameKinds = ["shaders", "everything", "DUMPS", "dumps"] }.Normalized();
		Assert.Equal(["dumps", "shaders"], cfg.GameKinds);
		Assert.Equal(["dumps", "caches", "leftovers", "shaders"], new AutoCleanConfig().GameKinds);
		Assert.False(new AutoCleanConfig().Games);
	}

	// ---- The review page

	/// <summary>The review page on a free port, with <paramref name="cfg"/> saved as settings.json, until disposed.</summary>
	sealed class Page : IAsyncDisposable {
		readonly int port;
		readonly Task<int> server;
		readonly string token;
		public readonly HttpClient Http;

		Page(int port, Task<int> server, HttpClient http, string token) => (this.port, this.server, Http, this.token) = (port, server, http, token);

		public static async Task<Page> StartAsync(AgentConfig cfg) {
			using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
				probe.Start();
				cfg.Port = ((IPEndPoint)probe.LocalEndpoint).Port;
			}
			cfg.ScanEveryMinutes = 0; // the page starts no scan of its own
			cfg.Save();
			Task<int> server = ReviewServer.RunAsync(cfg, openBrowser: false, CancellationToken.None);
			for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(cfg.Port); i++) await Task.Delay(250);
			var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{cfg.Port}/"), Timeout = TimeSpan.FromSeconds(30) };
			return new Page(cfg.Port, server, http, ReviewServer.TokenIn(await http.GetStringAsync("/"))!);
		}

		public async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, object? body = null) {
			using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
			request.Headers.Add("X-Agent-Token", token);
			using HttpResponseMessage answer = await Http.SendAsync(request);
			return (answer.StatusCode, await answer.Content.ReadAsStringAsync());
		}

		public async Task<JsonElement> StateAsync() {
			using var state = JsonDocument.Parse(await Http.GetStringAsync("/api/state"));
			return state.RootElement.Clone();
		}

		public async ValueTask DisposeAsync() {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
			Http.Dispose();
		}
	}

	[Fact]
	public async Task ThePage_GamesArea_OnlyWithGameModeOn() {
		await using Page page = await Page.StartAsync(Own(false));
		using (HttpResponseMessage off = await page.Http.GetAsync("/api/games")) {
			Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
			Assert.Contains("Game mode is off: turn it on in Settings.", await off.Content.ReadAsStringAsync());
		}
		Assert.Equal(HttpStatusCode.Conflict, (await page.PostAsync("/api/games/scan")).Status);
		Assert.Equal(HttpStatusCode.Conflict, (await page.PostAsync("/api/games/items/0123456789abcdef/remove")).Status);
		Assert.False((await page.StateAsync()).GetProperty("games").GetProperty("enabled").GetBoolean());

		var (status, body) = await page.PostAsync("/api/settings", new { gameMode = true });
		Assert.Equal(HttpStatusCode.OK, status);
		Assert.Contains("\"gameMode\": true", body);
		Assert.True(AgentConfig.Load().GameModeOn);
		Assert.True((await page.StateAsync()).GetProperty("games").GetProperty("enabled").GetBoolean());
		using (HttpResponseMessage on = await page.Http.GetAsync("/api/games")) Assert.Equal(HttpStatusCode.OK, on.StatusCode);
		// An item no longer in the list (or never there) isn't removed.
		Assert.Equal(HttpStatusCode.NotFound, (await page.PostAsync("/api/games/items/0123456789abcdef/remove")).Status);
		// "Leave it" takes a game item.
		Assert.Equal(HttpStatusCode.OK, (await page.PostAsync("/api/auto/hold", new { target = "m:0123456789abcdef", hold = true })).Status);
	}

	[Fact]
	public async Task ThePage_WhatGamesLeft_TheListSaysWhatHeiwardOnlyPointsTo() {
		new GameReport {
			ScannedAtUtc = DateTime.UtcNow, Build = AppBuild.Current,
			Categories = { new GameCategory(GameScanner.Idle, "Idle", "", [Item("0123456789abcdef", "idle", suggested: false, info: true) with { Removing = "Uninstall it in Steam." }]) },
		}.Save();
		await using Page page = await Page.StartAsync(Own(true));
		var (status, body) = await page.PostAsync("/api/games/items/0123456789abcdef/remove");
		Assert.Equal(HttpStatusCode.Conflict, status);
		Assert.Contains("Heiward doesn't move or uninstall games", body);
	}

	[Fact]
	public async Task ThePage_WhileManorDecides_ItsOwnSwitchWaits() {
		InstallManor("{\"name\":\"The Hall\",\"port\":19000,\"gameMode\":true}");
		await using Page page = await Page.StartAsync(Own(false));
		var (status, body) = await page.PostAsync("/api/settings", new { gameMode = false });
		Assert.Equal(HttpStatusCode.Conflict, status);
		Assert.Contains("The Hall turns game mode on. Change it in The Hall.", body);
		Assert.False(AgentConfig.Load().GameModeOn); // Heiward's own stays as the user left it
		JsonElement games = (await page.StateAsync()).GetProperty("games");
		Assert.True(games.GetProperty("enabled").GetBoolean());
		Assert.Equal("The Hall turns this on", games.GetProperty("manor").GetProperty("note").GetString());
	}

	[Fact]
	public async Task ThePage_TheSettingsFilesPath_OnlyWithDeveloperModeOn() {
		await using (Page page = await Page.StartAsync(new AgentConfig { DeveloperMode = "off" })) {
			Assert.Equal(JsonValueKind.Null, (await page.StateAsync()).GetProperty("config").GetProperty("path").ValueKind);
		}
		await using (Page page = await Page.StartAsync(new AgentConfig { DeveloperMode = "on" })) {
			Assert.Equal(AgentPaths.Config, (await page.StateAsync()).GetProperty("config").GetProperty("path").GetString());
		}
	}

	// ---- hei games

	(int Code, string Out, string Error) Hei(params string[] args) {
		var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "hei.exe")) {
			UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
		};
		foreach (string a in args) psi.ArgumentList.Add(a);
		psi.Environment["HEIWARD_HOME"] = home;
		psi.Environment["MANOR_HOME"] = manor;
		using var p = Process.Start(psi)!;
		Task<string> error = p.StandardError.ReadToEndAsync();
		string output = p.StandardOutput.ReadToEnd();
		Assert.True(p.WaitForExit(60_000));
		return (p.ExitCode, output.Trim(), error.Result.Trim());
	}

	[Fact]
	public void HeiGames_FollowsTheSwitch() {
		Own(false).Save();
		Assert.Equal((1, "", GameMode.Of(Own(false), null).CommandOffText), Hei("games"));
		Own(true).Save();
		Assert.Equal((0, "No games check yet: run 'hei games --scan'.", ""), Hei("games"));
	}
}

/// <summary>
/// The page in plain words: with developer mode off (and under Manor with its Developer options off), no command lines, log or
/// settings-file names, model or runtime names, or commit links. Each of those is only in a developer-mode branch of app.js.
/// </summary>
public sealed class PlainWordsTests {
	static string Asset(string name) {
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/" + name)!;
		return new StreamReader(s).ReadToEnd();
	}

	[Theory]
	[InlineData("Run \"hei setup\"")]
	[InlineData("\"hei setup\" installs them")]
	[InlineData("run \"hei install\"")]
	[InlineData("settings.json asks")]
	[InlineData("heiward.log")]
	[InlineData("DINOv2")]
	[InlineData("Qualcomm\\'s QNN")]
	[InlineData("DirectML")]
	[InlineData("aiDevice in the settings file")]
	[InlineData("excludeExtensions in the settings file")]
	[InlineData("Built from commit")]
	[InlineData("FFmpeg decodes")]
	public void Jargon_OnlyInADeveloperModeBranch(string jargon) {
		string script = Asset("app.js");
		var at = Regex.Matches(script, Regex.Escape(jargon)).Select(m => m.Index).ToList();
		Assert.NotEmpty(at);
		foreach (int i in at) {
			string line = script[(script.LastIndexOf('\n', i) + 1)..i];
			if (line.TrimStart().StartsWith("//", StringComparison.Ordinal) || line.TrimStart().StartsWith('*')) continue; // a comment
			// The developer-mode test it sits behind: dev ?, (dev ? …), s.dev.enabled ? …, or an if (s.dev.enabled) block just above.
			string before = script[Math.Max(0, i - 400)..i];
			Assert.True(Regex.IsMatch(before, @"\bdev \?|\(dev \?|\bdev && |s\.dev\.enabled \?|s\.dev\.enabled\)|dev\s*\n\s*\?"), $"\"{jargon}\" isn't behind developer mode: …{before[^120..]}");
		}
	}

	[Fact]
	public void TheDeveloperModeCard_HidesUnderManorWhileItsOff() {
		Assert.Contains("<section id=\"devmode-section\">", Asset("index.html"));
		string script = Asset("app.js");
		int start = script.IndexOf("function renderDevModeCard(", StringComparison.Ordinal);
		string render = script[start..script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal)];
		Assert.Contains("const hidden = !!manor && !on;", render);
		Assert.Contains("$('devmode-section').classList.toggle('hidden', hidden);", render);
	}

	[Fact]
	public void TheGamesArea_IsThere_AndGuardedLikeTheDeveloperArea() {
		string page = Asset("index.html");
		foreach (string id in new[] { "id=\"games-section\" class=\"hidden\"", "id=\"gamemode-card\"", "id=\"gamesview\"", "id=\"games-nav\"", "id=\"games-content\"" })
			Assert.Contains(id, page);
		string script = Asset("app.js");
		Assert.Contains("(route.view === 'games' && !state.games.enabled)", script);
		Assert.Contains("saveSettings({ gameMode: next })", script);
	}
}
