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
// The compare index may only skip pairs that cannot match: with it, every setting must give the
// same groups, differences and flags as judging every pair. These tests run both loops side by
// side on made-up libraries (SyntheticLibrary: scenes of look-alike photos and clips, copies,
// mirror images, dark and bright frames, and pairs planted right at the threshold and one step
// past it) and compare every result item.

using HEI.Core.AI;
using HEI.Core.Utils;
using HEI.TestSupport;

namespace HEI.Core.Tests;

[Collection("DatabaseUtils")] // ScanForDuplicates reads the shared static database
public class CompareIndexEquivalenceTests : IDisposable {

	readonly List<FileEntry> added = new();

	public void Dispose() => Unload();

	void Unload() {
		foreach (var e in added)
			DatabaseUtils.Database.Remove(e);
		added.Clear();
	}

	public sealed record Case(string Name, bool Videos, int Size, int Seed, int Positions = 1, float Percent = 96f,
		bool Ai = false, bool Flip = false, bool IgnoreBlack = false, bool IgnoreWhite = false, bool PHash = false, bool Combined = false) {
		public override string ToString() => Name;
	}

	public static TheoryData<Case> Cases => new() {
		new("photos grayscale", false, 2500, 1),
		new("photos grayscale 90%", false, 2500, 2, Percent: 90f),
		new("photos grayscale 99%", false, 2500, 3, Percent: 99f),
		new("photos flipped", false, 2500, 4, Flip: true),
		new("photos ignore black", false, 1500, 5, IgnoreBlack: true),
		new("photos ignore white", false, 1500, 6, IgnoreWhite: true),
		new("photos ignore both, flipped", false, 1500, 7, Flip: true, IgnoreBlack: true, IgnoreWhite: true),
		new("photos AI", false, 2500, 8, Ai: true),
		new("photos AI flipped, ignore black", false, 1500, 9, Ai: true, Flip: true, IgnoreBlack: true),
		new("videos grayscale", true, 2000, 10, Positions: 3),
		new("videos pHash", true, 2000, 11, Positions: 3, PHash: true),
		new("videos pHash 85% (no pHash bound)", true, 2000, 12, Positions: 3, Percent: 85f, PHash: true),
		new("videos combined, flipped", true, 2000, 13, Positions: 3, Combined: true, Flip: true),
		new("videos AI, ignore white", true, 2000, 14, Positions: 3, Ai: true, IgnoreWhite: true),
		new("videos 6 positions, combined, AI", true, 2000, 15, Positions: 6, Combined: true, Ai: true),
		// From BucketActivationThreshold videos on, the loop walks duration buckets.
		new("bucketed videos grayscale", true, ScanEngine.BucketActivationThreshold, 16, Positions: 3),
		new("bucketed videos pHash, flipped", true, ScanEngine.BucketActivationThreshold, 17, Positions: 3, PHash: true, Flip: true),
		new("bucketed videos combined, AI", true, ScanEngine.BucketActivationThreshold, 18, Positions: 2, Combined: true, Ai: true),
		new("bucketed videos ignore black, flipped", true, ScanEngine.BucketActivationThreshold, 19, Positions: 1, IgnoreBlack: true, Flip: true),
	};

	[Theory]
	[MemberData(nameof(Cases))]
	public void CompareIndex_GivesTheSameResultsAsEveryPair(Case c) {
		List<SyntheticFile> library = c.Videos
			? SyntheticLibrary.Videos(c.Size, c.Seed, c.Positions, c.Percent)
			: SyntheticLibrary.Photos(c.Size, c.Seed, c.Percent);

		(List<string> everyPair, long everyPairJudged, long everyPairMatched) = Scan(library, c, useIndex: false);
		(List<string> indexed, long indexedJudged, long indexedMatched) = Scan(library, c, useIndex: true);

		Assert.NotEmpty(everyPair);
		Assert.Equal(everyPairMatched, indexedMatched);
		Assert.Equal(everyPair, indexed);
		Assert.True(indexedJudged < everyPairJudged, $"the index judged {indexedJudged:N0} of {everyPairJudged:N0} pairs");
	}

	/// <summary>One compare phase over the library: every result item (path, group, difference, flags), sorted.</summary>
	(List<string> Items, long Judged, long Matched) Scan(List<SyntheticFile> library, Case c, bool useIndex) {
		Unload();
		var engine = new ScanEngine();
		engine.Settings.ThumbnailCount = c.Videos ? c.Positions : 1;
		engine.Settings.Percent = c.Percent;
		engine.Settings.UseAiMatching = c.Ai;
		engine.Settings.CompareHorizontallyFlipped = c.Flip;
		engine.Settings.IgnoreBlackPixels = c.IgnoreBlack;
		engine.Settings.IgnoreWhitePixels = c.IgnoreWhite;
		engine.Settings.UsePHashing = c.PHash;
		engine.Settings.CombineGrayscaleAndPHash = c.Combined;
		engine.Settings.DatabaseCheckpointIntervalMinutes = 0;
		engine.EnsureThumbnailPositions();
		engine.UseCompareIndex = useIndex;
		engine.ElapsedTimer.Start();

		var store = new UnionEmbeddingStore();
		foreach (SyntheticFile file in library) {
			var entry = new FileEntry {
				_Path = file.Path,
				Folder = file.Folder,
				FileSize = file.FileSize,
				invalid = false,
				IsImage = file.IsImage,
				mediaInfo = new MediaInfo { Duration = TimeSpan.FromSeconds(file.DurationSeconds) },
			};
			for (int j = 0; j < file.Frames.Length; j++) {
				double key = file.IsImage ? 0 : entry.GetGrayBytesIndex(engine.positionList[j], engine.Settings.MaxSamplingDurationSeconds);
				entry.grayBytes[key] = file.Frames[j];
				if (file.Embeddings[j] is byte[] embedding)
					store.Put(entry, key, embedding);
			}
			DatabaseUtils.Database.Add(entry);
			added.Add(entry);
		}
		engine.unionEmbeddingStore = store;
		engine.ScanForDuplicates();

		// A group by its members, so the items compare whatever the group ids.
		var groupNames = engine.Duplicates.GroupBy(d => d.GroupId)
			.ToDictionary(g => g.Key, g => string.Join(",", g.Select(d => d.Path).OrderBy(p => p, StringComparer.Ordinal)));
		var items = engine.Duplicates
			.Select(d => $"{d.Path} | group {groupNames[d.GroupId]} | difference {BitConverter.SingleToInt32Bits(d.Similarity):X8} | flags {d.Flags}")
			.OrderBy(l => l, StringComparer.Ordinal)
			.ToList();
		return (items, engine.LastComparePairsJudged, engine.LastComparePairsMatched);
	}
}
