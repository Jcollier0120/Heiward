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
	/// For a scan of another PC's files: each of them has a path here (its stand-in), but no file of its own until bytes of
	/// it are fetched for a read, and none again once they're let go. Its size and dates are the listing's
	/// (<see cref="About"/>): whatever asks whether a file is there, or its size and time, asks <see cref="Exists"/> or
	/// <see cref="FactsOf"/>, never the disk. Every reader that opens a photo or a file's ends by its path asks
	/// <see cref="Ready(string)"/> first, so what it reads is fetched, never read as zeros or missing.
	/// Null <see cref="Fetch"/> and <see cref="About"/> (every scan of this PC's own files): every file is as the disk has it.
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

		/// <summary>Whether a file is there, its size and its dates: what a scan asks of a file before it reads it.</summary>
		public readonly record struct FileFacts(bool Exists, long Length, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc);

		/// <summary>
		/// What the listing of another PC says of <c>path</c>: one of its files as listed, a path under its stand-ins that
		/// isn't listed as not there; null for a path that isn't another PC's, which the disk is asked about.
		/// </summary>
		public static Func<string, FileFacts?>? About { get; set; }

		/// <summary>Whether the file is there: another PC's as listed, this PC's on its disk.</summary>
		public static bool Exists(string path) => About?.Invoke(path) is FileFacts f ? f.Exists : File.Exists(path);

		/// <summary>The file's facts: another PC's as listed, this PC's as its disk has them (not there: <see cref="FileFacts.Exists"/> false).</summary>
		public static FileFacts FactsOf(string path) {
			if (About?.Invoke(path) is FileFacts listed) return listed;
			var file = new FileInfo(path);
			return file.Exists ? new FileFacts(true, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc) : default;
		}
	}
}
