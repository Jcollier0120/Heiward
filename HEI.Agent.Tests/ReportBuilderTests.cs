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

using HEI.Core;
using HEI.Core.ViewModels;

namespace HEI.Agent.Tests;

/// <summary>
/// What gets kept and what gets pre-ticked for the Recycle Bin. Fingerprint values are the ones
/// measured on the test set (resized 99.9+, recompressed 99.8+, crops/flips/colour edits 96.2–98.0,
/// different shots 94.3), so these tests pin the calibration, not just the code paths.
/// </summary>
public sealed class ReportBuilderTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "heiward-tests-" + Guid.NewGuid().ToString("N"));
	readonly Guid group = Guid.NewGuid();
	readonly FakeFingerprints fingerprints = new();

	/// <summary>Each file's similarity to whichever file ends up kept (the builder asks "file vs keeper").</summary>
	sealed class FakeFingerprints : IFingerprints {
		public readonly Dictionary<string, (float Gray, float? Ai)> ToKeeper = new(StringComparer.OrdinalIgnoreCase);
		public float? GrayPercent(string a, string b) => ToKeeper.TryGetValue(a, out var v) ? v.Gray : null;
		public float? AiPercent(string a, string b) => ToKeeper.TryGetValue(a, out var v) ? v.Ai : null;
	}

	public ReportBuilderTests() => Directory.CreateDirectory(dir);
	public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

	/// <param name="gray">Grayscale similarity to the kept file; <paramref name="ai"/>: the AI's cosine to it.</param>
	DuplicateItem Photo(string relPath, int w, int h, byte[] content, float gray = 100f, float? ai = null, DateTime? modified = null) {
		string path = Path.Combine(dir, relPath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, content);
		DateTime when = modified ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(path, when);
		fingerprints.ToKeeper[path] = (gray, ai);
		return new DuplicateItem {
			Path = path, Folder = Path.GetDirectoryName(path)!, SizeLong = content.Length, IsImage = true,
			FrameSize = $"{w}x{h}", FrameSizeInt = w + h, Similarity = gray, GroupId = group,
			Flags = ai != null ? DuplicateFlags.AiMatched : DuplicateFlags.None, DateModified = when.ToLocalTime(),
		};
	}

	static byte[] Bytes(int n, byte seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 31 + seed)).ToArray();

	ReportGroup Single(params DuplicateItem[] items) => Assert.Single(ReportBuilder.Build(items, fingerprints));
	static ReportItem Named(ReportGroup g, string name) => g.Items.Single(i => i.Name == name);

	[Fact]
	public void IdenticalCopyInDownloads_KeepsTheOneOutsideDownloads() {
		byte[] content = Bytes(5000, 1);
		var g = Single(Photo(@"Downloads\IMG_1 - Copy.jpg", 4032, 3024, content), Photo(@"Pictures\IMG_1.jpg", 4032, 3024, content));
		Assert.Equal("identical", g.Kind);
		Assert.EndsWith(@"Pictures\IMG_1.jpg", g.KeepPath);
		Assert.Equal("identical", Named(g, "IMG_1 - Copy.jpg").Relation);
		Assert.True(Named(g, "IMG_1 - Copy.jpg").Suggested);
		Assert.Equal(5000, g.ReclaimBytes);
	}

	[Fact]
	public void ResizedAndRecompressedCopies_ArePreTicked() {
		var g = Single(
			Photo("p.jpg", 4032, 3024, Bytes(9000, 1)),
			Photo("p.resized.jpg", 1280, 960, Bytes(2000, 2), gray: 99.95f),
			Photo("p.recompressed.jpg", 4032, 3024, Bytes(3000, 3), gray: 99.8f));
		Assert.Equal("copies", g.Kind);
		Assert.EndsWith("p.jpg", g.KeepPath);
		Assert.Equal("smaller", Named(g, "p.resized.jpg").Relation);
		Assert.Equal("compressed", Named(g, "p.recompressed.jpg").Relation);
		Assert.All(g.Items.Where(i => !i.Keep), i => Assert.True(i.Suggested));
	}

	[Fact]
	public void BiggerColourEdit_DoesNotReplaceTheOlderOriginal() {
		// Regression: "same resolution, largest file" kept a colour-boosted edit over the original.
		var original = Photo("p.jpg", 4032, 3024, Bytes(8000, 1), modified: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		var edited = Photo("p.edited.jpg", 4032, 3024, Bytes(9500, 2), gray: 97.1f, modified: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
		var g = Single(original, edited);
		Assert.EndsWith("p.jpg", g.KeepPath);
		Assert.Equal("variant", Named(g, "p.edited.jpg").Relation);
		Assert.False(Named(g, "p.edited.jpg").Suggested);
	}

	[Theory]
	[InlineData(97.66f, -1f, "variant")]   // crop: grayscale below the plain-copy line, no AI verdict
	[InlineData(96.16f, 94.0f, "variant")] // flip
	[InlineData(97.99f, 98.3f, "edited")]  // colour edit: the AI sees the same picture
	[InlineData(95.0f, 95.0f, "variant")]  // crop or look-alike
	public void ChangedPictures_AreLabelledButNeverTicked(float gray, float ai, string relation) {
		var g = Single(Photo("p.jpg", 4032, 3024, Bytes(8000, 1)), Photo("p.changed.jpg", 4032, 3024, Bytes(7000, 2), gray, ai < 0 ? null : ai));
		ReportItem changed = Named(g, "p.changed.jpg");
		Assert.Equal(relation, changed.Relation);
		Assert.False(changed.Suggested);
	}

	[Fact]
	public void LookAlikeShots_AreYourPickWithNothingTicked() {
		// Not numbered in a row: IMG_2 and IMG_3 next to each other would be a burst, and no set at all.
		var g = Single(Photo("Lake.heic", 4032, 3024, Bytes(8000, 1), gray: 94.3f, ai: 94.3f), Photo("Lake at dusk.heic", 4032, 3024, Bytes(8100, 2), gray: 94.3f, ai: 94.3f));
		Assert.Equal("similar", g.Kind);
		Assert.StartsWith("your pick", g.KeepReason);
		Assert.Equal(0, g.ReclaimBytes);
		Assert.DoesNotContain(g.Items, i => i.Suggested);
	}

	[Fact]
	public void HigherResolution_WinsOverEverythingElse() {
		var small = Photo("big-file-small-picture.png", 1280, 960, Bytes(20000, 1), modified: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		var large = Photo("small-file-big-picture.jpg", 4032, 3024, Bytes(9000, 2));
		var g = Single(small, large);
		Assert.EndsWith("small-file-big-picture.jpg", g.KeepPath);
		Assert.StartsWith("highest resolution", g.KeepReason);
	}

	[Fact]
	public void CopiesAreJudgedAgainstTheKeeper_NotAgainstWhateverPulledThemIntoTheGroup() {
		// Regression: VDF records each member's similarity to the member that pulled it into the group.
		// When that chain ran through an edited copy, plain resized copies of the original looked like
		// variants and lost their ticks. Their own fingerprints against the kept file decide now.
		var original = Photo("p.jpg", 4032, 3024, Bytes(8000, 1), modified: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		var resized = Photo("p.resized.jpg", 1280, 960, Bytes(2000, 2), gray: 99.93f, modified: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
		resized.Similarity = 96.5f; // VDF's tree edge, e.g. to an edited copy: must not matter
		var g = Single(original, resized);
		Assert.Equal("smaller", Named(g, "p.resized.jpg").Relation);
		Assert.True(Named(g, "p.resized.jpg").Suggested);
	}

	[Fact]
	public void IdenticalCopiesSplitAcrossGroups_JoinTheGroupOfTheirClosestMatch() {
		// Regression (a real library): VDF grouped a burst shot's original with its neighbour as a
		// look-alike, and the original's copy with a JPEG export of it, where the copy was kept.
		byte[] heic = Bytes(8000, 1);
		var neighbour = Photo(@"iCloud\IMG_3727.heic", 4032, 3024, Bytes(8100, 2), gray: 96.2f, ai: 95f);
		var original = Photo(@"iCloud\IMG_3730.heic", 4032, 3024, heic, modified: new DateTime(2026, 2, 11, 0, 0, 0, DateTimeKind.Utc));
		var copy = Photo(@"Test\IMG_3730.heic", 4032, 3024, heic, modified: new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc));
		var export = Photo(@"Test\IMG_3730 (exported).jpg", 2016, 1512, Bytes(3000, 3), gray: 99.82f);
		neighbour.GroupId = original.GroupId = Guid.NewGuid();

		var g = Single(neighbour, original, copy, export); // the neighbour, left alone, is no group
		Assert.Equal(original.Path, g.KeepPath);
		Assert.Equal("identical", g.Items.Single(i => i.Path == copy.Path).Relation);
		Assert.Equal("smaller", Named(g, "IMG_3730 (exported).jpg").Relation);
		Assert.All(g.Items.Where(i => !i.Keep), i => Assert.True(i.Suggested));
	}

	[Fact]
	public void TwoSetsOfCopiesInOneGroup_EachKeepsOneAndTicksTheOther() {
		// Regression (a real library): two look-alike burst shots, each imported twice, came as one
		// VDF group. Judged against one kept file, the second shot's copy was a look-alike, unticked.
		byte[] a = Bytes(8000, 1), b = Bytes(8100, 2);
		var shotA = Photo("20151008_204237.jpg", 5312, 2988, a, modified: new DateTime(2015, 10, 8, 20, 42, 37, DateTimeKind.Utc));
		var copyA = Photo("20151008_204237(1).jpg", 5312, 2988, a, modified: new DateTime(2015, 10, 8, 20, 42, 37, DateTimeKind.Utc));
		var shotB = Photo("20151008_204240.jpg", 5312, 2988, b, gray: 93.97f, modified: new DateTime(2015, 10, 8, 20, 42, 40, DateTimeKind.Utc));
		var copyB = Photo("20151008_204240(1).jpg", 5312, 2988, b, gray: 93.97f, modified: new DateTime(2015, 10, 8, 20, 42, 40, DateTimeKind.Utc));

		var groups = ReportBuilder.Build(new[] { shotA, copyA, shotB, copyB }, fingerprints);
		Assert.Equal(2, groups.Count);
		Assert.Contains(groups, g => g.KeepPath == shotA.Path && g.Items.Single(i => i.Path == copyA.Path).Suggested);
		Assert.Contains(groups, g => g.KeepPath == shotB.Path && g.Items.Single(i => i.Path == copyB.Path).Suggested);
	}

	[Theory]
	[InlineData("IMG_20141118_180734.jpg", "IMG_20141118_180734_Original.jpg")] // Samsung, Picasa
	[InlineData("IMG_1234-edited.jpg", "IMG_1234.jpg")]                         // Google Photos
	public void APhotoAndItsNamedEdit_AreBothKept(string edited, string original) {
		// Regression (a real library): a light edit scored 99.68% against its original, above the
		// plain-copy line, and the original was pre-ticked.
		var g = Single(
			Photo(edited, 1836, 3264, Bytes(7900, 1), modified: new DateTime(2014, 11, 18, 0, 0, 0, DateTimeKind.Utc)),
			Photo(original, 1836, 3264, Bytes(10900, 2), gray: 99.68f, modified: new DateTime(2014, 11, 19, 0, 0, 0, DateTimeKind.Utc)));
		Assert.DoesNotContain(g.Items, i => i.Suggested);
		Assert.Contains(g.Items, i => i.Relation == "edited");
	}

	[Theory]
	[InlineData("IMG_3345.GIF", "IMG_4327.GIF")]
	[InlineData("IMG_3785.GIF", "IMG_3784.JPG")]
	public void AnimatedPictures_MatchedByTheirFirstFrame_AreNotTicked(string a, string b) {
		// Regression (a real library): two different GIFs that start with the same frame, and a still
		// taken from a GIF, were pre-ticked as copies. (IMG_3784 and IMG_3785 are also numbered in a
		// row, so they're now no set at all.)
		var groups = ReportBuilder.Build(new[] { Photo(a, 720, 404, Bytes(9000, 1)), Photo(b, 588, 330, Bytes(2000, 2), gray: 99.87f) }, fingerprints);
		Assert.DoesNotContain(groups.SelectMany(g => g.Items), i => i.Suggested);
	}

	[Fact]
	public void BurstShots_AreNoSet_ThoughTheyMatchLikeResavedCopies() {
		// Regression: burst shots score 99.9%, above the plain-copy line, and were ticked as "compressed" copies.
		DuplicateItem Shot(int n) => Photo($@"Camera\IMG_{n}.jpg", 4032, 3024, Bytes(8000 + n, (byte)n), gray: 99.9f);
		Assert.Empty(ReportBuilder.Build(new[] { Shot(1001), Shot(1002), Shot(1003), Shot(1004) }, fingerprints));
	}

	[Fact]
	public void ABurstShotsCopy_StaysWithItsOwnShot_NotWithTheBurst() {
		// A backup of one shot of a burst, alone in its folder: a copy of that shot, not of the one kept from the burst.
		byte[] shot2 = Bytes(8002, 2);
		var burst = new[] {
			Photo(@"Camera\IMG_1001.jpg", 4032, 3024, Bytes(8001, 1), gray: 99.9f),
			Photo(@"Camera\IMG_1002.jpg", 4032, 3024, shot2),
			Photo(@"Camera\IMG_1003.jpg", 4032, 3024, Bytes(8003, 3), gray: 99.9f),
			Photo(@"Camera\IMG_1004.jpg", 4032, 3024, Bytes(9000, 4), gray: 99.9f), // the largest: kept first
			Photo(@"Backup\IMG_1002.jpg", 4032, 3024, shot2),
		};
		ReportGroup g = Single(burst);
		Assert.Equal("identical", g.Kind);
		Assert.Equal(new[] { "IMG_1002.jpg", "IMG_1002.jpg" }, g.Items.Select(i => i.Name));
	}

	[Fact]
	public void ShotsNamedAfterTheTimeTaken_SecondsApart_AreABurst() {
		// Regression (a real library): Samsung names each shot after its time, and read as plain numbers
		// 20:53:59 and 20:54:01 were 42 apart, too far for a burst: the three were a set of look-alikes.
		Assert.Empty(ReportBuilder.Build(new[] {
			Photo(@"Camera\20201105_205359_HDR.jpg", 4032, 3024, Bytes(8000, 1)),
			Photo(@"Camera\20201105_205401_HDR.jpg", 4032, 3024, Bytes(8100, 2), gray: 94.6f, ai: 95f),
			Photo(@"Camera\20201105_205411_HDR.jpg", 4032, 3024, Bytes(8200, 3), gray: 95.5f, ai: 96f),
		}, fingerprints));
	}

	[Fact]
	public void TwoShotsOfOneBurst_AreNeverInOneSet_ThoughNeitherIsKept() {
		// Regression (a real library): IMG_0569 and IMG_0570, taken a minute apart two weeks after
		// IMG_0538, were both look-alikes of it. The one more like IMG_0538 stays; the other is no look-alike.
		var g = Single(
			Photo(@"Camera\IMG_0538.heic", 4032, 3024, Bytes(8000, 1), modified: new DateTime(2023, 1, 24, 0, 0, 0, DateTimeKind.Utc)),
			Photo(@"Camera\IMG_0569.heic", 4032, 3024, Bytes(8100, 2), gray: 95.7f, ai: 95.7f, modified: new DateTime(2023, 2, 6, 12, 6, 0, DateTimeKind.Utc)),
			Photo(@"Camera\IMG_0570.heic", 4032, 3024, Bytes(8200, 3), gray: 96f, ai: 96f, modified: new DateTime(2023, 2, 6, 12, 7, 0, DateTimeKind.Utc)));
		Assert.Equal(new[] { "IMG_0538.heic", "IMG_0570.heic" }, g.Items.Select(i => i.Name));
	}

	[Fact]
	public void ABurstShot_IsNoCopy_OfARenamedCopyOfItsNeighbour() {
		// holiday.jpg is IMG_1002 copied and renamed. IMG_1003, the next shot, scores 99.9% against it,
		// which passes for a resaved copy: it was ticked for the Recycle Bin.
		byte[] shot = Bytes(8002, 2);
		var g = Single(
			Photo(@"Holiday\holiday.jpg", 4032, 3024, shot, modified: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
			Photo(@"Camera\IMG_1002.jpg", 4032, 3024, shot, modified: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
			Photo(@"Camera\IMG_1003.jpg", 4032, 3024, Bytes(8003, 3), gray: 99.9f, modified: new DateTime(2026, 2, 1, 0, 0, 5, DateTimeKind.Utc)));
		Assert.Equal("identical", g.Kind);
		Assert.Equal(new[] { "holiday.jpg", "IMG_1002.jpg" }, g.Items.Select(i => i.Name));
	}

	[Fact]
	public void NumberedCopies_OfOneShot_AreStillCopies() {
		// "(1)" and " - Copy" are the same shot, not the next one; IMG_1235 next to them makes a series.
		byte[] content = Bytes(8000, 1);
		Photo("IMG_1235.jpg", 4032, 3024, Bytes(8100, 2)); // on disk only: the neighbour
		var g = Single(Photo("IMG_1234.jpg", 4032, 3024, content), Photo("IMG_1234 (1).jpg", 4032, 3024, content), Photo("IMG_1234 - Copy.jpg", 4032, 3024, content));
		Assert.Equal("identical", g.Kind);
		Assert.Equal(3, g.Items.Count);
	}

	[Fact]
	public void NumberedAlikeFiles_WithoutASeriesAroundThem_AreJudgedAsUsual() {
		// Photo 1 and Photo 7 in two folders, each alone: nothing says burst.
		var g = Single(Photo(@"A\Photo 1.jpg", 4032, 3024, Bytes(9000, 1)), Photo(@"B\Photo 7.jpg", 4032, 3024, Bytes(8100, 2), gray: 95f, ai: 95f));
		Assert.Equal("similar", g.Kind);
	}

	[Fact]
	public void PicturesLessThan75PercentAlike_LeaveTheSet() {
		// VDF chains: q is like r, r is like p, but q looks nothing like the kept p.
		var g = Single(
			Photo("p.jpg", 4032, 3024, Bytes(9000, 1)), // the largest: kept
			Photo("r.jpg", 4032, 3024, Bytes(8100, 2), gray: 94f, ai: 95f),
			Photo("q.jpg", 4032, 3024, Bytes(8200, 3), gray: 70f, ai: 74.9f));
		Assert.Equal(new[] { "p.jpg", "r.jpg" }, g.Items.Select(i => i.Name));
		Assert.All(g.Items, i => Assert.True(i.Similarity >= ReportBuilder.MinAlikePercent));
	}

	[Fact]
	public void ASetOfOneLookAlikeUnder75Percent_IsNoSet() {
		Assert.Empty(ReportBuilder.Build(new[] { Photo("p.jpg", 4032, 3024, Bytes(9000, 1)), Photo("q.jpg", 4032, 3024, Bytes(8100, 2), gray: 60f, ai: 70f) }, fingerprints));
	}

	[Theory]
	[InlineData("IMG_1234", "img_#", 1234L)]
	[InlineData("IMG_1234 (1)", "img_#", 1234L)]
	[InlineData("IMG_1234 - Copy", "img_#", 1234L)]
	[InlineData("IMG_1234_Original", "img_#", 1234L)]
	[InlineData("IMG_1234-edited", "img_#", 1234L)]
	[InlineData("20260101_120000_003", "20260101_120000_#", 3L)]
	[InlineData("DSC01234", "dsc#", 1234L)]
	[InlineData("IMG-20201105-WA0001", "img-20201105-wa#", 1L)]  // WhatsApp: a date and a counter
	[InlineData("00001IMG_00001_BURST20260101120000123", "burst20260101120000123", 1L)]
	[InlineData("00000IMG_00000_BURST20260101120000123_COVER", "burst20260101120000123", 0L)]
	public void BurstSeries_ReadsTheSeriesAndTheNumber(string stem, string series, long number) =>
		Assert.Equal(new BurstSeries.Shot(series, number), BurstSeries.Parse(stem));

	[Theory]
	[InlineData("20201105_205359_HDR", "@", "2020-11-05 20:53:59.000")]         // Samsung
	[InlineData("20151008_204237(1)", "@", "2015-10-08 20:42:37.000")]
	[InlineData("IMG_20140830_092242", "img_@", "2014-08-30 09:22:42.000")]      // Android
	[InlineData("VID_20140830_092242", "vid_@", "2014-08-30 09:22:42.000")]
	[InlineData("PXL_20260101_120000123.MP", "pxl_@", "2026-01-01 12:00:00.123")] // Pixel, a motion photo
	[InlineData("Screenshot_20231105-205359_Chrome", "screenshot_@", "2023-11-05 20:53:59.000")]
	[InlineData("Screenshot 2024-05-08 104604", "screenshot @", "2024-05-08 10:46:04.000")] // Windows
	[InlineData("2020-11-05 20.53.59", "@", "2020-11-05 20:53:59.000")]         // Dropbox
	public void BurstSeries_ReadsTheTimeTaken(string stem, string series, string taken) {
		long ms = DateTime.ParseExact(taken, "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture).Ticks / TimeSpan.TicksPerMillisecond;
		Assert.Equal(new BurstSeries.Shot(series, ms, Timed: true), BurstSeries.Parse(stem));
	}

	[Theory]
	[InlineData("20201345_205359", "20201345_#", 205359L)] // no 13th month: a number, not a time
	[InlineData("IMG_20140830_0922", "img_20140830_#", 922L)]
	public void BurstSeries_NumbersThatOnlyLookLikeATime_AreNumbers(string stem, string series, long number) =>
		Assert.Equal(new BurstSeries.Shot(series, number), BurstSeries.Parse(stem));

	[Theory]
	[InlineData("Beach")]
	[InlineData("Copy of holiday")]
	public void BurstSeries_NamesWithoutANumber_AreNoSeries(string stem) => Assert.Null(BurstSeries.Parse(stem));

	[Fact]
	public void GroupKey_IgnoresOrderAndCase() {
		Assert.Equal(ReportBuilder.GroupKey(new[] { @"C:\A\x.jpg", @"C:\B\y.jpg" }), ReportBuilder.GroupKey(new[] { @"c:\b\Y.JPG", @"C:\A\x.jpg" }));
		Assert.NotEqual(ReportBuilder.GroupKey(new[] { @"C:\A\x.jpg", @"C:\B\y.jpg" }), ReportBuilder.GroupKey(new[] { @"C:\A\x.jpg", @"C:\B\z.jpg" }));
	}

	[Fact]
	public void FilesThatVanishedSinceTheScan_AreLeftOut() {
		var a = Photo("a.jpg", 4032, 3024, Bytes(8000, 1));
		var b = Photo("b.jpg", 1280, 960, Bytes(2000, 2));
		File.Delete(b.Path);
		Assert.Empty(ReportBuilder.Build(new[] { a, b }, fingerprints)); // a group of one is no group
	}
}
