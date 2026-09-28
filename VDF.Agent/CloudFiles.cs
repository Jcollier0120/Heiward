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
using System.Runtime.InteropServices;

namespace VDF.Agent {
	/// <summary>
	/// Whether a file lives in a cloud-synced folder (iCloud Photos, OneDrive, Dropbox: anything on the
	/// Windows Cloud Files API). Deleting such a file deletes it from the cloud and every other device
	/// too, so the review page warns and asks before it goes.
	/// Asks Windows whether the folder is inside a registered sync root. The file's own reparse tag is
	/// no use here: Windows disguises downloaded cloud files as plain files to most processes (a .NET
	/// process sees attributes 0x20 where PowerShell 5.1 sees ReparsePoint + IO_REPARSE_TAG_CLOUD_6).
	/// </summary>
	static class CloudFiles {
		static readonly ConcurrentDictionary<string, bool> byFolder = new(StringComparer.OrdinalIgnoreCase);

		[DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
		static extern int CfGetSyncRootInfoByPath(string filePath, int infoClass, out long info, uint infoLength, out uint returnedLength);

		public static bool IsSynced(string path) {
			string? folder = Path.GetDirectoryName(path);
			if (string.IsNullOrEmpty(folder)) return false;
			return byFolder.GetOrAdd(folder, static f => {
				try {
					// CF_SYNC_ROOT_INFO_BASIC (0): just the sync root's file id; S_OK means "under a sync root".
					return CfGetSyncRootInfoByPath(f, 0, out _, sizeof(long), out _) >= 0;
				}
				catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) {
					return false; // Windows before 10 1709 has no Cloud Files API
				}
			});
		}
	}
}
