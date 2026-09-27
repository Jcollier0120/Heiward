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
/// The crop arithmetic of the in-process tiled HEIF decoder. Decoding itself needs a real
/// tiled HEIF, which FFmpeg cannot write; VDF.IntegrationTests covers it via VDF_TEST_TILED_HEIC.
/// </summary>
public class HeifTileGridDecoderTests {
	[Fact]
	public void CropWindow_IsThePicturesWindowOnTheCodedGrid() {
		// iPhone 12 MP, 16:9: 8 x 5 tiles of 512 = 4096 x 2560 coded, 4032 x 2268 shown.
		Assert.Equal((0, 0, 4032, 2268), HeifTileGridDecoder.CropWindow(0, 0, 4032, 2268, 4096, 2560));
	}

	[Fact]
	public void CropWindow_RoundsOddOriginsDownToEvenForTheChromaPlanes() {
		Assert.Equal((12, 2, 1000, 800), HeifTileGridDecoder.CropWindow(13, 3, 1000, 800, 4096, 2560));
	}

	[Fact]
	public void CropWindow_NeverRunsOffTheCanvas() {
		Assert.Equal((4000, 2500, 96, 60), HeifTileGridDecoder.CropWindow(4000, 2500, 500, 500, 4096, 2560));
		Assert.Equal((0, 0, 4096, 2560), HeifTileGridDecoder.CropWindow(-8, -8, 9999, 9999, 4096, 2560));
	}
}
