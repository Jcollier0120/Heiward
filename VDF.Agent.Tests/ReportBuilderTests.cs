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

using VDF.Core;
using VDF.Core.ViewModels;

namespace VDF.Agent.Tests;

/// <summary>
/// What gets kept and what gets pre-ticked for the Recycle Bin. Similarity values are the ones
/// measured on the test set (resized 100, recompressed 99.8, crops/flips/colour edits 96.4–97.6,
/// different shots 94.3), so these tests pin the calibration, not just the code paths.
/// </summary>
public sealed class ReportBuilderTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "vdf-agent-tests-" + Guid.NewGuid().ToString("N"));
	readonly Guid group = Guid.NewGuid();

	public ReportBuilderTests() => Directory.CreateDirectory(dir);
	public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

	DuplicateItem Photo(string relPath, int w, int h, byte[] content, float similarity = 100f, bool ai = false, DateTime? modified = null) {
		string path = Path.Combine(dir, relPath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, content);
		DateTime when = modified ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(path, when);
		return new DuplicateItem {
			Path = path, Folder = Path.GetDirectoryName(path)!, SizeLong = content.Length, IsImage = true,
			FrameSize = $"{w}x{h}", FrameSizeInt = w + h, Similarity = similarity, GroupId = group,
			Flags = ai ? DuplicateFlags.AiMatched : DuplicateFlags.None, DateModified = when.ToLocalTime(),
		};
	}

	static byte[] Bytes(int n, byte seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 31 + seed)).ToArray();

	static ReportGroup Single(params DuplicateItem[] items) => Assert.Single(ReportBuilder.Build(items));
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
			Photo("p.resized.jpg", 1280, 960, Bytes(2000, 2), similarity: 100f),
			Photo("p.recompressed.jpg", 4032, 3024, Bytes(3000, 3), similarity: 99.8f));
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
		var edited = Photo("p.edited.jpg", 4032, 3024, Bytes(9500, 2), similarity: 97.1f, modified: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
		var g = Single(original, edited);
		Assert.EndsWith("p.jpg", g.KeepPath);
		Assert.Equal("variant", Named(g, "p.edited.jpg").Relation);
		Assert.False(Named(g, "p.edited.jpg").Suggested);
	}

	[Theory]
	[InlineData(97.5f, false, "variant")]  // crop, classic match below the plain-copy line
	[InlineData(96.4f, false, "variant")]  // mirror
	[InlineData(98.3f, true, "edited")]    // AI: same picture, colours changed
	[InlineData(95.0f, true, "variant")]   // AI: crop or look-alike
	public void ChangedPictures_AreLabelledButNeverTicked(float similarity, bool ai, string relation) {
		var g = Single(Photo("p.jpg", 4032, 3024, Bytes(8000, 1)), Photo("p.changed.jpg", 4032, 3024, Bytes(7000, 2), similarity, ai));
		ReportItem changed = Named(g, "p.changed.jpg");
		Assert.Equal(relation, changed.Relation);
		Assert.False(changed.Suggested);
	}

	[Fact]
	public void LookAlikeShots_AreYourPickWithNothingTicked() {
		var g = Single(Photo("IMG_2.heic", 4032, 3024, Bytes(8000, 1)), Photo("IMG_3.heic", 4032, 3024, Bytes(8100, 2), similarity: 94.3f, ai: true));
		Assert.Equal("similar", g.Kind);
		Assert.StartsWith("your pick", g.KeepReason);
		Assert.Equal(0, g.ReclaimBytes);
		Assert.DoesNotContain(g.Items, i => i.Suggested);
	}

	[Fact]
	public void HigherResolution_WinsOverEverythingElse() {
		var small = Photo("big-file-small-picture.png", 1280, 960, Bytes(20000, 1), modified: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		var large = Photo("small-file-big-picture.jpg", 4032, 3024, Bytes(9000, 2), similarity: 100f);
		var g = Single(small, large);
		Assert.EndsWith("small-file-big-picture.jpg", g.KeepPath);
		Assert.StartsWith("highest resolution", g.KeepReason);
	}

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
		Assert.Empty(ReportBuilder.Build(new[] { a, b })); // a group of one is no group
	}
}
