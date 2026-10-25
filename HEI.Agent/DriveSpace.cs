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

using System.Runtime.InteropServices;

namespace HEI.Agent {
	/// <summary>
	/// A drive's free space before and after a cleanup, and once more a while later (<see cref="AutoCleaner.LookAgain"/>).
	/// </summary>
	/// <param name="Drive">The drive the cleaned folder is really on, as <see cref="RealPath.VolumeOf"/> names it ("E:\").</param>
	sealed record DriveFree(string Drive, long Before, long After, long? Later = null, DateTime? LaterUtc = null) {
		/// <summary>"E:\ had 6.5 GB free, 20.1 GB after (38.5 GB later)".</summary>
		public string Describe() =>
			$"{Drive} had {Format.Bytes(Before)} free, {Format.Bytes(After)} after" + (Later is { } later ? $" ({Format.Bytes(later)} later)" : "");
	}

	/// <summary>
	/// Free space, as Windows says it, of the drive a folder is really on: through junctions and the folders drives are mounted in
	/// (C:\DevData\Temp may be the drive E:\'s Temp). A cleanup measures it as well as the files' sizes, which don't say what the
	/// drive got back: on ReFS (a Dev Drive) a file cloned from another shares its blocks, so deleting it frees nothing until the
	/// other goes too, and ReFS gives deleted space back a while after.
	/// </summary>
	static class DriveSpace {
		/// <summary>The drive <paramref name="path"/> is really on, and its free space now; null when Windows can't say.</summary>
		public static (string Drive, long Free)? Of(string path) =>
			RealPath.VolumeOf(path) is { } drive && Free(drive) is { } free ? (drive, free) : null;

		/// <summary>The free space of <paramref name="drive"/> ("E:\", or the folder a drive with no letter is mounted in); null when Windows can't say.</summary>
		public static long? Free(string drive) => GetDiskFreeSpaceEx(drive, out ulong available, out _, out _) ? (long)available : null;

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);
	}
}
