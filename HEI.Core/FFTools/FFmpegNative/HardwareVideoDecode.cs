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
using HEI.Core.Utils;

namespace HEI.Core.FFTools.FFmpegNative {
	/// <summary>
	/// The GPU's video decoder for videos. Reaching a sampled frame means decoding from the keyframe
	/// before it, often dozens of frames of 4K HEVC: on the GPU's fixed-function decoder that costs the
	/// CPU a fraction of decoding them in software, and runs beside the CPU's own decoders.
	///
	/// One device for the whole process, shared by every decoder (creating one costs about as much as
	/// decoding a video); a fixed number of videos on it at once (<see cref="Slots"/>), the rest on the
	/// CPU; and only codecs the decoder takes (H.264, HEVC, VP9, AV1, ...), never pictures. A video the
	/// GPU fails on is decoded again on the CPU; failures in a row turn the lane off for the scan.
	/// A driver crash takes the process with it, so a video on the GPU leaves a crash breadcrumb of its
	/// own (<see cref="ScanCrashJournal.PhaseGpuDecode"/>): the next scan then turns GPU decoding off on
	/// this PC, for videos and iPhone photos (<see cref="HeifHardwareLane"/>), instead of quarantining the file
	/// (<see cref="TurnOffAfterCrash"/>).
	///
	/// Measured on a Snapdragon X2 (Adreno X2-90, 48 GB) with 290 phone videos, mostly 4K HEVC, 9 workers:
	/// <list type="bullet">
	/// <item>CPU per video: 15-25 ms on the GPU against 230-500 ms in software; the frames are byte-identical.</item>
	/// <item>The decoder tops out near 12 videos a second, so at full speed more than 4 slots only queue
	/// on it: 17 workers took 12.7 s on the CPU alone, 10.0 s with 4 slots, 11.5 s with 8.</item>
	/// <item>Under a background scan's cap (2 cores' worth) the CPU is what's short: 56 s on the CPU alone,
	/// 32 s with 4 slots, 22 s with 8 (and 25 s of CPU instead of 122). Each slot holds up to ~0.5 GB of
	/// decoder surfaces for 4K 10-bit HEVC, in the RAM a GPU like this one shares: one slot per 4 GB when
	/// the user lets scans use more memory (<see cref="Pace.MoreMemory"/>), else 2.</item>
	/// </list>
	///
	/// HEI_VIDEO_HWDECODE: 0 or off, always (every video, for measurements), d3d12va (that device instead
	/// of D3D11VA). HEI_VIDEO_HWDECODE_SLOTS sets <see cref="Slots"/>.
	/// </summary>
	static unsafe class HardwareVideoDecode {
		internal enum LaneMode { Auto, Off, Always }

		static readonly LaneMode Configured = Environment.GetEnvironmentVariable("HEI_VIDEO_HWDECODE") switch {
			"0" or "off" => LaneMode.Off,
			"always" => LaneMode.Always,
			_ => LaneMode.Auto
		};
		internal static LaneMode Mode = Configured;

		/// <summary>D3D11VA: the most widely supported decoding path on Windows GPUs.</summary>
		static readonly AVHWDeviceType DeviceType = Environment.GetEnvironmentVariable("HEI_VIDEO_HWDECODE") == "d3d12va"
			? AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA : AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;

		/// <summary>
		/// Videos on the GPU at once, at the scan's pace and memory setting now (<see cref="Pace"/>); at full
		/// speed no more than this PC's GPU keeps up with (<see cref="Tuner"/>).
		/// </summary>
		internal static int Slots => SlotsOverride ?? (Pace.FullSpeed
			? Math.Min(SlotsFor(true, Pace.MoreMemory, Memory), Tuner.Limit)
			: SlotsFor(false, Pace.MoreMemory, Memory));

		/// <summary>How many videos this PC's GPU takes at full speed, learnt from how long they take there and on the CPU.</summary>
		internal static readonly LaneTuner Tuner = new("videos", FullSpeedSlots);

