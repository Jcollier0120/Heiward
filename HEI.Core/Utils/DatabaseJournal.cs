// /*
//     Copyright (C) 2026 0x90d
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

using System.Buffers;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;

namespace HEI.Core.Utils {
	// The database's journal: what changed since the database was last written whole, appended after its terminator. A
	// save writes only the entries that changed, where it wrote the whole database (hundreds of megabytes for a large
	// library) at every checkpoint and after every scan that changed anything. Builds before the journal stop reading at
	// the terminator, so they read the database as it was last written whole.
	//   magic "HEIJRNL1" | repeated [ kind (byte) | length (int32 LE >= 0) | payload | check (8 bytes) ]
	//   kind 1: an entry (MemoryPack FileEntry, as in the database), replacing the one of its path;
	//   kind 2: an entry gone (its path, UTF-8);
	//   kind 3: the header (Version, ImageHashPipeline: int32 LE each).
	//   check: the first 8 bytes of the SHA-256 of kind, length and payload. The first record that doesn't check (a write
	//   the process didn't live to finish) ends the journal, and the next save cuts it off before appending.
	static partial class DatabaseUtils {
		static ReadOnlySpan<byte> JournalMagic => "HEIJRNL1"u8;
		const byte KindEntry = 1, KindGone = 2, KindHeader = 3;
		const int RecordHead = 1 + sizeof(int), RecordCheck = 8;
		/// <summary>Magic, the two header ints and the terminator: the database with no entries.</summary>
		const int EmptyDatabaseLength = 8 + sizeof(int) * 3;

		/// <summary>
		/// The database is written whole again (its journal folded in) once the journal would reach this share of the
		/// database as last written whole, or the file would be this share larger than the database written whole now
		/// (entries replaced or gone).
		/// </summary>
		internal const double FoldShare = 0.2;

		static readonly StringComparer PathComparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		static Dictionary<string, UInt128> NewEntryHashes() => new(PathComparer);

		/// <summary>An entry's serialized bytes, hashed: a save compares them with what the file has.</summary>
		static UInt128 HashOf(ReadOnlySpan<byte> entry) {
			Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
			SHA256.HashData(entry, hash);
			return BinaryPrimitives.ReadUInt128LittleEndian(hash);
		}

		static void CheckOf(ReadOnlySpan<byte> record, Span<byte> check) {
			Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
			SHA256.HashData(record, hash);
			hash[..RecordCheck].CopyTo(check);
		}

		/// <summary>
		/// The database file as this process last read or wrote it: its stamp, where its whole part and its journal end,
		/// and each entry's <see cref="HashOf"/> as the file has it. Null when the process has done neither, or read an
		/// older format or the temp file: the next save writes the database whole.
		/// </summary>
		sealed class OnDisk {
			internal required string Path;
			internal long Length;
			internal DateTime WrittenUtc;
			/// <summary>Just past the terminator: the database as last written whole.</summary>
			internal required long WholeEnd;
			/// <summary>Just past the journal's last good record; <see cref="WholeEnd"/> when there's no journal.</summary>
			internal required long JournalEnd;
			/// <summary>The 8 bytes before <see cref="JournalEnd"/>, read back before appending.</summary>
			internal required byte[] Tail;
			internal required Dictionary<string, UInt128> Entries;
			internal int Version, ImageHashPipeline;
			internal long JournalBytes => JournalEnd - WholeEnd;
		}
		static OnDisk? onDisk;

		/// <summary>Whether the database file is still the one this process read or wrote last.</summary>
		static bool IsStill(OnDisk known) =>
			new FileInfo(CurrentDatabasePath) is { Exists: true } now &&
			now.FullName == known.Path && now.Length == known.Length && now.LastWriteTimeUtc == known.WrittenUtc;

		static byte[] ReadTail(Stream file, long end) {
			var tail = new byte[RecordCheck];
			file.Position = end - tail.Length;
			file.ReadExactly(tail);
			return tail;
		}

		/// <summary>The database just written whole: no journal after it.</summary>
		static OnDisk? Written(string path, DatabaseWrapper wrapper, Dictionary<string, UInt128> hashes) {
			try {
				using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				var info = new FileInfo(path);
				return new OnDisk {
					Path = info.FullName, Length = info.Length, WrittenUtc = info.LastWriteTimeUtc,
					WholeEnd = info.Length, JournalEnd = info.Length, Tail = ReadTail(file, info.Length), Entries = hashes,
					Version = wrapper.Version, ImageHashPipeline = wrapper.ImageHashPipeline,
				};
			}
			catch (Exception) {
				return null; // the next save writes it whole again
			}
		}

