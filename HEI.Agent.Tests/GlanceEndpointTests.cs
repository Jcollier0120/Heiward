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
/// Manor's disk tile asks /api/glance once a minute: the glance alone, as /api/state gives it, without the review lists,
/// the History and the settings.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME is process-wide
public sealed class GlanceEndpointTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-glance-" + Guid.NewGuid().ToString("N"));
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	readonly string? manorHome = Environment.GetEnvironmentVariable("MANOR_HOME");

	public GlanceEndpointTests() {
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
	public async Task TheGlance_Alone_AsTheStateGivesIt() {
		var cfg = new AgentConfig();
		using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
			probe.Start();
			cfg.Port = ((IPEndPoint)probe.LocalEndpoint).Port;
		}
		cfg.ScanEveryMinutes = 0; // the page starts no scan of its own
		cfg.Save();
		// Two sets of copies, one dealt with already: only the other's space counts as reclaimable.
		new Report(Report.CurrentVersion, DateTime.UtcNow.AddHours(-1), 1, "CPU", 4, new(), new(), new(), new() {
			new ReportGroup("a", "copies", "image", @"C:\Pictures\a.jpg", "kept", 100, 100f, new()),
			new ReportGroup("b", "copies", "image", @"C:\Pictures\b.jpg", "kept", 40, 100f, new()),
		}, AppBuild.Current).Save();
		DecisionStore.Set("b", new Decision("kept", DateTime.UtcNow, new(), 0));
		Task<int> server = ReviewServer.RunAsync(cfg, openBrowser: false, CancellationToken.None);
		try {
			for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(cfg.Port); i++) await Task.Delay(250);
			using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{cfg.Port}/"), Timeout = TimeSpan.FromSeconds(30) };
			using HttpResponseMessage answer = await http.GetAsync("/api/glance");
			Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
			Assert.Equal("no-store", answer.Headers.CacheControl?.ToString());
			using var light = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
			Assert.Equal(["glance"], light.RootElement.EnumerateObject().Select(p => p.Name));
			JsonElement glance = light.RootElement.GetProperty("glance");
			Assert.Equal(100, glance.GetProperty("reclaim").GetProperty("duplicates").GetInt64());
			// Each drive counted, one by one, for Manor's disk space to list: as many as the glance counts.
			JsonElement byDrive = glance.GetProperty("byDrive");
			Assert.Equal(glance.GetProperty("drives").GetInt32(), byDrive.GetArrayLength());
			foreach (JsonElement d in byDrive.EnumerateArray())
				Assert.Equal(["root", "label", "type", "totalBytes", "freeBytes"], d.EnumerateObject().Select(p => p.Name));

			// The same glance as the page's: its fields, and what can be reclaimed (free space may move between the two).
			using var state = JsonDocument.Parse(await http.GetStringAsync("/api/state"));
			JsonElement full = state.RootElement.GetProperty("glance");
			Assert.Equal(full.EnumerateObject().Select(p => p.Name), glance.EnumerateObject().Select(p => p.Name));
			Assert.Equal(full.GetProperty("reclaim").GetRawText(), glance.GetProperty("reclaim").GetRawText());
			Assert.Equal(full.GetProperty("drives").GetInt32(), glance.GetProperty("drives").GetInt32());
			Assert.Equal(full.GetProperty("totalBytes").GetInt64(), glance.GetProperty("totalBytes").GetInt64());
		}
		finally {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(cfg.Port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
		}
	}
}
