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
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent {
	/// <summary>
	/// Folders by where they really are. One folder can have several paths: a drive mounted in a folder (C:\Projects
	/// is the drive D:\), a junction (C:\worktrees is E:\worktrees), a symbolic link. Windows' parts don't agree on
	/// which they give: git, the change journal and a process's open folder give the real one; settings, %TEMP% and
	/// Claude Code the one as written. Compared as written, two paths of one folder silently differ, so paths are
	/// compared only by their real paths, ignoring case (the Steward's kit, spec/DEV-DRIVES.md).
	/// </summary>
	static class RealPath {
		/// <summary>
		/// <paramref name="path"/> where it really is: through every junction, symbolic link and folder a drive is
		/// mounted in, with the drive letter of the drive it's on, without a trailing separator (a drive's root keeps
		/// its own). For a path that doesn't exist (yet), the deepest folder above it that does, resolved, with the rest
		/// as written. Left as it is: a network path (\\server\share, \\wsl.localhost\..., a network drive), since
		/// resolving one can wake a server or WSL; a drive's root; and whatever can't be resolved.
		/// </summary>
		public static string Of(string path) {
			string full;
			try { full = Path.GetFullPath(path); }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
			full = Path.TrimEndingDirectorySeparator(full);
			if (full.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(full) is not { Length: 3 } root || root[1] != ':')
				return full;
			if (GetDriveType(root) is not (DriveFixed or DriveRemovable or DriveRamDisk))
				return full; // a network drive, or no drive at all
			var rest = new List<string>();
			for (string current = full; ; ) {
				if (current.Length <= root.Length) return Join(full[..root.Length], rest);
				if (Final(current) is { } final) return Join(final, rest);
				rest.Add(Path.GetFileName(current));
				current = Path.GetDirectoryName(current) ?? root;
			}
		}

		/// <summary>The two paths are the same folder (or file): their real paths match, ignoring case.</summary>
		public static bool Same(string a, string b) => string.Equals(Of(a), Of(b), StringComparison.OrdinalIgnoreCase);

		/// <summary><paramref name="path"/> is <paramref name="folder"/> or inside it, by their real paths.</summary>
		public static bool IsUnder(string path, string folder) => ListingPlan.IsUnder(Of(path), Of(folder));

		/// <summary>
		/// The drive <paramref name="path"/> is really on, by its root ("D:\"): through a folder a drive is mounted in,
		/// or a junction. A drive mounted only in a folder, with no letter, is named by that folder ("C:\Mnt\"). A
		/// drive's root is answered without reading anything; null for a network path or what Windows can't place.
		/// </summary>
		public static string? VolumeOf(string path) {
			string full;
			try { full = Path.GetFullPath(path); }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
			if (full.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(full) is not { Length: 3 } root || root[1] != ':') return null;
			if (Path.TrimEndingDirectorySeparator(full).Length <= 3 || GetDriveType(root) is not (DriveFixed or DriveRemovable or DriveRamDisk))
				return root.ToUpperInvariant(); // the drive's root itself, or a network drive: by its letter
			// Windows follows junctions and the folders drives are mounted in, to the deepest mount on the way.
			var buffer = new char[Math.Max(full.Length + 2, 1024)];
			SetThreadErrorMode(FailCriticalErrors, out uint mode);
			bool placed;
			try { placed = GetVolumePathName(full, buffer, buffer.Length); }
			finally { SetThreadErrorMode(mode, out _); }
			if (!placed) return null;
			string mount = new string(buffer, 0, Array.IndexOf(buffer, '\0') is int end and >= 0 ? end : buffer.Length);
			if (mount.Length == 3 && mount[1] == ':') return mount.ToUpperInvariant();
			return MountsOf(mount).FirstOrDefault(m => m.Length == 3 && m[1] == ':')?.ToUpperInvariant() ?? mount;
		}

		/// <summary>
		/// The paths <paramref name="path"/> has that Windows can tell: as written, its real path, and its real path
		/// through each other folder its drive is mounted in (E:\Temp\x is also C:\DevData\Temp\x when E:\ is mounted in
		/// C:\DevData). A junction pointing into it isn't known: nothing records which junctions point where.
		/// </summary>
		public static List<string> Spellings(string path) {
			var all = new List<string>();
			void Add(string p) {
				if (!all.Contains(p, StringComparer.OrdinalIgnoreCase)) all.Add(p);
			}
			try { Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))); }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return all; }
			string real = Of(path);
			Add(real);
			if (Path.GetPathRoot(real) is { Length: 3 } root && root[1] == ':')
				foreach (string mount in MountsOf(root).Where(m => !m.Equals(root, StringComparison.OrdinalIgnoreCase)))
					Add(Path.TrimEndingDirectorySeparator(Path.Join(mount, real[root.Length..])));
			return all;
		}

		/// <summary>
		/// The fixed drives that have no letter, each by the first folder it's mounted in (a Dev Drive mounted only in
		/// C:\Projects): DriveInfo lists drives by their letters, and a walk of C:\ doesn't go into a mounted drive.
		/// </summary>
		public static List<string> DrivesWithoutLetter() {
			var found = new List<string>();
			var name = new char[64];
			IntPtr find = FindFirstVolume(name, name.Length);
			if (find == new IntPtr(-1)) return found;
			try {
				do {
					string volume = new string(name, 0, Array.IndexOf(name, '\0') is int end and >= 0 ? end : name.Length);
					if (GetDriveType(volume) != DriveFixed) continue;
					var paths = new char[4096];
					if (!GetVolumePathNamesForVolumeName(volume, paths, paths.Length, out _)) continue;
					List<string> mounts = new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
					if (mounts.Count == 0 || mounts.Any(m => m.Length == 3 && m[1] == ':')) continue; // never mounted, or it has a letter
					string folder = Path.TrimEndingDirectorySeparator(mounts[0]);
					if (Directory.Exists(folder)) found.Add(Of(folder));
				} while (FindNextVolume(find, name, name.Length));
			}
			finally {
				FindVolumeClose(find);
			}
			return found;
		}

		/// <summary>Each place the drive mounted at <paramref name="mount"/> is mounted ("D:\", "C:\Projects\"); none when Windows can't say.</summary>
		static List<string> MountsOf(string mount) {
			var mounts = new List<string>();
			var name = new char[64];
			if (!GetVolumeNameForVolumeMountPoint(Path.EndsInDirectorySeparator(mount) ? mount : mount + '\\', name, name.Length)) return mounts;
			string volume = new string(name, 0, Array.IndexOf(name, '\0') is int end and >= 0 ? end : name.Length);
			var paths = new char[4096];
			if (!GetVolumePathNamesForVolumeName(volume, paths, paths.Length, out _)) return mounts;
			foreach (string p in new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries)) mounts.Add(p);
			return mounts;
		}

		static string Join(string folder, List<string> rest) {
			string joined = folder;
			for (int i = rest.Count - 1; i >= 0; i--) joined = Path.Join(joined, rest[i]);
			return Path.GetPathRoot(joined) == joined ? joined : Path.TrimEndingDirectorySeparator(joined);
		}

		/// <summary>Where Windows says the file or folder at <paramref name="path"/> is, after opening it; null when it can't be opened.</summary>
		static string? Final(string path) {
			string open = path.Length >= 248 ? @"\\?\" + path : path;
			// A card reader with no card answers with an error, not a "no disk" dialog.
			SetThreadErrorMode(FailCriticalErrors, out uint mode);
			SafeFileHandle handle;
			try { handle = CreateFile(open, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero); }
			finally { SetThreadErrorMode(mode, out _); }
			using (handle)
				return handle.IsInvalid ? null : FinalOf(handle, path.Length);
		}

		static string? FinalOf(SafeFileHandle handle, int pathLength) {
			var buffer = new char[Math.Max(pathLength * 2, 512)];
			uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
			if (length >= buffer.Length) {
				buffer = new char[length + 1];
				length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
			}
			if (length == 0 || length >= buffer.Length) return null;
			string final = new string(buffer, 0, (int)length);
			if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + final[8..];
			return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
		}

		const uint FileReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000, FailCriticalErrors = 1;
		const uint DriveRemovable = 2, DriveFixed = 3, DriveRamDisk = 6;

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool SetThreadErrorMode(uint mode, out uint oldMode);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint size, uint flags);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern bool GetVolumePathName(string path, [Out] char[] volumePath, int size);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern bool GetVolumeNameForVolumeMountPoint(string mount, [Out] char[] volume, int size);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern bool GetVolumePathNamesForVolumeName(string volume, [Out] char[] paths, int size, out int needed);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		static extern uint GetDriveType(string root);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FindFirstVolumeW")]
		static extern IntPtr FindFirstVolume([Out] char[] volume, int size);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FindNextVolumeW")]
		static extern bool FindNextVolume(IntPtr find, [Out] char[] volume, int size);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool FindVolumeClose(IntPtr find);
	}
}
