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

using VDF.Core.FFTools;
using VDF.IntegrationTests.Fixtures;

namespace VDF.IntegrationTests.FFTools;

/// <summary>
/// Tiled (Apple-style) HEIC coverage for issue #869: the photo only exists as a tile-grid
/// stream group, assembled by FFmpeg through an internal complex filtergraph. On FFmpeg
/// 8.1+ a plain -vf against that stream is rejected ("Simple and complex filtering cannot
/// be used together"), which broke every iPhone photo; and [0:v] / av_find_best_stream
/// address a single tile or an aux depth/gain-map stream, never the picture. The native
/// binding assembles the grid itself (HeifTileGridDecoder).
///
/// FFmpeg cannot WRITE tiled HEIF, so no fixture can be generated or checked in (a real
/// iPhone photo is personal data). These tests run against a real tiled HEIC supplied via
/// the VDF_TEST_TILED_HEIC environment variable and skip when it is unset.
/// </summary>
[Collection("Ffmpeg")]
public class TiledHeicTests {
	readonly FfmpegFixture _fixture;

	public TiledHeicTests(FfmpegFixture fixture) => _fixture = fixture;

	static string? TiledHeicPath {
		get {
			string? path = Environment.GetEnvironmentVariable("VDF_TEST_TILED_HEIC");
			return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
		}
	}

	const string SkipReason = "set VDF_TEST_TILED_HEIC to a tiled (iPhone) HEIC to run";

	[SkippableFact]
	public void GrayBytes_TiledHeic_ProcessMode_Succeeds() {
		Skip.If(!_fixture.FfmpegCliAvailable, _fixture.FfmpegNotFoundReason);
		Skip.If(TiledHeicPath == null, SkipReason);

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.UseNativeBinding = false;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;

		var gray = FfmpegEngine.GetThumbnail(new FfmpegSettings {
			File = TiledHeicPath!,
			Position = TimeSpan.Zero,
			GrayScale = 1,
			SoftwareDecodeOnly = true,
		}, extendedLogging: true);

		Assert.NotNull(gray);
		Assert.Equal(32 * 32, gray!.Length);
	}

	[SkippableFact]
	public void CombinedGrayAndRgb_TiledHeic_Succeeds() {
		Skip.If(!_fixture.FfmpegCliAvailable, _fixture.FfmpegNotFoundReason);
		Skip.If(TiledHeicPath == null, SkipReason);

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.UseNativeBinding = false;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;

		(byte[]? gray, byte[]? rgb) = FfmpegEngine.GetGrayAndRgb224Cli(
			TiledHeicPath!, TimeSpan.Zero, softwareDecodeOnly: true, extendedLogging: true);

		Assert.NotNull(gray);
		Assert.Equal(32 * 32, gray!.Length);
		Assert.NotNull(rgb);
		Assert.Equal(VDF.Core.AI.OnnxEmbedder.InputSide * VDF.Core.AI.OnnxEmbedder.InputSide * 3, rgb!.Length);
	}

	[SkippableFact]
	public void DisplayThumbnail_TiledHeic_ReturnsValidJpeg() {
		Skip.If(!_fixture.FfmpegCliAvailable, _fixture.FfmpegNotFoundReason);
		Skip.If(TiledHeicPath == null, SkipReason);

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.UseNativeBinding = false;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;

		var jpeg = FfmpegEngine.ExtractThumbnailJpeg(TiledHeicPath!, TimeSpan.Zero);

		Assert.NotNull(jpeg);
		Assert.True(jpeg!.Length > 2);
		Assert.Equal(0xFF, jpeg[0]); // JPEG SOI marker
		Assert.Equal(0xD8, jpeg[1]);
	}

