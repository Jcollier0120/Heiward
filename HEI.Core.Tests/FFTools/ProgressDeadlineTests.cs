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

using HEI.Core.FFTools.FFmpegNative;

namespace HEI.Core.Tests.FFTools;

/// <summary>
/// The native decoder's timeout: a decode that keeps making progress never counts as hung, however
/// long it takes in all (a 4K clip under the background pace), and one that stops still does.
/// </summary>
public class ProgressDeadlineTests {
	/// <summary>A clock that moves only when the test says.</summary>
	sealed class ManualClock : TimeProvider {
		long _ticks;
		public override long TimestampFrequency => TimeSpan.TicksPerSecond;
		public override long GetTimestamp() => _ticks;
		public void Advance(TimeSpan by) => _ticks += by.Ticks;
	}

	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

	[Fact]
	public void NoProgress_ExpiresAfterTheTimeout() {
		var clock = new ManualClock();
		var deadline = new ProgressDeadline(Timeout, clock);

		Assert.False(deadline.Expired()); // FFmpeg's first ask starts the timeout
		clock.Advance(Timeout);
		Assert.False(deadline.Expired());
		clock.Advance(TimeSpan.FromMilliseconds(1));
		Assert.True(deadline.Expired());
	}

	[Fact]
	public void SlowButSteadyProgress_NeverExpires() {
		// 40 steps of 10 s each: 400 s for one position, as a background scan's capped processor
		// can take on a 4K HEVC clip, and never 15 s without a step.
		var clock = new ManualClock();
		var deadline = new ProgressDeadline(Timeout, clock);

		for (int step = 0; step < 40; step++) {
			Assert.False(deadline.Expired(), $"expired at step {step}, {step * 10} s in");
			clock.Advance(TimeSpan.FromSeconds(10));
			Assert.False(deadline.Expired(), $"expired at step {step}, {(step + 1) * 10} s in");
			deadline.Progress();
		}
	}

	[Fact]
	public void DecodeTimeBetweenAsks_DoesNotCount() {
		// The decoder works between FFmpeg's asks (one packet, however long it takes); only the waiting
		// FFmpeg asks about counts. A minute of decoding after progress doesn't use up the timeout.
		var clock = new ManualClock();
		var deadline = new ProgressDeadline(Timeout, clock);
		Assert.False(deadline.Expired());

		deadline.Progress();
		clock.Advance(TimeSpan.FromMinutes(1));
		Assert.False(deadline.Expired());
		clock.Advance(TimeSpan.FromSeconds(14));
		Assert.False(deadline.Expired());
	}

	[Fact]
	public void ProgressThenStall_Expires() {
		// It keeps the protection against a file that truly hangs: after the last step, the timeout.
		var clock = new ManualClock();
		var deadline = new ProgressDeadline(Timeout, clock);
		for (int step = 0; step < 5; step++) {
			clock.Advance(TimeSpan.FromSeconds(10));
			deadline.Progress();
			Assert.False(deadline.Expired());
		}

		clock.Advance(TimeSpan.FromSeconds(14));
		Assert.False(deadline.Expired());
		clock.Advance(TimeSpan.FromSeconds(2));
		Assert.True(deadline.Expired());
		Assert.True(deadline.Expired()); // and stays expired while nothing moves
	}

	[Fact]
	public void Progress_AfterExpiry_StartsAgain() {
		// One decoder serves every position of a file: a slow seek on one doesn't doom the next.
		var clock = new ManualClock();
		var deadline = new ProgressDeadline(Timeout, clock);
		Assert.False(deadline.Expired());
		clock.Advance(TimeSpan.FromSeconds(20));
		Assert.True(deadline.Expired());

		deadline.Progress();
		Assert.False(deadline.Expired());
	}
}
