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

using HEI.Core.Utils;

namespace HEI.Core.Tests;

public class OrderedParallelTests {

	[Theory]
	[InlineData(1)]
	[InlineData(8)]
	public void ConsumesEveryRowInOrder_OnTheCallingThread(int parallelism) {
		int caller = Environment.CurrentManagedThreadId;
		var consumed = new List<int>();
		var rng = new Random(1);
		int[] delays = Enumerable.Range(0, 200).Select(_ => rng.Next(3)).ToArray();
		OrderedParallel.For(200, parallelism, window: 16,
			i => { Thread.Sleep(delays[i]); return i * 10; },
			(i, result) => {
				Assert.Equal(caller, Environment.CurrentManagedThreadId);
				Assert.Equal(i * 10, result);
				consumed.Add(i);
			},
			CancellationToken.None);
		Assert.Equal(Enumerable.Range(0, 200), consumed);
	}

	[Fact]
	public void RowsRunAtMostAWindowAheadOfTheOneConsumed() {
		// The row being consumed counts from when it's handed over (before consume starts), so
		// row 0 is "being consumed" from the start.
		int consuming = 0, furthestAhead = 0;
		OrderedParallel.For(300, parallelism: 8, window: 12,
			i => {
				int ahead = i - Volatile.Read(ref consuming);
				int seen;
				while ((seen = Volatile.Read(ref furthestAhead)) < ahead && Interlocked.CompareExchange(ref furthestAhead, ahead, seen) != seen) { }
				return i;
			},
			(i, _) => { if (i % 50 == 0) Thread.Sleep(20); Volatile.Write(ref consuming, i + 1); },
			CancellationToken.None);
		Assert.InRange(furthestAhead, 1, 12);
	}

	[Fact]
	public void AWorkersException_ReachesTheCaller() {
		var e = Assert.Throws<InvalidOperationException>(() =>
			OrderedParallel.For(100, parallelism: 4, window: 8,
				i => i == 37 ? throw new InvalidOperationException("row 37") : i,
				(_, _) => { },
				CancellationToken.None));
		Assert.Equal("row 37", e.Message);
	}

	[Fact]
	public void Cancelled_StopsAndThrows() {
		using var cts = new CancellationTokenSource();
		int consumed = 0;
		Assert.ThrowsAny<OperationCanceledException>(() =>
			OrderedParallel.For(10_000, parallelism: 4, window: 8,
				i => i,
				(i, _) => { if (++consumed == 50) cts.Cancel(); },
				cts.Token));
		Assert.InRange(consumed, 50, 60);
	}
}
