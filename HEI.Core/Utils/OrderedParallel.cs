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

using System.Runtime.ExceptionServices;

namespace HEI.Core.Utils {

	/// <summary>
	/// A parallel loop whose results are used in order: rows are worked on by several threads, and
	/// each row's result goes to the calling thread in row order, whichever thread finished first.
	/// The compare phase finds matching pairs this way. Merging them into groups depends on the
	/// order they arrive in, and merging straight from the workers made that order the threads'.
	/// </summary>
	internal static class OrderedParallel {

		/// <summary>
		/// Runs <paramref name="produce"/> for rows 0 .. <paramref name="count"/> - 1 on up to
		/// <paramref name="parallelism"/> threads, and <paramref name="consume"/> on the calling thread for
		/// each row in order. Rows are handed out in order, and at most <paramref name="window"/> rows run
		/// or wait ahead of the one being consumed, which bounds the results held at once.
		/// </summary>
		/// <exception cref="OperationCanceledException">The token was cancelled; rows already started finish first.</exception>
		public static void For<T>(int count, int parallelism, int window, Func<int, T> produce, Action<int, T> consume, CancellationToken cancellationToken) {
			if (count <= 0) return;
			int workers = Math.Clamp(parallelism, 1, count);
			if (workers == 1) {
				for (int i = 0; i < count; i++) {
					cancellationToken.ThrowIfCancellationRequested();
					consume(i, produce(i));
				}
				return;
			}
			window = Math.Max(window, workers);

			var results = new T[count];
			var done = new bool[count];
			var gate = new object();
			int taken = 0, consumed = 0;
			ExceptionDispatchInfo? failure = null;
			using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			using var wake = stop.Token.Register(() => { lock (gate) Monitor.PulseAll(gate); });

			void Work() {
				try {
					while (true) {
						int i;
						lock (gate) {
							while (taken < count && taken - consumed >= window && !stop.IsCancellationRequested)
								Monitor.Wait(gate);
							if (taken >= count || stop.IsCancellationRequested)
								return;
							i = taken++;
						}
						T result = produce(i);
						lock (gate) {
							results[i] = result;
							done[i] = true;
							Monitor.PulseAll(gate);
						}
					}
				}
				catch (Exception e) {
					lock (gate) failure ??= ExceptionDispatchInfo.Capture(e);
					stop.Cancel();
				}
			}

			var tasks = new Task[workers];
			for (int w = 0; w < workers; w++)
				tasks[w] = Task.Run(Work);
			try {
				for (int i = 0; i < count; i++) {
					T result;
					lock (gate) {
						while (!done[i] && failure == null && !stop.IsCancellationRequested)
							Monitor.Wait(gate);
						if (!done[i])
							break;
						result = results[i];
						results[i] = default!;
						consumed++;
						Monitor.PulseAll(gate);
					}
					consume(i, result);
				}
			}
			finally {
				stop.Cancel(); // a worker waiting for room leaves; one mid-row finishes that row
				Task.WaitAll(tasks);
			}
			failure?.Throw();
			cancellationToken.ThrowIfCancellationRequested();
		}
	}
}
