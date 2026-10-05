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
/// The manor's disk measurement: the drives' free space and the big tool caches, measured on Heiward's own clock and
/// served read-only, for this PC only, by GET /api/disk. Caches here are fixture folders; nothing measures the real ones.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME is process-wide
public sealed class DiskWatchTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-disk-" + Guid.NewGuid().ToString("N"));
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public DiskWatchTests() {
		Directory.CreateDirectory(Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "heiward"));
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	string Folder(string rel, params int[] fileSizes) {
		string path = Path.Combine(dir, rel);
		Directory.CreateDirectory(path);
		for (int i = 0; i < fileSizes.Length; i++) File.WriteAllBytes(Path.Combine(path, $"f{i}.bin"), new byte[fileSizes[i]]);
		return path;
	}

	[Fact]
	public void TheCaches_AreReevesDiskCachesJobs_PlusNuGetAndPip() {
		var caches = DiskWatch.Caches(@"C:\Users\me", @"C:\Users\me\AppData\Local", null, @"C:\Pkg\LocalCache\Local");
		Assert.Equal(new (string, string)[] {
			("foundry", @"C:\Users\me\.foundry"),
			("geniex-cache", @"C:\Users\me\.cache\geniex"),
			("reeve", @"C:\Users\me\.reeve"),
			("gradle", @"C:\Users\me\.gradle"),
			("npm-cache", @"C:\Users\me\AppData\Local\npm-cache"),
			("pnpm", @"C:\Users\me\AppData\Local\pnpm"),
			("android-sdk", @"C:\Users\me\AppData\Local\Android\Sdk"),
			("nuget", @"C:\Users\me\.nuget\packages"),
			("pip", @"C:\Users\me\AppData\Local\pip\Cache"),
			("npm-cache (Claude package)", @"C:\Pkg\LocalCache\Local\npm-cache"),
			("pnpm (Claude package)", @"C:\Pkg\LocalCache\Local\pnpm"),
			("android-sdk (Claude package)", @"C:\Pkg\LocalCache\Local\Android\Sdk"),
		}, caches);
		// npm's own setting wins, and without the Claude package there are no copies to measure.
		var plain = DiskWatch.Caches(@"C:\Users\me", @"C:\Users\me\AppData\Local", @"D:\npm", null);
		Assert.Equal(@"D:\npm", plain.Single(c => c.Name == "npm-cache").Path);
		Assert.Equal(9, plain.Count);
	}

	[Theory]
	[InlineData(9.9, "alert")]
	[InlineData(10, "warn")]
	[InlineData(14.9, "warn")]
	[InlineData(15, "ok")]
	[InlineData(80, "ok")]
	public void ADrivesLevel_FollowsReevesThresholds(double percentFree, string level) => Assert.Equal(level, DiskWatch.LevelOf(percentFree));

	[Fact]
	public void ADrive_SaysHowMuchIsFree() =>
		Assert.Equal(new DiskDrive(@"C:\", "Windows", 1000L << 30, 120L << 30, 12.0, "warn"), DiskWatch.DriveOf(@"C:\", "Windows", 1000L << 30, 120L << 30));

	[Fact]
	public void AReading_MeasuresTheCachesThatExist_WithTheirGrowth() {
		var caches = new List<(string, string)> {
			("gradle", Folder("gradle", 100, 200)),
			("npm-cache", Folder("npm", 50)),
			("pnpm", Path.Combine(dir, "nothing-here")),
		};
		Folder(Path.Combine("gradle", "deep", "er"), 1000);
		var drives = new List<DiskDrive> { DiskWatch.DriveOf(@"C:\", "", 100, 50) };
		var before = new DiskReading {
			MeasuredAtUtc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc),
			Caches = [new DiskCache("gradle", "x", 300, 2, null)],
		};
		DateTime now = new(2026, 10, 4, 13, 0, 0, 500, DateTimeKind.Utc);

		DiskReading r = DiskWatch.Take(before, drives, caches, now);
		Assert.Equal(new DateTime(2026, 10, 4, 13, 0, 0, DateTimeKind.Utc), r.MeasuredAtUtc);
		Assert.Equal(before.MeasuredAtUtc, r.PreviousAtUtc);
		Assert.Same(drives, r.Drives);
		Assert.Equal(new[] {
			new DiskCache("gradle", caches[0].Item2, 1300, 3, 1000),
			new DiskCache("npm-cache", caches[1].Item2, 50, 1, null), // not in the reading before
		}, r.Caches);
		Assert.Null(DiskWatch.Take(null, drives, caches, now).PreviousAtUtc);
	}

	[Fact]
	public void AClaudePackageCopy_ShowingTheSameFiles_IsCountedOnce() {
		string npm = Folder("npm", 10, 20), pnpm = Folder("pnpm", 5);
		var caches = new List<(string, string)> {
			("npm-cache", npm), ("pnpm", pnpm),
			("npm-cache (Claude package)", npm), // the same files, seen through the redirect
			("pnpm (Claude package)", Folder("pkg-pnpm", 5, 5)), // its own
		};
		DiskReading r = DiskWatch.Take(null, new(), caches, DateTime.UtcNow);
		Assert.Equal("npm-cache", r.Caches.Single(c => c.Name == "npm-cache (Claude package)").SameAs);
		Assert.Null(r.Caches.Single(c => c.Name == "pnpm (Claude package)").SameAs);
		using var doc = JsonDocument.Parse(JsonSerializer.Serialize(DiskWatch.Answer(r)));
		Assert.Equal(30 + 5 + 10, doc.RootElement.GetProperty("cachesBytes").GetInt64());
	}

	[Fact]
	public void AReading_IsDue_HourlyFromTheLast() {
		DateTime now = new(2026, 10, 4, 13, 0, 0, DateTimeKind.Utc);
		Assert.True(DiskWatch.Due(null, now));
		Assert.False(DiskWatch.Due(new DiskReading { MeasuredAtUtc = now.AddMinutes(-59) }, now));
		Assert.True(DiskWatch.Due(new DiskReading { MeasuredAtUtc = now.AddMinutes(-60) }, now));
		Assert.True(DiskWatch.Due(new DiskReading { MeasuredAtUtc = now.AddHours(2) }, now)); // the clock was set back
	}

	[Fact]
	public void TheAnswer_SaysWhatsWrong_AsReevesJobDid() {
		static (string Status, string[] Warnings) Of(DiskReading? r) {
			using var doc = JsonDocument.Parse(JsonSerializer.Serialize(DiskWatch.Answer(r)));
			return (doc.RootElement.GetProperty("status").GetString()!, doc.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray());
		}
		DiskDrive Drive(string root, double pct) => DiskWatch.DriveOf(root, "", 1000L << 30, (long)((1000L << 30) * pct / 100));

		Assert.Equal(("unknown", Array.Empty<string>()), Of(null));
		Assert.Equal(("ok", Array.Empty<string>()), Of(new DiskReading { Drives = [Drive(@"C:\", 40)], Caches = [new("gradle", "g", 9L << 30, 1, 4L << 30)] }));

		var (status, warnings) = Of(new DiskReading { Drives = [Drive(@"C:\", 12), Drive(@"D:\", 50)], Caches = [new("gradle", "g", 9L << 30, 1, 6L << 30)] });
		Assert.Equal("warn", status);
		Assert.Equal(2, warnings.Length);
		Assert.StartsWith(@"C:\ is below 15% free", warnings[0]);
		Assert.StartsWith("gradle grew ", warnings[1]);

		(status, warnings) = Of(new DiskReading { Drives = [Drive(@"C:\", 40), Drive(@"D:\", 5)] });
		Assert.Equal("alert", status);
		Assert.StartsWith(@"D:\ is below 10% free", Assert.Single(warnings));
	}

	// ---- GET /api/disk

	static async Task<(int Port, Task<int> Server)> StartPageAsync() {
		var cfg = new AgentConfig { ScanEveryMinutes = 0 };
		using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
			probe.Start();
			cfg.Port = ((IPEndPoint)probe.LocalEndpoint).Port;
		}
		cfg.Save();
		Task<int> server = ReviewServer.RunAsync(cfg, openBrowser: false, CancellationToken.None);
		for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(cfg.Port); i++) await Task.Delay(250);
		return (cfg.Port, server);
	}

	static async Task StopPageAsync(int port, Task<int> server) {
		if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(port);
		await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
	}

	[Fact]
	public async Task TheEndpoint_ServesTheLastReading_ForThisPcOnly() {
		var reading = new DiskReading {
			MeasuredAtUtc = new DateTime(2026, 10, 4, 13, 0, 0, DateTimeKind.Utc),
			PreviousAtUtc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc),
			Drives = [DiskWatch.DriveOf(@"C:\", "Windows", 1000L << 30, 120L << 30)],
			Caches = [new DiskCache("gradle", @"C:\Users\me\.gradle", 7L << 30, 1234, 6L << 30), new DiskCache("npm-cache", @"C:\Users\me\AppData\Local\npm-cache", 1L << 30, 99, null)],
		};
		reading.Save(); // fresh: the page's own clock leaves it be
		var (port, server) = await StartPageAsync();
		try {
			using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
			using HttpResponseMessage answer = await http.GetAsync("/api/disk");
			Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
			Assert.Equal("no-store", answer.Headers.CacheControl?.ToString());
			using var doc = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
			JsonElement root = doc.RootElement;
			Assert.Equal(new[] { "app", "measuredAt", "previousAt", "everyMinutes", "status", "warnings", "thresholds", "drives", "caches", "cachesBytes" },
				root.EnumerateObject().Select(p => p.Name));
			Assert.Equal("heiward", root.GetProperty("app").GetString());
			Assert.Equal("2026-10-04T13:00:00Z", root.GetProperty("measuredAt").GetString());
			Assert.Equal("2026-10-04T12:00:00Z", root.GetProperty("previousAt").GetString());
			Assert.Equal(60, root.GetProperty("everyMinutes").GetInt32());
			Assert.Equal("warn", root.GetProperty("status").GetString());
			Assert.Equal(2, root.GetProperty("warnings").GetArrayLength());
			JsonElement thresholds = root.GetProperty("thresholds");
			Assert.Equal(10, thresholds.GetProperty("alertPercentFree").GetDouble());
			Assert.Equal(15, thresholds.GetProperty("warnPercentFree").GetDouble());
			Assert.Equal(5L << 30, thresholds.GetProperty("cacheGrowthBytes").GetInt64());
			JsonElement c = root.GetProperty("drives")[0];
			Assert.Equal(new[] { "root", "label", "totalBytes", "freeBytes", "percentFree", "level" }, c.EnumerateObject().Select(p => p.Name));
			Assert.Equal(@"C:\", c.GetProperty("root").GetString());
			Assert.Equal(1000L << 30, c.GetProperty("totalBytes").GetInt64());
			Assert.Equal(120L << 30, c.GetProperty("freeBytes").GetInt64());
			Assert.Equal(12.0, c.GetProperty("percentFree").GetDouble());
			Assert.Equal("warn", c.GetProperty("level").GetString());
			JsonElement gradle = root.GetProperty("caches")[0];
			Assert.Equal(new[] { "name", "path", "bytes", "files", "grewBytes", "sameAs" }, gradle.EnumerateObject().Select(p => p.Name));
			Assert.Equal("gradle", gradle.GetProperty("name").GetString());
			Assert.Equal(7L << 30, gradle.GetProperty("bytes").GetInt64());
			Assert.Equal(1234, gradle.GetProperty("files").GetInt64());
			Assert.Equal(6L << 30, gradle.GetProperty("grewBytes").GetInt64());
			Assert.Equal(JsonValueKind.Null, gradle.GetProperty("sameAs").ValueKind);
			Assert.Equal(JsonValueKind.Null, root.GetProperty("caches")[1].GetProperty("grewBytes").ValueKind);
			Assert.Equal(8L << 30, root.GetProperty("cachesBytes").GetInt64());

			// Another name for this PC (a DNS-rebinding page) is refused, as for every request.
			using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/disk");
			rebound.Headers.Host = "attacker.example:" + port;
			Assert.Equal(HttpStatusCode.MisdirectedRequest, (await http.SendAsync(rebound)).StatusCode);
			// Read-only: there's nothing to post.
			Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("/api/disk", null)).StatusCode);
		}
		finally { await StopPageAsync(port, server); }
	}

	[Fact]
	public async Task TheEndpoint_BeforeTheFirstReading_SaysSo() {
		var (port, server) = await StartPageAsync();
		try {
			using var http = new HttpClient();
			using var doc = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/disk"));
			JsonElement root = doc.RootElement;
			Assert.Equal(JsonValueKind.Null, root.GetProperty("measuredAt").ValueKind);
			Assert.Equal("unknown", root.GetProperty("status").GetString());
			Assert.Equal(0, root.GetProperty("drives").GetArrayLength());
			Assert.Equal(0, root.GetProperty("caches").GetArrayLength());
		}
		finally { await StopPageAsync(port, server); }
	}
}
