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

namespace HEI.Core.Tests.Utils;

/// <summary>
/// The aligned look on drawn scenes: a crop, a turn and a filter of one scene line up with it; the same scene with one
/// thing in it moved (an arm, a boat) doesn't, nor does the scene seen from a step aside (near things shift against far ones).
/// </summary>
public class AlignedLookTests {
	/// <summary>A scene of soft blobs over two layers, a far one and a near one: <paramref name="near"/> shifts the near layer, <paramref name="moved"/> moves one blob.</summary>
	static double Scene(double x, double y, double near = 0, (double X, double Y)? moved = null) {
		double v = 110 + 40 * Math.Sin(3.1 * x + 1.3) * Math.Cos(2.3 * y) + 25 * Math.Sin(7.7 * x - 5.1 * y);
		var rng = new Random(7);
		for (int k = 0; k < 40; k++) {
			double bx = rng.NextDouble() * 1.5, by = rng.NextDouble(), r = 0.03 + 0.06 * rng.NextDouble(), a = rng.NextDouble() * 160 - 80;
			bool isNear = k % 2 == 0;
			if (isNear) bx += near;
			double d2 = ((x - bx) * (x - bx) + (y - by) * (y - by)) / (r * r);
			v += a * Math.Exp(-d2);
		}
		// An arm: in every scene, where it is in the one it moved in.
		var (ax, ay) = (0.8 + (moved?.X ?? 0), 0.45 + (moved?.Y ?? 0));
		v -= 90 * Math.Exp(-((x - ax) * (x - ax) / 0.0016 + (y - ay) * (y - ay) / 0.01));
		return v;
	}

	/// <summary>The scene drawn at <paramref name="w"/>×<paramref name="h"/> pixels from the window (x, y, width) of the 1.5×1 scene, turned by <paramref name="degrees"/> about its middle.</summary>
	static AlignedLook.Picture Draw(int w, int h, double x0, double y0, double width, double degrees = 0, Func<double, double, double>? scene = null, Func<double, double>? tone = null) {
		scene ??= (x, y) => Scene(x, y);
		double height = width * h / w, cx = x0 + width / 2, cy = y0 + height / 2, rad = degrees * Math.PI / 180;
		var pixels = new float[w * h];
		for (int v = 0; v < h; v++)
			for (int u = 0; u < w; u++) {
				double px = x0 + (u + 0.5) / w * width - cx, py = y0 + (v + 0.5) / h * height - cy;
				double x = cx + Math.Cos(rad) * px - Math.Sin(rad) * py, y = cy + Math.Sin(rad) * px + Math.Cos(rad) * py;
				double value = scene(x, y);
				pixels[v * w + u] = (float)Math.Clamp(tone?.Invoke(value) ?? value, 0, 255);
			}
		return new AlignedLook.Picture(pixels, w, h);
	}

	static readonly AlignedLook.Picture Original = Draw(384, 256, 0, 0, 1.5);

	[Fact]
	public void ACropTowardsACorner_LinesUp() {
		var crop = Draw(273, 182, 0.43, 0.29, 1.065); // 71% of each side, towards the bottom right
		Assert.True(AlignedLook.SamePicture(Original, crop));
		Assert.InRange(AlignedLook.Compare(Original, crop).Scale, 0.69, 0.73);
	}

	[Fact]
	public void ATurnedCopy_CutToItsUprightRectangle_LinesUp() {
		var turned = Draw(340, 227, 0.085, 0.057, 1.33, degrees: 5);
		Assert.True(AlignedLook.SamePicture(Original, turned));
		Assert.InRange(Math.Abs(AlignedLook.Compare(Original, turned).Angle), 4.5, 5.5);
	}

	[Fact]
	public void ABrighterContrastierCopy_LinesUp() =>
		Assert.True(AlignedLook.SamePicture(Original, Draw(384, 256, 0, 0, 1.5, tone: v => 1.25 * v + 15)));

	[Fact]
	public void TheKeptOneCanBeTheCrop() {
		var crop = Draw(384, 256, 0.2, 0.1, 1.1);
		Assert.True(AlignedLook.SamePicture(crop, Original));
		Assert.True(AlignedLook.Compare(crop, Original).Reversed);
	}

	[Fact]
	public void TheSceneWithOneThingMoved_DoesntLineUp() =>
		Assert.False(AlignedLook.SamePicture(Original, Draw(384, 256, 0, 0, 1.5, scene: (x, y) => Scene(x, y, moved: (0.12, 0.05)))));

	[Fact]
	public void TheSceneFromAStepAside_DoesntLineUp() =>
		Assert.False(AlignedLook.SamePicture(Original, Draw(384, 256, 0.02, 0, 1.5, scene: (x, y) => Scene(x, y, near: 0.06))));

	[Fact]
	public void AnEvenPicture_CantBeTold() {
		var fog = new AlignedLook.Picture(Enumerable.Repeat(128f, 384 * 256).ToArray(), 384, 256);
		Assert.Null(AlignedLook.SamePicture(Original, fog));
	}
}
