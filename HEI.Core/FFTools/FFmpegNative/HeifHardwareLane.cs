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

using FFmpeg.AutoGen;
using HEI.Core.Utils;

namespace HEI.Core.FFTools.FFmpegNative {
	/// <summary>
	/// The GPU's video decoder (D3D12VA) as a second lane for tiled HEIF photos, next to the CPU.
	/// On a Snapdragon X2 it decodes about 15 iPhone photos per second against about 90 on the
	/// CPU cores, far too slow on its own, but it costs almost no CPU, so taking photos off the
	/// CPU lane while it is saturated adds its throughput on top. At full speed it therefore takes one
	/// photo at a time (more sessions share the same decoder engine and add nothing) and only while
	/// <see cref="MinBusyCpuDecodes"/> others are decoding on the CPU: with fewer, the photo would decode
	/// faster on the CPU cores. In the background, under the cap on the processor, the CPU is what's
	/// short: it takes several photos (<see cref="Sessions"/>) whenever a session is free. The decoded
	/// pictures are bit-identical to the CPU decoder's.
	///
	/// Off without Windows or a D3D12 video decoder, after repeated failures on photos the CPU
	/// then read fine, after a driver crash (<see cref="HardwareVideoDecode.OffAfterCrash"/>), and
	/// with the environment variable HEI_HEIF_HWDECODE=0.
	/// </summary>
	static unsafe class HeifHardwareLane {
		internal enum LaneMode { Auto, Off, Always }

		/// <summary>Always is for measurements: every photo goes to the hardware lane.</summary>
		internal static LaneMode Mode = Environment.GetEnvironmentVariable("HEI_HEIF_HWDECODE") switch {
			"0" or "off" => LaneMode.Off,
			"always" => LaneMode.Always,
			_ => LaneMode.Auto
		};

		const int MaxConsecutiveFailures = 3;

		/// <summary>
		/// CPU decodes that must be running before a photo goes to the GPU. At full speed, 4: with
		/// fewer, the CPU cores are not saturated and tying a worker to the slower GPU costs more than
		/// it frees (on a Snapdragon X2, 4 parallel photos went from 88.5 to 83.0 photos/s with the
		/// lane, while 6, 8 and 12 gained 11-18%). In the background none: the CPU is capped, so the
		/// GPU, at a quarter of the CPU time per photo, is the better place for every photo it can take.
		/// </summary>
		static int MinBusyCpuDecodes => Pace.FullSpeed ? 4 : 0;

		/// <summary>Decoded tiles held on the GPU before the oldest is copied out (see TileCanvas).</summary>
		internal const int TilesInFlight = 4;

		/// <summary>
		/// Photos on the GPU at once. At full speed one: the decoder engine is what limits it then, and
		/// more sessions only share it. In the background, under the cap on the processor, a photo on
		/// the GPU costs the CPU about 125 ms against 195 (copying the tiles out and scaling the picture
		/// stays on the CPU), so the more of them go there the more fit under the cap, until the engine is
		/// saturated. Measured on a Snapdragon X2 under a background scan's cap, 9 workers, 600 iPhone
		/// photos: 10.5 photos/s on the CPU alone, 11.6 with 1 session, 12.1 with 2 or 4, 14.9 with 8.
		/// Each session holds only a tile decoder (512×512 tiles), a few megabytes.
		/// HEI_HEIF_HWDECODE_SESSIONS overrides it, for measuring.
		/// </summary>
		internal static int Sessions => SessionsOverride ?? (Pace.FullSpeed ? Math.Min(1, Tuner.Limit) : BackgroundSessions);

		/// <summary>Whether this PC's GPU takes photos at full speed, learnt from how long they take there and on the CPU (<see cref="LaneTuner"/>).</summary>
		internal static readonly LaneTuner Tuner = new("photos", 1);

		/// <summary>A photo took <paramref name="seconds"/>, on the GPU or the CPU.</summary>
		internal static void RecordFile(bool onGpu, double seconds) => Tuner.Record(onGpu, seconds);
		static readonly int? SessionsOverride = int.TryParse(Environment.GetEnvironmentVariable("HEI_HEIF_HWDECODE_SESSIONS"), out int s) && s > 0 ? s : null;
		internal const int BackgroundSessions = 8;

		static readonly object deviceLock = new();
		static AVBufferRef* device;
		static bool deviceTried;
		static int busy, cpuDecodes, consecutiveFailures, onGpu;

		/// <summary>A new scan: the count of photos on the GPU starts over.</summary>
		internal static void ResetForScan() {
			onGpu = 0;
			Tuner.ResetTotals();
		}

		/// <summary>For the scan's log: ", 420 on the GPU; 0.12 s a file on the GPU, 0.10 s on the CPU", or empty when no photo went to it.</summary>
		internal static string Describe() => onGpu == 0 ? "" : $", {onGpu:N0} on the GPU{Tuner.Describe()}";

		// Kept in a field: FFmpeg calls it back through a native pointer.
		static readonly AVCodecContext_get_format getFormat = GetFormat;

		/// <summary>
		/// One session's tile decoder, kept open between photos: creating a D3D12 decoder costs about as long
		/// as decoding a whole photo on it. Idle sessions wait in a pool; a thread holds one from
		/// <see cref="TryEnter"/> to <see cref="Exit"/>, and only that thread touches it.
		/// </summary>
		sealed class Session {
			public AVCodecContext* Decoder;
			public byte[]? Extradata;
			public (AVCodecID Codec, int Width, int Height) Shape;
		}
		static readonly System.Collections.Concurrent.ConcurrentBag<Session> idleSessions = new();
		[ThreadStatic] static Session? held;

