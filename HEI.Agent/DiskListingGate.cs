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
using System.Collections.Concurrent;
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <summary>
	/// The review page asks for the listings of all the folders open in its tree at once (<c>/api/tree</c>), and each one lists
	/// a folder's subfolders on disk. Separate disks don't slow each other, and an SSD answers several at once. A spinning
	/// disk seeks back and forth under two directory reads at once, though, and is slower than reading them one after the
	/// other. So on a disk that seeks, the listings take turns, as the scan's listing does (<see cref="DriveScanPlanner.GroupRootsByDisk"/>):
	/// <list type="bullet">
	/// <item>A disk that seeks is one Windows says has a seek penalty, or one it can't tell about (<see cref="DriveScanPlanner.ListingSlowness"/>).
	/// Network shares count too.</item>
	/// <item>Drive letters that are partitions of one disk take turns together (<see cref="DriveScanPlanner.QueryPhysicalDisk"/>).</item>
	/// </list>
	/// Both answers come from the volume's metadata, with no read and no elevation, as the scan asks them.
	/// </summary>
	static class DiskListingGate {
		static readonly ConcurrentDictionary<string, SemaphoreSlim> turns = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// <paramref name="list"/> (a listing of <paramref name="path"/>), at once on an SSD, or once the listings before it on its
		/// disk are done on a disk that seeks.
		/// </summary>
		/// <param name="diskOf">The physical disk behind a drive root; by default Windows' answer. Tests give their own.</param>
		/// <param name="slowness">How slow the drive is to walk (0 an SSD); by default Windows' answer. Tests give their own.</param>
		public static async Task<T> RunAsync<T>(string path, Func<T> list, CancellationToken ct = default,
				Func<string, string?>? diskOf = null, Func<string, int>? slowness = null) {
			string mount = MountOf(path);
			if ((slowness ?? DriveScanPlanner.ListingSlowness)(mount) == 0) return list();
			string disk = (diskOf ?? DriveScanPlanner.QueryPhysicalDisk)(mount) ?? "mount:" + mount;
			SemaphoreSlim turn = turns.GetOrAdd(disk, _ => new SemaphoreSlim(1, 1));
			await turn.WaitAsync(ct);
			try {
				return list();
			}
			finally {
				turn.Release();
			}
		}

		/// <summary>The drive root a path is on ("E:\", "\\nas\share"); the path itself when it has none.</summary>
		static string MountOf(string path) {
			try {
				return Path.GetPathRoot(path) is { Length: > 0 } root ? root : path;
			}
			catch (ArgumentException) {
				return path;
			}
		}
	}
}