		/// <summary>
		/// Reads the journal after the database's terminator, where <paramref name="file"/> stands, into
		/// <paramref name="wrapper"/>, up to its last good record. Never throws for the journal: one it can't read
		/// further is read up to there.
		/// </summary>
		static OnDisk? ReadJournal(FileStream file, string path, DatabaseWrapper wrapper, Dictionary<string, UInt128> hashes) {
			long wholeEnd = file.Position, end = wholeEnd;
			int read = 0;
			try {
				Span<byte> magic = stackalloc byte[JournalMagic.Length];
				if (file.Length - wholeEnd >= magic.Length && file.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) == magic.Length && magic.SequenceEqual(JournalMagic)) {
					end = wholeEnd + magic.Length;
					byte[] record = new byte[1 << 16];
					Span<byte> check = stackalloc byte[RecordCheck];
					while (file.Length - end >= RecordHead + RecordCheck) {
						file.Position = end;
						file.ReadExactly(record.AsSpan(0, RecordHead));
						byte kind = record[0];
						int length = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(1));
						if (kind is < KindEntry or > KindHeader || length < 0 || length > file.Length - end - RecordHead - RecordCheck)
							break;
						if (record.Length < RecordHead + length + RecordCheck)
							Array.Resize(ref record, RecordHead + length + RecordCheck);
						file.ReadExactly(record.AsSpan(RecordHead, length + RecordCheck));
						CheckOf(record.AsSpan(0, RecordHead + length), check);
						if (!check.SequenceEqual(record.AsSpan(RecordHead + length, RecordCheck)))
							break;
						Apply(kind, record.AsSpan(RecordHead, length), wrapper, hashes);
						end += RecordHead + length + RecordCheck;
						read++;
					}
					if (end < file.Length)
						Logger.Instance.Warn($"The scanned files' journal ends in a record cut short ({file.Length - end:N0} bytes), left by a save that didn't finish; the next save cuts it off.");
				}
			}
			catch (Exception e) {
				Logger.Instance.Warn($"The scanned files' journal is read up to a record that can't be read ({e.Message}); the next save cuts it off.");
			}
			if (read > 0)
				Logger.Instance.Info($"Scanned files' journal: {read:N0} change(s) since the database was last written whole.");
			try {
				var info = new FileInfo(path);
				return new OnDisk {
					Path = info.FullName, Length = info.Length, WrittenUtc = info.LastWriteTimeUtc,
					WholeEnd = wholeEnd, JournalEnd = end, Tail = ReadTail(file, end), Entries = hashes,
					Version = wrapper.Version, ImageHashPipeline = wrapper.ImageHashPipeline,
				};
			}
			catch (Exception) {
				return null; // the next save writes it whole
			}
		}

		/// <summary>One record, read and checked: deserialized before anything changes, so it applies whole or not at all.</summary>
		static void Apply(byte kind, ReadOnlySpan<byte> payload, DatabaseWrapper wrapper, Dictionary<string, UInt128> hashes) {
			switch (kind) {
				case KindEntry:
					FileEntry entry = MemoryPackSerializer.Deserialize<FileEntry>(payload)
						?? throw new InvalidDataException("a journal entry deserialized to null");
					wrapper.Entries.Remove(entry);
					wrapper.Entries.Add(entry);
					hashes.Remove(entry.Path); // a path that changed only in case takes the entry's own
					hashes[entry.Path] = HashOf(payload);
					break;
				case KindGone:
					string path = Encoding.UTF8.GetString(payload);
					wrapper.Entries.Remove(new FileEntry { _Path = path });
					hashes.Remove(path);
					break;
				case KindHeader:
					if (payload.Length != sizeof(int) * 2)
						throw new InvalidDataException("a journal header of the wrong length");
					wrapper.Version = BinaryPrimitives.ReadInt32LittleEndian(payload);
					wrapper.ImageHashPipeline = BinaryPrimitives.ReadInt32LittleEndian(payload[sizeof(int)..]);
					break;
			}
		}

		static void WriteRecord(ArrayBufferWriter<byte> journal, byte kind, ReadOnlySpan<byte> payload) {
			int size = RecordHead + payload.Length + RecordCheck;
			Span<byte> record = journal.GetSpan(size)[..size];
			record[0] = kind;
			BinaryPrimitives.WriteInt32LittleEndian(record[1..], payload.Length);
			payload.CopyTo(record[RecordHead..]);
			CheckOf(record[..(RecordHead + payload.Length)], record[(RecordHead + payload.Length)..]);
			journal.Advance(size);
		}

		/// <summary>
		/// Appends what changed since the file was read or written to its journal, in one write; with nothing changed,
		/// writes nothing. False when the database should be written whole instead: the journal would reach
		/// <see cref="FoldShare"/> of it, or the file's end isn't what this process left there.
		/// </summary>
		static bool TryAppend(OnDisk known) {
			// The journal's room before it's folded in: what changed past it is never gathered.
			long room = (long)(known.WholeEnd * FoldShare) - known.JournalBytes;
			var journal = new ArrayBufferWriter<byte>(1 << 16);
			if (known.JournalBytes == 0)
				journal.Write(JournalMagic);
			var buffer = new ArrayBufferWriter<byte>(1 << 16);
			var seen = new HashSet<string>(PathComparer);
			var changed = new List<(string Path, UInt128 Hash)>();
			long whole = EmptyDatabaseLength;
			// A snapshot, as SerializeDatabaseStreaming takes: a checkpoint runs while scan threads fill entries in.
			foreach (FileEntry entry in DbWrapper.Entries.ToArray()) {
				seen.Add(entry.Path);
				buffer.ResetWrittenCount();
				try {
					MemoryPackSerializer.Serialize(buffer, entry);
				}
				catch (Exception) {
					// Being hashed right now (see SerializeDatabaseStreaming): the file keeps what it has of it, and the
					// next save looks at it again.
					continue;
				}
				whole += sizeof(int) + buffer.WrittenCount;
				UInt128 hash = HashOf(buffer.WrittenSpan);
				if (known.Entries.TryGetValue(entry.Path, out UInt128 was) && was == hash)
					continue;
				WriteRecord(journal, KindEntry, buffer.WrittenSpan);
				changed.Add((entry.Path, hash));
				if (journal.WrittenCount > room)
					return false;
			}
			var gone = new List<string>();
			foreach (string path in known.Entries.Keys)
				if (!seen.Contains(path)) {
					WriteRecord(journal, KindGone, Encoding.UTF8.GetBytes(path));
					gone.Add(path);
				}
			bool header = DbWrapper.Version != known.Version || DbWrapper.ImageHashPipeline != known.ImageHashPipeline;
			if (header) {
				Span<byte> ints = stackalloc byte[sizeof(int) * 2];
				BinaryPrimitives.WriteInt32LittleEndian(ints, DbWrapper.Version);
				BinaryPrimitives.WriteInt32LittleEndian(ints[sizeof(int)..], DbWrapper.ImageHashPipeline);
				WriteRecord(journal, KindHeader, ints);
			}
			if (changed.Count == 0 && gone.Count == 0 && !header) {
				Logger.Instance.Info($"Scanned files unchanged on disk ({Database.Count:N0} files), nothing to save.");
				return true;
			}
			long added = journal.WrittenCount;
			if (known.JournalBytes + added > known.WholeEnd * FoldShare || known.JournalEnd + added > whole * (1 + FoldShare))
				return false;

			using (var file = new FileStream(CurrentDatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
				// Not the file this process left, whatever its stamp says: written whole over it, as before the journal.
				Span<byte> tail = stackalloc byte[RecordCheck];
				file.Position = known.JournalEnd - tail.Length;
				file.ReadExactly(tail);
				if (!tail.SequenceEqual(known.Tail))
					return false;
				if (file.Length != known.JournalEnd)
					file.SetLength(known.JournalEnd); // a record cut short goes first
				file.Position = known.JournalEnd;
				// One write: a process ended mid-save leaves all of it or none of it.
				file.Write(journal.WrittenSpan);
			}
			Logger.Instance.Info($"Save scanned files to disk: {changed.Count:N0} changed and {gone.Count:N0} gone of {Database.Count:N0} files, appended to the journal ({added:N0} bytes).");

			known.JournalEnd += added;
			known.Length = known.JournalEnd;
			known.WrittenUtc = new FileInfo(CurrentDatabasePath).LastWriteTimeUtc;
			known.Tail = journal.WrittenSpan[^RecordCheck..].ToArray();
			foreach ((string path, UInt128 hash) in changed) {
				known.Entries.Remove(path);
				known.Entries[path] = hash;
			}
			foreach (string path in gone)
				known.Entries.Remove(path);
			known.Version = DbWrapper.Version;
			known.ImageHashPipeline = DbWrapper.ImageHashPipeline;
			return true;
		}
	}
}
