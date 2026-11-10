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

namespace HEI.Core.AI {
	/// <summary>
	/// Where an embedding store's file stands, so a save can append to it. A save adds the records that changed after
	/// those already there, in the store's own layout. Every build reads a store's records in order, a later record of a
	/// file replacing an earlier one, so builds from before appending read such a file whole. The file is written whole
	/// again (folded) once the records later ones replaced, and those of files the database forgot, would make up
	/// <see cref="FoldShare"/> of it, or when it isn't the file this process read or wrote last.
	/// </summary>
	sealed class StoreFile {
		internal const double FoldShare = 0.2;

		readonly Dictionary<string, long> recordBytes;
		long goodEnd, deadBytes;
		(string Path, long Length, DateTime WrittenUtc)? stamp;

		internal StoreFile(StringComparer comparer) => recordBytes = new(comparer);

		/// <summary>Bytes in the file that a fold would leave out: records replaced by later ones, and forgotten files'.</summary>
		internal long DeadBytes => deadBytes;
		/// <summary>Just past the last record read or written whole.</summary>
		internal long GoodEnd => goodEnd;

		static (string, long, DateTime)? StampOf(string path) =>
			new FileInfo(path) is { Exists: true } info ? (info.FullName, info.Length, info.LastWriteTimeUtc) : null;

		/// <summary>The file's magic, read or written: its records start here.</summary>
		internal void Started(long end) => goodEnd = end;

		/// <summary>A record of <paramref name="file"/> in the file at [start, end): one before it of the same file is dead.</summary>
		internal void Record(string file, long start, long end) {
			if (recordBytes.TryGetValue(file, out long was))
				deadBytes += was;
			recordBytes[file] = end - start;
			goodEnd = end;
		}

		/// <summary>The file as read just now (a record cut short at its end included): appends may follow.</summary>
		internal void Read(string path) => stamp = StampOf(path);

		/// <summary>The file just written whole, holding exactly <paramref name="records"/> after a magic of <paramref name="start"/> bytes.</summary>
		internal void WroteWhole(string path, long start, List<(string File, long Bytes)> records) {
			recordBytes.Clear();
			deadBytes = 0;
			goodEnd = start;
			foreach ((string file, long bytes) in records)
				Record(file, goodEnd, goodEnd + bytes);
			stamp = StampOf(path);
		}

		/// <summary>A file the store dropped (the database forgot it): its record stays in the file, dead, until a fold.</summary>
		internal void Forget(string file) {
			if (recordBytes.Remove(file, out long was))
				deadBytes += was;
		}

		/// <summary>Nothing known of the file: the next save writes it whole.</summary>
		internal void Lost() {
			recordBytes.Clear();
			goodEnd = deadBytes = 0;
			stamp = null;
		}

		/// <summary>
		/// Whether <paramref name="records"/> (<paramref name="bytes"/> in all) may go after the file's own: it's still
		/// the file this process read or wrote, and the records they replace, with those dead already, stay under
		/// <see cref="FoldShare"/> of the file.
		/// </summary>
		internal bool CanAppend(string path, List<(string File, long Bytes)> records, long bytes) {
			if (stamp is not { } known || StampOf(path) is not { } now || now != known)
				return false;
			long replaced = 0;
			foreach ((string file, _) in records)
				if (recordBytes.TryGetValue(file, out long was))
					replaced += was;
			return deadBytes + replaced <= FoldShare * (goodEnd + bytes);
		}

		/// <summary>
		/// Appends <paramref name="bytes"/>, holding <paramref name="records"/>, after the file's last good record, in one
		/// write: a process ended mid-save leaves all of them or none. A record cut short at the end goes first.
		/// </summary>
		internal void Append(string path, ReadOnlySpan<byte> bytes, List<(string File, long Bytes)> records) {
			using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) {
				if (stream.Length != goodEnd)
					stream.SetLength(goodEnd);
				stream.Position = goodEnd;
				stream.Write(bytes);
			}
			foreach ((string file, long n) in records)
				Record(file, goodEnd, goodEnd + n);
			stamp = StampOf(path);
		}
	}
}
