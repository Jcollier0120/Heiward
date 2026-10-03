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

namespace HEI.Agent.Tests;

/// <summary>
/// Manor's Developer options: with Manor installed and its settings.json saying "developerOptions": true or false, they
/// decide Heiward's developer mode everywhere it counts (the page and its developer requests, the daily developer check,
/// automatic cleanup, <c>hei dev</c> and <c>hei status --json</c>), and the page's own switch waits, Heiward's own
/// choice kept. Without Manor, or with Manor's settings not saying, Heiward's own switch decides as it always has.
/// Nothing here reads the real Manor's settings or Heiward's, and nothing asks Manor's page.
/// </summary>
[Collection(AgentHomeCollection.Name)] // MANOR_HOME and HEIWARD_HOME are process-wide
public sealed class ManorDeveloperOptionsTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-devopts-" + Guid.NewGuid().ToString("N"));
	readonly string? manorHome = Environment.GetEnvironmentVariable("MANOR_HOME");
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	readonly string home, manor;

	public ManorDeveloperOptionsTests() {
		home = Path.Combine(dir, "heiward");
		manor = Path.Combine(dir, "manor");
		Directory.CreateDirectory(home);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		Environment.SetEnvironmentVariable("MANOR_HOME", manor); // nothing there until a test installs it: no Manor
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("MANOR_HOME", manorHome);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	string ManorSettings => Path.Combine(manor, "settings.json");

	/// <summary>Manor installed (its app folder), with <paramref name="settings"/> as its settings.json.</summary>
	void InstallManor(string settings) {
		Directory.CreateDirectory(Path.Combine(manor, "app"));
		File.WriteAllText(ManorSettings, settings);
	}

	/// <summary>The Hall's settings.json, its Developer options <paramref name="developerOptions"/> as JSON.</summary>
	static string Says(string developerOptions) => $"{{\"name\":\"The Hall\",\"port\":19000,\"developerOptions\":{developerOptions}}}";

	const string Silent = "{\"name\":\"The Hall\",\"port\":19000,\"theme\":\"onyx\"}";

	static AgentConfig Own(bool on) => new() { DeveloperMode = on ? "on" : "off" };

	static Manor TheHall(bool? developerOptions) => new("The Hall", 19000, null, developerOptions);

	// ---- What Manor's settings say

	[Theory]
	[InlineData("{\"developerOptions\":true}", true)]
	[InlineData("{\"developerOptions\":false}", false)]
	[InlineData("\uFEFF{ \"name\": \"The Hall\", \"developerOptions\": true }", true)]
	public void DeveloperOptions_TrueOrFalse_AreManors(string json, bool on) => Assert.Equal(on, Manor.FromJson(json).DeveloperOptions);

	[Theory]
	[InlineData("{}")]
	[InlineData("{\"developerOptions\":null}")]
	[InlineData("{\"developerOptions\":\"true\"}")]
	[InlineData("{\"developerOptions\":\"on\"}")]
	[InlineData("{\"developerOptions\":1}")]
	[InlineData("{\"developerOptions\":0}")]
	[InlineData("{\"developerOptions\":[true]}")]
	[InlineData("{\"developerOptions\":{\"on\":true}}")]
	[InlineData("{\"DeveloperOptions\":true}")] // the name as Manor writes it, exactly
	[InlineData("{\"developerOptions\":true")] // cut short
	[InlineData("[true]")]
	public void DeveloperOptions_AnythingElse_SaysNothing(string json) => Assert.Null(Manor.FromJson(json).DeveloperOptions);

	[Fact]
	public void DeveloperOptions_LeaveTheRestOfManorAsItWas() =>
		Assert.Equal(new Manor("The Hall", 19000, "onyx", false), Manor.FromJson("{\"name\":\"The Hall\",\"port\":19000,\"theme\":\"onyx\",\"developerOptions\":false}"));

	// ---- Which switch decides

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	[InlineData(false, false)]
	public void ManorsDeveloperOptions_Decide_WhateverHeiwardsOwnSwitchSays(bool own, bool manors) {
		DevMode mode = DevMode.Of(Own(own), TheHall(manors));
		Assert.Equal(manors, mode.On);
		Assert.True(mode.ByManor);
		Assert.Equal("The Hall", mode.Manor!.Name);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void WithoutManor_OrWithManorNotSaying_HeiwardsOwnSwitchDecides(bool own) {
		Assert.Equal(new DevMode(own, null), DevMode.Of(Own(own), null));
		Assert.Equal(new DevMode(own, null), DevMode.Of(Own(own), TheHall(null)));
	}

	[Fact]
	public void Now_ReadsManorsSettings_FreshEachTime() {
		AgentConfig own = Own(false);
		Assert.Equal(new DevMode(false, null), DevMode.Now(own)); // no Manor

		// Settings without the app folder: Manor isn't installed, whatever they say.
		Directory.CreateDirectory(manor);
		File.WriteAllText(ManorSettings, Says("true"));
		Assert.Equal(new DevMode(false, null), DevMode.Now(own));

		InstallManor(Says("true"));
		Assert.Equal(new DevMode(true, TheHall(true)), DevMode.Now(own));
		File.WriteAllText(ManorSettings, Says("false"));
		Assert.Equal(new DevMode(false, TheHall(false)), DevMode.Now(Own(true)));
		File.WriteAllText(ManorSettings, Says("\"on\""));
		Assert.Equal(new DevMode(true, null), DevMode.Now(Own(true)));
		File.WriteAllText(ManorSettings, Silent);
		Assert.Equal(new DevMode(false, null), DevMode.Now(own));

		// Manor uninstalled: Heiward's own switch again.
		File.WriteAllText(ManorSettings, Says("true"));
		Directory.Delete(Path.Combine(manor, "app"));
		Assert.Equal(new DevMode(false, null), DevMode.Now(own));
	}

	[Fact]
	public void WhatThePageAndTheCommandsSay() {
		DevMode on = DevMode.Of(Own(false), TheHall(true)), off = DevMode.Of(Own(true), TheHall(false));
		Assert.Equal("The Hall's Developer options turn this on", on.ManorNote);
		Assert.Equal("The Hall's Developer options turn this off", off.ManorNote);
		Assert.Equal("The Hall's Developer options turn developer mode on, for every agent in the manor. Change it in The Hall.", on.ManorDecidesText);
		Assert.Equal("Developer mode is off: The Hall's Developer options turn it off. Change it in The Hall.", off.PageOffText);
		Assert.Equal("Developer mode is off: The Hall's Developer options turn it off. Change it in The Hall: http://manor.localhost:19000/", off.CommandOffText);
		Assert.Equal("on (The Hall's Developer options)", on.Describe());
		Assert.Equal("off (The Hall's Developer options)", off.Describe());

		// Standalone, as before.
		DevMode own = DevMode.Of(Own(false), null);
		Assert.Null(own.ManorNote);
		Assert.Null(own.ManorDecidesText);
		Assert.Equal("Developer mode is off: turn it on in Settings.", own.PageOffText);
		Assert.Equal("Developer mode is off: turn it on in the review page's Settings, or set \"developerMode\": \"on\" in " + AgentPaths.Config, own.CommandOffText);
		Assert.Equal("off", own.Describe());
		Assert.Equal("on", DevMode.Of(Own(true), null).Describe());
	}

	// ---- The daily developer check, and automatic cleanup

	[Fact]
	public void TheDailyDeveloperCheck_FollowsManor() {
		// No check yet: due whenever developer mode is on.
		InstallManor(Says("true"));
		Assert.True(DevScan.Due(DevMode.Now(Own(false))));
		File.WriteAllText(ManorSettings, Says("false"));
		Assert.False(DevScan.Due(DevMode.Now(Own(true))));
		File.WriteAllText(ManorSettings, Silent);
		Assert.True(DevScan.Due(DevMode.Now(Own(true))));
		Assert.False(DevScan.Due(DevMode.Now(Own(false))));

		// Checked today: once a day still, whoever decides.
		new DevReport { ScannedAtUtc = DateTime.UtcNow, Build = AppBuild.Current }.Save();
		File.WriteAllText(ManorSettings, Says("true"));
		Assert.False(DevScan.Due(DevMode.Now(Own(false))));
	}

	/// <summary>Cleans nothing: says what it would have.</summary>
	sealed class Recorder : IAutoActions {
		public readonly List<string> Cleaned = new();
		public RecycleResult Recycle(ReportGroup group, IReadOnlyCollection<string> paths) => new(new(), new(), 0);
		public CleanResult Clean(DevItem item) {
			Cleaned.Add(item.Id);
			return new CleanResult(item.Bytes, 0, null);
		}
		public PruneResult Prune(RepoBranches repo, IReadOnlyCollection<string>? branches) => new(new(), new(), true, null);
	}

	[Fact]
	public void AutomaticCleanup_TakesDeveloperLeftovers_OnlyWhileDeveloperModeIsOn_AsManorSays() {
		DateTime now = DateTime.UtcNow;
		var temp = new DevItem("tmp", "temp", "Old temp files", @"C:\x\tmp", new() { @"C:\x\tmp" }, 5 << 20, null, true, null, "");
		new DevReport { ScannedAtUtc = now, Build = AppBuild.Current, Categories = { new DevCategory("temp", "Temp", "", new() { temp }) } }.Save();
		var cfg = new AgentConfig { AutoClean = new AutoCleanConfig { Developer = true } };
		// Listed for nine days, with automatic cleanup on long before: due, the developer check having just run.
		List<string> Run(bool own) {
			var s = new AutoCleanState { DeveloperSinceUtc = now.AddDays(-60) };
			s.FirstSeenUtc["d:tmp"] = now.AddDays(-9);
			s.Save();
			cfg.DeveloperMode = own ? "on" : "off";
			var actions = new Recorder();
			AutoCleaner.RunAndSave(cfg, DevMode.Now(cfg), devChecked: true, actions);
			return actions.Cleaned;
		}

		Assert.Empty(Run(own: false)); // no Manor: Heiward's own switch
		Assert.Equal(new[] { "tmp" }, Run(own: true));
		InstallManor(Says("false"));
		Assert.Empty(Run(own: true));
		File.WriteAllText(ManorSettings, Says("true"));
		Assert.Equal(new[] { "tmp" }, Run(own: false));
		File.WriteAllText(ManorSettings, Silent);
		Assert.Empty(Run(own: false));
	}

	// ---- hei status --json, and hei dev

	[Fact]
	public void StatusJson_SaysWhetherDeveloperModeIsOn_AndWhoDecides() {
		static (bool On, string By) Status(AgentConfig cfg) {
			using var doc = JsonDocument.Parse(AgentStatus.Build(cfg, DateTime.UtcNow, pageUp: false, nextRun: null).ToJson());
			JsonElement root = doc.RootElement;
			// The last two, as the contract has it: only ever added to, at the end.
			Assert.Equal(new[] { "developerMode", "developerModeBy" }, root.EnumerateObject().TakeLast(2).Select(p => p.Name));
			return (root.GetProperty("developerMode").GetBoolean(), root.GetProperty("developerModeBy").GetString()!);
		}

		Assert.Equal((false, "heiward"), Status(Own(false)));
		Assert.Equal((true, "heiward"), Status(Own(true)));
		InstallManor(Says("true"));
		Assert.Equal((true, "manor"), Status(Own(false)));
		File.WriteAllText(ManorSettings, Says("false"));
		Assert.Equal((false, "manor"), Status(Own(true)));
		File.WriteAllText(ManorSettings, Silent);
		Assert.Equal((true, "heiward"), Status(Own(true)));
	}

	/// <summary>hei.exe from this build, run with its own HEIWARD_HOME and MANOR_HOME.</summary>
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
	public void HeiDev_FollowsManor_AndHeiwardsOwnSwitchWithout() {
		const string NoCheckYet = "No developer check yet: run 'hei dev --scan'.";
		Own(false).Save();
		Assert.Equal((1, "", DevMode.Of(Own(false), null).CommandOffText), Hei("dev"));
		InstallManor(Says("true"));
		Assert.Equal((0, NoCheckYet, ""), Hei("dev"));

		Own(true).Save();
		File.WriteAllText(ManorSettings, Says("false"));
		Assert.Equal((1, "", "Developer mode is off: The Hall's Developer options turn it off. Change it in The Hall: http://manor.localhost:19000/"), Hei("dev"));
		File.WriteAllText(ManorSettings, Silent);
		Assert.Equal((0, NoCheckYet, ""), Hei("dev"));
		Assert.Equal("on", AgentConfig.Load().DeveloperMode); // Manor never writes to Heiward's own
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

		/// <summary>A button's request, with the page's token.</summary>
		public async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, object body) {
			using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
			request.Headers.Add("X-Agent-Token", token);
			using HttpResponseMessage answer = await Http.SendAsync(request);
			return (answer.StatusCode, await answer.Content.ReadAsStringAsync());
		}

		/// <summary>The page's poll: its developer part.</summary>
		public async Task<(bool Enabled, string? Manor, string? Url, string? Note)> DevAsync() {
			using var state = JsonDocument.Parse(await Http.GetStringAsync("/api/state"));
			JsonElement dev = state.RootElement.GetProperty("dev");
			JsonElement m = dev.GetProperty("manor");
			return m.ValueKind == JsonValueKind.Null
				? (dev.GetProperty("enabled").GetBoolean(), null, null, null)
				: (dev.GetProperty("enabled").GetBoolean(), m.GetProperty("name").GetString(), m.GetProperty("url").GetString(), m.GetProperty("note").GetString());
		}

		/// <summary>The Developer area's own request: 200 with developer mode on, else 409 and why.</summary>
		public async Task<(HttpStatusCode Status, string? Error)> DeveloperAreaAsync() {
			using HttpResponseMessage answer = await Http.GetAsync("/api/dev");
			string body = await answer.Content.ReadAsStringAsync();
			return (answer.StatusCode, answer.IsSuccessStatusCode ? null : JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
		}

		public async ValueTask DisposeAsync() {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
			Http.Dispose();
		}
	}

	[Fact]
	public async Task ThePage_FollowsManor_PollByPoll_AndItsOwnSwitchWaits() {
		InstallManor(Says("true"));
		await using Page page = await Page.StartAsync(Own(false));

		Assert.Equal((true, "The Hall", "http://manor.localhost:19000/", "The Hall's Developer options turn this on"), await page.DevAsync());
		Assert.Equal((HttpStatusCode.OK, null), await page.DeveloperAreaAsync());

		// The switch waits while Manor decides, either way, and Heiward's own choice stays as it was.
		foreach (bool wanted in new[] { false, true }) {
			var (status, body) = await page.PostAsync("/api/settings", new { developerMode = wanted });
			Assert.Equal(HttpStatusCode.Conflict, status);
			Assert.Equal("The Hall's Developer options turn developer mode on, for every agent in the manor. Change it in The Hall.",
				JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
			Assert.Equal("off", AgentConfig.Load().DeveloperMode);
		}
		// The page's other settings are still its own.
		Assert.Equal(HttpStatusCode.OK, (await page.PostAsync("/api/settings", new { keepHistory = false })).Status);
		Assert.False(AgentConfig.Load().KeepHistory);

		File.WriteAllText(ManorSettings, Says("false"));
		Assert.Equal((false, "The Hall", "http://manor.localhost:19000/", "The Hall's Developer options turn this off"), await page.DevAsync());
		Assert.Equal((HttpStatusCode.Conflict, "Developer mode is off: The Hall's Developer options turn it off. Change it in The Hall."), await page.DeveloperAreaAsync());

		// Manor not saying: Heiward's own switch, which is off, and can be turned on again.
		File.WriteAllText(ManorSettings, Silent);
		Assert.Equal((false, null, null, null), await page.DevAsync());
		Assert.Equal((HttpStatusCode.Conflict, "Developer mode is off: turn it on in Settings."), await page.DeveloperAreaAsync());
		Assert.Equal(HttpStatusCode.OK, (await page.PostAsync("/api/settings", new { developerMode = true })).Status);
		Assert.Equal("on", AgentConfig.Load().DeveloperMode);
		Assert.Equal((true, null, null, null), await page.DevAsync());

		// Manor turning it off leaves Heiward's own on, for when Manor's gone.
		File.WriteAllText(ManorSettings, Says("false"));
		Assert.Equal((false, "The Hall", "http://manor.localhost:19000/", "The Hall's Developer options turn this off"), await page.DevAsync());
		Assert.Equal("on", AgentConfig.Load().DeveloperMode);
		Directory.Delete(Path.Combine(manor, "app"), true);
		Assert.Equal((true, null, null, null), await page.DevAsync());
		Assert.Equal((HttpStatusCode.OK, null), await page.DeveloperAreaAsync());
	}

	[Fact]
	public async Task Standalone_ThePagesOwnSwitch_WorksAsBefore() {
		await using Page page = await Page.StartAsync(Own(false));
		Assert.Equal((false, null, null, null), await page.DevAsync());
		Assert.Equal((HttpStatusCode.Conflict, "Developer mode is off: turn it on in Settings."), await page.DeveloperAreaAsync());

		var (status, body) = await page.PostAsync("/api/settings", new { developerMode = true });
		Assert.Equal(HttpStatusCode.OK, status);
		Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("developerMode").GetBoolean());
		Assert.Equal("on", AgentConfig.Load().DeveloperMode);
		Assert.Equal((true, null, null, null), await page.DevAsync());
		Assert.Equal((HttpStatusCode.OK, null), await page.DeveloperAreaAsync());

		Assert.Equal(HttpStatusCode.OK, (await page.PostAsync("/api/settings", new { developerMode = false })).Status);
		Assert.Equal("off", AgentConfig.Load().DeveloperMode);
		Assert.Equal((false, null, null, null), await page.DevAsync());
	}
}
