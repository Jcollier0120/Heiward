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

namespace HEI.Core.Utils {
	/// <summary>
	/// For a scan of another PC's files: each of them is a stand-in here, its size and dates as there, holding only the
	/// bytes fetched for a read and emptied once read. Every reader that opens a photo or a file's ends by its path asks
	/// <see cref="Ready(string)"/> first, so a stand-in emptied since is fetched again rather than read as zeros.
	/// Null <see cref="Fetch"/> (every scan of this PC's own files): every file is ready as it is.
	/// </summary>
	public static class RemoteFiles {
		/// <summary>
		/// Fetches what a read of <c>path</c> needs, from <c>offset</c>, <c>count</c> bytes (-1: the whole file); true once
		/// it's there, or for a file that isn't another PC's; false when it couldn't be fetched, so the read fails as for a
		/// file that can't be opened.
		/// </summary>
		public static Func<string, long, long, bool>? Fetch { get; set; }

		/// <summary>The whole file is there to read (another PC's fetched, if it wasn't).</summary>
		public static bool Ready(string path) => Fetch?.Invoke(path, 0, -1) ?? true;

		/// <summary>Bytes <paramref name="offset"/> to <paramref name="offset"/> + <paramref name="count"/> are there to read.</summary>
		public static bool Ready(string path, long offset, long count) => Fetch?.Invoke(path, offset, count) ?? true;
	}
}
