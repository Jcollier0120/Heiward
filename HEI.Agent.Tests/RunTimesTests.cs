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

using System.Globalization;
using System.Text.Json;

namespace HEI.Agent.Tests;

/// <summary>/api/ping's scans at a glance, for Manor's employee cards: the last run and how it went, the next, one under way.</summary>
[Collection(AgentHomeCollection.Name)]
public sealed class RunTimesTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-runs-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public RunTimesTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

	/// <summary>Task Scheduler's next run as schtasks prints it: local time, in this PC's format.</summary>
	static string Local(DateTime utc) => utc.ToLocalTime().ToString(CultureInfo.CurrentCulture);

	static RunTimes Build(LastScan? last = null, DateTime? reportAt = null, bool scanning = false, DateTime? lastStarted = null,
		bool paused = false, int every = 360, string? nextRun = null) =>
		RunTimes.Build(Now, last, reportAt, scanning, lastStarted, paused, every, nextRun);

	[Fact]
	public void NothingYet_IsAllNull() =>
		Assert.Equal(new RunTimes(null, null, null, null), Build());

	[Fact]
	public void TheLastRun_IsTheLastScanToEnd_AndWhetherItWentThrough() {
		var ok = new LastScan(Now.AddHours(-2), Now.AddHours(-1.5), true, 0);
		RunTimes t = Build(ok, reportAt: Now.AddDays(-3));
		Assert.Equal(Now.AddHours(-1.5), t.LastRunAt);
		Assert.True(t.LastRunOk);

		Assert.False(Build(new LastScan(Now.AddHours(-2), Now.AddHours(-1.5), false, 130)).LastRunOk);
	}

	[Fact]
	public void BeforeAnyScanSaidHowItEnded_TheReportsTime_AndNoWordOnHowItWent() {
		RunTimes t = Build(reportAt: Now.AddHours(-3));
		Assert.Equal(Now.AddHours(-3), t.LastRunAt);
		Assert.Null(t.LastRunOk);
	}

	[Fact]
	public void RunningSince_TheScanHoldingTheLock() {
		var last = new LastScan(Now.AddHours(-6), Now.AddHours(-5), true, 0);
		Assert.Equal(Now.AddMinutes(-3), Build(last, scanning: true, lastStarted: Now.AddMinutes(-3)).RunningSince);
		Assert.Null(Build(last, scanning: false, lastStarted: Now.AddMinutes(-3)).RunningSince);
		// Just after it took the lock, the start on file is still the last scan's: not this one's.
		Assert.Null(Build(last, scanning: true, lastStarted: Now.AddHours(-6)).RunningSince);
		Assert.Equal(Now.AddMinutes(-3), Build(scanning: true, lastStarted: Now.AddMinutes(-3)).RunningSince);
	}

	[Fact]
	public void NextRunAt_IsTaskSchedulersNextRun_NullWhilePausedOrUnscheduled() {
		DateTime next = Now.AddHours(4);
		Assert.Equal(next, Build(nextRun: Local(next)).NextRunAt);
		Assert.Equal(DateTimeKind.Utc, Build(nextRun: Local(next)).NextRunAt!.Value.Kind);
		Assert.Null(Build(nextRun: Local(next), paused: true).NextRunAt);
		Assert.Null(Build(nextRun: Local(next), every: 0).NextRunAt);
		Assert.Null(Build(nextRun: null).NextRunAt);
		// Task Scheduler's own wording in another language (Scheduler.NextRun passes it through): no time to give.
		Assert.Null(Build(nextRun: "scheduled").NextRunAt);
	}

	[Fact]
	public void ANextRunAlreadyPast_MovesOnByTheInterval() {
		// Asked up to a minute ago: the run it named has come and gone.
		Assert.Equal(Now.AddMinutes(-1).AddHours(6), Build(nextRun: Local(Now.AddMinutes(-1)), every: 360).NextRunAt);
		Assert.Equal(Now.AddHours(6), Build(nextRun: Local(Now), every: 360).NextRunAt);
		// The PC slept through a few: the next still to come, every 15 minutes at the most often, as the task runs.
		Assert.Equal(Now.AddMinutes(5), Build(nextRun: Local(Now.AddMinutes(-40)), every: 5).NextRunAt);
	}

	[Fact]
	public void AScanRecordsHowItEnded() {
		DateTime start = Now.AddMinutes(-30);
		Assert.True(LastScan.Record(start, Now, 0, null).Ok);
		LastScan? read = LastScan.Load();
		Assert.NotNull(read);
		Assert.Equal(new LastScan(start, Now, true, 0), read);

		LastScan stopped = LastScan.Record(start, Now, 130, null);
		Assert.Equal((false, 130, (string?)null), (stopped.Ok, stopped.ExitCode, stopped.Error));
		Assert.Equal((false, 130), (LastScan.Record(start, Now, null, new OperationCanceledException()).Ok, LastScan.Load()!.ExitCode!.Value));
		LastScan failed = LastScan.Record(start, Now, null, new IOException("disk gone"));
		Assert.Equal((false, (int?)null, "disk gone"), (failed.Ok, failed.ExitCode, failed.Error));
		Assert.False(LastScan.Record(start, Now, 2, null).Ok);
	}

	[Fact]
	public async Task AScanThatRan_IsTheLastRun_OneThatFoundAnotherRunning_IsNot() {
		// None of its folders exist: the scan ends at once, without scanning anything, and didn't go through.
		var cfg = new AgentConfig { ScanAllDrives = false, Folders = new() { Path.Combine(dir, "no such folder") } };
		Assert.Equal(2, await AgentScanner.RunAsync(cfg, notify: false, scheduled: false, CancellationToken.None));
		LastScan? last = LastScan.Load();
		Assert.NotNull(last);
		Assert.Equal((false, 2), (last.Ok, last.ExitCode));
		Assert.Equal(AgentScanner.LastStartedUtc(), last.StartedUtc);

		RunTimes t = RunTimes.Now(cfg);
		Assert.Equal(last.EndedUtc, t.LastRunAt);
		Assert.False(t.LastRunOk);
		Assert.Null(t.RunningSince);

		// Another scan holds the lock: this one doesn't run, and the last run stands. The one under way shows.
		DateTime started = DateTime.UtcNow.AddSeconds(1);
		File.WriteAllText(AgentPaths.ScanStarted, started.ToString("O"));
		using (new FileStream(AgentPaths.ScanLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) {
			Assert.Equal(0, await AgentScanner.RunAsync(cfg, notify: false, scheduled: false, CancellationToken.None));
			Assert.Equal(last, LastScan.Load());
			Assert.Equal(started, RunTimes.Now(cfg).RunningSince);
		}
		Assert.Null(RunTimes.Now(cfg).RunningSince);
	}

	[Fact]
	public void ThePing_AddsTheRunTimes_ToWhatItSaidBefore() {
		var cfg = new AgentConfig { ScanEveryMinutes = 0 };
		LastScan.Record(Now.AddMinutes(-30), Now, 0, null);
		// As Results.Json writes it: the web defaults.
		using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ReviewServer.Ping(cfg), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
		JsonElement root = doc.RootElement;
		Assert.Equal(new[] { "app", "store", "exe", "lastRunAt", "lastRunOk", "nextRunAt", "runningSince", "tour" }, root.EnumerateObject().Select(p => p.Name));
		Assert.True(root.GetProperty("tour").GetBoolean()); // its page has a tour at #/tour (tour.js)
		Assert.Equal("heiward", root.GetProperty("app").GetString());
		Assert.Equal("2026-10-03T12:00:00Z", root.GetProperty("lastRunAt").GetString());
		Assert.True(root.GetProperty("lastRunOk").GetBoolean());
		Assert.Equal(JsonValueKind.Null, root.GetProperty("nextRunAt").ValueKind); // scans only when asked
		Assert.Equal(JsonValueKind.Null, root.GetProperty("runningSince").ValueKind);
	}
}
