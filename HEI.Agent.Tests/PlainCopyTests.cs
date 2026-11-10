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

	/// <summary>The picture's colours changed: to its gray (<paramref name="bw"/>), or warmer, red up 8% and blue down 12%.</summary>
	static byte[] Recolored(byte[] rgb, bool bw) {
		var px = new byte[rgb.Length];
		for (int o = 0; o < rgb.Length; o += 3) {
			int r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
			if (bw)
				px[o] = px[o + 1] = px[o + 2] = (byte)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
			else
				(px[o], px[o + 1], px[o + 2]) = ((byte)Math.Min(255, Math.Round(r * 1.08)), (byte)g, (byte)Math.Round(b * 0.88));
		}
		return px;
	}

	[Fact]
	public void AResizeOrARecompression_HasTheSameColours() {
		string original = Save("original.bmp", Scene());
		foreach (int side in new[] { 1200, 600, 300 }) {
			double? d = ColorLook.Difference(original, Jpeg(original, $"copy-{side}.jpg", side));
			Assert.True(d <= ReportBuilder.SameColorsLimit, $"{side}: {d}");
		}
	}

	[Fact]
	public void ABlackAndWhiteCopy_PassesTheGrayFramesButNotTheColours() {
		// Its gray is the original's, weighed as the gray frames weigh it. (The close look decodes to gray with Windows' own
		// weights, so on this saturated scene it sees a little; on the look-alike set's photos 92 passed it all.)
		string original = Save("original.bmp", Scene());
		string copy = Jpeg(Save("bw.bmp", Recolored(Scene(), bw: true)), "bw.jpg", W);
		var j = Judge(original, copy);
		Assert.True(j.Percent >= ReportBuilder.PlainCopyPercent && j.Cell <= ReportBuilder.PlainCopyCellLimit, $"the gray frames can't see it: {j}");
		double? d = ColorLook.Difference(original, copy);
		Assert.True(d > 4 * ReportBuilder.SameColorsLimit, $"{d}");
	}

	[Fact]
	public void AWarmerCopy_HasOtherColours() {
		string original = Save("original.bmp", Scene());
		double? d = ColorLook.Difference(original, Jpeg(Save("warm.bmp", Recolored(Scene(), bw: false)), "warm.jpg", W));
		Assert.True(d > 2 * ReportBuilder.SameColorsLimit, $"{d}");
	}

	[Fact]
	public void BrightnessAlone_MovesNoColour() {
		// Each pixel's gray goes up by as much as its red, green and blue: blue and red minus gray stay put.
		var a = new byte[64 * 64 * 3];
		for (int i = 0; i < a.Length; i++) a[i] = (byte)(40 + i * 7 % 150);
		byte[] brighter = a.Select(v => (byte)(v + 30)).ToArray();
		Assert.Equal(0.0, ColorLook.Difference(a, a, 64));
		Assert.True(ColorLook.Difference(a, brighter, 64) < 0.01);
	}

	[Fact]
	public void OnePartRecoloured_ShowsInItsCell() {
		// A red jumper made blue, an eighth of the picture each way: the picture's average hardly moves, its cell does.
		var a = new byte[64 * 64 * 3];
		for (int i = 0; i < a.Length; i += 3) (a[i], a[i + 1], a[i + 2]) = (60, 70, 180); // BGR: reddish
		byte[] b = (byte[])a.Clone();
		for (int y = 0; y < 8; y++)
			for (int x = 0; x < 8; x++) {
				int i = (y * 64 + x) * 3;
				(b[i], b[i + 1], b[i + 2]) = (180, 70, 60); // bluish
			}
		Assert.True(ColorLook.Difference(a, b, 64) > 100);
		Assert.Throws<ArgumentException>(() => ColorLook.Difference(a, b.AsSpan(3).ToArray(), 64));
	}

	[Fact]
	public void ColoursAreKeptWithTheCloseLook_UntilAFileChanges() {
		string a = Save("a.bmp", Scene()), b = Jpeg(a, "b.jpg", 600);
		int colors = 0;
		bool? Colors() { colors++; return false; }

		var first = ReportBuilder.ContentHashes.Load();
		Assert.False(first.SameColors(a, b, Colors)); // not looked at up close: no plain copy, and nothing compared
		Assert.Equal(0, colors);
		Assert.True(first.UpClose(a, b, () => true));
		Assert.False(first.SameColors(b, a, Colors));
		Assert.False(first.SameColors(a, b, Colors));
		first.Save();
		Assert.Equal(1, colors);

		var second = ReportBuilder.ContentHashes.Load();
		Assert.True(second.UpClose(a, b, () => throw new InvalidOperationException("looked at again")));
		Assert.False(second.SameColors(a, b, Colors));
		Assert.Equal(1, colors);

		File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(b).AddMinutes(5));
		var third = ReportBuilder.ContentHashes.Load();
		Assert.True(third.UpClose(a, b, () => true));
		Assert.False(third.SameColors(a, b, Colors));
		Assert.Equal(2, colors);
	}

	[Fact]
	public void ACloseLookKeptBeforeColoursWereCompared_HasThemComparedOnce() {
		// looks.json from before this check: the close look is kept, the colours are compared on the next scan.
		string a = Save("a.bmp", Scene()), b = Jpeg(a, "b.jpg", 600);
		var (first, second) = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? (a, b) : (b, a);
		Directory.CreateDirectory(Path.GetDirectoryName(ReportBuilder.ContentHashes.LooksPath)!);
		File.WriteAllText(ReportBuilder.ContentHashes.LooksPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> {
			[first + "\n" + second] = new {
				sizeA = new FileInfo(first).Length, modifiedA = File.GetLastWriteTimeUtc(first),
				sizeB = new FileInfo(second).Length, modifiedB = File.GetLastWriteTimeUtc(second), same = true,
			},
		}));
		int colors = 0;
		var hashes = ReportBuilder.ContentHashes.Load();
		Assert.True(hashes.UpClose(a, b, () => throw new InvalidOperationException("looked at again")));
		Assert.True(hashes.SameColors(a, b, () => { colors++; return true; }));
		hashes.Save();
		var again = ReportBuilder.ContentHashes.Load();
		Assert.True(again.UpClose(a, b, () => false));
		Assert.True(again.SameColors(a, b, () => { colors++; return false; }));
		Assert.Equal(1, colors);
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

	const int BigW = 3000, BigH = 2000;

	/// <summary>
	/// A large photo with fine detail all over (a smooth scene under a light texture at five scales), RGB24 rows top-down;
	/// <paramref name="blur"/>: a box blur of that radius over the whole of it, as a "soften" filter does.
	/// </summary>
	static byte[] Detailed(int blur = 0) {
		var px = new byte[BigW * BigH * 3];
		for (int y = 0; y < BigH; y++)
			for (int x = 0; x < BigW; x++) {
				double u = x / (double)BigW, v = y / (double)BigH;
				double t = Noise(x, y, 8, 4) * 12 + Noise(x, y, 12, 5) * 9 + Noise(x, y, 24, 1) * 7 + Noise(x, y, 48, 2) * 5 + Noise(x, y, 96, 3) * 4;
				int o = (y * BigW + x) * 3;
				px[o] = (byte)Math.Clamp(110 + 70 * u + t, 0, 255);
				px[o + 1] = (byte)Math.Clamp(95 + 80 * v + t, 0, 255);
				px[o + 2] = (byte)Math.Clamp(120 + 40 * Math.Sin(4 * u + 2 * v) + t, 0, 255);
			}
		if (blur > 0) {
			BoxBlur(px, blur, horizontal: true);
			BoxBlur(px, blur, horizontal: false);
		}
		return px;

		// Value noise, -1 to 1: a fixed pseudo-random value every cell pixels, eased between.
		static double Noise(int x, int y, int cell, int seed) {
			int cx = x / cell, cy = y / cell;
			double fx = Ease((x % cell) / (double)cell), fy = Ease((y % cell) / (double)cell);
			double top = Lerp(At(cx, cy), At(cx + 1, cy), fx), bottom = Lerp(At(cx, cy + 1), At(cx + 1, cy + 1), fx);
			return Lerp(top, bottom, fy);
			double At(int i, int j) => (uint)(((i * 73856093) ^ (j * 19349663) ^ (seed * 83492791)) * 2654435761u) / (double)uint.MaxValue * 2 - 1;
			static double Ease(double f) => f * f * (3 - 2 * f);
			static double Lerp(double a, double b, double f) => a + (b - a) * f;
		}

		static void BoxBlur(byte[] px, int r, bool horizontal) {
			int lines = horizontal ? BigH : BigW, length = horizontal ? BigW : BigH;
			var line = new int[length];
			for (int l = 0; l < lines; l++)
				for (int c = 0; c < 3; c++) {
					int At(int k) => ((horizontal ? l * BigW + k : k * BigW + l) * 3) + c;
					for (int k = 0; k < length; k++) line[k] = px[At(k)];
					for (int k = 0; k < length; k++) {
						int sum = 0, n = 0;
						for (int d = Math.Max(0, k - r); d <= Math.Min(length - 1, k + r); d++) { sum += line[d]; n++; }
						px[At(k)] = (byte)(sum / n);
					}
				}
		}
	}

	string SaveBig(string name, byte[] rgb) {
		string path = Path.Combine(dir, name);
		File.WriteAllBytes(path, PictureCompareTests.Bmp(rgb, BigW, BigH));
		return path;
	}

	static double Kept(string a, string b) {
		CloseLook.Result? look = CloseLook.Look(a, b);
		Assert.NotNull(look);
		return CloseLook.DetailKept(look.Value.DetailA, look.Value.DetailB);
	}

	[Fact]
	public void AResizeOrARecompression_KeepsItsDetail() {
		string original = SaveBig("original.bmp", Detailed());
		foreach (int side in new[] { BigW, 1500, 1080, 600 }) {
			double kept = Kept(original, Jpeg(original, $"copy-{side}.jpg", side));
			Assert.True(kept >= ReportBuilder.SameDetailLimit, $"{side}: {kept}");
		}
	}

	[Fact]
	public void ABlurOverTheWholePhoto_PassesTheCloseLookButNotItsDetail() {
		// Under two pixels at the close look's 512: every block keeps its average, half the detail goes.
		string original = SaveBig("original.bmp", Detailed());
		string soft = Jpeg(SaveBig("soft.bmp", Detailed(blur: 10)), "soft.jpg", BigW);
		var j = Judge(original, soft);
		Assert.True(PlainCopy(j), $"gray frames and blocks can't see it: {j}");
		double kept = Kept(original, soft);
		Assert.True(kept < ReportBuilder.SameDetailLimit, $"{kept}");
		Assert.Equal(kept, Kept(soft, original)); // either way round: a sharper copy of a soft photo isn't a plain copy of it either
	}

	[Fact]
	public void Detail_IsTheStepsBetweenPixels_NotTheirBrightness() {
		var flat = new byte[64 * 64];
		Array.Fill(flat, (byte)128);
		Assert.Equal(0, CloseLook.Detail(flat, 64));
		Assert.Equal(1, CloseLook.DetailKept(0, 0)); // two flat pictures: as sharp as each other
		var stripes = new byte[64 * 64];
		for (int i = 0; i < stripes.Length; i++) stripes[i] = (byte)(i % 2 == 0 ? 100 : 140);
		byte[] brighter = stripes.Select(v => (byte)(v + 50)).ToArray();
		Assert.Equal(CloseLook.Detail(stripes, 64), CloseLook.Detail(brighter, 64));
		Assert.Equal(40, CloseLook.Detail(stripes, 64)); // a step of 40 to the right, none down
		Assert.Equal(0.5, CloseLook.DetailKept(20, 40));
		Assert.Throws<ArgumentException>(() => CloseLook.Detail(stripes, 32));
	}

	[Fact]
	public void TheCloseLooksDecode_TellsHowSharpEachIs() {
		// A pair looked at up close this review isn't decoded again for its sharpness; a close look kept from an earlier
		// review is, once. (The copy is blurred after the close look, to tell the two apart.)
		string a = SaveBig("a.bmp", Detailed()), b = Jpeg(a, "b.jpg", BigW);
		var scan = new ScanFingerprints(null, withAi: false);
		Assert.True(scan.SameUpClose(a, b));
		File.Copy(Jpeg(SaveBig("soft.bmp", Detailed(blur: 10)), "soft.jpg", BigW), b, overwrite: true);
		Assert.True(scan.SameDetail(a, b)); // from the close look's decode
		Assert.False(new ScanFingerprints(null, withAi: false).SameDetail(a, b)); // decoded now
	}

	[Fact]
	public void ACloseLookKeptBeforeSharpnessWasCompared_HasItComparedOnce() {
		// looks.json from before this check: the close look is kept, the sharpness is compared on the next scan, and kept.
		string a = Save("a.bmp", Scene()), b = Jpeg(a, "b.jpg", 600);
		var (first, second) = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? (a, b) : (b, a);
		Directory.CreateDirectory(Path.GetDirectoryName(ReportBuilder.ContentHashes.LooksPath)!);
		File.WriteAllText(ReportBuilder.ContentHashes.LooksPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> {
			[first + "\n" + second] = new {
				sizeA = new FileInfo(first).Length, modifiedA = File.GetLastWriteTimeUtc(first),
				sizeB = new FileInfo(second).Length, modifiedB = File.GetLastWriteTimeUtc(second), same = true, colors = true,
			},
		}));
		int details = 0;
		var hashes = ReportBuilder.ContentHashes.Load();
		Assert.True(hashes.UpClose(a, b, () => throw new InvalidOperationException("looked at again")));
		Assert.True(hashes.SameColors(a, b, () => throw new InvalidOperationException("colours compared again")));
		Assert.False(hashes.SameDetail(a, b, () => { details++; return false; }));
		Assert.False(hashes.SameDetail(b, a, () => { details++; return true; }));
		hashes.Save();
		var again = ReportBuilder.ContentHashes.Load();
		Assert.True(again.UpClose(a, b, () => true));
		Assert.False(again.SameDetail(a, b, () => { details++; return true; }));
		Assert.True(again.SameColors(a, b, () => false)); // each answer is kept beside the other
		Assert.Equal(1, details);

		File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(b).AddMinutes(5));
		var changed = ReportBuilder.ContentHashes.Load();
		Assert.False(changed.SameDetail(a, b, () => true)); // not looked at up close yet: no plain copy, nothing compared
		Assert.True(changed.UpClose(a, b, () => true));
		Assert.True(changed.SameDetail(a, b, () => { details++; return true; }));
		Assert.Equal(2, details);
	}
}
