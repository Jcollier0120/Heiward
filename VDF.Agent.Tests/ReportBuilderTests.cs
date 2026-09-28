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
/// What gets kept and what gets pre-ticked for the Recycle Bin. Fingerprint values are the ones
/// measured on the test set (resized 99.9+, recompressed 99.8+, crops/flips/colour edits 96.2–98.0,
/// different shots 94.3), so these tests pin the calibration, not just the code paths.
/// </summary>
public sealed class ReportBuilderTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "vdf-agent-tests-" + Guid.NewGuid().ToString("N"));
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
		var g = Single(Photo("IMG_2.heic", 4032, 3024, Bytes(8000, 1), gray: 94.3f, ai: 94.3f), Photo("IMG_3.heic", 4032, 3024, Bytes(8100, 2), gray: 94.3f, ai: 94.3f));
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
