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
	unsafe class VideoStreamDecoder : IDisposable {
		AVCodecContext* _pCodecContext;
		AVFormatContext* _pFormatContext;
		AVFrame* _pFrame;
		AVPacket* _pPacket;
		AVFrame* _pReceivedFrame;
		readonly int _streamIndex;
		readonly AVIOInterruptCB_callback _interruptCbDelegate;
		readonly ProgressDeadline _deadline;
		/// <summary>The video holds a slot of <see cref="HardwareVideoDecode"/>, given back on dispose.</summary>
		string? _gpuSlotFor;

		/// <param name="gpuLane">
		/// Decode on the GPU when <see cref="HardwareVideoDecode"/> has a slot free and a hardware path for the
		/// codec (only when <paramref name="HWDeviceType"/> is none); else on the CPU.
		/// </param>
		/// <param name="timeoutMs">How long the decode may go without progress before it counts as hung.</param>
		/// <param name="clock">The clock the timeout runs on; tests pass one they control.</param>
		public VideoStreamDecoder(string url, AVHWDeviceType HWDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE, int timeoutMs = 15_000, bool gpuLane = false, TimeProvider? clock = null) {
			_pFormatContext = ffmpeg.avformat_alloc_context();
			if (_pFormatContext == null)
				throw new FFInvalidExitCodeException("Failed to allocate AVFormatContext.");

			// Set up an interrupt callback so FFmpeg aborts blocking I/O when the
			// decode stops making progress.  This lets Dispose() run normally and
			// release the file handle — unlike killing a thread, which would leak it.
			// The timeout starts again on every step of TryDecodeFrame, not once per
			// position: under a background scan's capped processor, decoding forward
			// from the keyframe to one position of a 4K HEVC clip took more than 15 s.
			// The interrupt then fired mid-decode and the file went to the FFmpeg process,
			// whose frames differ — so the AI vectors depended on how busy the PC was.
			_deadline = new ProgressDeadline(TimeSpan.FromMilliseconds(timeoutMs), clock);
			_interruptCbDelegate = _ => _deadline.Expired() ? 1 : 0;
			_pFormatContext->interrupt_callback = new AVIOInterruptCB { callback = _interruptCbDelegate };

			_pReceivedFrame = ffmpeg.av_frame_alloc();
			if (_pReceivedFrame == null)
				throw new FFInvalidExitCodeException("Failed to allocate AVFrame for received frame.");
			// avformat_open_input frees the context and nulls the local on failure.
			// Sync the field with the local before checking the result so the finalizer
			// does not later see a dangling pointer if the open fails.
			var pFormatContext = _pFormatContext;
			int openRet = ffmpeg.avformat_open_input(&pFormatContext, url, null, null);
			_pFormatContext = pFormatContext;
			openRet.ThrowExceptionIfError();
			// The probe decodes a frame or so with no step of ours in between, so its time does count
			// against the timeout. In the background scans of 4K iPhone clips that ran out of time,
			// it never was the probe: every interrupt came in TryDecodeFrame.
			ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();
			AVCodec* codec = null;

			_streamIndex = ffmpeg.av_find_best_stream(_pFormatContext,
				AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0).ThrowExceptionIfError();
			_pCodecContext = ffmpeg.avcodec_alloc_context3(codec);
			if (_pCodecContext == null)
				throw new FFInvalidExitCodeException("Failed to allocate AVCodecContext.");
			bool onGpu = false;
			if (HWDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
				ffmpeg.av_hwdevice_ctx_create(&_pCodecContext->hw_device_ctx, HWDeviceType, null, null, 0).ThrowExceptionIfError();
			else if (gpuLane && HardwareVideoDecode.TryEnter(codec, url)) {
				_gpuSlotFor = url;
				onGpu = true;
				// The process's one device, shared: libavcodec picks its hardware format, or software
				// for a profile the GPU doesn't decode (frames then simply arrive in system memory).
				_pCodecContext->hw_device_ctx = HardwareVideoDecode.NewDeviceReference();
			}
			try {
				OpenCodec(codec, HWDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE || onGpu);
			}
			catch {
				// A constructor that throws is never disposed: give the slot back here.
				ReleaseGpuSlot();
				throw;
			}
		}

		/// <summary>Whether this video decodes on <see cref="HardwareVideoDecode"/>'s GPU lane.</summary>
		public bool OnGpuLane => _gpuSlotFor != null;

		void ReleaseGpuSlot() {
			if (_gpuSlotFor is { } url) {
				_gpuSlotFor = null;
				HardwareVideoDecode.Exit(url);
			}
		}

		void OpenCodec(AVCodec* codec, bool hardware) {
			ffmpeg.avcodec_parameters_to_context(_pCodecContext, _pFormatContext->streams[_streamIndex]->codecpar).ThrowExceptionIfError();
			ffmpeg.avcodec_open2(_pCodecContext, codec, null).ThrowExceptionIfError();

			CodecName = ffmpeg.avcodec_get_name(codec->id);
			AVCodecParameters* codecpar = _pFormatContext->streams[_streamIndex]->codecpar;
			AVPacketSideData* matrixSideData = ffmpeg.av_packet_side_data_get(codecpar->coded_side_data, codecpar->nb_coded_side_data,
				AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
			if (matrixSideData != null && matrixSideData->size >= 9 * sizeof(int))
				StreamOrientation = FrameOrientation.FromDisplayMatrix(new ReadOnlySpan<int>(matrixSideData->data, 9));
			// Container-level pixel aspect ratio for anamorphic content; 0/1 when unknown.
			StreamSampleAspectRatio = _pFormatContext->streams[_streamIndex]->sample_aspect_ratio;
			FrameSize = new Size(_pCodecContext->width, _pCodecContext->height);
			if (FrameSize.Width <= 0 || FrameSize.Height <= 0)
				throw new FFInvalidExitCodeException($"Invalid frame dimensions {FrameSize.Width}x{FrameSize.Height}.");
			// For HW decode we intentionally defer the source pixel format until the
			// first frame has been downloaded with av_hwframe_transfer_data — only then
			// do we know the real sw_format (e.g. P010LE for 10-bit HEVC vs NV12 for
			// 8-bit). Guessing before decode breaks 10-bit content.
			PixelFormat = hardware ? AVPixelFormat.AV_PIX_FMT_NONE : _pCodecContext->pix_fmt;
			IsHardwareDecode = hardware;

			_pPacket = ffmpeg.av_packet_alloc();
			if (_pPacket == null)
				throw new FFInvalidExitCodeException("Failed to allocate AVPacket.");
			_pFrame = ffmpeg.av_frame_alloc();
			if (_pFrame == null)
				throw new FFInvalidExitCodeException("Failed to allocate AVFrame.");
		}

		public string CodecName { get; private set; } = "";
		/// <summary>The stream's display matrix, as far as it says how to turn the picture (#910).</summary>
		public FrameOrientation StreamOrientation { get; private set; }

		/// <summary>
		/// How to turn <paramref name="frame"/> upright: its own display matrix when the decoder
		/// attached one (EXIF orientation of a still, a per-frame matrix), else the stream's.
		/// </summary>
		public FrameOrientation GetOrientation(AVFrame frame) {
			AVFrameSideData* sideData = ffmpeg.av_frame_get_side_data(&frame, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
			if (sideData != null && sideData->size >= 9 * sizeof(int))
				return FrameOrientation.FromDisplayMatrix(new ReadOnlySpan<int>(sideData->data, 9));
			return StreamOrientation;
		}
		public Size FrameSize { get; private set; }
		public AVPixelFormat PixelFormat { get; private set; }
		public bool IsHardwareDecode { get; private set; }
		public AVRational StreamSampleAspectRatio { get; private set; }
		/// <summary>
		/// True when the container carries stream groups (e.g. the HEIF tile grid of an Apple
		/// photo). av_find_best_stream can only pick a single coded stream — for a tiled photo
		/// that is one 512x512 tile or an auxiliary depth/gain-map stream, never the assembled
		/// picture, so callers decoding such images must use the FFmpeg process instead (#869).
		/// </summary>
		public bool HasStreamGroups => _pFormatContext != null && _pFormatContext->nb_stream_groups > 0;

		protected virtual void Dispose(bool disposing) {
			ReleaseUnmanaged();
		}

		~VideoStreamDecoder() {
			Dispose(false);
		}

		public void Dispose() {
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		public void ReleaseUnmanaged() {
			ReleaseGpuSlot();
			// Null each field after freeing so a partially-constructed object's finalizer
			// or a double-Dispose can't pass dangling pointers back to FFmpeg.
			if (_pFrame != null) {
				AVFrame* pFrame = _pFrame;
				ffmpeg.av_frame_free(&pFrame);
				_pFrame = null;
			}
			if (_pReceivedFrame != null) {
				AVFrame* pReceivedFrame = _pReceivedFrame;
				ffmpeg.av_frame_free(&pReceivedFrame);
				_pReceivedFrame = null;
			}
			if (_pPacket != null) {
				AVPacket* pPacket = _pPacket;
				ffmpeg.av_packet_free(&pPacket);
				_pPacket = null;
			}
			if (_pCodecContext != null) {
				AVCodecContext* pCodecContext = _pCodecContext;
				ffmpeg.avcodec_free_context(&pCodecContext);
				_pCodecContext = null;
			}
			if (_pFormatContext != null) {
				AVFormatContext* pFormatContext = _pFormatContext;
				ffmpeg.avformat_close_input(&pFormatContext);
				_pFormatContext = null;
			}
		}

		public bool TryDecodeFrame(out AVFrame frame, TimeSpan position) {
			// The whole timeout for the seek — and not what earlier positions of the file took.
			_deadline.Progress();
			ffmpeg.av_frame_unref(_pFrame);
			ffmpeg.av_frame_unref(_pReceivedFrame);

			AVRational timebase = _pFormatContext->streams[_streamIndex]->time_base;
			double AV_TIME_BASE = (double)timebase.den / timebase.num;
			long targetPts = Convert.ToInt64(position.TotalSeconds * AV_TIME_BASE);

			// Only seek when a non-zero position is requested. Seeking to the start is at best a
			// no-op and on single-frame image demuxers (image2/mjpeg) it overshoots the lone
			// packet, leaving nothing to decode — which silently broke native still-image decode
			// and forced a CLI fallback for every JPEG (native analogue of the #801 -ss-on-stills
			// bug). For position 0 we just decode forward from the start, matching the CLI grabber.
			if (targetPts > 0) {
				if (ffmpeg.av_seek_frame(_pFormatContext, _streamIndex, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
					ffmpeg.av_seek_frame(_pFormatContext, _streamIndex, targetPts, ffmpeg.AVSEEK_FLAG_ANY).ThrowExceptionIfError();

				ffmpeg.avcodec_flush_buffers(_pCodecContext);
			}

			// Decode forward from keyframe until we reach the target PTS.
			// Cap iterations to prevent infinite loops on corrupt files.
			const int maxIterations = 10_000;
			// AVERROR_INVALIDDATA on the first read(s) after seek is normal: the demuxer
			// can hand us partial packets between the seek target and the next keyframe.
			// Skip them silently rather than tearing down the decoder and falling back
			// to the CLI process — see issue #731. Cap so a truly corrupt file still bails.
			const int maxBadPackets = 64;
			int badPacketCount = 0;
			// Once the demuxer is exhausted we send a single null packet to put the decoder
			// into draining mode. Intra single-frame codecs (e.g. MJPEG still images) buffer
			// their only frame and emit it only after a flush; without draining that frame is
			// never received and still-image decoding fails outright, forcing a CLI fallback
			// (native analogue of the #801 -ss-on-stills bug).
			bool draining = false;
			bool reachedTarget = false;
			for (int iter = 0; iter < maxIterations; iter++) {
				// Each pass hands the decoder a packet (or takes a frame out of it): progress, so the
				// timeout starts again. What it guards is reading the next packet, where FFmpeg asks
				// the interrupt callback; the time the decoder spent on the last one, however slow
				// under the background pace, doesn't count against it. The caps above bound the
				// work instead, whatever the clock says.
				_deadline.Progress();
				if (!draining) {
					int error;
					while (true) {
						ffmpeg.av_packet_unref(_pPacket);
						error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);
						if (error == ffmpeg.AVERROR_EOF) {
							// No more packets — flush the decoder rather than giving up.
							ffmpeg.av_packet_unref(_pPacket);
							ffmpeg.avcodec_send_packet(_pCodecContext, null).ThrowExceptionIfError();
							draining = true;
							break;
						}
						if (error == ffmpeg.AVERROR_INVALIDDATA) {
							if (++badPacketCount > maxBadPackets) {
								frame = *_pFrame;
								return false;
							}
							continue;
						}
						error.ThrowExceptionIfError();
						if (_pPacket->stream_index == _streamIndex) break;
					}

					if (!draining) {
						int sendErr;
						try {
							sendErr = ffmpeg.avcodec_send_packet(_pCodecContext, _pPacket);
						}
						finally {
							ffmpeg.av_packet_unref(_pPacket);
						}
						if (sendErr == ffmpeg.AVERROR_INVALIDDATA || sendErr == ffmpeg.AVERROR(ffmpeg.EINVAL)) {
							if (++badPacketCount > maxBadPackets) {
								frame = *_pFrame;
								return false;
							}
							continue;
						}
						sendErr.ThrowExceptionIfError();
					}
				}

				int recvErr = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);
				if (recvErr == ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
					// In draining mode EAGAIN cannot occur; if it somehow does, the decoder
					// has nothing left to give, so bail rather than spin to maxIterations.
					if (draining) {
						frame = *_pFrame;
						return false;
					}
					continue;
				}
				if (recvErr < 0) { // includes AVERROR_EOF: decoder fully drained, no frame at target
					frame = *_pFrame;
					return false;
				}

				// Check if we've reached or passed the target position
				if (_pFrame->pts >= targetPts || _pFrame->pts == ffmpeg.AV_NOPTS_VALUE) {
					reachedTarget = true;
					break;
				}

				// Not at target yet - discard this frame and decode the next
				ffmpeg.av_frame_unref(_pFrame);
			}
			// Out of passes before the target (keyframes very far apart): no frame. Without this the
			// discarded frame went out as if decoded — the timeout used to end such a decode first.
			if (!reachedTarget) {
				frame = *_pFrame;
				return false;
			}

			// Only download when the frame actually lives in GPU memory. Hardware
			// decoders can silently fall back to software frames (unsupported
			// profile/level); calling av_hwframe_transfer_data on those returns
			// EINVAL and needlessly failed the whole file to the CLI fallback.
			// Callers already read the source format from the frame itself when
			// hardware decode was requested, so a software frame flows through fine.
			if (_pCodecContext->hw_device_ctx != null && _pFrame->hw_frames_ctx != null) {
				ffmpeg.av_hwframe_transfer_data(_pReceivedFrame, _pFrame, 0).ThrowExceptionIfError();
				// The transfer copies only pixel data; frame properties live on the source
				// hw frame and must be carried across explicitly. Notably sample_aspect_ratio,
				// which the display-thumbnail path reads to widen anamorphic content — without
				// this the SAR correction silently no-ops under hardware decode.
				ffmpeg.av_frame_copy_props(_pReceivedFrame, _pFrame);
				frame = *_pReceivedFrame;
			}
			else
				frame = *_pFrame;

			return true;
		}

	}
}
