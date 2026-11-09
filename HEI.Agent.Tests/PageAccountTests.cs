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
using System.Text.Json;

namespace HEI.Agent.Tests;

/// <summary>
/// On a PC where more than one account signs in, each account's page answers only that account's programs: another
/// account's are refused, the page included, and its ping says whose page it is.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME is process-wide
public sealed class PageAccountTests : IDisposable {
	const string AnotherAccount = "S-1-5-21-1111111111-2222222222-3333333333-1001";
	readonly string dir = RealTemp.Folder("hei-account-");
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	readonly string? manorHome = Environment.GetEnvironmentVariable("MANOR_HOME");

	public PageAccountTests() {
		Directory.CreateDirectory(Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("MANOR_HOME", Path.Combine(dir, "manor")); // no Manor
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		Environment.SetEnvironmentVariable("MANOR_HOME", manorHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	static int FreePort() {
		using var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		return ((IPEndPoint)probe.LocalEndpoint).Port;
	}

	static AgentConfig Config() {
		var cfg = new AgentConfig { Port = FreePort(), ScanEveryMinutes = 0 }; // the page starts no scan of its own
		cfg.Save();
		return cfg;
	}

	static async Task Listening(int port) {
		for (int i = 0; i < 80; i++) {
			try {
				using var tcp = new TcpClient();
				await tcp.ConnectAsync(IPAddress.Loopback, port);
				return;
			}
			catch (SocketException) { await Task.Delay(250); }
		}
		Assert.Fail($"nothing listens on port {port}");
	}

	[Theory]
	[InlineData(AnotherAccount)]
	[InlineData(null)] // Windows wouldn't say whose program it is: refused all the same
	public async Task AnotherAccountsProgram_IsRefused_ThePageIncluded(string? sid) {
		AgentConfig cfg = Config();
		using var stop = new CancellationTokenSource();
		Task<int> server = ReviewServer.RunAsync(cfg, openBrowser: false, stop.Token, caller: (_, _) => (4242, sid));
		try {
			await Listening(cfg.Port);
			using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{cfg.Port}/"), Timeout = TimeSpan.FromSeconds(30) };
			foreach (string path in new[] { "/", "/api/ping", "/api/state", "/api/glance" }) {
				using HttpResponseMessage answer = await http.GetAsync(path);
				Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
				Assert.Equal("no-store", answer.Headers.CacheControl?.ToString());
				string body = await answer.Content.ReadAsStringAsync();
				Assert.Equal(ReviewServer.AnotherAccountsPage, body);
				Assert.Null(ReviewServer.TokenIn(body));
			}
			using var quit = new HttpRequestMessage(HttpMethod.Post, "/api/quit");
			quit.Headers.Add("X-Agent-Token", "guessed");
			using (HttpResponseMessage answer = await http.SendAsync(quit)) Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
			Assert.False(server.IsCompleted);
			// This account's own starters don't take another account's page for theirs.
			Assert.False(await ReviewServer.IsUpAsync(cfg.Port));
			Assert.Null(await ReviewServer.PageExeAsync(cfg.Port));
			Assert.False(await ReviewServer.AskToCloseAsync(cfg.Port));
		}
		finally {
			stop.Cancel();
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
		}
	}

	[Fact]
	public async Task ThisAccountsPrograms_GetThePage_AndItsPingSaysWhoseItIs() {
		AgentConfig cfg = Config();
		Task<int> server = ReviewServer.RunAsync(cfg, openBrowser: false, CancellationToken.None); // the real lookup: this test is this account's
		try {
			for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(cfg.Port); i++) await Task.Delay(250);
			Assert.True(await ReviewServer.IsUpAsync(cfg.Port));
			using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{cfg.Port}/"), Timeout = TimeSpan.FromSeconds(30) };
			using (HttpResponseMessage page = await http.GetAsync("/")) {
				Assert.Equal(HttpStatusCode.OK, page.StatusCode);
				Assert.NotNull(ReviewServer.TokenIn(await page.Content.ReadAsStringAsync()));
			}
			using var ping = JsonDocument.Parse(await http.GetStringAsync("/api/ping"));
			Assert.Equal(PageAccount.Mine, ping.RootElement.GetProperty("account").GetString());
			Assert.True(PageAccount.ListenedByThisAccount(cfg.Port));
			Assert.False(PageAccount.HeldByAnother(cfg.Port));

			// Another program of this account, as the browser is: told by its process, not this one's.
			string curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
			if (File.Exists(curl)) {
				var psi = new System.Diagnostics.ProcessStartInfo(curl) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
				foreach (string a in new[] { "-s", "-o", "NUL", "-w", "%{http_code}", $"http://127.0.0.1:{cfg.Port}/" }) psi.ArgumentList.Add(a);
				using var p = System.Diagnostics.Process.Start(psi)!;
				string status = await p.StandardOutput.ReadToEndAsync();
				await p.WaitForExitAsync();
				Assert.Equal("200", status);
			}
		}
		finally {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(cfg.Port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
		}
	}

	[Fact]
	public async Task TheCaller_IsFoundFromItsPort_AsThisAccountsProcess() {
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		using var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
		using TcpClient accepted = await listener.AcceptTcpClientAsync();
		var remote = (IPEndPoint)accepted.Client.RemoteEndPoint!;
		var local = (IPEndPoint)accepted.Client.LocalEndPoint!;

		(int? pid, string? sid) = PageAccount.Caller(remote.Address, remote.Port, local.Address, local.Port);
		Assert.Equal(Environment.ProcessId, pid);
		Assert.True(PageAccount.IsThisAccount(sid));
		// A port no one connects from: no process, and so no account.
		(int? nobody, string? nobodys) = PageAccount.Caller(remote.Address, FreePort(), local.Address, local.Port);
		Assert.Null(nobody);
		Assert.Null(nobodys);
	}

	[Fact]
	public void OnlyThisAccountsSid_IsThisAccount() {
		Assert.NotEqual("", PageAccount.Sid);
		Assert.True(PageAccount.IsThisAccount(PageAccount.Sid));
		Assert.True(PageAccount.IsThisAccount(PageAccount.Sid.ToLowerInvariant()));
		Assert.False(PageAccount.IsThisAccount(AnotherAccount));
		Assert.False(PageAccount.IsThisAccount("S-1-5-18")); // SYSTEM
		Assert.False(PageAccount.IsThisAccount(null));
		Assert.False(PageAccount.IsThisAccount(""));
		Assert.Equal(PageAccount.Sid, PageAccount.OfProcess(Environment.ProcessId));
	}

	[Fact]
	public void TheAccountsHash_SaysWhoseNotWho() {
		Assert.Matches("^[0-9a-f]{16}$", PageAccount.Mine);
		Assert.Equal(PageAccount.Hash(AnotherAccount), PageAccount.Hash(AnotherAccount.ToLowerInvariant()));
		Assert.NotEqual(PageAccount.Mine, PageAccount.Hash(AnotherAccount));
		Assert.DoesNotContain(PageAccount.Sid, PageAccount.Mine, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void APing_IsThisAccountsPage_OnlyWhenItSaysSo() {
		string Ping(string? account) => JsonSerializer.Serialize(account == null
			? new Dictionary<string, object> { ["app"] = "heiward", ["exe"] = @"C:\Heiward\Heiward.exe", ["store"] = true }
			: new Dictionary<string, object> { ["app"] = "heiward", ["exe"] = @"C:\Heiward\Heiward.exe", ["store"] = true, ["account"] = account });

		Assert.Equal(new ReviewServer.PagePing(@"C:\Heiward\Heiward.exe", true, false), ReviewServer.ReadPing(Ping(PageAccount.Mine), () => false));
		Assert.Null(ReviewServer.ReadPing(Ping(PageAccount.Hash(AnotherAccount)), () => true)); // another account's, whoever listens
		Assert.Null(ReviewServer.ReadPing(Ping(""), () => true));
		// An earlier Heiward's ping doesn't say: it's this account's when this account's program listens there.
		Assert.NotNull(ReviewServer.ReadPing(Ping(null), () => true));
		Assert.Null(ReviewServer.ReadPing(Ping(null), () => false));
		// Not Heiward's, or not a ping.
		Assert.Null(ReviewServer.ReadPing(JsonSerializer.Serialize(new { app = "another-app", account = PageAccount.Mine }), () => true));
		Assert.Null(ReviewServer.ReadPing("<html>", () => true));
		Assert.Null(ReviewServer.ReadPing("[]", () => true));
	}

	[Fact]
	public void APortAnotherAccountHolds_GivesThisAccountAPortOfItsOwn_KeptInItsSettings() {
		var cfg = new AgentConfig { Port = 18484 };
		cfg.Save();
		Assert.Equal(18484, ReviewServer.OwnPort(cfg, _ => false));
		Assert.Equal(18484, AgentConfig.Load().Port);

		int port = ReviewServer.OwnPort(cfg, p => p == 18484);
		Assert.NotEqual(18484, port);
		Assert.Equal(port, cfg.Port);
		Assert.Equal(port, AgentConfig.Load().Port); // the Start menu, notifications and the installer find it there
		// And keeps it: the next start finds it free.
		Assert.Equal(port, ReviewServer.OwnPort(AgentConfig.Load(), p => p == 18484));
	}

	[Fact]
	public void APortNoOneElseListensOn_IsNotHeldByAnother() {
		int free = FreePort();
		Assert.False(PageAccount.HeldByAnother(free));
		Assert.False(PageAccount.ListenedByThisAccount(free));
		using var mine = new TcpListener(IPAddress.Loopback, 0);
		mine.Start();
		int port = ((IPEndPoint)mine.LocalEndpoint).Port;
		Assert.False(PageAccount.HeldByAnother(port));
		Assert.True(PageAccount.ListenedByThisAccount(port));
	}
}