		/// <summary>A video's frames took <paramref name="seconds"/>, on the GPU or the CPU.</summary>
		internal static void RecordFile(bool onGpu, double seconds) => Tuner.Record(onGpu, seconds);
		static readonly int? SlotsOverride = int.TryParse(Environment.GetEnvironmentVariable("HEI_VIDEO_HWDECODE_SLOTS"), out int s) && s > 0 ? s : null;
		static readonly long Memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

		/// <summary>With less memory (the setting off, or a game running): 2, about a gigabyte at most.</summary>
		internal const int LeanSlots = 2;
		/// <summary>At full speed: the fastest, as the decoder saturates (see the measurements above).</summary>
		internal const int FullSpeedSlots = 4;
		/// <summary>The most decoder memory one slot holds, for a 4K 10-bit HEVC video.</summary>
		internal const long BytesPerSlot = 512L << 20;

		/// <summary>
		/// In the background, where the GPU saves the capped CPU the most, with more memory: one slot per 4 GB
		/// of RAM, 2 to 8. At full speed no more than 4 (and no more than that), with less memory 2.
		/// </summary>
		internal static int SlotsFor(bool fullSpeed, bool moreMemory, long memoryBytes) {
			if (!moreMemory)
				return LeanSlots;
			int roomy = (int)Math.Clamp(memoryBytes / (4L << 30), LeanSlots, 8);
			return fullSpeed ? Math.Min(FullSpeedSlots, roomy) : roomy;
		}

		/// <summary>The most memory the GPU's decoder takes in the background with more memory, for the settings page.</summary>
		internal static long MoreMemoryBytes => SlotsFor(fullSpeed: false, moreMemory: true, Memory) * BytesPerSlot;

		/// <summary>The file in the database folder that says a driver crash turned GPU decoding off.</summary>
		internal const string CrashMarkerName = "gpu-decoding-off.txt";

		const int MaxConsecutiveFailures = 3;

		static readonly object deviceLock = new();
		static AVBufferRef* device;
		static bool deviceTried;
		static int busy, consecutiveFailures;
		static bool crashedBefore;
		static int onGpu, failed;
		/// <summary>This thread's last decoder took a GPU slot (<see cref="TakeEntered"/>).</summary>
		[ThreadStatic] static bool enteredHere;

		/// <summary>Whether this thread's video went to the GPU since the last <see cref="TakeEntered"/>.</summary>
		internal static bool EnteredHere => enteredHere;

		/// <summary>Whether this thread's video went to the GPU, clearing it for the next video.</summary>
		internal static bool TakeEntered() {
			bool entered = enteredHere;
			enteredHere = false;
			return entered;
		}

		/// <summary>
		/// Takes a slot for a video coded with <paramref name="codec"/>, or returns false to decode it on
		/// the CPU. On true, the caller hands <see cref="NewDeviceReference"/> to its decoder and calls
		/// <see cref="Exit"/> when done.
		/// </summary>
		internal static bool TryEnter(AVCodec* codec, string path) {
			if (Mode == LaneMode.Off || crashedBefore || !OperatingSystem.IsWindows() || !Decodes(codec))
				return false;
			int slots = Mode == LaneMode.Always ? int.MaxValue : Slots;
			// No slot left at full speed on a GPU slower than the cores: an occasional video still goes, to keep measuring.
			if (slots == 0 && Pace.FullSpeed && Tuner.Probe())
				slots = 1;
			while (true) {
				int now = Volatile.Read(ref busy);
				if (now >= slots)
					return false;
				if (Interlocked.CompareExchange(ref busy, now + 1, now) == now)
					break;
			}
			if (!EnsureDevice()) {
				Interlocked.Decrement(ref busy);
				return false;
			}
			Interlocked.Increment(ref onGpu);
			enteredHere = true;
			// A crash from here on is the driver's, not the video's.
			ScanCrashJournal.Rephase(ScanCrashJournal.PhaseGpuDecode, path);
			return true;
		}

		/// <summary>
		/// Gives the slot back. Safe from any thread (a finalizer's too), so it leaves the crash journal
		/// alone: the worker that decoded the video puts its breadcrumb back (<see cref="LeftGpu"/>).
		/// </summary>
		internal static void Exit(string path) => Interlocked.Decrement(ref busy);

