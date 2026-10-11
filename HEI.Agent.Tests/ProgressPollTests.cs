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
/// While a scan runs, the page polls /api/progress: the scan's status and the agent's pause, without the report, the
/// index and the rest of /api/state, which it reads far less often.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME is process-wide
public sealed class ProgressPollTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-progress-" + Guid.NewGuid().ToString("N"));
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	readonly string? manorHome = Environment.GetEnvironmentVariable("MANOR_HOME");

	public ProgressPollTests() {
		Directory.CreateDirectory(Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("MANOR_HOME", Path.Combine(dir, "manor")); // no Manor
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		Environment.SetEnvironmentVariable("MANOR_HOME", manorHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	[Fact]
	public async Task Progress_IsTheScanAndTheAgent_Only() {
		var cfg = new AgentConfig();
		using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
			probe.Start();
			cfg.Port = ((IPEndPoint)probe.LocalEndpoint).Port;
		}
		cfg.ScanEveryMinutes = 0; // the page starts no scan of its own
		cfg.Save();
		AgentPaths.WriteAtomic(AgentPaths.ScanStatus, JsonSerializer.Serialize(
			new ScanStatus(Environment.ProcessId, DateTime.UtcNow, "checking files", 0, 0, Roots: [@"C:\"], Phase: ScanPhase.Checking,
				Drives: [new DriveCheck(@"C:\", 89_144, 133_456, 0, 0)]), AgentConfig.Json));
		Task<int> server = ReviewServer.RunAsync(cfg, openBrowser: false, CancellationToken.None);
		try {
			for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(cfg.Port); i++) await Task.Delay(250);
			using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{cfg.Port}/"), Timeout = TimeSpan.FromSeconds(30) };
			using var progress = JsonDocument.Parse(await http.GetStringAsync("/api/progress?seen=true"));
			JsonElement root = progress.RootElement;
			Assert.Equal(["scan", "agent"], root.EnumerateObject().Select(p => p.Name));
			JsonElement status = root.GetProperty("scan").GetProperty("status");
			Assert.Equal("checking", status.GetProperty("phase").GetString());
			Assert.Equal(89_144, status.GetProperty("drives")[0].GetProperty("done").GetInt32());
			Assert.False(root.GetProperty("agent").GetProperty("paused").GetBoolean());
		}
		finally {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(cfg.Port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
		}
	}
}