		/// <summary>Takes a session for one photo, or returns false to decode it on the CPU.</summary>
		internal static bool TryEnter(string path) {
			if (Mode == LaneMode.Off || HardwareVideoDecode.OffAfterCrash || (Mode == LaneMode.Auto && Volatile.Read(ref cpuDecodes) < MinBusyCpuDecodes))
				return false;
			int sessions = Sessions;
			// The GPU slower than the cores at full speed: an occasional photo still goes, to keep measuring.
			if (sessions == 0 && Pace.FullSpeed && Tuner.Probe())
				sessions = 1;
			while (true) {
				int now = Volatile.Read(ref busy);
				if (now >= sessions)
					return false;
				if (Interlocked.CompareExchange(ref busy, now + 1, now) == now)
					break;
			}
			if (EnsureDevice()) {
				Interlocked.Increment(ref onGpu);
				held = idleSessions.TryTake(out Session? idle) ? idle : new Session();
				// A crash from here on is the driver's, not the photo's (HardwareVideoDecode.TurnOffAfterCrash).
				ScanCrashJournal.Rephase(ScanCrashJournal.PhaseGpuDecode, path);
				return true;
			}
			Interlocked.Decrement(ref busy);
			return false;
		}

		internal static void Exit(string path) {
			if (held != null) {
				idleSessions.Add(held);
				held = null;
			}
			Interlocked.Decrement(ref busy);
			ScanCrashJournal.Rephase(ScanCrashJournal.PhaseImage, path);
		}

		internal static void CpuDecodeStarted() => Interlocked.Increment(ref cpuDecodes);
		internal static void CpuDecodeEnded() => Interlocked.Decrement(ref cpuDecodes);

		internal static void RecordSuccess() => Volatile.Write(ref consecutiveFailures, 0);

		/// <summary>The hardware lane failed on a photo the CPU lane then decoded.</summary>
		internal static void RecordFailure(Exception e) {
			if (Interlocked.Increment(ref consecutiveFailures) < MaxConsecutiveFailures || Mode == LaneMode.Off)
				return;
			Mode = LaneMode.Off;
			Utils.Logger.Instance.Warn($"HEIF hardware decoding turned off after {MaxConsecutiveFailures} failures in a row; photos decode on the CPU. Last error: {e.Message}");
		}

		/// <summary>
		/// This thread's session's open tile decoder for tiles coded like <paramref name="par"/>, flushed and
		/// ready; reopened when a photo's tiles are coded differently. Only call while holding a session.
		/// </summary>
		internal static AVCodecContext* RentDecoder(AVCodec* decoder, AVCodecParameters* par) {
			Session session = held ?? throw new InvalidOperationException("No HEIF lane session held.");
			var extradata = new ReadOnlySpan<byte>(par->extradata, par->extradata_size);
			if (session.Decoder != null && session.Shape == (par->codec_id, par->width, par->height) && extradata.SequenceEqual(session.Extradata)) {
				ffmpeg.avcodec_flush_buffers(session.Decoder);
				return session.Decoder;
			}
			CloseDecoder();
			AVCodecContext* codec = ffmpeg.avcodec_alloc_context3(decoder);
			if (codec == null)
				throw new FFInvalidExitCodeException("Failed to allocate AVCodecContext.");
			try {
				ffmpeg.avcodec_parameters_to_context(codec, par).ThrowExceptionIfError();
				codec->hw_device_ctx = ffmpeg.av_buffer_ref(device);
				if (codec->hw_device_ctx == null)
					throw new FFInvalidExitCodeException("Failed to reference the D3D12 video device.");
				codec->get_format = getFormat;
				// iPhone tiles are HEVC Main Still Picture, a subset of Main (one intra picture)
				// that FFmpeg's D3D12 path otherwise refuses.
				codec->hwaccel_flags |= ffmpeg.AV_HWACCEL_FLAG_ALLOW_PROFILE_MISMATCH;
				codec->thread_count = 1;
				// Surfaces for the tiles held in flight on top of the decoder's own.
				codec->extra_hw_frames = TilesInFlight + 2;
				ffmpeg.avcodec_open2(codec, decoder, null).ThrowExceptionIfError();
			}
			catch {
				ffmpeg.avcodec_free_context(&codec);
				throw;
			}
			session.Decoder = codec;
			session.Extradata = extradata.ToArray();
			session.Shape = (par->codec_id, par->width, par->height);
			return codec;
		}

		/// <summary>Drops this thread's session's decoder, e.g. after it failed mid-photo.</summary>
		internal static void CloseDecoder() {
			Session? session = held;
			if (session == null || session.Decoder == null)
				return;
			AVCodecContext* codec = session.Decoder;
			ffmpeg.avcodec_free_context(&codec);
			session.Decoder = null;
			session.Extradata = null;
		}

		static bool EnsureDevice() {
			if (device != null)
				return true;
			lock (deviceLock) {
				if (device != null || deviceTried)
					return device != null;
				deviceTried = true;
				if (!OperatingSystem.IsWindows())
					return false;
				AVBufferRef* created = null;
				int ret = ffmpeg.av_hwdevice_ctx_create(&created, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA, null, null, 0);
				if (ret < 0 || created == null) {
					Utils.Logger.Instance.Info($"HEIF hardware decoding unavailable (no D3D12 video device, error {ret}); photos decode on the CPU.");
					return false;
				}
				device = created;
				return true;
			}
		}

		static AVPixelFormat GetFormat(AVCodecContext* context, AVPixelFormat* formats) {
			for (AVPixelFormat* p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
				if (*p == AVPixelFormat.AV_PIX_FMT_D3D12)
					return *p;
			// No hardware path for this stream: fail it, and the photo goes to the CPU lane.
			return AVPixelFormat.AV_PIX_FMT_NONE;
		}
	}
}
