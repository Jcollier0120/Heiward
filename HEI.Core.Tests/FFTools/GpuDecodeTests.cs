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

using FFmpeg.AutoGen;
using HEI.Core.FFTools.FFmpegNative;

namespace HEI.Core.Tests.FFTools;

/// <summary>The GPU's video decoder as a lane beside the CPU: how many videos it takes, and what turns it off.</summary>
[Collection("DatabaseUtils")] // HardwareVideoDecode's crash verdict is static
public class GpuDecodeTests : IDisposable {
	readonly string folder = Path.Combine(Path.GetTempPath(), "hei-gpu-" + Guid.NewGuid().ToString("N"));

	public GpuDecodeTests() => Directory.CreateDirectory(folder);

	public void Dispose() {
		HardwareVideoDecode.TurnOffAfterCrash(folder + "-none", crashedOn: null); // no marker: back on for the next test
		try { Directory.Delete(folder, true); } catch { }
	}

	const long GB = 1L << 30;

	[Theory]
	// In the background with more memory: one video per 4 GB, 2 to 8.
	[InlineData(false, true, 8, 2)]
	[InlineData(false, true, 16, 4)]
	[InlineData(false, true, 48, 8)]
	[InlineData(false, true, 128, 8)]
	// At full speed the decoder saturates: no more than 4.
	[InlineData(true, true, 48, 4)]
	[InlineData(true, true, 8, 2)]
	// Less memory (the setting off, or a game running): 2 whatever the RAM.
	[InlineData(false, false, 48, 2)]
	[InlineData(true, false, 48, 2)]
	public void VideosOnTheGpu_FollowThePaceAndTheMemorySetting(bool fullSpeed, bool moreMemory, int ramGb, int slots) =>
		Assert.Equal(slots, HardwareVideoDecode.SlotsFor(fullSpeed, moreMemory, ramGb * GB));

	[Fact]
	public void ADriverCrash_TurnsGpuDecodingOff_UntilTheMarkerIsDeleted() {
		HardwareVideoDecode.TurnOffAfterCrash(folder, crashedOn: null);
		Assert.False(HardwareVideoDecode.OffAfterCrash);

		HardwareVideoDecode.TurnOffAfterCrash(folder, crashedOn: @"C:\videos\a.mov");
		Assert.True(HardwareVideoDecode.OffAfterCrash);
		string marker = Path.Combine(folder, HardwareVideoDecode.CrashMarkerName);
		Assert.Contains(@"C:\videos\a.mov", File.ReadAllText(marker));

		// The next scans find the marker and stay off.
		HardwareVideoDecode.TurnOffAfterCrash(folder, crashedOn: null);
		Assert.True(HardwareVideoDecode.OffAfterCrash);

		File.Delete(marker);
		HardwareVideoDecode.TurnOffAfterCrash(folder, crashedOn: null);
		Assert.False(HardwareVideoDecode.OffAfterCrash);
	}

	[Fact]
	public void AtFullSpeed_AGpuSlowerThanTheCores_GivesItsSlotsBack_AndKeepsMeasuring() {
		bool pace = HEI.Core.Utils.Pace.FullSpeed;
		HEI.Core.Utils.Pace.FullSpeed = true;
		try {
			var tuner = new LaneTuner("videos", max: 4);
			Assert.Equal(4, tuner.Limit);
			// The GPU takes a second a video, a CPU worker half that: one slot back per decision.
			for (int i = 0; i < 200 && tuner.Limit > 0; i++) {
				tuner.Record(onGpu: true, 1.0);
				tuner.Record(onGpu: false, 0.5);
			}
			Assert.Equal(0, tuner.Limit);
			// One video in 40 still goes, so a change (the game closed) is noticed.
			Assert.Equal(1, Enumerable.Range(0, 40).Count(_ => tuner.Probe()));
			// Quicker than the cores again: the slots come back.
			for (int i = 0; i < 200 && tuner.Limit < 4; i++) {
				tuner.Record(onGpu: true, 0.2);
				tuner.Record(onGpu: false, 0.5);
			}
			Assert.Equal(4, tuner.Limit);
			Assert.Contains("s a file on the GPU", tuner.Describe());
		}
		finally {
			HEI.Core.Utils.Pace.FullSpeed = pace;
		}
	}

	[Fact]
	public void InTheBackground_TheSlotsStay_WhateverTheTimes() {
		bool pace = HEI.Core.Utils.Pace.FullSpeed;
		HEI.Core.Utils.Pace.FullSpeed = false;
		try {
			var tuner = new LaneTuner("photos", max: 1);
			for (int i = 0; i < 100; i++) {
				tuner.Record(onGpu: true, 1.0);
				tuner.Record(onGpu: false, 0.1);
			}
			Assert.Equal(1, tuner.Limit);
		}
		finally {
			HEI.Core.Utils.Pace.FullSpeed = pace;
		}
	}

	[Theory]
	// The GPU's decoder hands over NV12 tagged full range: the converter must be told.
	[InlineData(AVPixelFormat.AV_PIX_FMT_NV12, AVColorRange.AVCOL_RANGE_JPEG, true)]
	[InlineData(AVPixelFormat.AV_PIX_FMT_P010LE, AVColorRange.AVCOL_RANGE_JPEG, true)]
	[InlineData(AVPixelFormat.AV_PIX_FMT_YUV420P, AVColorRange.AVCOL_RANGE_JPEG, true)]
	// yuvj formats say it themselves; TV range is swscale's default.
	[InlineData(AVPixelFormat.AV_PIX_FMT_YUVJ420P, AVColorRange.AVCOL_RANGE_JPEG, false)]
	[InlineData(AVPixelFormat.AV_PIX_FMT_NV12, AVColorRange.AVCOL_RANGE_MPEG, false)]
	[InlineData(AVPixelFormat.AV_PIX_FMT_NV12, AVColorRange.AVCOL_RANGE_UNSPECIFIED, false)]
	public void FullRangeFrames_InFormatsThatDontSaySo_AreFlagged(AVPixelFormat format, AVColorRange range, bool needed) {
		var frame = new AVFrame { format = (int)format, color_range = range };
		Assert.Equal(needed, VideoFrameConverter.NeedsFullRange(frame, format));
	}
}
