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
/// The home page's "In a manor" card: shown without Manor, until its Not now hides it for good (settings.json's
/// manorCard, which Settings turns on again); never with Manor installed. Nothing here reads the real Manor's settings or
/// Heiward's, and nothing asks Manor's site.
/// </summary>
[Collection(AgentHomeCollection.Name)] // MANOR_HOME and HEIWARD_HOME are process-wide
public sealed class ManorCardTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-manorcard-" + Guid.NewGuid().ToString("N"));
	readonly string? manorHome = Environment.GetEnvironmentVariable("MANOR_HOME");
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	readonly string home, manor;

	public ManorCardTests() {
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

	void InstallManor() {
		Directory.CreateDirectory(Path.Combine(manor, "app"));
		File.WriteAllText(Path.Combine(manor, "settings.json"), "{\"name\":\"The Hall\",\"port\":19000}");
	}

	static readonly Manor TheHall = new("The Hall", 19000, null);

	// ---- Who sees it

	[Fact]
	public void WithoutManor_TheCardShows_WithTheSitesLink() =>
		Assert.Equal(new ManorCard(true, ManorCard.ManorSiteUrl), ManorCard.For(new AgentConfig(), null));

	[Fact]
	public void WithManor_ThereIsNoCard_AndNoSwitch() {
		Assert.Null(ManorCard.For(new AgentConfig(), TheHall));
		Assert.Null(ManorCard.For(new AgentConfig { ManorCard = false }, TheHall));
	}

	[Fact]
	public void AfterNotNow_TheCardStaysHidden_ButItsSwitchIsThere() =>
		Assert.Equal(new ManorCard(false, ManorCard.ManorSiteUrl), ManorCard.For(new AgentConfig { ManorCard = false }, null));

	[Fact]
	public void TheSite_IsOneHttpsAddress() {
		Assert.True(Uri.TryCreate(ManorCard.ManorSiteUrl, UriKind.Absolute, out Uri? site));
		Assert.Equal("https", site!.Scheme);
	}

	// ---- settings.json

	[Fact]
	public void SettingsFromBefore_ShowTheCard() => Assert.True(AgentConfig.FromJson("{\"keepHistory\":true}").ManorCard);

	[Fact]
	public void NotNow_IsKeptInSettings() {
		new AgentConfig { ManorCard = false }.Save();
		Assert.Contains("\"manorCard\": false", File.ReadAllText(AgentPaths.Config));
		Assert.False(AgentConfig.Load().ManorCard);
	}

	// ---- The page

	static string Asset(string name) {
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/" + name)!;
		return new StreamReader(s).ReadToEnd();
	}

	[Fact]
	public void TheCard_IsLastOnTheHomePage_BelowTheDrives() {
		string page = Asset("index.html");
		int card = page.IndexOf("id=\"manor-card-section\"", StringComparison.Ordinal);
		Assert.True(card > 0);
		foreach (string before in new[] { "id=\"drives\"", "id=\"dev-section\"", "id=\"hotspots-section\"", "id=\"history\"" })
			Assert.True(page.IndexOf(before, StringComparison.Ordinal) is int at && at > 0 && at < card, before + " comes first");
		Assert.True(card < page.IndexOf("id=\"footer\"", StringComparison.Ordinal));
		// Hidden until the page's poll says to show it, so it never shows with Manor installed.
		Assert.Contains("<section id=\"manor-card-section\" class=\"hidden\"", page);
	}

	[Fact]
	public void TheCard_IsTextInThePage_WithNothingFetched() {
		string script = Asset("app.js");
		int start = script.IndexOf("function renderManorCard(", StringComparison.Ordinal);
		Assert.True(start > 0);
		string render = script[start..script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal)];
		Assert.DoesNotContain("fetch(", render);
		Assert.DoesNotContain("<img", render);
		// The site's address comes from the page's poll (ManorCard.ManorSiteUrl), never written into the page.
		Assert.DoesNotContain(ManorCard.ManorSiteUrl, script);
		Assert.DoesNotContain(ManorCard.ManorSiteUrl, Asset("index.html"));
	}

	/// <summary>The review page on a free port, with <paramref name="cfg"/> saved as settings.json, until disposed.</summary>
	sealed class Page : IAsyncDisposable {
		readonly int port;
		readonly Task<int> server;
		readonly string token;
		readonly HttpClient http;

		Page(int port, Task<int> server, HttpClient http, string token) => (this.port, this.server, this.http, this.token) = (port, server, http, token);

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

		public async Task<HttpStatusCode> PostAsync(string path, object body) {
			using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
			request.Headers.Add("X-Agent-Token", token);
			using HttpResponseMessage answer = await http.SendAsync(request);
			return answer.StatusCode;
		}

		/// <summary>The page's poll: its "In a manor" card, null when there's none.</summary>
		public async Task<(bool Show, string Url)?> CardAsync() {
			using var state = JsonDocument.Parse(await http.GetStringAsync("/api/state"));
			JsonElement card = state.RootElement.GetProperty("manorCard");
			return card.ValueKind == JsonValueKind.Null ? null : (card.GetProperty("show").GetBoolean(), card.GetProperty("url").GetString()!);
		}

		public async ValueTask DisposeAsync() {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
			http.Dispose();
		}
	}

	[Fact]
	public async Task ThePage_ShowsTheCardWithoutManor_HidesItAfterNotNow_AndNeverWithManor() {
		await using Page page = await Page.StartAsync(new AgentConfig());
		Assert.Equal((true, ManorCard.ManorSiteUrl), await page.CardAsync());

		// Not now: hidden, and kept in settings.json.
		Assert.Equal(HttpStatusCode.OK, await page.PostAsync("/api/settings", new { manorCard = false }));
		Assert.False(AgentConfig.Load().ManorCard);
		Assert.Equal((false, ManorCard.ManorSiteUrl), await page.CardAsync());

		// The page's other settings leave it as it is.
		Assert.Equal(HttpStatusCode.OK, await page.PostAsync("/api/settings", new { keepHistory = false }));
		Assert.False(AgentConfig.Load().ManorCard);

		// Settings' switch brings it back.
		Assert.Equal(HttpStatusCode.OK, await page.PostAsync("/api/settings", new { manorCard = true }));
		Assert.True(AgentConfig.Load().ManorCard);
		Assert.Equal((true, ManorCard.ManorSiteUrl), await page.CardAsync());

		// With Manor installed: no card and no switch, whatever the setting, from the next poll.
		InstallManor();
		Assert.Null(await page.CardAsync());
		Assert.True(AgentConfig.Load().ManorCard); // Manor never writes to Heiward's own

		// Manor gone: the card again, as the setting says.
		Directory.Delete(Path.Combine(manor, "app"), true);
		Assert.Equal((true, ManorCard.ManorSiteUrl), await page.CardAsync());
	}

	[Fact]
	public async Task ThePage_RemembersNotNow_FromOneStartToTheNext() {
		await using (Page first = await Page.StartAsync(new AgentConfig()))
			Assert.Equal(HttpStatusCode.OK, await first.PostAsync("/api/settings", new { manorCard = false }));
		await using Page again = await Page.StartAsync(AgentConfig.Load());
		Assert.Equal((false, ManorCard.ManorSiteUrl), await again.CardAsync());
	}
}
