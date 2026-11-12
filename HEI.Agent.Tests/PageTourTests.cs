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
using Acornima;
using Acornima.Ast;
using Jint;

namespace HEI.Agent.Tests;

/// <summary>
/// The page's tour opens by itself the first time the page is opened after a new install, from the Store or GitHub, and
/// not again once it's done or skipped, nor after an update or a restart; installing again after an uninstall shows it
/// again. Heiward decides (<see cref="PageTour"/>), not the browser: the page kept "heiward.toured" in Edge's profile,
/// which an uninstall leaves as it was, so a PC that had Heiward before never saw the tour after a fresh install (the
/// owner, 2026-11-12: "I didn't get any kind of onboarding for Heiward from the store even though I did a fresh install").
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME and MANOR_HOME are process-wide
public sealed class PageTourTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-tour-" + Guid.NewGuid().ToString("N"));
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	readonly string? manorHome = Environment.GetEnvironmentVariable("MANOR_HOME");

	public PageTourTests() {
		Directory.CreateDirectory(Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("MANOR_HOME", Path.Combine(dir, "manor")); // no Manor
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		Environment.SetEnvironmentVariable("MANOR_HOME", manorHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>What the install does about the tour (Installer.InstallAsync): decides from what it replaces, then says so.</summary>
	static void Install(string? replaces, bool firstScanWaits) {
		if (PageTour.ForInstall(replaces, firstScanWaits)) PageTour.Begin();
	}

	// ---- What the install decides

	[Fact]
	public void ANewInstall_OpensTheTour() {
		// The Store version's setup, or GitHub's, on a PC with no copy installed: "Installed", its first scan waiting.
		Assert.True(PageTour.ForInstall(replaces: null, firstScanWaits: true));
		// One whose first scan doesn't wait (a report kept from a copy uninstalled before): a new install all the same.
		Assert.True(PageTour.ForInstall(replaces: null, firstScanWaits: false));
	}

	[Theory]
	[InlineData("1.10.3")] // "Updated from 1.10.3 to ..."
	[InlineData("1.10.8")] // "Reinstalled 1.10.8": over the copy that's there
	[InlineData("")] // the self-update's, and the Store version's when it was set up before: the version isn't known
	public void AnUpdate_DoesNotOpenItAgain(string replaces) =>
		Assert.False(PageTour.ForInstall(replaces, firstScanWaits: false));

	[Fact]
	public void ACopyThatNeverScanned_OpensIt_AsTheSetupsDoneSays() =>
		// The setup's Done says the tour asks whether to scan now: so it shows while the first scan waits.
		Assert.True(PageTour.ForInstall("1.10.3", firstScanWaits: true));

	[Fact]
	public void TheInstall_SaysSo_BeforeTheStoreVersionsSetupEnds() {
		string installer = File.ReadAllText(Path.Combine(RepoRoot(), "HEI.Agent", "Installer.cs")).ReplaceLineEndings("\n");
		int decides = installer.IndexOf("bool tourDue = PageTour.ForInstall(replaces, firstScanWaits);", StringComparison.Ordinal);
		int says = installer.IndexOf("if (tourDue) PageTour.Begin();", StringComparison.Ordinal);
		int setUp = installer.IndexOf("AgentPaths.WriteAtomic(AgentPaths.StoreSetUp,", StringComparison.Ordinal);
		Assert.True(decides > 0 && says > decides, "the install decides, then writes it");
		// setup.txt ends the Store version's setup: the page's poll that sees it done sees the tour due too.
		Assert.True(setUp > says, "the tour is due before the Store version's setup is done");
		// Read before this install records its own version (Apps & Features, setup.txt).
		Assert.True(installer.IndexOf("string? replaces = Replaces()", StringComparison.Ordinal) is int replaces && replaces > 0 && replaces < decides);
	}

	// ---- What the page is told, and what it writes

	[Fact]
	public async Task ThePage_OpensItOnce_ThenNotAfterARestart_OrAnUpdate_ButAfterInstallingAgain() {
		Assert.False(PageTour.Due); // a development build's page, or a copy from before: no install said so

		// A fresh install.
		Install(replaces: null, firstScanWaits: true);
		await using (Page page = await Page.StartAsync()) {
			Assert.True(await page.TourDueAsync());
			// Asking again, as every poll and page load does, writes nothing.
			DateTime written = File.GetLastWriteTimeUtc(AgentPaths.TourDue);
			Assert.True(await page.TourDueAsync());
			await page.Http.GetStringAsync("/");
			Assert.Equal(written, File.GetLastWriteTimeUtc(AgentPaths.TourDue));
		}

		// Restarted before the tour was done or skipped (its window closed): it opens the next time.
		await using (Page page = await Page.StartAsync()) {
			Assert.True(await page.TourDueAsync());
			// Done, or skipped (Skip, Esc, Later): the page says so once.
			Assert.Equal(HttpStatusCode.OK, await page.PostAsync("/api/tour/done"));
			Assert.False(await page.TourDueAsync());
			Assert.False(File.Exists(AgentPaths.TourDue));
			// #/tour again, or Manor's #/tour?from=: its end changes nothing, and writes nothing.
			Assert.Equal(HttpStatusCode.OK, await page.PostAsync("/api/tour/done"));
			Assert.False(await page.TourDueAsync());
			Assert.False(File.Exists(AgentPaths.TourDue));
		}

		// A restart, then an update: neither opens it again.
		await using (Page page = await Page.StartAsync()) {
			Assert.False(await page.TourDueAsync());
			Install(replaces: "1.10.8", firstScanWaits: false);
			Assert.False(await page.TourDueAsync());

			// Uninstalled (keeping the settings and the report, so no first scan waits), then installed again: no copy
			// to replace, since the uninstall removed Apps & Features' entry, or Windows the Store version's setup.txt.
			Install(replaces: null, firstScanWaits: false);
			Assert.True(await page.TourDueAsync());
		}
	}

	[Fact]
	public async Task ThePage_RefusesTheEnd_WithoutItsToken() {
		Install(replaces: null, firstScanWaits: true);
		await using Page page = await Page.StartAsync();
		using HttpResponseMessage answer = await page.Http.PostAsJsonAsync("/api/tour/done", new { });
		Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
		Assert.True(await page.TourDueAsync()); // another site can't make it skip the tour
	}

	// ---- The page's own script (tour.js, as hei.exe carries it)

	[Fact]
	public void TheScript_AsksHeiward_NotTheBrowser() {
		string tour = ThePage.Asset("tour.js");
		var names = AllNodes(new Parser().ParseScript(tour)).OfType<Identifier>().Select(i => i.Name).ToHashSet();
		Assert.DoesNotContain("localStorage", names);
		Assert.DoesNotContain("sessionStorage", names);
		Assert.DoesNotContain("heiward.toured", tour);
	}

	[Fact]
	public void ANewInstall_OpensItByItself_OnceTheSetupIsDone() {
		var js = Script();
		js.Execute("state = { tourDue: true }; setup = true;");
		Assert.False(js.Evaluate("tourOnLoad()").AsBoolean()); // the setup (or its Done, until Open Heiward): it waits
		Assert.Equal("[]", Started(js));
		js.Execute("setup = false;"); // Open Heiward, or the next start from the Start menu
		Assert.True(js.Evaluate("tourOnLoad()").AsBoolean());
		Assert.Equal("[null]", Started(js));
	}

	[Fact]
	public void Done_OrSkipped_ItDoesNotOpenAgain() {
		var js = Script();
		js.Execute("state = { tourDue: true }; markToured();");
		Assert.Equal("[\"/api/tour/done\"]", js.Evaluate("JSON.stringify(posted)").AsString());
		// Its next end (#/tour again) writes nothing more.
		js.Execute("markToured();");
		Assert.Equal(1, (int)js.Evaluate("posted.length").AsNumber());
		// The page opened again, after a restart or an update, the state saying it isn't due: no tour.
		js.Execute("state = { tourDue: false };");
		Assert.True(js.Evaluate("tourOnLoad()").AsBoolean());
		Assert.Equal("[]", Started(js));
	}

	[Theory]
	[InlineData("#/tour", null)]
	[InlineData("#/tour?from=http://localhost:19000/", "http://localhost:19000/")]
	public void TheTour_StillOpensAtItsAddress_DueOrNot(string hash, string? from) {
		var js = Script();
		js.Execute($"state = {{ tourDue: false }}; location.hash = {JsonSerializer.Serialize(hash)};");
		Assert.True(js.Evaluate("tourOnLoad()").AsBoolean());
		Assert.Equal(JsonSerializer.Serialize(new[] { from }), Started(js));
	}

	/// <summary>tour.js's decisions, against just enough of the page: its state, the setup, the address and startTour().</summary>
	static Engine Script() {
		string tour = ThePage.Asset("tour.js");
		var js = new Engine();
		js.Execute("""
			var state = null, setup = false, started = [], posted = [];
			var location = { hash: '' };
			function setupShowing(s) { return setup; }
			function uninstallShowing() { return false; }
			function startTour(from) { started.push(from); } // from: the page #/tour?from= names, else null
			function tourFromHash(hash) { var q = hash.indexOf('?from='); return q < 0 ? null : hash.slice(q + 6); }
			function post(url) { posted.push(url); return { catch: function () { } }; }
			function setInterval() { return 1; }
			function clearInterval() { }
			""");
		foreach (string name in new[] { "tourDue", "markToured", "isTourHash", "tourOnLoad" })
			js.Execute(ThePage.Function(tour, name));
		return js;
	}

	static string Started(Engine js) => js.Evaluate("JSON.stringify(started)").AsString();

	static IEnumerable<Node> AllNodes(Node node) {
		var todo = new Stack<Node>();
		todo.Push(node);
		while (todo.Count > 0) {
			Node n = todo.Pop();
			yield return n;
			foreach (Node child in n.ChildNodes) todo.Push(child);
		}
	}

	static string RepoRoot() {
		string? at = AppContext.BaseDirectory;
		while (at != null && !File.Exists(Path.Combine(at, "HEI.Agent", "Installer.cs"))) at = Path.GetDirectoryName(at);
		Assert.NotNull(at);
		return at!;
	}

	/// <summary>The review page on a free port, scanning nothing of its own, until disposed.</summary>
	sealed class Page : IAsyncDisposable {
		readonly int port;
		readonly Task<int> server;
		readonly string token;
		public readonly HttpClient Http;

		Page(int port, Task<int> server, HttpClient http, string token) => (this.port, this.server, Http, this.token) = (port, server, http, token);

		public static async Task<Page> StartAsync() {
			var cfg = new AgentConfig();
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

		public async Task<HttpStatusCode> PostAsync(string path) {
			using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) };
			request.Headers.Add("X-Agent-Token", token);
			using HttpResponseMessage answer = await Http.SendAsync(request);
			return answer.StatusCode;
		}

		public async Task<bool> TourDueAsync() {
			using var state = JsonDocument.Parse(await Http.GetStringAsync("/api/state"));
			return state.RootElement.GetProperty("tourDue").GetBoolean();
		}

		public async ValueTask DisposeAsync() {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
			Http.Dispose();
		}
	}
}
