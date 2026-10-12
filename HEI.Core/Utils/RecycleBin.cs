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

using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace HEI.Core.Utils {
	/// <summary>
	/// Whether Windows' Recycle Bin can take a file or folder. The shell's delete, as Heiward calls it (FOF_ALLOWUNDO with no
	/// confirmation), deletes for good whatever the bin can't take, and a batch that overfills a drive's bin makes Windows
	/// delete its oldest items for good, the person's own deletes among them. So each is checked first, and refused rather
	/// than lost. The agent, the web page and the desktop app all ask here.
	/// </summary>
	[SupportedOSPlatform("windows")]
	public static class RecycleBin {
		/// <summary>
		/// Why the Recycle Bin can't take <paramref name="fullPath"/> (<paramref name="bytes"/> in all), so the shell would delete it
		/// for good: on a network or removable drive, a drive set to delete at once, or too big for its bin. Null when it can.
		/// </summary>
		/// <param name="room">
		/// The room left in each drive's bin for this batch: what's already in the bin counts, so a batch never pushes the
		/// bin over its size. Null: only the bin's size counts.
		/// </param>
		public static string? Refusal(string fullPath, long bytes, Room? room = null) {
			string? root = Path.GetPathRoot(fullPath);
			if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
				return "on a network location, which has no Recycle Bin";
			var drive = new DriveInfo(root);
			if (drive.DriveType != DriveType.Fixed)
				return $"on a {drive.DriveType.ToString().ToLowerInvariant()} drive, which has no Recycle Bin";
			(bool nuke, long maxBytes) = Settings(root, drive);
			if (nuke)
				return "this drive is set to delete files immediately instead of using the Recycle Bin";
			if (bytes > maxBytes)
				return $"too large for this drive's Recycle Bin ({maxBytes / (1 << 20):N0} MB); delete it yourself if you're sure";
			if (room != null && !room.Take(root, maxBytes, bytes))
				return "the Recycle Bin on this drive is nearly full, and Windows would delete what's already in it for good to make room; empty it, then try again";
			return null;
		}

		/// <summary>The room left in each drive's Recycle Bin, counted down as a batch fills it.</summary>
		public class Room {
			readonly Dictionary<string, long> left = new(StringComparer.OrdinalIgnoreCase);
			readonly Func<string, long> used;

			/// <param name="used">What a drive's bin holds now; the shell's own count (<see cref="Used"/>) when null.</param>
			public Room(Func<string, long>? used = null) => this.used = used ?? Used;

			/// <summary>Takes <paramref name="bytes"/> of the bin's room on <paramref name="root"/>; false, taking nothing, when they don't fit.</summary>
			public bool Take(string root, long maxBytes, long bytes) {
				if (!left.TryGetValue(root, out long room)) room = maxBytes - used(root);
				if (bytes > room) {
					left[root] = room;
					return false;
				}
				left[root] = room - bytes;
				return true;
			}
		}

		/// <summary>The first path below <paramref name="folder"/> longer than the Recycle Bin takes (259 characters), or null.</summary>
		public static string? TooLongInside(string folder) {
			var all = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
			try { return Directory.EnumerateFileSystemEntries(folder, "*", all).FirstOrDefault(e => e.Length > 259); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return folder; }
		}

		/// <summary>
		/// The files of <paramref name="paths"/> the bin can take, and why each other one can't, for one batch of the shell's
		/// delete: what's refused stays where it is.
		/// </summary>
		public static (List<string> Ok, Dictionary<string, string> Refused) Sort(IEnumerable<string> paths, Room? room = null) {
			room ??= new Room();
			var ok = new List<string>();
			var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (string p in paths) {
				string? why;
				try {
					var fi = new FileInfo(p);
					why = Refusal(fi.FullName, fi.Length, room);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { why = e.Message; }
				if (why == null) ok.Add(p);
				else refused[p] = why;
			}
			return (ok, refused);
		}

		/// <summary>What the Recycle Bin on the drive <paramref name="root"/> holds now, in bytes; 0 when Windows can't say.</summary>
		public static long Used(string root) {
			try {
				var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
				return SHQueryRecycleBin(root, ref info) == 0 ? Math.Max(0, info.i64Size) : 0;
			}
			catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException) {
				return 0;
			}
		}

		/// <summary>
		/// The volume's Recycle Bin settings from HKCU\...\BitBucket\Volume\{guid}: NukeOnDelete and
		/// MaxCapacity (MB). Without them, assume Windows' default cap, conservatively 5% of the volume.
		/// </summary>
		static (bool Nuke, long MaxBytes) Settings(string root, DriveInfo drive) {
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

		[StructLayout(LayoutKind.Sequential)]
		struct SHQUERYRBINFO {
			public int cbSize;
			public long i64Size;
			public long i64NumItems;
		}

		[DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
		static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int bufferLength);
	}
}
