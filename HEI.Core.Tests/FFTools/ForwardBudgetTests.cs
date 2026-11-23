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

using HEI.Core.FFTools;
using HEI.Core.FFTools.FFmpegNative;

namespace HEI.Core.Tests.FFTools;

/// <summary>
/// What reaching one position of a video may cost: frames decoded on from its keyframe, and time. A video with one keyframe
/// for the whole of it took 21 minutes of a scan of another PC, and wasn't sampled in the end.
/// </summary>
public class ForwardBudgetTests {
	/// <summary>A clock that moves only when told to.</summary>
	sealed class Clock : TimeProvider {
		long now;
		public override long TimestampFrequency => TimeSpan.TicksPerSecond;
		public override long GetTimestamp() => now;
		public void Advance(TimeSpan by) => now += by.Ticks;
	}

	[Fact]
	public void PastItsFrames_APositionIsntReached() {
		var budget = new ForwardBudget(maxFrames: 3, clock: new Clock());
		budget.Start();
		for (int i = 0; i < 3; i++) budget.Decode(10);
		var e = Assert.Throws<KeyframesTooFarException>(() => budget.Decode(10));
		Assert.Contains("keyframes are too far apart", e.Message);
		Assert.Contains("4 frames", e.Message);
	}

	[Fact]
	public void PastItsTime_APositionIsntReached_HoweverFewFrames() {
		var clock = new Clock();
		var budget = new ForwardBudget(maxFrames: 1_000, maxTime: TimeSpan.FromSeconds(45), clock: clock);
		budget.Start();
		budget.Decode(10);
		clock.Advance(TimeSpan.FromSeconds(44));
		budget.Decode(10);
		clock.Advance(TimeSpan.FromSeconds(2));
		Assert.Throws<KeyframesTooFarException>(() => budget.Decode(10));
	}

	[Fact]
	public void EachPosition_HasABudgetOfItsOwn() {
		var clock = new Clock();
		var budget = new ForwardBudget(maxFrames: 3, maxTime: TimeSpan.FromSeconds(10), clock: clock);
		budget.Start();
		for (int i = 0; i < 3; i++) budget.Decode(10);
		clock.Advance(TimeSpan.FromSeconds(9));
		budget.Start(); // the next position: nothing decoded for it yet
		Assert.Equal(0, budget.Decoded);
		budget.Decode(20);
		budget.Decode(20);
		clock.Advance(TimeSpan.FromSeconds(9));
		budget.Decode(20); // its third frame, 9 s on, not 18: the frames and the time are the position's
		Assert.Throws<KeyframesTooFarException>(() => budget.Decode(20));
	}

	[Fact]
	public void TheDefault_ReachesAnyCameraOrRecordersKeyframes() {
		// x264 writes one at least every 250 frames, phones and game captures every second or two: far within it.
		Assert.True(ForwardBudget.DefaultFrames >= 1_000);
		Assert.True(ForwardBudget.DefaultTime >= TimeSpan.FromSeconds(30));
	}

	[Fact]
	public void TooFar_IsTheFilesOwnMake_WithAHintOfItsOwn() {
		string? hint = FfmpegErrorClassifier.HintFor(FfmpegErrorCategory.KeyframesTooFar);
		Assert.NotNull(hint);
		Assert.Contains("keyframes", hint);
	}
}
