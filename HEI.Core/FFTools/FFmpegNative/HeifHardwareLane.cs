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

namespace HEI.Core.FFTools.FFmpegNative {
	/// <summary>
	/// The GPU's video decoder (D3D12VA) as a second lane for tiled HEIF photos, next to the CPU.
	/// On a Snapdragon X2 it decodes about 15 iPhone photos per second against about 90 on the
	/// CPU cores, far too slow on its own, but it costs almost no CPU, so taking photos off the
	/// CPU lane while it is saturated adds its throughput on top. It therefore takes one photo at
	/// a time (more sessions share the same decoder engine and add nothing) and only while
	/// <see cref="MinBusyCpuDecodes"/> others are decoding on the CPU: with fewer, the photo would
	/// decode faster on the CPU cores. The decoded pictures are bit-identical to the CPU decoder's.
	///
	/// Off without Windows or a D3D12 video decoder, after repeated failures on photos the CPU
	/// then read fine, and with the environment variable HEI_HEIF_HWDECODE=0.
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
		/// CPU decodes that must be running before a photo goes to the GPU. With fewer, the CPU
		/// cores are not saturated and tying a worker to the slower GPU costs more than it frees:
		/// on a Snapdragon X2, 4 parallel photos went from 88.5 to 83.0 photos/s with the lane,
		/// while 6, 8 and 12 gained 11-18%.
		/// </summary>
		const int MinBusyCpuDecodes = 4;

		/// <summary>Decoded tiles held on the GPU before the oldest is copied out (see TileCanvas).</summary>
		internal const int TilesInFlight = 4;

		static readonly object deviceLock = new();
		static AVBufferRef* device;
		static bool deviceTried;
		static int busy, cpuDecodes, consecutiveFailures;

		// Kept in a field: FFmpeg calls it back through a native pointer.
		static readonly AVCodecContext_get_format getFormat = GetFormat;

		/// <summary>Takes the lane for one photo, or returns false to decode it on the CPU.</summary>
		internal static bool TryEnter() {
			if (Mode == LaneMode.Off || (Mode == LaneMode.Auto && Volatile.Read(ref cpuDecodes) < MinBusyCpuDecodes))
				return false;
			if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
				return false;
			if (EnsureDevice())
				return true;
			Volatile.Write(ref busy, 0);
			return false;
		}

		internal static void Exit() => Volatile.Write(ref busy, 0);

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

		// The lane's tile decoder, kept open between photos: creating a D3D12 decoder costs about
		// as long as decoding a whole photo on it. Only the lane holder touches these.
		static AVCodecContext* decoderContext;
		static byte[]? decoderExtradata;
		static (AVCodecID Codec, int Width, int Height) decoderShape;

		/// <summary>
		/// The lane's open tile decoder for tiles coded like <paramref name="par"/>, flushed and
		/// ready; reopened when a photo's tiles are coded differently. Only call while holding the lane.
		/// </summary>
		internal static AVCodecContext* RentDecoder(AVCodec* decoder, AVCodecParameters* par) {
			var extradata = new ReadOnlySpan<byte>(par->extradata, par->extradata_size);
			if (decoderContext != null && decoderShape == (par->codec_id, par->width, par->height) && extradata.SequenceEqual(decoderExtradata)) {
				ffmpeg.avcodec_flush_buffers(decoderContext);
				return decoderContext;
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
			decoderContext = codec;
			decoderExtradata = extradata.ToArray();
			decoderShape = (par->codec_id, par->width, par->height);
			return codec;
		}

		/// <summary>Drops the lane's decoder, e.g. after it failed mid-photo.</summary>
		internal static void CloseDecoder() {
			if (decoderContext == null)
				return;
			AVCodecContext* codec = decoderContext;
			ffmpeg.avcodec_free_context(&codec);
			decoderContext = null;
			decoderExtradata = null;
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