	[SkippableFact]
	public void NativeBinding_TiledHeic_AssemblesTheGridInProcess_AndMatchesTheProcessPath() {
		Skip.If(!_fixture.FfmpegCliAvailable, _fixture.FfmpegNotFoundReason);
		Skip.If(!_fixture.NativeBindingAvailable, "FFmpeg native libraries not available");
		Skip.If(TiledHeicPath == null, SkipReason);

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;

		FfmpegEngine.UseNativeBinding = false;
		(byte[]? cliGray, byte[]? cliRgb) = FfmpegEngine.GetGrayAndRgb224Cli(
			TiledHeicPath!, TimeSpan.Zero, softwareDecodeOnly: true, extendedLogging: true);
		Assert.NotNull(cliGray);
		Assert.NotNull(cliRgb);

		FfmpegEngine.UseNativeBinding = true;
		bool ok = FfmpegEngine.TryGetImageInfoAndGrayBytes(TiledHeicPath!,
			out byte[]? gray, out int width, out int height, extendedLogging: true);
		Assert.True(ok);
		Assert.Equal(32 * 32, gray!.Length);
		// The photo's size, not a 512x512 tile's (#869).
		Assert.True(width > 512 && height > 512, $"{width}x{height}");

		// The AI frame backfill for cached photos (GetThumbnail) decodes the grid in-process too.
		var rgb = FfmpegEngine.GetThumbnail(new FfmpegSettings {
			File = TiledHeicPath!,
			Position = TimeSpan.Zero,
			Rgb224 = true,
			SoftwareDecodeOnly = true,
		}, extendedLogging: true);
		Assert.NotNull(rgb);
		Assert.Equal(cliRgb!.Length, rgb!.Length);

		// The same picture as the command line's grid assembly: the AI frames and the gray bytes
		// derived from them (as every photo is hashed) must agree, or a photo hashed one way would
		// stop matching itself hashed the other.
		double rgbDifference = rgb.Zip(cliRgb, (a, b) => Math.Abs(a - b)).Average();
		Assert.True(rgbDifference < 1, $"AI frame mean difference {rgbDifference:F2}");
		double similarity = (1 - VDF.Core.Utils.GrayBytesUtils.PercentageDifference(gray, VDF.Core.Utils.GrayBytesUtils.FromRgb224(cliRgb))) * 100;
		Assert.True(similarity > 99.5, $"gray similarity {similarity:F2}%");
	}

	[SkippableFact]
	public void NativeBinding_TiledHeic_GrayAndAiFrameWithoutAPriorDecode() {
		Skip.If(!_fixture.NativeBindingAvailable, "FFmpeg native libraries not available");
		Skip.If(TiledHeicPath == null, SkipReason);

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.UseNativeBinding = true;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;

		// GetThumbnail on its own (cached gray bytes, AI frame backfill) decodes the grid itself.
		var gray = FfmpegEngine.GetThumbnail(new FfmpegSettings {
			File = TiledHeicPath!, Position = TimeSpan.Zero, GrayScale = 1, SoftwareDecodeOnly = true,
		}, extendedLogging: true);
		var rgb = FfmpegEngine.GetThumbnail(new FfmpegSettings {
			File = TiledHeicPath!, Position = TimeSpan.Zero, Rgb224 = true, SoftwareDecodeOnly = true,
		}, extendedLogging: true);
		Assert.Equal(32 * 32, gray!.Length);
		Assert.Equal(VDF.Core.AI.OnnxEmbedder.InputSide * VDF.Core.AI.OnnxEmbedder.InputSide * 3, rgb!.Length);
		Assert.True(VDF.Core.Utils.GrayBytesUtils.VerifyGrayScaleValues(gray));
	}

	[SkippableFact]
	public void NativeBinding_TiledHeic_Rgb224Path_HashesLikeTheProcessPath() {
		Skip.If(!_fixture.FfmpegCliAvailable, _fixture.FfmpegNotFoundReason);
		Skip.If(!_fixture.NativeBindingAvailable, "FFmpeg native libraries not available");
		Skip.If(TiledHeicPath == null, SkipReason);

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;

		FfmpegEngine.UseNativeBinding = false;
		(_, byte[]? cliRgb) = FfmpegEngine.GetGrayAndRgb224Cli(TiledHeicPath!, TimeSpan.Zero, softwareDecodeOnly: true, extendedLogging: true);
		Assert.NotNull(cliRgb);

		// The scan's path: one in-process decode gives the AI frame and the photo's size; the gray
		// frame is derived from that AI frame, as for every photo.
		FfmpegEngine.UseNativeBinding = true;
		Assert.True(FfmpegEngine.TryGetImageInfoAndRgb224(TiledHeicPath!, out byte[]? rgb, out int width, out int height, extendedLogging: true));
		Assert.True(width > 512 && height > 512, $"{width}x{height}");
		Assert.Equal(cliRgb!.Length, rgb!.Length);
		double similarity = (1 - VDF.Core.Utils.GrayBytesUtils.PercentageDifference(
			VDF.Core.Utils.GrayBytesUtils.FromRgb224(rgb), VDF.Core.Utils.GrayBytesUtils.FromRgb224(cliRgb))) * 100;
		Assert.True(similarity > 99.7, $"gray similarity {similarity:F2}%");
	}
}
