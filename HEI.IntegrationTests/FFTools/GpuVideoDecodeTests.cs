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
using HEI.Core.FFTools;
using HEI.Core.FFTools.FFmpegNative;
using HEI.IntegrationTests.Fixtures;

namespace HEI.IntegrationTests.FFTools;

/// <summary>
/// The GPU's video decoder (<see cref="HardwareVideoDecode"/>) hands a scan the very frames the CPU does,
/// so a video decoded on either compares the same. A full-range video (as iPhones record, yuvj420p) is the
/// one that matters: the GPU's NV12 says full range only in a tag, which the frame converter must heed.
///
/// The lean FFmpeg Heiward ships has no H.264 or HEVC encoder that works everywhere, so no sample can be
/// generated; point HEI_TEST_VIDEO at a phone video (H.264 or HEVC) to run this on a PC with a GPU.
/// </summary>
[Collection("Ffmpeg")]
public class GpuVideoDecodeTests {
	readonly FfmpegFixture _fixture;

	public GpuVideoDecodeTests(FfmpegFixture fixture) => _fixture = fixture;

	[SkippableFact]
	public void TheGpusFrames_AreTheCpus() {
		Skip.If(!OperatingSystem.IsWindows(), "GPU decoding is Windows only (D3D11VA)");
		Skip.If(!_fixture.NativeBindingAvailable || !_fixture.FfmpegCliAvailable, _fixture.FfmpegNotFoundReason ?? "FFmpeg native libraries not available");
		string? path = Environment.GetEnvironmentVariable("HEI_TEST_VIDEO");
		Skip.If(string.IsNullOrWhiteSpace(path) || !File.Exists(path), "set HEI_TEST_VIDEO to a phone video (H.264 or HEVC) to run");
		MediaInfo info = FFProbeEngine.GetMediaInfo(path!, false) ?? throw new InvalidOperationException("ffprobe could not read " + path);

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.UseNativeBinding = true;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;
		try {
			byte[] onCpu = Gray(HardwareVideoDecode.LaneMode.Off);
			byte[] onGpu = Gray(HardwareVideoDecode.LaneMode.Always);
			Skip.If(HardwareVideoDecode.VideosOnGpu == 0, "the GPU did not take this video (no D3D11 video decoder for its codec on this PC)");
			Assert.Equal(onCpu, onGpu);
		}
		finally {
			HardwareVideoDecode.ResetForScan();
		}

		byte[] Gray(HardwareVideoDecode.LaneMode mode) {
			HardwareVideoDecode.ResetForScan();
			HardwareVideoDecode.Mode = mode;
			var entry = new FileEntry(path!) { mediaInfo = info, grayBytes = new(), PHashes = new() };
			Assert.True(FfmpegEngine.GetGrayBytesFromVideo(entry, ScanEngine.BuildSamplePositions(1), 0, false));
			return Assert.Single(entry.grayBytes.Values)!;
		}
	}
}
