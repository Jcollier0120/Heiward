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
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace HEI.Agent.Tests;

/// <summary>
/// The manor, with Manor installed: "Back to &lt;manor&gt;" first in the title bar, with Manor's icon (the Steward's kit
/// 2.3.0, kit/test/manor.test.ts), and Manor's theme for every page in the manor. Heiward reads Manor's settings.json on
/// each load of its page. Without Manor, the page is as it always was. Nothing here reads the real Manor's settings.
/// </summary>
[Collection(AgentHomeCollection.Name)] // MANOR_HOME and HEIWARD_HOME are process-wide
public sealed class ManorTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-manor-" + Guid.NewGuid().ToString("N"));
	readonly string? manorHome = Environment.GetEnvironmentVariable("MANOR_HOME");
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public ManorTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("MANOR_HOME", Path.Combine(dir, "no-manor"));
		ManorIcon.Forget();
	}

	public void Dispose() {
		ManorIcon.Forget();
		Environment.SetEnvironmentVariable("MANOR_HOME", manorHome);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>
	/// A Manor folder: settings.json with <paramref name="settings"/> (none when null), and an app folder when
	/// <paramref name="app"/> says so, with its own icon when <paramref name="art"/> is given.
	/// </summary>
	string ManorAt(string? settings, bool app = true, string name = "manor", string? art = null) {
		string folder = Path.Combine(dir, name);
		Directory.CreateDirectory(folder);
		if (settings != null) File.WriteAllText(Path.Combine(folder, "settings.json"), settings);
		if (app) Directory.CreateDirectory(Path.Combine(folder, "app", "art"));
		if (art != null) File.WriteAllText(Path.Combine(folder, "app", "art", "manor-icon.svg"), art);
		return folder;
	}

	static Manor Read(string json) => Manor.FromJson(json);

	// ---- Installed, or not

	[Fact]
	public void NoManorFolder_IsStandalone() => Assert.Null(Manor.Load(Path.Combine(dir, "nowhere")));

	[Fact]
	public void AnEmptyManorFolder_IsStandalone() => Assert.Null(Manor.Load(ManorAt(null, app: false)));

	[Fact]
	public void SettingsWithoutApp_IsStandalone() => Assert.Null(Manor.Load(ManorAt("{\"name\":\"Old\",\"theme\":\"onyx\"}", app: false)));

	[Fact]
	public void AppWithoutSettings_IsStandalone() => Assert.Null(Manor.Load(ManorAt(null, app: true)));

	[Fact]
	public void SettingsAndApp_IsManorInstalled() {
		Manor? manor = Manor.Load(ManorAt("{\"name\":\"Weasel Manor\",\"port\":18585,\"theme\":\"onyx\"}"));
		Assert.Equal(new Manor("Weasel Manor", 18585, "onyx"), manor);
		Assert.Equal("http://manor.localhost:18585/", manor!.Url);
	}

	[Fact]
	public void AnEmptyObject_IsManorsDefaults() => Assert.Equal(new Manor("Manor", 18585, null), Manor.Load(ManorAt("{}")));

	[Fact]
	public void AByteOrderMark_IsFine() {
		string folder = ManorAt(null);
		byte[] json = Encoding.UTF8.GetBytes("{ \"name\": \"The Hall\", \"port\": 19000, \"theme\": \"quest\" }");
		File.WriteAllBytes(Path.Combine(folder, "settings.json"), [0xEF, 0xBB, 0xBF, .. json]);
		Assert.Equal(new Manor("The Hall", 19000, "quest"), Manor.Load(folder));
		Assert.Equal("The Hall", Read("\uFEFF{\"name\":\"The Hall\"}").Name);
	}

	[Theory]
	[InlineData("")]
	[InlineData("{nope")]
	[InlineData("{\"theme\": \"onyx\"")] // cut short
	[InlineData("[\"onyx\"]")]
	[InlineData("\"onyx\"")]
	[InlineData("null")]
	[InlineData("42")]
	public void SettingsThatArentAnObject_LeaveManorInstalled_WithItsDefaults(string json) =>
		Assert.Equal(new Manor("Manor", 18585, null), Manor.Load(ManorAt(json)));

	[Fact]
	public void SettingsThatCantBeRead_LeaveManorInstalled_WithItsDefaults() {
		string folder = ManorAt("{\"name\":\"The Hall\"}");
		// Another program writing it holds it open with no sharing.
		using (new FileStream(Path.Combine(folder, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
			Assert.Equal(new Manor("Manor", 18585, null), Manor.Load(folder));
	}

	// ---- The name

	[Theory]
	[InlineData("{\"name\":\"The Hall\"}", "The Hall")]
	[InlineData("{\"name\":\"  The Hall \\t\"}", "The Hall")]
	[InlineData("{\"name\":\"\"}", "Manor")]
	[InlineData("{\"name\":\"   \"}", "Manor")]
	[InlineData("{\"name\":42}", "Manor")]
	[InlineData("{\"name\":null}", "Manor")]
	[InlineData("{\"name\":[\"The Hall\"]}", "Manor")]
	[InlineData("{}", "Manor")]
	public void TheName_IsTrimmed_AndManorWithoutOne(string json, string name) => Assert.Equal(name, Read(json).Name);

	[Fact]
	public void TheName_IsCutTo60Characters() {
		string sixty = new string('a', 59) + "b";
		Assert.Equal(sixty, Read($"{{\"name\":\"  {sixty}  \"}}").Name);
		Assert.Equal(sixty, Read($"{{\"name\":\"{sixty}cdefg\"}}").Name);
		// Never half of an emoji: the cut falls before it.
		Assert.Equal(new string('a', 59), Read($"{{\"name\":\"{new string('a', 59)}\\ud83c\\udff0 Castle\"}}").Name);
	}

	// ---- The port, in Manor's address

	[Theory]
	[InlineData("{\"port\":19000}", 19000)]
	[InlineData("{\"port\":1024}", 1024)]
	[InlineData("{\"port\":65535}", 65535)]
	[InlineData("{\"port\":19000.0}", 19000)] // a whole number, as JavaScript reads it
	[InlineData("{\"port\":80}", 18585)]
	[InlineData("{\"port\":1023}", 18585)]
	[InlineData("{\"port\":65536}", 18585)]
	[InlineData("{\"port\":0}", 18585)]
	[InlineData("{\"port\":-19000}", 18585)]
	[InlineData("{\"port\":19000.5}", 18585)]
	[InlineData("{\"port\":\"19000\"}", 18585)]
	[InlineData("{\"port\":null}", 18585)]
	[InlineData("{\"port\":1e100}", 18585)]
	[InlineData("{}", 18585)]
	public void ThePort_IsManorsOwn_OrItsDefault(string json, int port) {
		Assert.Equal(port, Read(json).Port);
		Assert.Equal($"http://manor.localhost:{port}/", Read(json).Url);
	}

	// ---- The theme

	[Theory]
	[InlineData("light")]
	[InlineData("dark")]
	[InlineData("arcade")]
	[InlineData("onyx")]
	[InlineData("carbon")]
	[InlineData("tinsel")]
	[InlineData("rosegold")]
	[InlineData("quest")]
	public void EveryThemeOfTheManor_IsTaken(string theme) => Assert.Equal(theme, Read($"{{\"theme\":\"{theme}\"}}").Theme);

	[Theory]
	[InlineData("{\"theme\":\"system\"}")] // Match Windows
	[InlineData("{\"theme\":\"\"}")]
	[InlineData("{\"theme\":\"Onyx\"}")]
	[InlineData("{\"theme\":\"neon\"}")]
	[InlineData("{\"theme\":\" onyx\"}")]
	[InlineData("{\"theme\":3}")]
	[InlineData("{\"theme\":true}")]
	[InlineData("{\"theme\":null}")]
	[InlineData("{\"theme\":[\"onyx\"]}")]
	[InlineData("{}")]
	public void SystemMissingOrUnknown_IsMatchWindows(string json) => Assert.Null(Read(json).Theme);

	[Fact]
	public void TheThemes_AreTheMenus_ButMatchWindows() {
		// theme.js's list, which app.css colours: Manor's choices are its names, "system" being no data-theme.
		var names = Regex.Matches(Asset("theme.js"), @"\{ name: '([a-z]+)'").Select(m => m.Groups[1].Value).ToList();
		Assert.Equal(new[] { "system" }.Concat(Manor.Themes), names);
	}

	// ---- Manor's folder

	[Fact]
	public void ManorsFolder_IsMANOR_HOME() {
		string folder = ManorAt("{\"name\":\"Elsewhere\",\"theme\":\"carbon\"}");
		Environment.SetEnvironmentVariable("MANOR_HOME", folder);
		Assert.Equal(folder, Manor.Folder);
		Assert.Equal(new Manor("Elsewhere", 18585, "carbon"), Manor.Load());

		Environment.SetEnvironmentVariable("MANOR_HOME", Path.Combine(dir, "nowhere"));
		Assert.Null(Manor.Load());
	}

	[Fact]
	public void MANOR_HOME_IsTakenAsAFullPath() {
		Environment.SetEnvironmentVariable("MANOR_HOME", Path.Combine("some", "manor"));
		Assert.Equal(Path.GetFullPath(Path.Combine("some", "manor")), Manor.Folder);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("  ")]
	public void WithoutMANOR_HOME_ManorsFolder_IsDotManor_InTheUsersProfile(string? set) {
		Environment.SetEnvironmentVariable("MANOR_HOME", set);
		Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".manor"), Manor.Folder);
	}

	// ---- The page, stamped

	const string Page = "<!doctype html>\n<html lang=\"en\">\n<head></head>\n<body>\n  <header class=\"titlebar\">\n    <a class=\"brand\">Heiward</a>\n  </header>\n" +
		"<p>&lt;html lang=\"en\"&gt;</p>\n</body>\n</html>\n";

	const string Back = "\n    <a class=\"manor-back\" href=\"http://manor.localhost:19000/\" title=\"Back to The Hall\"><img src=\"/manor-icon.svg\" alt=\"\" width=\"22\" height=\"22\">" +
		"<span>Back to The Hall</span></a><span class=\"manor-sep\" aria-hidden=\"true\"></span>";

	[Fact]
	public void Standalone_ThePageIsAsItIs() => Assert.Same(Page, Manor.Stamp(Page, null));

	[Fact]
	public void AtTheManor_TheHtmlTagHasItsTheme_AndTheTitleBarStartsWithTheWayBack() =>
		Assert.Equal(
			"<!doctype html>\n<html lang=\"en\" data-theme=\"onyx\" data-manor=\"The Hall\" data-manor-url=\"http://manor.localhost:19000/\">\n<head></head>\n<body>\n" +
			"  <header class=\"titlebar\">" + Back + "\n    <a class=\"brand\">Heiward</a>\n  </header>\n<p>&lt;html lang=\"en\"&gt;</p>\n</body>\n</html>\n",
			Manor.Stamp(Page, new Manor("The Hall", 19000, "onyx")));

	[Fact]
	public void MatchWindows_HasNoDataTheme_ButIsStillManors() {
		string page = Manor.Stamp(Page, new Manor("The Hall", 19000, null));
		Assert.Contains("<html lang=\"en\" data-manor=\"The Hall\" data-manor-url=\"http://manor.localhost:19000/\">", page);
		Assert.Contains("<header class=\"titlebar\">" + Back, page);
		Assert.DoesNotContain("data-theme", page);
	}

	[Fact]
	public void ManorsName_IsEscaped() {
		string page = Manor.Stamp(Page, Read("{\"name\":\"Tom & Jerry's \\\"<Hall>\\\"\"}"));
		const string name = "Tom &amp; Jerry&#39;s &quot;&lt;Hall&gt;&quot;";
		Assert.Contains($"<html lang=\"en\" data-manor=\"{name}\" data-manor-url=\"http://manor.localhost:18585/\">", page);
		Assert.Contains($"<a class=\"manor-back\" href=\"http://manor.localhost:18585/\" title=\"Back to {name}\">", page);
		Assert.Contains($"<span>Back to {name}</span>", page);
		Assert.DoesNotContain("<Hall>", page);
		Assert.Equal("&amp;&lt;&gt;&quot;&#39;Hall \U0001F3F0 &amp;#39;", Manor.Attribute("&<>\"'Hall \U0001F3F0 &#39;"));
	}

	[Fact]
	public void OnlyTheFirstOfEachTag_IsStamped() {
		string page = Manor.Stamp("<html lang=\"en\"><header class=\"titlebar\"><html lang=\"en\"><header class=\"titlebar\">", new Manor("The Hall", 19000, "dark"));
		Assert.Equal("<html lang=\"en\" data-theme=\"dark\" data-manor=\"The Hall\" data-manor-url=\"http://manor.localhost:19000/\"><header class=\"titlebar\">" + Back +
			"<html lang=\"en\"><header class=\"titlebar\">", page);
	}

	[Fact]
	public void APageWithoutTheTags_IsAsItIs() => Assert.Equal("<html><header>", Manor.Stamp("<html><header>", new Manor("Manor", 18585, "dark")));

	static string Asset(string name) {
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/" + name)!;
		return new StreamReader(s).ReadToEnd();
	}

	[Fact]
	public void TheReviewPage_IsStamped_AndKeepsItsToken() {
		string page = Manor.Stamp(Asset("index.html").Replace("__AGENT_TOKEN__", "0123456789abcdef"), new Manor("Weasel Manor", 18600, "rosegold"));
		Assert.Contains("<html lang=\"en\" data-theme=\"rosegold\" data-manor=\"Weasel Manor\" data-manor-url=\"http://manor.localhost:18600/\">", page);
		// As the kit's pages have it (kit/test/manor.test.ts): the way back first in the title bar.
		Assert.Matches(new Regex("<header class=\"titlebar[^\"]*\"[^>]*>\\s*<a class=\"manor-back\" href=\"http://manor\\.localhost:18600/\" title=\"Back to Weasel Manor\">" +
			"<img src=\"/manor-icon\\.svg\" alt=\"\" width=\"22\" height=\"22\"><span>Back to Weasel Manor</span></a><span class=\"manor-sep\" aria-hidden=\"true\"></span>\\s*<a class=\"brand\""), page);
		Assert.Equal("0123456789abcdef", ReviewServer.TokenIn(page));
	}

	// ---- Manor's icon

	/// <summary>A port nothing listens on.</summary>
	static int ClosedPort() {
		using var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		return ((IPEndPoint)probe.LocalEndpoint).Port;
	}

	/// <summary>Manor's page, as far as its icon goes: <paramref name="body"/> for /favicon.svg, 404 for the rest; how many asked.</summary>
	sealed class ManorPage : IDisposable {
		readonly TcpListener listener = new(IPAddress.Loopback, 0);
		readonly byte[] body;
		int asked;

		public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
		public int Asked => Volatile.Read(ref asked);

		public ManorPage(string body) {
			this.body = Encoding.UTF8.GetBytes(body);
			listener.Start();
			_ = Task.Run(Serve);
		}

		async Task Serve() {
			while (true) {
				TcpClient client;
				try { client = await listener.AcceptTcpClientAsync(); }
				catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException) { return; }
				using (client) {
					try {
						NetworkStream stream = client.GetStream();
						var request = new StringBuilder();
						var buffer = new byte[4096];
						while (!request.ToString().Contains("\r\n\r\n")) {
							int n = await stream.ReadAsync(buffer);
							if (n == 0) break;
							request.Append(Encoding.ASCII.GetString(buffer, 0, n));
						}
						Interlocked.Increment(ref asked);
						bool icon = request.ToString().StartsWith("GET /favicon.svg ", StringComparison.Ordinal);
						byte[] content = icon ? body : Encoding.ASCII.GetBytes("not found");
						string head = $"HTTP/1.1 {(icon ? "200 OK" : "404 Not Found")}\r\nContent-Type: image/svg+xml\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n";
						await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
						await stream.WriteAsync(content);
					}
					catch (IOException) { }
				}
			}
		}

		public void Dispose() => listener.Stop();
	}

	const string Live = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><title>spotter</title></svg>";
	const string Own = "<svg><title>own</title></svg>";

	[Fact]
	public async Task ManorsIcon_IsTheOneItsPageShows() {
		using var page = new ManorPage(Live);
		Assert.Equal(Live, await ManorIcon.GetAsync(ManorAt($"{{\"port\":{page.Port}}}", art: Own), DateTime.UtcNow));
	}

	[Fact]
	public async Task ManorsIcon_IsKeptForTenMinutes_ForItsPort() {
		DateTime now = DateTime.UtcNow;
		string moved = Live.Replace("spotter", "moved");
		using var elsewhere = new ManorPage(moved);
		string folder;
		using (var page = new ManorPage(Live)) {
			folder = ManorAt($"{{\"port\":{page.Port}}}", art: Own);
			Assert.Equal(Live, await ManorIcon.GetAsync(folder, now));
			Assert.Equal(Live, await ManorIcon.GetAsync(folder, now.AddMinutes(9)));
			Assert.Equal(1, page.Asked);
		}
		// Manor's page gone: still the icon it gave, until ten minutes are up; then Manor's own art.
		Assert.Equal(Live, await ManorIcon.GetAsync(folder, now.AddMinutes(9.9)));
		Assert.Equal(Own, await ManorIcon.GetAsync(folder, now.AddMinutes(10)));
		// Kept for its port only: Manor on another port is asked again.
		Assert.Equal(Live, await ManorIcon.GetAsync(folder, now.AddMinutes(10).AddSeconds(-1)));
		File.WriteAllText(Path.Combine(folder, "settings.json"), $"{{\"port\":{elsewhere.Port}}}");
		Assert.Equal(moved, await ManorIcon.GetAsync(folder, now.AddMinutes(1)));
		Assert.Equal(1, elsewhere.Asked);
	}

	[Fact]
	public async Task ManorDown_ItsOwnArt_ElseAHouse() {
		int closed = ClosedPort();
		Assert.Equal(Own, await ManorIcon.GetAsync(ManorAt($"{{\"port\":{closed}}}", name: "down", art: Own), DateTime.UtcNow));
		Assert.Equal(ManorIcon.HouseSvg, await ManorIcon.GetAsync(ManorAt($"{{\"port\":{closed}}}", name: "no-art"), DateTime.UtcNow));
		Assert.Equal(ManorIcon.HouseSvg, await ManorIcon.GetAsync(Path.Combine(dir, "nowhere"), DateTime.UtcNow));
		Assert.Equal(ManorIcon.HouseSvg, await ManorIcon.GetAsync(ManorAt("{}", app: false, name: "uninstalled"), DateTime.UtcNow));
	}

	[Theory]
	[InlineData("<svg onload=\"alert(1)\"/>")]
	[InlineData("<svg><script>x</script></svg>")]
	[InlineData("<html><svg/></html>")]
	public async Task AnIconThatRuns_IsNeverServed(string svg) {
		using var page = new ManorPage(svg);
		Assert.Equal(Own, await ManorIcon.GetAsync(ManorAt($"{{\"port\":{page.Port}}}", name: "bad-page", art: Own), DateTime.UtcNow));
		Assert.Equal(ManorIcon.HouseSvg, await ManorIcon.GetAsync(ManorAt($"{{\"port\":{page.Port}}}", name: "bad-art", art: svg), DateTime.UtcNow));
	}

	[Fact]
	public async Task AnIconOver512KB_IsNotTaken() {
		using var page = new ManorPage("<svg xmlns=\"http://www.w3.org/2000/svg\"><title>" + new string('x', ManorIcon.MaxBytes) + "</title></svg>");
		Assert.Equal(Own, await ManorIcon.GetAsync(ManorAt($"{{\"port\":{page.Port}}}", art: Own), DateTime.UtcNow));
	}

	[Theory]
	[InlineData("<svg onload=\"alert(1)\"/>", false)]
	[InlineData("<svg><script>x</script></svg>", false)]
	[InlineData("<svg><SCRIPT>x</SCRIPT></svg>", false)]
	[InlineData("<svg><a href=\"javascript:alert(1)\"/></svg>", false)]
	[InlineData("<svg><foreignObject><p>x</p></foreignObject></svg>", false)]
	[InlineData("<svg><g onclick = \"x\"/></svg>", false)]
	[InlineData("<html><svg/></html>", false)]
	[InlineData("svg", false)]
	[InlineData("", false)]
	[InlineData("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"/>", true)]
	[InlineData("<!-- Manor -->\n<svg viewBox=\"0 0 16 16\"><style>path{fill:#5f5f5f}</style><path d=\"M0 0h1\"/></svg>", true)]
	[InlineData("  <svg>\n</svg>", true)]
	public void SafeSvg_IsAnSvg_WithNothingThatRuns(string svg, bool safe) => Assert.Equal(safe, ManorIcon.IsSafe(svg));

	[Fact]
	public void TheHouse_IsSafe() => Assert.True(ManorIcon.IsSafe(ManorIcon.HouseSvg));

	// ---- As served

	/// <summary>The page Heiward serves follows Manor's settings.json from one load to the next, and is its own again once Manor's gone.</summary>
	[Fact]
	public async Task TheServedPage_FollowsManor_LoadByLoad() {
		string home = Path.Combine(dir, "heiward");
		Directory.CreateDirectory(home);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		using var manorsPage = new ManorPage(Live);
		string manor = ManorAt($"{{\"name\":\"The Hall\",\"port\":{manorsPage.Port},\"theme\":\"arcade\"}}");
		Environment.SetEnvironmentVariable("MANOR_HOME", manor);
		int port = ClosedPort();
		// Scans only on Scan now: the page starts none of its own.
		Task<int> server = ReviewServer.RunAsync(new AgentConfig { Port = port, ScanEveryMinutes = 0 }, openBrowser: false, CancellationToken.None);
		try {
			for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(port); i++) await Task.Delay(250);
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
			async Task<(string Body, string Policy)> Load(string path) {
				using HttpResponseMessage answer = await http.GetAsync($"http://127.0.0.1:{port}{path}");
				Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
				Assert.Equal("no-cache", answer.Headers.CacheControl?.ToString());
				return (await answer.Content.ReadAsStringAsync(), answer.Headers.GetValues("Content-Security-Policy").Single());
			}

			var (first, policy) = await Load("/");
			Assert.Contains("script-src 'self';", policy); // nothing inline
			Assert.Contains($"<html lang=\"en\" data-theme=\"arcade\" data-manor=\"The Hall\" data-manor-url=\"http://manor.localhost:{manorsPage.Port}/\">", first);
			Assert.Contains($"<a class=\"manor-back\" href=\"http://manor.localhost:{manorsPage.Port}/\" title=\"Back to The Hall\">", first);
			var (icon, iconPolicy) = await Load("/manor-icon.svg");
			Assert.Equal(Live, icon);
			Assert.StartsWith("default-src 'none';", iconPolicy);

			File.WriteAllText(Path.Combine(manor, "settings.json"), "{\"name\":\"The Hall\",\"port\":19000,\"theme\":\"system\"}");
			var (second, _) = await Load("/");
			Assert.Contains("<html lang=\"en\" data-manor=\"The Hall\" data-manor-url=\"http://manor.localhost:19000/\">", second);
			Assert.Contains("<a class=\"manor-back\" href=\"http://manor.localhost:19000/\"", second);

			Directory.Delete(Path.Combine(manor, "app"), true);
			var (standalone, _) = await Load("/");
			Assert.Equal(Asset("index.html").Replace("__AGENT_TOKEN__", ReviewServer.TokenIn(standalone)), standalone);
		}
		finally {
			await ReviewServer.AskToCloseAsync(port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
		}
	}
}