		/// <summary>On the worker thread, once the video's GPU decoder is gone: a crash from here on is the video's again.</summary>
		internal static void LeftGpu(string path) => ScanCrashJournal.Rephase(ScanCrashJournal.PhaseSampling, path);

		/// <summary>A new reference to the shared device, for one decoder's hw_device_ctx.</summary>
		internal static AVBufferRef* NewDeviceReference() => ffmpeg.av_buffer_ref(device);

		internal static void RecordSuccess() => Volatile.Write(ref consecutiveFailures, 0);

		/// <summary>The GPU failed on a video the CPU then decoded.</summary>
		internal static void RecordFailure(string path, string reason) {
			Interlocked.Increment(ref failed);
			Logger.Instance.Info($"The GPU could not decode '{path}' ({reason}); the CPU did.");
			if (Interlocked.Increment(ref consecutiveFailures) < MaxConsecutiveFailures || Mode == LaneMode.Off)
				return;
			Mode = LaneMode.Off;
			Logger.Instance.Warn($"GPU video decoding turned off for this scan after {MaxConsecutiveFailures} failures in a row; videos decode on the CPU.");
		}

		/// <summary>
		/// At the start of a scan: whether a driver crash turned GPU video decoding off on this PC (the
		/// marker in <paramref name="databaseFolder"/>); and if the last scan died decoding <paramref name="crashedOn"/>
		/// on the GPU, turns it off now.
		/// </summary>
		internal static void TurnOffAfterCrash(string? databaseFolder, string? crashedOn) {
			if (databaseFolder == null)
				return;
			string marker = Path.Combine(databaseFolder, CrashMarkerName);
			if (crashedOn != null) {
				try {
					File.WriteAllText(marker,
						$"{DateTime.Now:yyyy-MM-dd HH:mm}: a scan stopped while the GPU decoded '{crashedOn}', so videos and photos decode on the CPU. Delete this file to try the GPU again.{Environment.NewLine}");
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
				Logger.Instance.Warn($"The last scan stopped while the GPU decoded '{crashedOn}': most likely the graphics driver. Videos and photos decode on the CPU from now on (delete {marker} to try the GPU again).");
			}
			crashedBefore = File.Exists(marker);
		}

		/// <summary>A driver crash turned GPU decoding off on this PC (<see cref="TurnOffAfterCrash"/>).</summary>
		internal static bool OffAfterCrash => crashedBefore;

		/// <summary>A new scan: the counters start over, and failures in the last one no longer keep the lane off (a driver crash still does).</summary>
		internal static void ResetForScan() {
			onGpu = failed = 0;
			consecutiveFailures = 0;
			Mode = Configured;
			Tuner.ResetTotals();
		}

		/// <summary>For the scan's log: ", 120 on the GPU (2 decoded again on the CPU); 0.41 s a file on the GPU, 0.62 s on the CPU", or empty when no video went to it.</summary>
		internal static string Describe() =>
			onGpu == 0 ? "" : $", {onGpu:N0} on the GPU{(failed > 0 ? $" ({failed} decoded again on the CPU)" : "")}{Tuner.Describe()}";

		internal static int VideosOnGpu => onGpu;

		/// <summary>Whether the decoder has a hardware path for <see cref="DeviceType"/>, so a video that can't use it never takes a slot.</summary>
		static bool Decodes(AVCodec* codec) {
			if (codec == null)
				return false;
			for (int i = 0; ; i++) {
				AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, i);
				if (config == null)
					return false;
				const int HwDeviceCtx = 0x01; // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX, which FFmpeg.AutoGen doesn't carry
				if (config->device_type == DeviceType && (config->methods & HwDeviceCtx) != 0)
					return true;
			}
		}

		static bool EnsureDevice() {
			if (device != null)
				return true;
			lock (deviceLock) {
				if (device != null || deviceTried)
					return device != null;
				deviceTried = true;
				AVBufferRef* created = null;
				int ret = ffmpeg.av_hwdevice_ctx_create(&created, DeviceType, null, null, 0);
				if (ret < 0 || created == null) {
					Logger.Instance.Info($"GPU video decoding unavailable (no {DeviceType} device, error {ret}); videos decode on the CPU.");
					return false;
				}
				device = created;
				return true;
			}
		}
	}
}
