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
// Identical inputs must give identical groups. The compare phase merges matching pairs into
// groups one pair at a time, checking each against the group's representative (the first file
// that started it), so the groups depend on the order the pairs are merged in. That order used
// to follow the database's (a rescan loads it in another order than a first scan builds it) and
// the parallel workers' timing: rescanning an unchanged folder of 1,200 look-alike photos
// gave 128 groups, then 137. These tests merge a library whose files chain into each other
// (each like its neighbours, not like files two steps away), where any change of order shows.

using HEI.Core.Utils;

namespace HEI.Core.Tests;

[Collection("DatabaseUtils")] // ScanForDuplicates reads the shared static database
public class GroupingDeterminismTests : IDisposable {

	readonly List<FileEntry> added = new();

	public void Dispose() {
		foreach (var e in added)
			DatabaseUtils.Database.Remove(e);
	}

	const int FrameLength = GrayBytesUtils.Side * GrayBytesUtils.Side;

	/// <summary>
	/// Files whose frames are one grey level each, scattered over the range: at 96% two files
	/// match when their levels are about 10 apart, so every file matches a few dozen neighbours
	/// and the neighbours' neighbours only sometimes.
	/// </summary>
	List<FileEntry> AddChainedLibrary(int count, bool images, int seed = 7) {
		var rng = new Random(seed);
		var entries = new List<FileEntry>(count);
		for (int i = 0; i < count; i++) {
			var frame = new byte[FrameLength];
			Array.Fill(frame, (byte)rng.Next(0, 256));
			var entry = new FileEntry {
				_Path = $@"C:\library\{(images ? "photo" : "clip")}_{i:D4}.{(images ? "jpg" : "mp4")}",
				FileSize = 1000 + i,
				invalid = false,
				IsImage = images,
				mediaInfo = new MediaInfo { Duration = images ? TimeSpan.Zero : TimeSpan.FromSeconds(100) },
			};
			if (images)
				entry.grayBytes[0] = frame;
			else
				entry.grayBytes[entry.GetGrayBytesIndex(0.5f)] = frame;
			entries.Add(entry);
		}
		return entries;
	}

	/// <summary>Puts the library in the database in the given order, as a scan or a database load would.</summary>
	void Load(IEnumerable<FileEntry> entries) {
		foreach (var e in added)
			DatabaseUtils.Database.Remove(e);
		added.Clear();
		foreach (var e in entries) {
			DatabaseUtils.Database.Add(e);
			added.Add(e);
		}
	}

	static ScanEngine NewEngine(int matchingParallelism) {
		var engine = new ScanEngine();
		engine.Settings.ThumbnailCount = 1;
		engine.positionList.Add(0.5f);
		engine.Settings.Percent = 96f;
		engine.Settings.MatchingMaxDegreeOfParallelism = matchingParallelism;
		engine.ElapsedTimer.Start();
		return engine;
	}

	/// <summary>The groups as sorted lists of paths, sorted: comparable across runs whatever the group ids.</summary>
	static List<string> Groups(ScanEngine engine) =>
		engine.Duplicates
			.GroupBy(d => d.GroupId)
			.Select(g => string.Join("|", g.Select(d => d.Path).OrderBy(p => p, StringComparer.Ordinal)))
			.OrderBy(g => g, StringComparer.Ordinal)
			.ToList();

	List<string> Scan(IEnumerable<FileEntry> order, int matchingParallelism) {
		Load(order);
		var engine = NewEngine(matchingParallelism);
		engine.ScanForDuplicates();
		return Groups(engine);
	}

	[Theory]
	[InlineData(true)]  // images: compared on the parallel path from 400 files up
	[InlineData(false)] // videos: the linear video loop, parallel from 400 files up
	public void SameFiles_InAnyOrder_GiveTheSameGroups(bool images) {
		var library = AddChainedLibrary(600, images);
		List<string> first = Scan(library, matchingParallelism: 8);
		Assert.True(first.Count >= 5, $"test setup: the library should form several groups (got {first.Count})");

		var rng = new Random(1);
		for (int run = 0; run < 4; run++) {
			var shuffled = library.OrderBy(_ => rng.Next()).ToList();
			Assert.Equal(first, Scan(shuffled, matchingParallelism: 8));
		}
		// One worker merges the pairs in the same order as many.
		Assert.Equal(first, Scan(library, matchingParallelism: 1));
	}

	// Libraries at or above BucketActivationThreshold videos are compared bucket by bucket; the
	// buckets ran in parallel and each merged its own pairs, so their order mattered there too.
	[Fact]
	public void BucketedVideos_InAnyOrder_GiveTheSameGroups() {
		var library = AddChainedLibrary(ScanEngine.BucketActivationThreshold, images: false);
		// Spread the clips over a few seconds of duration so several buckets share candidates.
		for (int i = 0; i < library.Count; i++) {
			var entry = library[i];
			byte[] frame = entry.grayBytes.Values.Single()!;
			entry.grayBytes.Clear();
			entry.mediaInfo = new MediaInfo { Duration = TimeSpan.FromSeconds(100 + i % 4) };
			entry.grayBytes[entry.GetGrayBytesIndex(0.5f)] = frame;
		}
		List<string> first = Scan(library, matchingParallelism: 8);
		Assert.True(first.Count >= 5, $"test setup: the library should form several groups (got {first.Count})");

		var rng = new Random(3);
		Assert.Equal(first, Scan(library.OrderBy(_ => rng.Next()).ToList(), matchingParallelism: 8));
	}
}
