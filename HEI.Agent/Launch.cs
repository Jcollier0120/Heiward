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

using System.Diagnostics;

namespace HEI.Agent {
	/// <summary>
	/// A scan or developer check the review page has just started, until its process holds its lock.
	/// `hei scan` is a process of its own: between starting it and its taking scan.lock there's a moment,
	/// longer on a cold start, when nothing looks like it's running. A page that polled then went back to
	/// its slow poll and showed the scan up to 15 seconds later. While a launch is starting, the page shows
	/// it as running.
	/// </summary>
	sealed class Launch {
		/// <summary>A process that hasn't taken its lock by then isn't going to.</summary>
		public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

		readonly object gate = new();
		Func<bool>? alive;
		DateTime since;
		IReadOnlyList<string>? roots;

		/// <summary>Remembers a process just started. <paramref name="roots"/>: what it reads, when that isn't every drive.</summary>
		public void Started(Process? process, DateTime nowUtc, IReadOnlyList<string>? roots = null) {
			if (process == null) return;
			Started(() => {
				try { return !process.HasExited; }
				catch (InvalidOperationException) { return false; }
			}, nowUtc, roots);
		}

		public void Started(Func<bool> isAlive, DateTime nowUtc, IReadOnlyList<string>? roots = null) {
			lock (gate) {
				alive = isAlive;
				since = nowUtc;
				this.roots = roots;
			}
		}

		/// <summary>
		/// True from the start until its process takes the lock (<paramref name="locked"/>), ends without
		/// taking it, or <see cref="Limit"/> passes. Then it's forgotten, so it never outlives that moment.
		/// </summary>
		public bool Starting(bool locked, DateTime nowUtc) {
			lock (gate) {
				if (alive == null) return false;
				if (locked || nowUtc - since > Limit || !alive()) {
					alive = null;
					roots = null;
					return false;
				}
				return true;
			}
		}

		/// <summary>What the starting process reads, when that isn't every drive (a drive scanned only when asked).</summary>
		public IReadOnlyList<string>? Roots {
			get { lock (gate) return roots; }
		}
	}
}
