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

using VDF.Core.FFTools.FFmpegNative;

namespace VDF.Core.Tests.FFTools;

/// <summary>
/// The canvas arithmetic of the in-process tiled HEIF decoder. Decoding itself needs a real
/// tiled HEIF, which FFmpeg cannot write; VDF.IntegrationTests covers it via VDF_TEST_TILED_HEIC.
/// </summary>
public class HeifTileGridDecoderTests {
	static (int X, int Y)[] Grid(int columns, int rows, int tileWidth, int tileHeight) =>
		Enumerable.Range(0, columns * rows).Select(i => (i % columns * tileWidth, i / columns * tileHeight)).ToArray();

	[Theory]
	[InlineData(4032, 2268, 4)] // 12 MP iPhone, 16:9: 2268/8 would leave 283 rows, under 448
	[InlineData(2268, 4032, 4)] // the same upright
	[InlineData(5712, 3213, 4)] // 24 MP iPhone
	[InlineData(4032, 3024, 4)] // 12 MP 4:3
	[InlineData(8064, 6048, 8)] // 48 MP: room for the largest factor
	[InlineData(1737, 3088, 2)] // a cropped photo
	[InlineData(800, 600, 1)]   // small enough to keep every pixel
	public void CanvasFactor_KeepsTheShorterSideAtTwiceTheAiInput(int width, int height, int expected) {
		var offsets = Grid((width + 511) / 512, (height + 511) / 512, 512, 512);

		int factor = HeifTileGridDecoder.CanvasFactor(width, height, 512, 512, offsets);

		Assert.Equal(expected, factor);
		Assert.True(factor == 1 || Math.Min(width, height) / factor >= HeifTileGridDecoder.MinCanvasSide);
	}

	[Fact]
	public void CanvasFactor_OnlyPicksFactorsThatDivideTilesIntoWholeChromaSamples() {
		// 500-pixel tiles: 500 is a multiple of 4 but not of 8 or 16, so only factor 2 lands
		// every tile on even canvas coordinates.
		Assert.Equal(2, HeifTileGridDecoder.CanvasFactor(4000, 3000, 500, 500, Grid(8, 6, 500, 500)));
	}

	[Fact]
	public void CanvasFactor_FallsBackToOneForMisalignedOffsets() {
		(int X, int Y)[] offsets = { (0, 0), (510, 0) };
		Assert.Equal(1, HeifTileGridDecoder.CanvasFactor(4000, 3000, 512, 512, offsets));
	}

	[Fact]
	public void CropWindow_ScalesThePicturesWindowOntoTheCanvas() {
		// 8 x 5 tiles of 512 = 4096 x 2560 coded, 4032 x 2268 shown, canvas at a quarter.
		Assert.Equal((0, 0, 1008, 567), HeifTileGridDecoder.CropWindow(0, 0, 4032, 2268, 4, 1024, 640));
		Assert.Equal((0, 0, 4032, 2268), HeifTileGridDecoder.CropWindow(0, 0, 4032, 2268, 1, 4096, 2560));
	}

	[Fact]
	public void CropWindow_RoundsOddOriginsDownToEvenAndStaysOnTheCanvas() {
		Assert.Equal((2, 0, 1022, 320), HeifTileGridDecoder.CropWindow(12, 3, 4096, 1280, 4, 1024, 640));
		Assert.Equal((0, 0, 1024, 640), HeifTileGridDecoder.CropWindow(-8, -8, 9999, 9999, 4, 1024, 640));
	}
}
