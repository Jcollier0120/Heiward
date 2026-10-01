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

using System.Text.Json;

namespace HEI.Agent.Tests;

/// <summary><c>hei status --json</c>: what scripts and other tools read, so its names and meaning can't drift.</summary>
[Collection(AgentHomeCollection.Name)]
public sealed class AgentStatusTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-status-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public AgentStatusTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
	const string NextRun = "scheduled"; // Task Scheduler's wording varies with the PC's language: passed through as it is

	static AgentStatus Build(AgentConfig? cfg = null, DateTime? at = null, string? nextRun = NextRun) =>
		AgentStatus.Build(cfg ?? new AgentConfig(), at ?? Now, pageUp: false, nextRun);

	[Fact]
	public void NotPaused_IsRunning() {
		AgentStatus s = Build();
		Assert.True(s.Running);
		Assert.Null(s.StoppedSince);
		Assert.Null(s.PausedUntil);
		Assert.True(s.Scheduled);
		Assert.Equal(NextRun, s.NextScan);
		Assert.Equal("Scans every hour. No scan yet.", s.Summary);
	}

	[Fact]
	public void PausedUntilResumed_IsNotRunning_AndHasNoEnd() {
		AgentPause.Start(null, Now);
		AgentStatus s = Build(at: Now.AddDays(3));
		Assert.False(s.Running);
		Assert.Equal(Now, s.StoppedSince);
		Assert.Null(s.PausedUntil);
		Assert.StartsWith("Paused until you resume.", s.Summary);

		AgentPause.Resume();
		Assert.True(Build().Running);
	}

	[Fact]
	public void ATimedPause_SaysWhenItEnds_ThenRunsAgain() {
		AgentPause.Start(60, Now);
		AgentStatus s = Build(at: Now.AddMinutes(10));
		Assert.False(s.Running);
		Assert.Equal(Now, s.StoppedSince);
		Assert.Equal(Now.AddHours(1), s.PausedUntil);

		AgentStatus after = Build(at: Now.AddMinutes(61));
		Assert.True(after.Running);
		Assert.Null(after.StoppedSince);
		Assert.Null(after.PausedUntil);
	}

	[Fact]
	public void Running_DoesNotNeedAScanTask_ScheduledSaysThat() {
		AgentStatus missing = Build(nextRun: null);
		Assert.True(missing.Running);
		Assert.False(missing.Scheduled);
		Assert.StartsWith("No scheduled scans: the scan task is missing or turned off.", missing.Summary);

		// On demand only: a leftover task doesn't make scans scheduled.
		AgentStatus onDemand = Build(new AgentConfig { ScanEveryMinutes = 0 });
		Assert.True(onDemand.Running);
		Assert.False(onDemand.Scheduled);
		Assert.StartsWith("Scans only when you press Scan now.", onDemand.Summary);
	}

	[Fact]
	public void ToReview_CountsTheSetsNotDecidedYet() {
		static ReportGroup Set(string key) => new(key, "copies", "image", @"C:\Pictures\" + key + ".jpg", "kept", 100, 99.9f, new());
		new Report(Report.CurrentVersion, Now.AddHours(-2), 1, "NPU", 10, new(), new(), new(), new() { Set("a"), Set("b"), Set("c") }, AppBuild.Current).Save();
		DecisionStore.Set("b", new Decision("kept", Now, new(), 0));

		AgentStatus s = Build();
		Assert.Equal(2, s.ToReview);
		Assert.Equal(Now.AddHours(-2), s.LastScan);
		Assert.EndsWith("2 sets to review.", s.Summary);
	}

	[Fact]
	public void TheJson_KeepsItsNames() {
		AgentPause.Start(null, Now);
		string json = Build().ToJson();
		Assert.DoesNotContain('\n', json); // one line: one object per call, easy to read from a pipe

		using var doc = JsonDocument.Parse(json);
		JsonElement root = doc.RootElement;
		Assert.Equal(
			new[] { "app", "running", "stoppedSince", "pausedUntil", "scheduled", "nextScan", "scanning", "lastScan", "toReview", "page", "summary" },
			root.EnumerateObject().Select(p => p.Name));
		Assert.Equal("heiward", root.GetProperty("app").GetString());
		Assert.False(root.GetProperty("running").GetBoolean());
		Assert.Equal("2026-09-30T12:00:00Z", root.GetProperty("stoppedSince").GetString());
		Assert.Equal(JsonValueKind.Null, root.GetProperty("pausedUntil").ValueKind);
		Assert.Equal(JsonValueKind.Null, root.GetProperty("lastScan").ValueKind);
		Assert.Equal(0, root.GetProperty("toReview").GetInt32());

		JsonElement page = root.GetProperty("page");
		Assert.Equal(new[] { "url", "up" }, page.EnumerateObject().Select(p => p.Name));
		Assert.Equal("http://heiward.localhost:18484/", page.GetProperty("url").GetString());
		Assert.False(page.GetProperty("up").GetBoolean());
	}

	[Fact]
	public void TheJson_SaysUtc_EvenForATimeReadBackWithoutAZone() {
		// A report written by hand, or by an older build, may hold a time without "Z": it was UTC all the same.
		File.WriteAllText(AgentPaths.Report, JsonSerializer.Serialize(
			new Report(Report.CurrentVersion, Now, 1, "NPU", 10, new(), new(), new(), new(), AppBuild.Current), AgentConfig.Json).Replace("12:00:00Z", "12:00:00"));
		using var doc = JsonDocument.Parse(Build().ToJson());
		Assert.Equal("2026-09-30T12:00:00Z", doc.RootElement.GetProperty("lastScan").GetString());
	}
}
