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

using HEI.Core.Utils;

namespace HEI.Agent.Tests;

/// <summary>
/// "The same picture, pixel for pixel", on real files decoded the way a scan decodes them: a resize or a recompression
/// stays a plain copy, while a small local edit (a blurred face, a signature) that the 32×32 average hides does not.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME is process-wide
public sealed class PlainCopyTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-plaincopy-" + Guid.NewGuid().ToString("N"));
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public PlainCopyTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "heiward"));
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	const int W = 1200, H = 900;

	/// <summary>A smooth scene with a textured face-like patch and a light wall, RGB24 rows top-down.</summary>
	static byte[] Scene(bool blurFace = false, bool signed = false) {
		var px = new byte[W * H * 3];
		for (int y = 0; y < H; y++)
			for (int x = 0; x < W; x++) {
				double u = x / (double)W, v = y / (double)H;
				double r = 120 + 80 * u, g = 90 + 100 * v, b = 140 + 60 * Math.Sin(5 * u + 3 * v);
				if (InFace(x, y)) (r, g, b) = Face(x, y);
				if (u > 0.65 && v > 0.78) (r, g, b) = (225, 220, 210); // a light wall
				int o = (y * W + x) * 3;
				px[o] = (byte)Math.Clamp(r, 0, 255);
				px[o + 1] = (byte)Math.Clamp(g, 0, 255);
				px[o + 2] = (byte)Math.Clamp(b, 0, 255);
			}
		if (blurFace) // a strong box blur over the patch: every block keeps about its average
			for (int y = 270; y < 405; y++)
				for (int x = 540; x < 684; x++) {
					double r = 0, g = 0, b = 0;
					int n = 0;
					for (int dy = -24; dy <= 24; dy += 4)
						for (int dx = -24; dx <= 24; dx += 4) {
							var (fr, fg, fb) = Face(x + dx, y + dy);
							r += fr; g += fg; b += fb; n++;
						}
					int o = (y * W + x) * 3;
					(px[o], px[o + 1], px[o + 2]) = ((byte)(r / n), (byte)(g / n), (byte)(b / n));
				}
		if (signed) // a few dark strokes on the wall
			foreach (var (x0, y0, w, h) in new[] { (840, 770, 170, 3), (860, 745, 3, 45), (905, 750, 60, 3), (905, 780, 60, 3), (965, 750, 3, 33) })
				for (int y = y0; y < y0 + h; y++)
					for (int x = x0; x < x0 + w; x++) {
						int o = (y * W + x) * 3;
						(px[o], px[o + 1], px[o + 2]) = (25, 25, 40);
					}
		return px;

		static bool InFace(int x, int y) => x >= 540 && x < 684 && y >= 270 && y < 405;
		// Spots 40 pixels apart, a face's features: detail the 32×32 frame averages away, but a closer look sees.
		static (double, double, double) Face(int x, int y) {
			double t = 60 * Math.Sin(x * Math.PI / 20) * Math.Cos(y * Math.PI / 20);
			return (190 + t, 150 + t, 130 + t);
		}
	}

	string Save(string name, byte[] rgb) {
		string path = Path.Combine(dir, name);
		File.WriteAllBytes(path, PictureCompareTests.Bmp(rgb, W, H));
		return path;
	}

	/// <summary>The picture saved again as a JPEG by Windows, at most <paramref name="maxSide"/> on its longer side (and at most about half its own: the thumbnail's reduced decode).</summary>
	string Jpeg(string bmp, string name, int maxSide) {
		Assert.True(WicImageDecoder.TryThumbnailJpeg(bmp, maxSide, out byte[]? jpeg));
		string path = Path.Combine(dir, name);
		File.WriteAllBytes(path, jpeg!);
		return path;
	}

	static byte[] Gray(string path) {
		Assert.True(WicImageDecoder.TryDecode(path, out byte[]? gray, out _, out _, out _), WicImageDecoder.LastFailure);
		return gray!;
	}

	/// <summary>What the review asks of a plain copy of a photo: the 32×32 average, every cell, and the close look.</summary>
	static (float Percent, int Cell, double? Close) Judge(string a, string b) {
		byte[] ga = Gray(a), gb = Gray(b);
		return (100f * (1f - GrayBytesUtils.PercentageDifference(ga, gb)), GrayBytesUtils.MaxCellDifference(ga, gb), CloseLook.Difference(a, b));
	}

	static bool PlainCopy((float Percent, int Cell, double? Close) j) =>
		j.Percent >= ReportBuilder.PlainCopyPercent && j.Cell <= ReportBuilder.PlainCopyCellLimit && j.Close <= ReportBuilder.CloseLookLimit;

	[Fact]
	public void AResizeOrARecompression_IsStillAPlainCopy() {
		string original = Save("original.bmp", Scene());
		foreach (int side in new[] { 1200, 600, 300 }) {
			var j = Judge(original, Jpeg(original, $"copy-{side}.jpg", side));
			Assert.True(PlainCopy(j), $"{side}: {j}");
		}
	}

	[Fact]
	public void ABlurredFace_PassesTheAverageButNotTheCloseLook() {
		string original = Save("original.bmp", Scene());
		string bmp = Save("blurred.bmp", Scene(blurFace: true));
		var j = Judge(original, Jpeg(bmp, "blurred.jpg", W));
		Assert.True(j.Percent >= ReportBuilder.PlainCopyPercent, $"the average hides it: {j}");
		Assert.False(PlainCopy(j), j.ToString());
		Assert.True(j.Close > ReportBuilder.CloseLookLimit, j.ToString());
	}

	[Fact]
	public void ASignature_PassesTheAverageButNotTheCells() {
		string original = Save("original.bmp", Scene());
		var j = Judge(original, Jpeg(Save("signed.bmp", Scene(signed: true)), "signed.jpg", W));
		Assert.True(j.Percent >= ReportBuilder.PlainCopyPercent, $"the average hides it: {j}");
		Assert.True(j.Cell > ReportBuilder.PlainCopyCellLimit, j.ToString());
		Assert.False(PlainCopy(j), j.ToString());
	}

	[Fact]
	public void AHardLink_IsNoCopy() =>
		// One file under two paths: the scan leaves the pair out (and the Recycler won't count one as the other's copy).
		Assert.True(AgentScanner.BuildSettings(new AgentConfig { ScanAllDrives = false }, new List<string>()).ExcludeHardLinks);

	[Fact]
	public void ACloseLook_IsKeptForTheNextScan_UntilAFileChanges() {
		string a = Save("a.bmp", Scene()), b = Jpeg(a, "b.jpg", 600);
		int looks = 0;
		bool? Look() { looks++; return true; }

		var first = ReportBuilder.ContentHashes.Load();
		Assert.True(first.UpClose(a, b, Look));
		Assert.True(first.UpClose(b, a, Look)); // either way round, once
		first.Save();
		Assert.Equal(1, looks);

		Assert.True(ReportBuilder.ContentHashes.Load().UpClose(a, b, Look));
		Assert.Equal(1, looks);

		File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(b).AddMinutes(5));
		Assert.True(ReportBuilder.ContentHashes.Load().UpClose(a, b, Look));
		Assert.Equal(2, looks);
	}

	[Fact]
	public void ASaveThatChangesNothing_WritesNothing() {
		// A scan that finds the same files leaves hashes.json, looks.json and texts.json as they were; one that changes them writes.
		string a = Save("a.bmp", Scene()), b = Jpeg(a, "b.jpg", 600);
		var first = ReportBuilder.ContentHashes.Load();
		Assert.True(first.UpClose(a, b, () => true));
		first.Save();
		string[] files = { ReportBuilder.ContentHashes.FilePath, ReportBuilder.ContentHashes.LooksPath, ReportBuilder.ContentHashes.TextsPath };
		DateTime old = DateTime.UtcNow.AddDays(-1);
		foreach (string f in files) File.SetLastWriteTimeUtc(f, old);

		var second = ReportBuilder.ContentHashes.Load();
		Assert.True(second.UpClose(a, b, () => true));
		second.Save();
		Assert.All(files, f => Assert.Equal(old, File.GetLastWriteTimeUtc(f)));

		ReportBuilder.ContentHashes.Load().Save(); // no pair to look at this time: the look drops out, and looks.json is written
		Assert.NotEqual(old, File.GetLastWriteTimeUtc(ReportBuilder.ContentHashes.LooksPath));
	}
}
