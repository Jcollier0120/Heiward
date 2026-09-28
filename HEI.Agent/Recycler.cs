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
using System.Text;
using Microsoft.Win32;
using HEI.Core.Utils;

namespace HEI.Agent {
	sealed record RecycleFailure(string Path, string Reason);
	sealed record RecycleResult(List<string> Recycled, List<RecycleFailure> Failed, long RecycledBytes);

	/// <summary>
	/// Moves files the user ticked to the Recycle Bin — and only there. With "no confirmation", the
	/// shell silently deletes for good whatever the bin can't take (network and removable drives,
	/// files bigger than the bin, volumes set to "don't move files to the Recycle Bin"), so every
	/// such file is refused up front instead. Also refused: files outside the group, files changed
	/// since the scan, and any request that would leave the group without a single copy.
	/// </summary>
	static class Recycler {
		public static RecycleResult Recycle(ReportGroup group, IReadOnlyCollection<string> requested) {
			var failed = new List<RecycleFailure>();
			var byPath = group.Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
			var chosen = requested.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

			foreach (string p in chosen.Where(p => !byPath.ContainsKey(p)).ToList()) {
				failed.Add(new(p, "not part of this group"));
				chosen.Remove(p);
			}
			// At least one copy must survive, and it must still be on disk.
			var survivors = group.Items.Where(i => !chosen.Contains(i.Path, StringComparer.OrdinalIgnoreCase) && File.Exists(i.Path)).ToList();
			if (survivors.Count == 0) {
				failed.AddRange(chosen.Select(p => new RecycleFailure(p, "that would remove every copy; keep at least one")));
				return new(new(), failed, 0);
			}

			var ok = new List<string>();
			foreach (string p in chosen) {
				string? why = RefuseReason(p, byPath[p]);
				if (why != null) failed.Add(new(p, why));
				else ok.Add(p);
			}
			if (ok.Count == 0)
				return new(new(), failed, 0);

			var op = new FileUtils.SHFILEOPSTRUCT {
				wFunc = FileUtils.FileOperationType.FO_DELETE,
				pFrom = string.Join('\0', ok) + "\0\0",
				fFlags = FileUtils.FileOperationFlags.FOF_ALLOWUNDO | FileUtils.FileOperationFlags.FOF_NOCONFIRMATION |
						 FileUtils.FileOperationFlags.FOF_NOERRORUI | FileUtils.FileOperationFlags.FOF_SILENT,
			};
			int rc = FileUtils.SHFileOperation(ref op);
			var recycled = new List<string>();
			long bytes = 0;
			foreach (string p in ok) {
				if (File.Exists(p))
					failed.Add(new(p, rc != 0 ? $"Windows refused (error {rc})" : "still there after the move"));
				else {
					recycled.Add(p);
					bytes += byPath[p].Size;
				}
			}
			AgentPaths.AppendLog($"recycled {recycled.Count} file(s) ({bytes:N0} bytes) from group {group.Key}; {failed.Count} refused");
			return new(recycled, failed, bytes);
		}

		/// <summary>Why <paramref name="path"/> must not go through the shell's delete, or null when it may.</summary>
		static string? RefuseReason(string path, ReportItem item) {
			var fi = new FileInfo(path);
			if (!fi.Exists)
				return "already gone";
			if (fi.Length != item.Size || Math.Abs((fi.LastWriteTimeUtc - item.ModifiedUtc).TotalSeconds) > 2)
				return "changed since the scan; scan again first";
			string? root = Path.GetPathRoot(fi.FullName);
			if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
				return "on a network location, which has no Recycle Bin";
			var drive = new DriveInfo(root);
			if (drive.DriveType != DriveType.Fixed)
				return $"on a {drive.DriveType.ToString().ToLowerInvariant()} drive, which has no Recycle Bin";
			(bool nuke, long maxBytes) = BinSettings(root, drive);
			if (nuke)
				return "this drive is set to delete files immediately instead of using the Recycle Bin";
			if (fi.Length > maxBytes)
				return $"too large for this drive's Recycle Bin ({maxBytes / (1 << 20):N0} MB); delete it yourself if you're sure";
			return null;
		}

		/// <summary>
		/// The volume's Recycle Bin settings from HKCU\...\BitBucket\Volume\{guid}: NukeOnDelete and
		/// MaxCapacity (MB). Without them, assume Windows' default cap, conservatively 5% of the volume.
		/// </summary>
		static (bool Nuke, long MaxBytes) BinSettings(string root, DriveInfo drive) {
			long fallback = drive.TotalSize / 20;
			try {
				var sb = new StringBuilder(64);
				if (!GetVolumeNameForVolumeMountPoint(root, sb, sb.Capacity))
					return (false, fallback);
				// "\\?\Volume{guid}\" -> "{guid}"
				string name = sb.ToString();
				int open = name.IndexOf('{'), close = name.IndexOf('}');
				if (open < 0 || close < open)
					return (false, fallback);
				using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
					$@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{name[open..(close + 1)]}");
				if (key == null)
					return (false, fallback);
				bool nuke = key.GetValue("NukeOnDelete") is int n && n != 0;
				long max = key.GetValue("MaxCapacity") is int mb && mb > 0 ? (long)mb << 20 : fallback;
				return (nuke, max);
			}
			catch {
				return (false, fallback);
			}
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int bufferLength);
	}
}
