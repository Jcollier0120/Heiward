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

/// <summary>
/// scan-status.json is written when it's worth the page's while (StatusPace): a new step, stage or pace at once, the counts
/// moving every few seconds while the page shows and every half minute while it doesn't, and the last state when the scan
/// is done. The clock is moved by hand: nothing here waits for real time to pass.
/// </summary>
public sealed class StatusFileTests {
	static readonly DateTime Started = new(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc);

	static ScanStatus Status(string phase = ScanPhase.Checking, int done = 0, string? stage = null, bool fullSpeed = false) =>
		new(1234, Started, stage ?? "Checking files", done, 1000, fullSpeed, [@"D:\"], phase,
			Drives: phase == ScanPhase.Checking ? [new DriveCheck(@"D:\", done, 1000, done * 1000L, 1_000_000)] : null);

	/// <summary>A clock and timers that move only when a test says.</summary>
	sealed class HandClock : TimeProvider {
		readonly List<HandTimer> timers = new();
		long ticks;
		public override long TimestampFrequency => TimeSpan.TicksPerSecond;
		public override long GetTimestamp() => Interlocked.Read(ref ticks);
		public override DateTimeOffset GetUtcNow() => new DateTimeOffset(Started).AddTicks(GetTimestamp());

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
			var t = new HandTimer(this, callback, state);
			t.Change(dueTime, period);
			lock (timers) timers.Add(t);
			return t;
		}

		public void Advance(TimeSpan by) {
			long to = Interlocked.Add(ref ticks, by.Ticks);
			List<HandTimer> due;
			lock (timers) due = timers.ToList();
			foreach (HandTimer t in due) t.FireUpTo(to);
		}

		sealed class HandTimer(HandClock clock, TimerCallback callback, object? state) : ITimer {
			long next = -1, period = -1;
			public bool Change(TimeSpan dueTime, TimeSpan period) {
				lock (this) {
					next = dueTime == Timeout.InfiniteTimeSpan ? -1 : clock.GetTimestamp() + dueTime.Ticks;
					this.period = period == Timeout.InfiniteTimeSpan ? -1 : period.Ticks;
				}
				return true;
			}
			public void FireUpTo(long now) {
				while (true) {
					lock (this) {
						if (next < 0 || next > now) return;
						next = period > 0 ? next + period : -1;
					}
					callback(state);
				}
			}
			public void Dispose() { lock (this) next = -1; }
			public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
		}
	}

	[Fact]
	public void TheFirstStatus_IsWrittenAtOnce_AndTheSameOneNeverAgain() {
		var clock = new HandClock();
		var pace = new StatusPace(() => true, clock);
		string json = Assert.IsType<string>(pace.Due(Status(done: 5)));
		Assert.Equal(json, JsonSerializer.Serialize(Status(done: 5), AgentConfig.Json));
		pace.Wrote(Status(done: 5), json);
		clock.Advance(TimeSpan.FromMinutes(5));
		Assert.Null(pace.Due(Status(done: 5)));
		Assert.Null(pace.Due(Status(done: 5), final: true));
	}

	[Fact]
	public void TheCountsMovingAlone_AreWrittenEveryFewSeconds_WhileThePageShows() {
		var clock = new HandClock();
		var pace = new StatusPace(() => true, clock);
		pace.Wrote(Status(done: 0), pace.Due(Status(done: 0))!);
		int written = 0;
		for (int second = 1; second <= 30; second++) {
			clock.Advance(TimeSpan.FromSeconds(1));
			if (pace.Due(Status(done: second)) is { } json) {
				pace.Wrote(Status(done: second), json);
				written++;
				Assert.Equal(0, second % (int)StatusPace.Watched.TotalSeconds);
			}
		}
		Assert.Equal(30 / (int)StatusPace.Watched.TotalSeconds, written);
		// Under the page's "Running on the NPU" (10 s), and more than one of its 1.5 s looks apart.
		Assert.InRange(StatusPace.Watched, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void TheCountsMovingAlone_AreWrittenEveryHalfMinute_WhileNobodyLooks() {
		var clock = new HandClock();
		bool watched = false;
		var pace = new StatusPace(() => watched, clock);
		pace.Wrote(Status(done: 0), pace.Due(Status(done: 0))!);
		var writes = new List<int>();
		for (int second = 1; second <= 120; second++) {
			clock.Advance(TimeSpan.FromSeconds(1));
			if (pace.Due(Status(done: second)) is { } json) {
				pace.Wrote(Status(done: second), json);
				writes.Add(second);
			}
		}
		Assert.Equal([30, 60, 90, 120], writes);

		// The page opens: the next look that's a few seconds on writes.
		watched = true;
		clock.Advance(TimeSpan.FromSeconds(StatusPace.Watched.TotalSeconds));
		Assert.NotNull(pace.Due(Status(done: 999)));
	}

	[Theory]
	[InlineData(ScanPhase.Listing, ScanPhase.Checking)]
	[InlineData(ScanPhase.Checking, ScanPhase.Comparing)]
	[InlineData(ScanPhase.Comparing, ScanPhase.Finishing)]
	public void ANewStep_IsWrittenAtOnce(string from, string to) {
		var clock = new HandClock();
		var pace = new StatusPace(() => false, clock);
		pace.Wrote(Status(from), pace.Due(Status(from))!);
		clock.Advance(TimeSpan.FromMilliseconds(10));
		Assert.NotNull(pace.Due(Status(to)));
	}

	[Fact]
	public void ANewStageOfComparingOrFinishing_OrANewPace_IsWrittenAtOnce_ButEachFilesStepWhileChecking_Waits() {
		var clock = new HandClock();
		var pace = new StatusPace(() => false, clock);
		pace.Wrote(Status(ScanPhase.Comparing, stage: "Comparing duplicates"), pace.Due(Status(ScanPhase.Comparing, stage: "Comparing duplicates"))!);
		clock.Advance(TimeSpan.FromSeconds(1));
		Assert.NotNull(pace.Due(Status(ScanPhase.Comparing, stage: "Validating groups")));
		Assert.NotNull(pace.Due(Status(ScanPhase.Comparing, stage: "Comparing duplicates", fullSpeed: true)));

		var checking = new StatusPace(() => false, clock);
		checking.Wrote(Status(stage: "Probing"), checking.Due(Status(stage: "Probing"))!);
		clock.Advance(TimeSpan.FromSeconds(1));
		Assert.Null(checking.Due(Status(stage: "Sampling frames", done: 1)));
		Assert.NotNull(checking.Due(Status(stage: "Sampling frames", done: 1, fullSpeed: true)));
	}

	[Fact]
	public void TheLastState_IsWrittenWhateverThePaceHeldBack() {
		var clock = new HandClock();
		var pace = new StatusPace(() => false, clock);
		pace.Wrote(Status(done: 1), pace.Due(Status(done: 1))!);
		clock.Advance(TimeSpan.FromSeconds(1));
		Assert.Null(pace.Due(Status(done: 2)));
		Assert.NotNull(pace.Due(Status(done: 2), final: true));
	}

	[Fact]
	public async Task TheFile_IsWrittenAtOnce_ThenByThePace_AndLastOfAll_ThenDeleted() {
		var clock = new HandClock();
		int done = 0;
		var looked = new SemaphoreSlim(0);
		var written = new List<ScanStatus>();
		bool deleted = false;
		ScanStatus Now() {
			ScanStatus s = Status(done: Volatile.Read(ref done));
			looked.Release();
			return s;
		}
		void Write(string json) { lock (written) written.Add(JsonSerializer.Deserialize<ScanStatus>(json, AgentConfig.Json)!); }
		async Task Second() {
			Interlocked.Increment(ref done);
			clock.Advance(TimeSpan.FromSeconds(1));
			Assert.True(await looked.WaitAsync(TimeSpan.FromSeconds(30)), "the second's look didn't come");
		}

		var file = new StatusFile(Now, () => false, Write, () => deleted = true, clock);
		Assert.True(await looked.WaitAsync(TimeSpan.FromSeconds(30)));
		Assert.Equal([0], written.Select(s => s.Position));

		// Nobody looks: the counts moving alone are written every half minute.
		for (int i = 0; i < 45; i++) await Second();
		lock (written) Assert.Equal([0, 30], written.Select(s => s.Position));

		// Done at 45: that lands, then the file goes.
		await file.DisposeAsync();
		Assert.Equal([0, 30, 45], written.Select(s => s.Position));
		Assert.True(deleted);
		await file.DisposeAsync(); // once is enough
		Assert.Equal(3, written.Count);
	}

	[Fact]
	public async Task TheLastState_IsNotWrittenAgain_WhenItHadLanded() {
		var clock = new HandClock();
		var written = new List<string>();
		var file = new StatusFile(() => Status(ScanPhase.Finishing, stage: "Cleaning up automatically"), () => true, written.Add, () => { }, clock);
		await file.DisposeAsync();
		Assert.Single(written);
	}
}
