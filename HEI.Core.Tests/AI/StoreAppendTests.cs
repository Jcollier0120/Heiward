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

using HEI.Core.AI;

namespace HEI.Core.Tests.AI;

/// <summary>
/// The embedding stores append the records that changed, in their own layout, after those in the file: every build
/// reads the records in order, a later one of a file replacing the earlier one. Written whole only once replaced and
/// pruned records would make up a fifth of the file.
/// </summary>
[Collection("EmbeddingStorePath")]
public class StoreAppendTests : IDisposable {
	readonly string tempDir;
	string UnionPath => UnionEmbeddingStore.StorePath;
	string DensePath => DenseEmbeddingStore.StorePath;

	public StoreAppendTests() {
		tempDir = Path.Combine(Path.GetTempPath(), $"hei_store_append_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		UnionEmbeddingStore.TestOverrideStorePath = Path.Combine(tempDir, "UnionEmbeddings.db");
		DenseEmbeddingStore.TestOverrideStorePath = Path.Combine(tempDir, "DenseEmbeddings.db");
	}

	public void Dispose() {
		UnionEmbeddingStore.TestOverrideStorePath = null;
		DenseEmbeddingStore.TestOverrideStorePath = null;
		try { Directory.Delete(tempDir, true); } catch { }
	}

	static FileEntry Photo(int i, long size = 1000) {
		var entry = new FileEntry { Folder = @"P:\Pictures", FileSize = size, DateModified = new DateTime(700_000_000_000L + i, DateTimeKind.Utc) };
		entry.Path = $@"P:\Pictures\IMG_{i:D6}.jpg";
		return entry;
	}

	static byte[] Embedding(int seed) {
		var e = new byte[EmbeddingMath.Dimensions];
		new Random(seed).NextBytes(e);
		return e;
	}

	static UnionEmbeddingStore UnionWith(int count) {
		var store = new UnionEmbeddingStore();
		for (int i = 0; i < count; i++)
			store.Put(Photo(i), 0, Embedding(i));
		return store;
	}

	static HashSet<string> Paths(int count) =>
		new(Enumerable.Range(0, count).Select(i => Photo(i).Path), StringComparer.OrdinalIgnoreCase);

	[Fact]
	public void Union_NewRecords_AreAppended_TheFileBeforeThemStaysAsItWas() {
		var store = UnionWith(200);
		store.Save(Paths(201));
		byte[] whole = File.ReadAllBytes(UnionPath);

		var loaded = UnionEmbeddingStore.Load();
		loaded.Put(Photo(200), 0, Embedding(200));
		loaded.Save(Paths(201));
		byte[] after = File.ReadAllBytes(UnionPath);
		Assert.Equal(whole, after.AsSpan(0, whole.Length).ToArray());
		Assert.InRange(after.Length - whole.Length, EmbeddingMath.Dimensions, 2 * EmbeddingMath.Dimensions);

		var again = UnionEmbeddingStore.Load();
		Assert.Equal(201, again.Count);
		Assert.Equal(Embedding(200), again.GetEmbedding(Photo(200), 0));
	}

	[Fact]
	public void Union_NothingNew_WritesNothing() {
		UnionWith(50).Save(Paths(50));
		DateTime written = File.GetLastWriteTimeUtc(UnionPath);
		long length = new FileInfo(UnionPath).Length;
		UnionEmbeddingStore.Load().Save(Paths(50));
		Assert.Equal(length, new FileInfo(UnionPath).Length);
		Assert.Equal(written, File.GetLastWriteTimeUtc(UnionPath));
	}

	[Fact]
	public void Union_ReplacedRecord_LaterOneWins_AndAFifthReplacedFolds() {
		UnionWith(100).Save(Paths(100));
		long whole = new FileInfo(UnionPath).Length;
		var store = UnionEmbeddingStore.Load();

		// A photo whose file changed: its new record goes after the old one, which a load passes over.
		store.Put(Photo(7, size: 2000), 0, Embedding(1007));
		store.Save(Paths(100));
		Assert.True(new FileInfo(UnionPath).Length > whole);
		var loaded = UnionEmbeddingStore.Load();
		Assert.Equal(100, loaded.Count);
		Assert.Equal(Embedding(1007), loaded.GetEmbedding(Photo(7, size: 2000), 0));
		Assert.Null(loaded.GetEmbedding(Photo(7), 0));

		// More than a fifth of the file replaced: written whole, its own size again.
		for (int i = 0; i < 30; i++)
			loaded.Put(Photo(i, size: 3000), 0, Embedding(3000 + i));
		loaded.Save(Paths(100));
		Assert.Equal(whole, new FileInfo(UnionPath).Length);
		var folded = UnionEmbeddingStore.Load();
		Assert.Equal(100, folded.Count);
		Assert.Equal(Embedding(3029), folded.GetEmbedding(Photo(29, size: 3000), 0));
	}

	[Fact]
	public void Union_Pruned_StaysOnDiskUntilAFifth_ThenFolds() {
		UnionWith(100).Save(Paths(100));
		long whole = new FileInfo(UnionPath).Length;

		// Five files the database forgot: dropped in memory, their records dead in the file.
		var store = UnionEmbeddingStore.Load();
		store.Save(Paths(95));
		Assert.Equal(95, store.Count);
		Assert.Equal(whole, new FileInfo(UnionPath).Length);

		// Thirty: past a fifth, written whole without them.
		store.Save(Paths(70));
		Assert.Equal(70, UnionEmbeddingStore.Load().Count);
		Assert.True(new FileInfo(UnionPath).Length < whole);
	}

	/// <summary>A process killed while appending, at every byte: the records before it load, and the next save cuts it off.</summary>
	[Fact]
	public void Union_AppendCutShort_AtAnyByte_KeepsTheRecordsBefore() {
		UnionWith(20).Save(Paths(22));
		byte[] previous = File.ReadAllBytes(UnionPath);
		var store = UnionEmbeddingStore.Load();
		store.Put(Photo(20), 0, Embedding(20));
		store.Put(Photo(21), 0, Embedding(21));
		store.Save(Paths(22));
		byte[] full = File.ReadAllBytes(UnionPath);

		for (int cut = previous.Length; cut < full.Length; cut++) {
			File.WriteAllBytes(UnionPath, full.AsSpan(0, cut).ToArray());
			var loaded = UnionEmbeddingStore.Load();
			Assert.InRange(loaded.Count, 20, 21);
			for (int i = 0; i < 20; i++)
				Assert.Equal(Embedding(i), loaded.GetEmbedding(Photo(i), 0));
		}

		// A large record cut short, then a smaller one saved: the file ends at its last record.
		var big = UnionEmbeddingStore.Load();
		for (int k = 0; k < 6; k++)
			big.Put(Photo(30), k, Embedding(300 + k));
		big.Save(Paths(40));
		byte[] withBig = File.ReadAllBytes(UnionPath);
		File.WriteAllBytes(UnionPath, withBig.AsSpan(0, withBig.Length - 10).ToArray());
		var reopened = UnionEmbeddingStore.Load();
		Assert.Equal(21, reopened.Count); // 0-20: the cut loop left 21 cut short, and 30 is cut short now
		reopened.Put(Photo(31), 0, Embedding(31));
		reopened.Save(Paths(40));
		var whole = UnionEmbeddingStore.Load();
		Assert.Equal(22, whole.Count);
		Assert.Equal(Embedding(31), whole.GetEmbedding(Photo(31), 0));
		Assert.Equal(new FileInfo(UnionPath).Length, UnionRecords(UnionPath).End);
	}

	/// <summary>The union file read as builds before appending read it: every record in order, a later one of a file over the earlier one.</summary>
	static (Dictionary<string, (long Size, int Positions)> Records, long End) UnionRecords(string path) {
		var records = new Dictionary<string, (long Size, int Positions)>(StringComparer.OrdinalIgnoreCase);
		using var stream = File.OpenRead(path);
		using var reader = new BinaryReader(stream);
		Assert.Equal("VDFAI002"u8.ToArray(), reader.ReadBytes(8));
		while (stream.Position < stream.Length) {
			string file = reader.ReadString();
			long size = reader.ReadInt64();
			reader.ReadInt64();
			int count = reader.ReadInt32();
			for (int i = 0; i < count; i++) { reader.ReadDouble(); Assert.Equal(EmbeddingMath.Dimensions, reader.ReadBytes(EmbeddingMath.Dimensions).Length); }
			records[file] = (size, count);
		}
		return (records, stream.Position);
	}

	/// <summary>
	/// What builds before appending read: their loader reads records to the end of the file, a later one of a file
	/// over the earlier one, which this replays on the bytes (as the old loader, record by record).
	/// </summary>
	[Fact]
	public void Union_AppendedFile_IsReadWholeByTheLoaderBeforeAppending() {
		UnionWith(30).Save(Paths(40));
		var store = UnionEmbeddingStore.Load();
		for (int i = 30; i < 33; i++)
			store.Put(Photo(i), 0, Embedding(i));
		store.Put(Photo(3, size: 5000), 0, Embedding(5003));
		store.Save(Paths(40));

		var (records, _) = UnionRecords(UnionPath);
		Assert.Equal(33, records.Count);
		Assert.Equal(5000, records[Photo(3).Path].Size);
	}

	static DenseEmbeddingStore.DenseRecord Dense(int seed, int frames = 3) {
		var data = new byte[frames][];
		for (int i = 0; i < frames; i++) data[i] = Embedding(seed * 10 + i);
		return new DenseEmbeddingStore.DenseRecord(1000 + seed, 2000 + seed, 15f, data);
	}

	[Fact]
	public void Dense_CheckpointWithNothingNew_WritesNothing_NewRecordsAreAppended() {
		var store = new DenseEmbeddingStore();
		for (int i = 0; i < 40; i++) store.Put($@"V:\clips\{i}.mp4", Dense(i));
		store.Save(keepOnly: null);
		byte[] whole = File.ReadAllBytes(DensePath);
		DateTime written = File.GetLastWriteTimeUtc(DensePath);

		var loaded = DenseEmbeddingStore.Load();
		loaded.Save(keepOnly: null); // a checkpoint, every file cached
		loaded.Save(new HashSet<string>(Enumerable.Range(0, 41).Select(i => $@"V:\clips\{i}.mp4"), StringComparer.OrdinalIgnoreCase));
		Assert.Equal(written, File.GetLastWriteTimeUtc(DensePath));

		loaded.Put(@"V:\clips\40.mp4", Dense(40));
		loaded.Save(keepOnly: null);
		byte[] after = File.ReadAllBytes(DensePath);
		Assert.Equal(whole, after.AsSpan(0, whole.Length).ToArray());
		var again = DenseEmbeddingStore.Load();
		Assert.Equal(41, again.Count);
		Assert.True(again.TryGet(@"V:\clips\40.mp4", 1040, 2040, out var got));
		Assert.Equal(Dense(40).Frames[2], got.Frames[2]);
	}

	[Fact]
	public void Dense_AppendCutShort_KeepsTheRecordsBefore() {
		var store = new DenseEmbeddingStore();
		for (int i = 0; i < 10; i++) store.Put($@"V:\clips\{i}.mp4", Dense(i));
		store.Save(keepOnly: null);
		byte[] previous = File.ReadAllBytes(DensePath);
		var loaded = DenseEmbeddingStore.Load();
		loaded.Put(@"V:\clips\10.mp4", Dense(10));
		loaded.Save(keepOnly: null);
		byte[] full = File.ReadAllBytes(DensePath);
		for (int cut = previous.Length; cut < full.Length; cut += 7) {
			File.WriteAllBytes(DensePath, full.AsSpan(0, cut).ToArray());
			Assert.Equal(10, DenseEmbeddingStore.Load().Count);
		}
	}
}
