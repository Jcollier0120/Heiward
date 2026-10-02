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
using HEI.Core.Utils;

namespace HEI.Agent.Tests;

/// <summary>
/// A desktop with more than one graphics card: the installer and the Store version's setup ask which one does the
/// GPU work, settings.json keeps it by name, and the page's Settings changes it, except while a scan runs on it.
/// </summary>
[Collection(AgentHomeCollection.Name)]
public sealed class GpuChoiceTests : IDisposable {
	const ulong GB = 1UL << 30;

	static readonly IReadOnlyList<GpuAdapter> Desktop = GpuAdapters.Keyed(new DxgiAdapter[] {
		new(0, "Intel(R) UHD Graphics 770", 128UL << 20, 0x8086, false),
		new(1, "NVIDIA GeForce RTX 4070", 12 * GB, 0x10DE, false),
		new(2, "NVIDIA GeForce RTX 4070", 12 * GB, 0x10DE, false),
	});

	[Theory]
	[InlineData("2", "NVIDIA GeForce RTX 4070")]      // its number in the installer's list
	[InlineData("3", "NVIDIA GeForce RTX 4070 #2")]
	[InlineData("nvidia geforce rtx 4070 #2", "NVIDIA GeForce RTX 4070 #2")]
	[InlineData(" Intel(R) UHD Graphics 770", "Intel(R) UHD Graphics 770")]
	[InlineData("default", "")]                       // Windows' default
	[InlineData("4", null)]
	[InlineData("0", null)]
	[InlineData("AMD Radeon RX 7900 XTX", null)]
	public void InstallGpu_TakesANumberOrAName(string value, string? key) => Assert.Equal(key, Installer.GpuArgument(value, Desktop));

	[Fact]
	public void Unattended_OnADesktop_TheCardWithTheMostMemoryOfItsOwn() =>
		Assert.Equal("NVIDIA GeForce RTX 4070", Installer.AskGpu(Desktop, current: "", assumeYes: true, forAi: true));

	[Fact]
	public void Reinstalling_KeepsTheCardChosenBefore() =>
		Assert.Equal("NVIDIA GeForce RTX 4070 #2", Installer.AskGpu(Desktop, current: "NVIDIA GeForce RTX 4070 #2", assumeYes: true, forAi: false));

	[Fact]
	public void Reinstalling_AfterTheCardWentAway_SuggestsAnotherOne() =>
		Assert.Equal("NVIDIA GeForce RTX 4070", Installer.AskGpu(Desktop, current: "NVIDIA GeForce GTX 1080", assumeYes: true, forAi: true));

	[Fact]
	public void OneCard_NothingToAsk_WindowsDefault() =>
		Assert.Equal("", Installer.AskGpu(Desktop.Take(1).ToList(), current: "Intel(R) UHD Graphics 770", assumeYes: false, forAi: true));

	[Theory]
	[InlineData(12UL << 30, "NVIDIA GeForce RTX 4070, 12 GB of its own memory")]
	[InlineData(128UL << 20, "NVIDIA GeForce RTX 4070, shares the PC's memory")]
	public void TheInstaller_SaysHowMuchMemoryACardHas(ulong memory, string text) =>
		Assert.Equal(text, Installer.GpuText(new GpuAdapter(0, "NVIDIA GeForce RTX 4070", "NVIDIA GeForce RTX 4070", memory, 0x10DE)));

	[Fact]
	public void StoreSetup_PassesTheCard_ToTheInstall() =>
		Assert.Equal("install --yes --no-browser --scan-speed background --device gpu --gpu NVIDIA GeForce RTX 4070 #2",
			string.Join(' ', StoreSetup.Arguments(new SetupRequest("gpu", false, "background", Gpu: "nvidia geforce rtx 4070 #2"), Desktop)!));

	[Fact]
	public void StoreSetup_RefusesACardThisPCDoesntHave() =>
		Assert.Null(StoreSetup.Arguments(new SetupRequest("gpu", false, "background", Gpu: "AMD Radeon RX 7900 XTX"), Desktop));

	[Fact]
	public void StoreSetup_WithoutACard_LeavesItToTheInstall() =>
		Assert.DoesNotContain("--gpu", StoreSetup.Arguments(new SetupRequest("npu", false, "background", Gpu: null), Desktop)!);

	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-gpu-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public GpuChoiceTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	[Fact]
	public void TheCard_IsKeptInSettingsJson_ByName() {
		Assert.Equal("", new AgentConfig().Gpu); // Windows' default, as before there was a choice
		new AgentConfig { Gpu = "NVIDIA GeForce RTX 4070 #2" }.Save();
		Assert.Contains("\"gpu\": \"NVIDIA GeForce RTX 4070 #2\"", File.ReadAllText(AgentPaths.Config));
		Assert.Equal("NVIDIA GeForce RTX 4070 #2", AgentConfig.Load().Gpu);
	}

	[Fact]
	public void AScan_IsGivenTheCard() =>
		Assert.Equal("NVIDIA GeForce RTX 4070", AgentScanner.BuildSettings(new AgentConfig { Gpu = "NVIDIA GeForce RTX 4070", ScanAllDrives = false }, new List<string>()).Gpu);

	/// <summary>
	/// The page's Settings: the card can't change while a scan runs (it keeps the one it started on), says why,
	/// and changes once the scan is over. A card the PC doesn't have is refused.
	/// </summary>
	[Fact]
	public async Task TheCard_CantChange_WhileAScanRuns() {
		int port;
		using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
			probe.Start();
			port = ((IPEndPoint)probe.LocalEndpoint).Port;
		}
		var cfg = new AgentConfig { Port = port, ScanEveryMinutes = 0 };
		cfg.Save();
		Task<int> page = ReviewServer.RunAsync(cfg, openBrowser: false, CancellationToken.None);
		try {
			for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(port); i++) await Task.Delay(250);
			using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
			string token = ReviewServer.TokenIn(await http.GetStringAsync("/"))!;
			Task<HttpResponseMessage> Choose(string gpu) {
				var request = new HttpRequestMessage(HttpMethod.Post, "/api/settings") { Content = JsonContent.Create(new { gpu }) };
				request.Headers.Add("X-Agent-Token", token);
				return http.SendAsync(request);
			}
			async Task<bool> Locked() {
				using var state = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("/api/state"));
				return state.RootElement.GetProperty("gpu").GetProperty("locked").GetBoolean();
			}

			// A scan holds scan.lock, as `hei scan` does.
			using (new FileStream(AgentPaths.ScanLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) {
				using HttpResponseMessage busy = await Choose("");
				Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
				Assert.Contains("Let it finish, or stop it", await busy.Content.ReadAsStringAsync());
				Assert.True(await Locked());
			}

			using HttpResponseMessage unknown = await Choose("A graphics card this PC has never had");
			Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

			IReadOnlyList<GpuAdapter> cards = GpuAdapters.List();
			string pick = cards.Count > 0 ? cards[^1].Key : "";
			using HttpResponseMessage done = await Choose(pick.ToUpperInvariant());
			Assert.Equal(HttpStatusCode.OK, done.StatusCode);
			Assert.Equal(pick, AgentConfig.Load().Gpu); // saved as the card's own name
			Assert.False(await Locked());
		}
		finally {
			if (!page.IsCompleted) await ReviewServer.AskToCloseAsync(port);
			await Task.WhenAny(page, Task.Delay(TimeSpan.FromSeconds(10)));
		}
	}
}
