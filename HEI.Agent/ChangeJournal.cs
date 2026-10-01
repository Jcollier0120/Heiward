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
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent {
	/// <summary>A file's or folder's id on its volume: 64 bits on NTFS, 128 on ReFS.</summary>
	readonly record struct FileId(ulong Low, ulong High);

	/// <summary>One change the journal recorded: what changed (<paramref name="Reason"/>, USN_REASON_*), and in which folder.</summary>
	readonly record struct JournalRecord(FileId File, FileId Parent, long Usn, uint Reason, uint Attributes) {
		public bool IsFolder => (Attributes & (uint)FileAttributes.Directory) != 0;
	}

	/// <summary>
	/// A volume's NTFS (or ReFS) change journal, read as a normal user: Windows records every file
	/// created, changed, renamed or deleted, so a scan can learn what changed since the last one
	/// without walking the drive. Read this way, Windows leaves the names out of the records; the
	/// folder each change happened in is found by its id (<see cref="PathOf"/>). Reading the journal
	/// doesn't read the disk when nothing changed: Windows keeps where it ends in memory.
	/// </summary>
	sealed class VolumeJournal : IDisposable {
		const uint FileTraverse = 0x20, FileReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;
		const uint FsctlQueryUsnJournal = 0x000900F4, FsctlReadUnprivilegedUsnJournal = 0x000903AB;

		readonly SafeFileHandle volume, root;

		/// <summary>The journal's instance: a new one (deleted and made again) starts from scratch.</summary>
		public ulong Id { get; }
		/// <summary>The oldest change still in the journal; anything older has been overwritten.</summary>
		public long FirstUsn { get; }
		/// <summary>Where the next change will go: reading up to here reads everything so far.</summary>
		public long NextUsn { get; }

		VolumeJournal(SafeFileHandle volume, SafeFileHandle root, ulong id, long first, long next) {
			this.volume = volume;
			this.root = root;
			Id = id;
			FirstUsn = first;
			NextUsn = next;
		}

		/// <summary>The journal of the volume <paramref name="path"/> is on, or null: no journal (FAT, exFAT, a network drive), or not readable.</summary>
		public static VolumeJournal? Open(string path) {
			string? drive = Path.GetPathRoot(Path.GetFullPath(path));
			if (drive is not { Length: 3 } || drive[1] != ':') return null; // a drive letter: not a share or a mounted folder
			SafeFileHandle volume = CreateFile($@"\\.\{drive[0]}:", FileTraverse, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
			if (volume.IsInvalid) return null;
			var data = new byte[80];
			if (!DeviceIoControl(volume, FsctlQueryUsnJournal, null, 0, data, data.Length, out int got, IntPtr.Zero) || got < 24) {
				volume.Dispose();
				return null;
			}
			// OpenFileById needs a handle to anything on the volume: its root folder.
			SafeFileHandle root = CreateFile(drive, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
			if (root.IsInvalid) {
				volume.Dispose();
				return null;
			}
			return new VolumeJournal(volume, root, BinaryPrimitives.ReadUInt64LittleEndian(data),
				BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16)));
		}

		/// <summary>
		/// The changes from <paramref name="from"/> up to <see cref="NextUsn"/> as it was when the journal
		/// was opened. Null when they can't all be read (the journal moved on past <paramref name="from"/>).
		/// </summary>
		public List<JournalRecord>? Read(long from) {
			if (from < FirstUsn || from > NextUsn) return null;
			var records = new List<JournalRecord>();
			var input = new byte[48];
			var output = new byte[1 << 16];
			long usn = from;
			while (usn < NextUsn) {
				BinaryPrimitives.WriteInt64LittleEndian(input, usn);
				BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 0xFFFFFFFF); // every reason
				BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12), 0);         // not only when the file is closed
				BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16), 0);         // don't wait for more
				BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24), 0);
				BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), Id);
				BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(40), 2);         // USN_RECORD_V2 ...
				BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(42), 3);         // ... or V3 (128-bit ids)
				if (!DeviceIoControl(volume, FsctlReadUnprivilegedUsnJournal, input, input.Length, output, output.Length, out int got, IntPtr.Zero))
					return null;
				long next = BinaryPrimitives.ReadInt64LittleEndian(output);
				for (int pos = 8; pos + 8 <= got;) {
					ReadOnlySpan<byte> r = output.AsSpan(pos, got - pos);
					int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(r);
					if (length < 8 || length > r.Length) return null;
					if (Parse(r[..length]) is { } record && record.Usn < NextUsn)
						records.Add(record);
					pos += length;
				}
				if (next <= usn || got <= 8) break;
				usn = next;
			}
			return records;
		}

		static JournalRecord? Parse(ReadOnlySpan<byte> r) {
			ushort major = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
			if (major == 2 && r.Length >= 60)
				return new JournalRecord(new(BinaryPrimitives.ReadUInt64LittleEndian(r[8..]), 0), new(BinaryPrimitives.ReadUInt64LittleEndian(r[16..]), 0),
					BinaryPrimitives.ReadInt64LittleEndian(r[24..]), BinaryPrimitives.ReadUInt32LittleEndian(r[40..]), BinaryPrimitives.ReadUInt32LittleEndian(r[52..]));
			if (major == 3 && r.Length >= 76)
				return new JournalRecord(
					new(BinaryPrimitives.ReadUInt64LittleEndian(r[8..]), BinaryPrimitives.ReadUInt64LittleEndian(r[16..])),
					new(BinaryPrimitives.ReadUInt64LittleEndian(r[24..]), BinaryPrimitives.ReadUInt64LittleEndian(r[32..])),
					BinaryPrimitives.ReadInt64LittleEndian(r[40..]), BinaryPrimitives.ReadUInt32LittleEndian(r[56..]), BinaryPrimitives.ReadUInt32LittleEndian(r[68..]));
			return null;
		}

		/// <summary>
		/// The full path of the file or folder with this id, or null when it's gone or can't be opened;
		/// <paramref name="denied"/> says which: Windows' own folders and other accounts' aren't for this
		/// user to open, and a scan's walk can't go into them either.
		/// </summary>
		public string? PathOf(FileId id, out bool denied) {
			denied = false;
			var descriptor = new byte[24];
			BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 24);
			BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), id.High == 0 ? 0u : 2u); // FileIdType, or ExtendedFileIdType
			BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), id.Low);
			BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(16), id.High);
			using SafeFileHandle handle = OpenFileById(root, descriptor, FileReadAttributes, ShareAll, IntPtr.Zero, BackupSemantics);
			if (handle.IsInvalid) {
				denied = Marshal.GetLastWin32Error() == 5; // ERROR_ACCESS_DENIED
				return null;
			}
			var path = new StringBuilder(1024);
			uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
			if (length == 0 || length >= path.Capacity) return null;
			string full = path.ToString();
			return full.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? null : full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full[4..] : full;
		}

		public void Dispose() {
			root.Dispose();
			volume.Dispose();
		}

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern SafeFileHandle OpenFileById(SafeFileHandle hint, byte[] id, uint access, uint share, IntPtr security, uint flags);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
	}
}
