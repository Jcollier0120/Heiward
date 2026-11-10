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

using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>
	/// Sidecar cache for the union pass's neural embeddings (384 bytes per sampled
	/// position, keyed like <see cref="FileEntry.grayBytes"/>). Deliberately NOT part of
	/// the main scan database: at library scale the data is bulky, only needed while AI
	/// matching is enabled, and fully recomputable — keeping it out of FileEntry means
	/// non-AI users pay nothing for it and a database from any VDF version loads
	/// unchanged. Records self-validate by file size + mtime, so an edited file simply
	/// re-embeds. Thread-safe: parallel decode workers probe while the single embedding
	/// worker writes.
	/// </summary>
	sealed class UnionEmbeddingStore {
		sealed class FileRecord {
			public FileRecord(long fileSize, long mtimeUtcTicks) {
				FileSize = fileSize;
				MTimeUtcTicks = mtimeUtcTicks;
			}
			public readonly long FileSize;
			public readonly long MTimeUtcTicks;
			public readonly ConcurrentDictionary<double, byte[]> Positions = new();
		}

		static ReadOnlySpan<byte> Magic => "VDFAI002"u8;
		const int MaxSanePositionCount = 10_000;

		static readonly StringComparer PathComparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		readonly ConcurrentDictionary<string, FileRecord> records = new(PathComparer);
		readonly object saveLock = new();
		/// <summary>Files whose record changed since the last save: the records a save appends.</summary>
		readonly ConcurrentDictionary<string, byte> dirty = new(PathComparer);
		readonly StoreFile onDisk = new(PathComparer);
		string path = StorePath;

		internal static string StorePath => PathFor(null);

		/// <summary>
		/// The sidecar for embeddings of one model: <c>null</c> = VDF's int8 CPU model (the original
		/// file name), otherwise e.g. <see cref="NpuComponents.ModelKey"/>. Different models' vectors
		/// must never be compared with each other, so each gets its own file.
		/// </summary>
		internal static string PathFor(string? cacheKey) =>
			TestOverrideStorePath ??
			FileUtils.SafePathCombine(CoreUtils.ResolveDatabaseFolder(Utils.DatabaseUtils.CustomDatabaseFolder),
				cacheKey == null ? "UnionEmbeddings.db" : $"UnionEmbeddings.{cacheKey}.db");

		/// <summary>Test hook: isolates store tests from the real database folder.</summary>
		internal static string? TestOverrideStorePath;

		internal int Count => records.Count;

		internal bool HasEmbedding(FileEntry entry, double positionKey) =>
			TryGetCurrent(entry)?.Positions.ContainsKey(positionKey) == true;

		internal byte[]? GetEmbedding(FileEntry entry, double positionKey) =>
			TryGetCurrent(entry) is { } record && record.Positions.TryGetValue(positionKey, out byte[]? embedding)
				? embedding
				: null;

		internal void Put(FileEntry entry, double positionKey, byte[] embedding) {
			long size = entry.FileSize;
			long mtime = entry.DateModified.Ticks;
			FileRecord record = records.AddOrUpdate(entry.Path,
				_ => new FileRecord(size, mtime),
				(_, existing) => existing.FileSize == size && existing.MTimeUtcTicks == mtime
					? existing
					: new FileRecord(size, mtime)); // file changed since the record was written — start fresh
			record.Positions[positionKey] = embedding;
			dirty[entry.Path] = 0;
		}

		FileRecord? TryGetCurrent(FileEntry entry) =>
			records.TryGetValue(entry.Path, out FileRecord? record) &&
			record.FileSize == entry.FileSize && record.MTimeUtcTicks == entry.DateModified.Ticks
				? record
				: null;

		internal static UnionEmbeddingStore Load(string? cacheKey = null) {
			string path = PathFor(cacheKey);
			var store = new UnionEmbeddingStore { path = path };
			if (!File.Exists(path))
				return store;
			try {
				using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
				using var reader = new BinaryReader(stream, Encoding.UTF8);
				Span<byte> magic = stackalloc byte[Magic.Length];
				if (reader.Read(magic) != Magic.Length || !magic.SequenceEqual(Magic))
					throw new IOException("bad magic");
				store.onDisk.Started(stream.Position);
				while (stream.Position < stream.Length) {
					long start = stream.Position;
					string file;
					FileRecord record;
					try {
						file = reader.ReadString();
						long size = reader.ReadInt64();
						long mtime = reader.ReadInt64();
						int positionCount = reader.ReadInt32();
						if (positionCount < 0 || positionCount > MaxSanePositionCount)
							throw new IOException("implausible record header");
						record = new FileRecord(size, mtime);
						for (int i = 0; i < positionCount; i++) {
							double key = reader.ReadDouble();
							byte[] embedding = reader.ReadBytes(EmbeddingMath.Dimensions);
							// ReadBytes returns a SHORT array at EOF instead of throwing — a file
							// truncated inside an embedding must count as corruption, or the
							// poisoned record would be served forever (size/mtime still match).
							if (embedding.Length != EmbeddingMath.Dimensions)
								throw new IOException("truncated record");
							record.Positions[key] = embedding;
						}
					}
					catch (Exception e) {
						// A save cut short while appending: the records before it are whole, and the next save
						// cuts this one off.
						Logger.Instance.Warn($"AI embedding cache read up to a record cut short ({e.Message}); {store.records.Count:N0} record(s) kept: {path}");
						break;
					}
					// A later record of a file replaces the earlier one: saves append the records that changed.
					store.records[file] = record;
					store.onDisk.Record(file, start, stream.Position);
				}
				store.onDisk.Read(path);
			}
			catch (Exception e) {
				// The cache is recomputable — a torn or corrupt file must never block a scan.
				Logger.Instance.Warn($"AI embedding cache unreadable, rebuilding ({e.Message}): {path}");
				store.records.Clear();
				store.onDisk.Lost();
			}
			return store;
		}

		static void WriteRecord(BinaryWriter writer, string file, FileRecord record) {
			// Snapshot the positions: the count must match what gets written.
			var positions = record.Positions.ToArray();
			writer.Write(file);
			writer.Write(record.FileSize);
			writer.Write(record.MTimeUtcTicks);
			writer.Write(positions.Length);
			foreach ((double key, byte[] embedding) in positions) {
				if (embedding.Length != EmbeddingMath.Dimensions)
					throw new InvalidOperationException($"Unexpected embedding length {embedding.Length}");
				writer.Write(key);
				writer.Write(embedding);
			}
		}

		/// <summary>
		/// Persists what changed: the records added or changed since the last save are appended to the file
		/// (<see cref="StoreFile"/>), and it's written whole, atomically, only once replaced and pruned records would
		/// make up a fifth of it. <paramref name="keepOnly"/> should be every path
		/// known to the scan database — records are pruned only when their file left the
		/// database entirely, so alternating scans between different libraries (or scans
		/// with an offline drive) never throw cached embeddings away. Best-effort: failure
		/// only costs recompute time. No-op when nothing was added since load and nothing is pruned.
		/// </summary>
		internal void Save(IReadOnlySet<string>? keepOnly) {
			// Pruned even with nothing new: the database forgetting files (Settings.ForgetUnlisted) shrinks the cache
			// too, on disk once their records are a fifth of it.
			List<string> stale = keepOnly == null ? new() : records.Keys.Where(k => !keepOnly.Contains(k)).ToList();
			if (dirty.IsEmpty && stale.Count == 0)
				return;
			string tempPath = path + ".tmp";
			List<string> taken = new();
			try {
				lock (saveLock) {
					foreach (string file in stale) {
						records.TryRemove(file, out _);
						dirty.TryRemove(file, out _);
						onDisk.Forget(file);
					}
					taken = dirty.Keys.ToList();
					foreach (string file in taken)
						dirty.TryRemove(file, out _);

					var appended = new List<(string File, long Bytes)>();
					using var changed = new MemoryStream();
					using (var writer = new BinaryWriter(changed, Encoding.UTF8, leaveOpen: true))
						foreach (string file in taken)
							if (records.TryGetValue(file, out FileRecord? record)) {
								long start = changed.Position;
								WriteRecord(writer, file, record);
								appended.Add((file, changed.Position - start));
							}
					if (onDisk.CanAppend(path, appended, changed.Length)) {
						if (changed.Length > 0)
							onDisk.Append(path, changed.GetBuffer().AsSpan(0, (int)changed.Length), appended);
						return;
					}

					var written = new List<(string File, long Bytes)>(records.Count);
					long magicEnd;
					using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
					using (var writer = new BinaryWriter(stream, Encoding.UTF8)) {
						writer.Write(Magic);
						magicEnd = stream.Position;
						foreach ((string file, FileRecord record) in records) {
							long start = stream.Position;
							WriteRecord(writer, file, record);
							written.Add((file, stream.Position - start));
						}
					}
					File.Move(tempPath, path, overwrite: true);
					onDisk.WroteWhole(path, magicEnd, written);
				}
			}
			catch (Exception e) {
				Logger.Instance.Warn($"Failed to save the AI embedding cache (will recompute next time): {e.Message}");
				// The file's end is unknown now: the next save writes it whole, these records with it.
				onDisk.Lost();
				foreach (string file in taken)
					dirty.TryAdd(file, 0);
				try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
			}
		}
	}
}
