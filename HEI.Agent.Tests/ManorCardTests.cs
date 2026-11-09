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
/// About's Castellan card: shown without Manor, never with Manor installed. It was last on the home page, with a Not
/// now that hid it for good (settings.json's manorCard), until the owner moved it into About (2026-11-09): About is
/// opened on purpose, so it has no Not now and no setting. Nothing here reads the real Manor's settings or Heiward's,
/// and nothing asks Castellan's site. The card is launched (ManorCard.Launched): ShowBeforeLaunch is set anyway, so
/// these tests keep running should it be held back again, apart from those that check the shipped default.
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
		ManorCard.ShowBeforeLaunch = true; // the card as it'll be at launch; "As shipped" tests the shipped default
	}

	public void Dispose() {
		ManorCard.ShowBeforeLaunch = false;
		Environment.SetEnvironmentVariable("MANOR_HOME", manorHome);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	void InstallManor() {
		Directory.CreateDirectory(Path.Combine(manor, "app"));
		File.WriteAllText(Path.Combine(manor, "settings.json"), "{\"name\":\"The Hall\",\"port\":19000}");
	}

	static readonly Manor TheHall = new("The Hall", 19000, null);

	// ---- As shipped (ManorCard.Launched on): the card shows without Manor, linking to Castellan's site.

	[Fact]
	public void TheShippedDefault_ShowsTheCard_WithNoManorInstalled() {
		ManorCard.ShowBeforeLaunch = false;
		Assert.True(ManorCard.Launched);
		Assert.Equal(new ManorCard("https://castellan-software.com"), ManorCard.For(null));
		Assert.Null(ManorCard.For(TheHall));
	}

	[Fact]
	public async Task ThePage_ShowsTheCard_AsShipped() {
		ManorCard.ShowBeforeLaunch = false;
		await using Page page = await Page.StartAsync(new AgentConfig());
		Assert.Equal("https://castellan-software.com", await page.CardAsync());
	}

	// ---- What it says

	/// <summary>renderManorCard, from its first line to the next function.</summary>
	static string Render() {
		string script = Asset("app.js");
		int start = script.IndexOf("function renderManorCard(", StringComparison.Ordinal);
		Assert.True(start > 0);
		return script[start..script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal)];
	}

	[Fact]
	public void TheCard_NamesCastellan_AndSaysStaff() {
		string render = Render();
		Assert.Contains("Castellan is the house they share", render);
		Assert.Contains("See Castellan", render);
		Assert.Contains("Try all the staff free for 7 days, with no card.", render);
		Assert.Contains("Heiward stays free, and works the same without it.", render);
		Assert.DoesNotContain("agent", render, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Manor", render.Replace("renderManorCard", "").Replace("MANOR_STAFF", "").Replace("manor-card", ""));
		Assert.Contains("<h2 id=\"manor-card-title\">Castellan</h2>", Asset("index.html"));
	}

	[Fact]
	public void TheCard_GivesTheHouseholdPlansPrices_AndTheOfferOnlyUntilItEnds() {
		string render = Render();
		// castellan-software.com/pricing: the Household plan is $4 a month or $36 a year; 25% off ($3, $27) for whoever
		// subscribes by December 11, 2026.
		Assert.Contains("the Household plan is $4 a month, or $36 a year (3 months free).", render);
		Assert.Contains("Until December 11, 2026, it\\'s 25% off: $3 a month, or $27 a year.", render);
		Assert.Contains("Date.now() <= CASTELLAN_OFFER_ENDS.getTime()", render);
		Assert.Contains("const CASTELLAN_OFFER_ENDS = new Date(2026, 11, 11, 23, 59, 59);", Asset("app.js"));
		Assert.Contains("Stop paying and keep what you have", render);
	}

	// ---- Who sees it (as at launch)

	[Fact]
	public void WithoutManor_TheCardShows_WithTheSitesLink() =>
		Assert.Equal(new ManorCard(ManorCard.ManorSiteUrl), ManorCard.For(null));

	[Fact]
	public void WithManor_ThereIsNoCard() => Assert.Null(ManorCard.For(TheHall));

	[Fact]
	public void TheSite_IsOneHttpsAddress() {
		Assert.True(Uri.TryCreate(ManorCard.ManorSiteUrl, UriKind.Absolute, out Uri? site));
		Assert.Equal("https", site!.Scheme);
	}

	// ---- settings.json

	[Fact]
	public void AnOldNotNow_IsLeftBehind_AndTheCardStillShows() {
		// Settings from before, with the home page card's Not now in them: read as ever, the key gone on the next save.
		AgentConfig cfg = AgentConfig.FromJson("{\"keepHistory\": false, \"manorCard\": false}");
		Assert.False(cfg.KeepHistory);
		cfg.Save();
		Assert.DoesNotContain("manorCard", File.ReadAllText(AgentPaths.Config));
		Assert.NotNull(ManorCard.For(null));
	}

	// ---- The page

	static string Asset(string name) {
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/" + name)!;
		return new StreamReader(s).ReadToEnd();
	}

	[Fact]
	public void TheCard_IsInAbout_NotOnTheHomePage() {
		string page = Asset("index.html");
		int card = page.IndexOf("id=\"manor-card-section\"", StringComparison.Ordinal);
		int about = page.IndexOf("<main id=\"aboutview\"", StringComparison.Ordinal);
		Assert.True(about > 0 && card > about && card < page.IndexOf("</main>", about, StringComparison.Ordinal), "the card is in About");
		// After About Heiward, before Where its work runs, which is last as on every kit agent's About.
		Assert.True(page.IndexOf("id=\"about-card\"", StringComparison.Ordinal) < card);
		Assert.True(card < page.IndexOf("id=\"where-card\"", StringComparison.Ordinal));
		// Hidden until the page's poll says to show it, so it never shows with Manor installed.
		Assert.Contains("<section id=\"manor-card-section\" class=\"hidden\"", page);
		// About draws it; the home page no longer does.
		string script = Asset("app.js");
		int home = script.IndexOf("function renderHome(", StringComparison.Ordinal);
		Assert.DoesNotContain("renderManorCard", script[home..script.IndexOf("\nfunction kindRows(", home, StringComparison.Ordinal)]);
		int render = script.IndexOf("function renderAbout(", StringComparison.Ordinal);
		Assert.Contains("renderManorCard(s.manorCard);", script[render..script.IndexOf("\nconst outLink", render, StringComparison.Ordinal)]);
	}

	[Fact]
	public void TheCard_HasNoNotNow_AndSettingsNoSwitchForIt() {
		string render = Render();
		Assert.DoesNotContain("Not now", render);
		Assert.DoesNotContain("manorCard", render);
		string script = Asset("app.js");
		int start = script.IndexOf("function renderUpdatesCard(", StringComparison.Ordinal);
		string updates = script[start..script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal)];
		Assert.DoesNotContain("Castellan", updates);
		Assert.DoesNotContain("manorCard", updates);
	}

	[Fact]
	public void TheCard_IsTextInThePage_WithNothingFetched() {
		string script = Asset("app.js");
		string render = Render();
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

		/// <summary>The page's poll: its Castellan card's link, null when there's no card.</summary>
		public async Task<string?> CardAsync() {
			using var state = JsonDocument.Parse(await http.GetStringAsync("/api/state"));
			JsonElement card = state.RootElement.GetProperty("manorCard");
			return card.ValueKind == JsonValueKind.Null ? null : card.GetProperty("url").GetString();
		}

		public async ValueTask DisposeAsync() {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
			http.Dispose();
		}
	}

	[Fact]
	public async Task ThePage_ShowsTheCardWithoutManor_AndNeverWithManor() {
		await using Page page = await Page.StartAsync(new AgentConfig());
		Assert.Equal(ManorCard.ManorSiteUrl, await page.CardAsync());

		// An older page's Not now changes nothing now: there's no such setting.
		Assert.Equal(HttpStatusCode.OK, await page.PostAsync("/api/settings", new { manorCard = false }));
		Assert.DoesNotContain("manorCard", File.ReadAllText(AgentPaths.Config));
		Assert.Equal(ManorCard.ManorSiteUrl, await page.CardAsync());

		// With Manor installed: no card, from the next poll.
		InstallManor();
		Assert.Null(await page.CardAsync());

		// Manor gone: the card again.
		Directory.Delete(Path.Combine(manor, "app"), true);
		Assert.Equal(ManorCard.ManorSiteUrl, await page.CardAsync());
	}
}
