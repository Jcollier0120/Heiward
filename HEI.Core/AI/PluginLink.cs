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

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>
	/// A Windows ML plugin loaded through a directory junction in {ai} to its folder, for a plugin that must be loaded from a
	/// path its user can list. Windows ML's Vitis AI plugin (1.8.75.0) compiles the model's NPU kernels with the AIE compiler
	/// built into it (Peano), which opens the plugin's own DLL as its file system (the DLL exports populateFileSystem) by the
	/// DLL's 8.3 short path. A standard user can't list C:\Program Files\WindowsApps, so GetShortPathNameW fails there
	/// (ERROR_ACCESS_DENIED) after writing "C:\PROGRA~1\WindowsApps\", and Peano opens that: "Failed to load VFS from
	/// C:\PROGRA~1\WindowsApps\", every kernel fails to compile and the plugin ends the process (exit 0xFFFFFFFF, on a
	/// Ryzen AI 7 350 with NPU driver 32.0.20102.3930, 2026-10-15). Through a junction the user can list, the same files
	/// compile. Making a junction needs no rights beyond the folder it's made in: no administrator, no developer mode.
	/// </summary>
	internal static partial class PluginLink {
		/// <summary>
		/// <paramref name="libraryPath"/> through a junction at <paramref name="link"/> to its folder, made or re-pointed when
		/// it doesn't point there (a Windows update puts the plugin in a new folder). The plugin's own path when the junction
		/// can't be made (a state folder on a drive without junctions, say): never throws.
		/// </summary>
		public static string Through(string libraryPath, string link) {
			string folder = Path.GetDirectoryName(libraryPath)!;
			try {
				if (!PointsTo(link, folder)) {
					// An old junction (to the plugin's folder before an update) or an empty folder: a junction replaces it.
					if (Directory.Exists(link)) Directory.Delete(link);
					Make(link, folder);
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception) {
				// Another process may have made it meanwhile.
				if (!PointsTo(link, folder)) {
					Logger.Instance.Info($"Couldn't link {link} to {folder} ({e.Message}): the plugin is loaded from its own folder.");
					return libraryPath;
				}
			}
			return Path.Combine(link, Path.GetFileName(libraryPath));
		}

		/// <summary>Whether <paramref name="link"/> is a junction (or a link) to <paramref name="folder"/>.</summary>
		internal static bool PointsTo(string link, string folder) {
			try {
				return new DirectoryInfo(link).LinkTarget is string target &&
					string.Equals(Plain(target), Plain(folder), StringComparison.OrdinalIgnoreCase);
			}
			catch (IOException) { return false; }
			catch (UnauthorizedAccessException) { return false; }

			static string Plain(string path) =>
				Path.TrimEndingDirectorySeparator(path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..] : path);
		}

		const uint GenericWrite = 0x40000000;
		const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
		const uint SetReparsePoint = 0x000900A4; // FSCTL_SET_REPARSE_POINT
		const uint MountPointTag = 0xA0000003; // IO_REPARSE_TAG_MOUNT_POINT: a junction

		[LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
		private static partial SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode,
			IntPtr lpSecurityAttributes, FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

		[LibraryImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize,
			IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

		/// <summary>Makes <paramref name="link"/> a junction to <paramref name="target"/> (an empty folder made into one).</summary>
		internal static void Make(string link, string target) {
			if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Junctions are Windows'.");
			target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
			Directory.CreateDirectory(link);
			using SafeFileHandle folder = CreateFileW(link, GenericWrite, FileShare.None, IntPtr.Zero, FileMode.Open, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
			if (folder.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Couldn't open {link}");
			// REPARSE_DATA_BUFFER for a mount point: the NT path ("\??\C:\…") the junction leads to, then the path it shows.
			byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target), print = Encoding.Unicode.GetBytes(target);
			int paths = substitute.Length + 2 + print.Length + 2;
			var data = new byte[8 + 8 + paths];
			BinaryPrimitives.WriteUInt32LittleEndian(data, MountPointTag);
			BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(8 + paths));
			BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0);
			BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)substitute.Length);
			BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)(substitute.Length + 2));
			BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)print.Length);
			substitute.CopyTo(data, 16);
			print.CopyTo(data, 16 + substitute.Length + 2);
			if (!DeviceIoControl(folder, SetReparsePoint, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
				throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Couldn't link {link} to {target}");
		}
	}
}
