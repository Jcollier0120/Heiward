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
	/// Sidecar cache for the dense keyframe embeddings the visual partial-duplicate pass
	/// uses (~25 KB per video). Deliberately NOT part of the main scan database: the data
	/// is bulky, only needed while that pass runs, and fully recomputable. Records are
	/// validated by file size + mtime, so an edited file re-extracts. Plain length-prefixed
	/// binary — no serializer dependency, trivially Native-AOT-safe. A frame slot may be
	/// EMPTY (length 0): the frame exists on the timeline but was excluded from matching
	/// (too dark, or a duplicate of its predecessor) — the slot keeps the index↔time
	/// mapping intact.
	/// </summary>
	sealed class DenseEmbeddingStore {
		internal sealed record DenseRecord(long FileSize, long MTimeUtcTicks, float IntervalSeconds, byte[][] Frames) {
			/// <summary>
			/// A record without frames marks a file whose keyframes could not be decoded, so the
			/// next scan does not decode it again (#880). A successful sweep always has frames:
			/// FFmpeg producing none is itself a failure. Keyed by size and modification time
			/// like every record, so a repaired or replaced file is sampled again.
			/// </summary>
			internal bool IsFailure => Frames.Length == 0;
			internal static DenseRecord Failure(long fileSize, long mtimeUtcTicks, float intervalSeconds) =>
				new(fileSize, mtimeUtcTicks, intervalSeconds, Array.Empty<byte[]>());
		}

		// VDFAI003: v2 layout with a per-frame validity flag (VDFAI001 had none and
		// never shipped; VDFAI002 is the union store's magic).
		static ReadOnlySpan<byte> Magic => "VDFAI003"u8;
		const int MaxSaneFrameCount = 100_000;

		// Concurrent: the sampling phase's Parallel.For has workers probing (TryGet) while
		// others insert freshly computed records (Put) — a plain Dictionary would race.
		static readonly StringComparer PathComparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		readonly ConcurrentDictionary<string, DenseRecord> records = new(PathComparer);
		readonly object saveLock = new();
		/// <summary>Files whose record changed since the last save: the records a save appends.</summary>
		readonly ConcurrentDictionary<string, byte> dirty = new(PathComparer);
		readonly StoreFile onDisk = new(PathComparer);
		string path = StorePath;

		internal static string StorePath => PathFor(null);

		/// <summary>One sidecar per embedding model, as for <see cref="UnionEmbeddingStore.PathFor"/>.</summary>
		internal static string PathFor(string? cacheKey) =>
			TestOverrideStorePath ??
			FileUtils.SafePathCombine(CoreUtils.ResolveDatabaseFolder(Utils.DatabaseUtils.CustomDatabaseFolder),
				cacheKey == null ? "DenseEmbeddings.db" : $"DenseEmbeddings.{cacheKey}.db");

		/// <summary>Test hook: isolates store tests from the real database folder.</summary>
		internal static string? TestOverrideStorePath;

		internal int Count => records.Count;

		internal static DenseEmbeddingStore Load(string? cacheKey = null) {
			string path = PathFor(cacheKey);
			var store = new DenseEmbeddingStore { path = path };
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
					DenseRecord record;
					try {
						file = reader.ReadString();
						long size = reader.ReadInt64();
						long mtime = reader.ReadInt64();
						float interval = reader.ReadSingle();
						int frameCount = reader.ReadInt32();
						if (interval <= 0 || frameCount < 0 || frameCount > MaxSaneFrameCount)
							throw new IOException("implausible record header");
						var frames = new byte[frameCount][];
						for (int i = 0; i < frameCount; i++) {
							byte validity = reader.ReadByte();
							if (validity == 0) {
								frames[i] = Array.Empty<byte>();
								continue;
							}
							if (validity != 1)
								throw new IOException("bad frame validity flag");
							frames[i] = reader.ReadBytes(EmbeddingMath.Dimensions);
							// ReadBytes returns a SHORT array at EOF instead of throwing — a file
							// truncated inside a frame must count as corruption, or the poisoned
							// record would be served forever (size/mtime still match) and every
							// later Save would fail on its length check.
							if (frames[i].Length != EmbeddingMath.Dimensions)
								throw new IOException("truncated record");
						}
						record = new DenseRecord(size, mtime, interval, frames);
					}
					catch (Exception e) {
						// A save cut short while appending: the records before it are whole, and the next save
						// cuts this one off.
						Logger.Instance.Warn($"Dense embedding cache read up to a record cut short ({e.Message}); {store.records.Count:N0} record(s) kept: {path}");
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
				Logger.Instance.Warn($"Dense embedding cache unreadable, rebuilding ({e.Message}): {path}");
				store.records.Clear();
				store.onDisk.Lost();
			}
			return store;
		}

		internal bool TryGet(string file, long fileSize, long mtimeUtcTicks, out DenseRecord record) {
			if (records.TryGetValue(file, out DenseRecord? found) &&
				found.FileSize == fileSize && found.MTimeUtcTicks == mtimeUtcTicks) {
				record = found;
				return true;
			}
			record = null!;
			return false;
		}

		internal void Put(string file, DenseRecord record) {
			records[file] = record;
			dirty[file] = 0;
		}

		static void WriteRecord(BinaryWriter writer, string file, DenseRecord record) {
			writer.Write(file);
			writer.Write(record.FileSize);
			writer.Write(record.MTimeUtcTicks);
			writer.Write(record.IntervalSeconds);
			writer.Write(record.Frames.Length);
			foreach (byte[] frame in record.Frames) {
				if (frame.Length == 0) {
					writer.Write((byte)0); // invalid slot (dark/duplicate frame)
					continue;
				}
				if (frame.Length != EmbeddingMath.Dimensions)
					throw new InvalidOperationException($"Unexpected embedding length {frame.Length}");
				writer.Write((byte)1);
				writer.Write(frame);
			}
		}

		/// <summary>
		/// Persists what changed: the records added since the last save are appended to the file
		/// (<see cref="StoreFile"/>), and it's written whole, atomically, only once replaced and pruned records would
		/// make up a fifth of it. A save with nothing new and nothing to prune writes nothing (every scan wrote the
		/// whole cache, and each checkpoint again). <paramref name="keepOnly"/> should be every path
		/// known to the scan database — records are pruned only when their file left the
		/// database entirely, so alternating scans between different libraries (or scans
		/// with an offline drive) never throw cached embeddings away. Best-effort: failure
		/// only costs recompute time.
		/// </summary>
		internal void Save(IReadOnlySet<string>? keepOnly) {
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
							if (records.TryGetValue(file, out DenseRecord? record)) {
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
						foreach ((string file, DenseRecord record) in records) {
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
				Logger.Instance.Warn($"Failed to save the dense embedding cache (will recompute next time): {e.Message}");
				// The file's end is unknown now: the next save writes it whole, these records with it.
				onDisk.Lost();
				foreach (string file in taken)
					dirty.TryAdd(file, 0);
				try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
			}
		}
	}
}
