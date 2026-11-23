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

using System.Diagnostics;
using HEI.Core;
using HEI.Core.FFTools;
using HEI.Core.FFTools.FFmpegNative;
using HEI.Core.Utils;
using HEI.IntegrationTests.Fixtures;
using Xunit.Abstractions;

namespace HEI.IntegrationTests.FFTools;

/// <summary>
/// A video whose keyframes are too far apart to reach a sampled position (one keyframe for the whole of it): sampling it
/// costs at most what <see cref="ForwardBudget"/> allows, once, and it's left out, as a video that can't be decoded is.
/// On a scan of another PC one such 4K video took 21 minutes: 10,000 frames decoded on the graphics card, then 10,000 more
/// on the processor, and none sampled.
/// </summary>
[Collection("Ffmpeg")]
public class KeyframesTooFarTests(FfmpegFixture fixture, ITestOutputHelper output) {
	static FileEntry EntryFor(string path) {
		var entry = new FileEntry(path) { mediaInfo = FFProbeEngine.GetMediaInfo(path, extendedLogging: false) };
		Assert.NotNull(entry.mediaInfo);
		return entry;
	}

	sealed class LogCollector : IDisposable {
		readonly List<string> messages = new();
		public LogCollector() => Logger.Instance.LogEntryAdded += OnEntry;
		void OnEntry(LogEntry entry) { lock (messages) messages.Add(entry.Message); }
		public bool Any(string part) {
			lock (messages) return messages.Any(m => m.Contains(part, StringComparison.Ordinal));
		}
		public void Dispose() => Logger.Instance.LogEntryAdded -= OnEntry;
	}

	[SkippableFact]
	public void APositionPastTheBudget_IsntDecodedTo_AndOneWithinIt_Is() {
		Skip.If(!fixture.NativeBindingAvailable, "FFmpeg native libraries not available");
		Skip.If(fixture.H264_OneKeyframe == null, "one-keyframe test video not generated");

		// Its one keyframe at 0 s, 25 frames a second: 3.6 s is 90 frames on from it.
		using (var vsd = new VideoStreamDecoder(fixture.H264_OneKeyframe!) { Budget = new ForwardBudget(maxFrames: 20) }) {
			var e = Assert.Throws<KeyframesTooFarException>(() => vsd.TryDecodeFrame(out _, TimeSpan.FromSeconds(3.6)));
			Assert.Contains("keyframes are too far apart", e.Message);
			// The budget is a position's: one close to the keyframe still decodes with the same decoder.
			Assert.True(vsd.TryDecodeFrame(out _, TimeSpan.FromSeconds(0.4)));
		}
		using (var vsd = new VideoStreamDecoder(fixture.H264_OneKeyframe!))
			Assert.True(vsd.TryDecodeFrame(out _, TimeSpan.FromSeconds(3.6)), "the default budget reaches 90 frames on");
	}

	[SkippableFact]
	public void AVideoPastTheBudget_IsLeftOutOnce_NotTriedAgainOnTheProcessorOrByTheFFmpegProcess() {
		Skip.If(!fixture.FfmpegCliAvailable, fixture.FfmpegNotFoundReason);
		Skip.If(!fixture.NativeBindingAvailable, "FFmpeg native libraries not available");
		Skip.If(fixture.H264_OneKeyframe == null, "one-keyframe test video not generated");

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.UseNativeBinding = true;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;
		Func<ForwardBudget> budget = FfmpegEngine.NewForwardBudget;
		int budgets = 0;
		var leftOut = new List<(string Path, string Why)>();
		FfmpegEngine.NewForwardBudget = () => { budgets++; return new ForwardBudget(maxFrames: 20); };
		FfmpegEngine.VideoLeftOut = (path, why) => leftOut.Add((path, why));
		try {
			var entry = EntryFor(fixture.H264_OneKeyframe!);
			using var log = new LogCollector();
			Assert.False(FfmpegEngine.GetGrayBytesFromVideo(entry, [0.9f], 0, extendedLogging: false));

			Assert.True(entry.Flags.Has(EntryFlags.ThumbnailError)); // not tried again while it's unchanged
			Assert.Equal(1, budgets); // one decoder: no second try on the processor
			var (path, why) = Assert.Single(leftOut);
			Assert.Equal(fixture.H264_OneKeyframe, path);
			Assert.Contains("keyframes are too far apart", why);
			Assert.True(log.Any("Left '"), "the log names the video left out");
			// The FFmpeg process's failure would say this: it wasn't asked.
			Assert.False(log.Any("Failed to retrieve graybytes"), "the FFmpeg process was tried after the budget ran out");
		}
		finally {
			FfmpegEngine.NewForwardBudget = budget;
			FfmpegEngine.VideoLeftOut = null;
		}
	}

	/// <summary>
	/// A long video with one keyframe, timed: <c>HEI_FAR_KEYFRAMES_VIDEO</c> names it (none: nothing runs). Unbounded, as before
	/// the budget, then as it is now.
	/// </summary>
	[SkippableFact]
	public void Probe_ALongVideoWithOneKeyframe_IsBounded() {
		string? video = Environment.GetEnvironmentVariable("HEI_FAR_KEYFRAMES_VIDEO");
		Skip.If(string.IsNullOrEmpty(video), "HEI_FAR_KEYFRAMES_VIDEO isn't set");
		Skip.If(!fixture.NativeBindingAvailable, "FFmpeg native libraries not available");

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.UseNativeBinding = true;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		Func<ForwardBudget> budget = FfmpegEngine.NewForwardBudget;
		try {
			foreach (bool bounded in new[] { false, true }) {
				FfmpegEngine.NewForwardBudget = bounded ? budget : () => new ForwardBudget(int.MaxValue, TimeSpan.FromDays(1));
				var entry = EntryFor(video!);
				var timer = Stopwatch.StartNew();
				bool sampled = FfmpegEngine.GetGrayBytesFromVideo(entry, [0.5f], 0, extendedLogging: false);
				output.WriteLine($"{(bounded ? "bounded" : "unbounded")}: {timer.Elapsed.TotalSeconds:N1} s, sampled {sampled}, " +
					$"left out {entry.Flags.Has(EntryFlags.ThumbnailError)}");
			}
		}
		finally { FfmpegEngine.NewForwardBudget = budget; }
	}
}
