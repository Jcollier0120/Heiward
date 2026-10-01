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

using System.Diagnostics;
using FFmpeg.AutoGen;

namespace HEI.Core.FFTools.FFmpegNative {
	/// <summary>
	/// Decodes the tile grid of a HEIF still in-process. Every iPhone photo is one: a 12 MP
	/// picture is 40 separately coded 512x512 HEVC tiles plus a tile-grid stream group saying
	/// where each goes (#869). The command line assembles it through a filtergraph, one process
	/// per photo; here all tiles go through one frame-threaded decoder, several at a time.
	///
	/// Each decoded tile is area-averaged straight onto a canvas shrunk by a whole factor (4 for
	/// an iPhone photo), so no full-resolution picture is ever built. The 224x224 AI frame comes
	/// from the canvas in one bicubic step, and the 32x32 gray bytes come from the AI frame
	/// (GrayBytesUtils.FromRgb224), the rule every photo follows whatever decoded it, so a
	/// HEIC still matches its own JPEG copy.
	/// </summary>
	static unsafe class HeifTileGridDecoder {
		internal readonly record struct Result(byte[] Gray32, byte[]? Rgb224, int Width, int Height);

		/// <summary>
		/// The canvas keeps at least this many pixels on its shorter side, twice the AI input, so
		/// the bicubic step down to 224x224 still has detail to average.
		/// </summary>
		internal const int MinCanvasSide = 2 * AI.OnnxEmbedder.InputSide;

		static readonly int[] CanvasFactors = { 8, 4, 2 };

		/// <summary>
		/// The largest of 8, 4 and 2 that keeps the canvas's shorter side at
		/// <see cref="MinCanvasSide"/> or more and divides tile sizes and offsets into multiples of
		/// 2 * factor, so every tile lands on whole chroma samples; 1 when none does.
		/// </summary>
		internal static int CanvasFactor(int width, int height, int tileWidth, int tileHeight, ReadOnlySpan<(int X, int Y)> offsets) {
			foreach (int factor in CanvasFactors) {
				int step = 2 * factor;
				if (Math.Min(width, height) / factor < MinCanvasSide || tileWidth % step != 0 || tileHeight % step != 0)
					continue;
				bool aligned = true;
				foreach (var (x, y) in offsets) {
					if (x % step != 0 || y % step != 0) {
						aligned = false;
						break;
					}
				}
				if (aligned)
					return factor;
			}
			return 1;
		}

		/// <summary>
		/// The picture's crop window (horizontal_offset, vertical_offset, width, height) on a canvas
		/// shrunk by <paramref name="factor"/>, clamped to it. The origin is rounded down to even so
		/// the 4:2:0 chroma planes stay aligned, a shift of at most one canvas pixel.
		/// </summary>
		internal static (int X, int Y, int Width, int Height) CropWindow(int x, int y, int width, int height, int factor, int canvasWidth, int canvasHeight) {
			int cx = Math.Clamp(x / factor, 0, canvasWidth) & ~1;
			int cy = Math.Clamp(y / factor, 0, canvasHeight) & ~1;
			return (cx, cy, Math.Min(canvasWidth - cx, (width + factor / 2) / factor), Math.Min(canvasHeight - cy, (height + factor / 2) / factor));
		}

		/// <summary>
		/// Decodes the photo's primary tile grid. Returns false when the file has none (a
		/// single-image HEIF, which the regular native decoder handles). Throws when there is a
		/// grid this path cannot take (10-bit or 4:4:4 tiles, tiles that differ in format,
		/// missing tiles); callers then fall back to the FFmpeg process.
		/// </summary>
		internal static bool TryDecode(string path, bool wantRgb, out Result result, int timeoutMs = 15_000) {
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			if (HeifHardwareLane.TryEnter(path)) {
				Exception failure;
				try {
					bool decoded = TryDecode(path, wantRgb, hardware: true, out result, timeoutMs);
					HeifHardwareLane.RecordSuccess();
					if (decoded)
						HeifHardwareLane.RecordFile(onGpu: true, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
					return decoded;
				}
				catch (Exception e) {
					failure = e;
				}
				finally {
					HeifHardwareLane.Exit(path);
				}
				// Decode this photo on the CPU instead, off the lane (a crash now would be the photo's);
				// the lane turns itself off if the GPU keeps failing on photos the CPU can read.
				if (!TryDecodeOnCpu(path, wantRgb, out result, timeoutMs))
					return false;
				HeifHardwareLane.RecordFailure(failure);
				return true;
			}
			bool onCpu = TryDecodeOnCpu(path, wantRgb, out result, timeoutMs);
			// How long each side takes for a photo, so the GPU takes photos only while it keeps up (LaneTuner).
			if (onCpu)
				HeifHardwareLane.RecordFile(onGpu: false, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
			return onCpu;
		}

		static bool TryDecodeOnCpu(string path, bool wantRgb, out Result result, int timeoutMs) {
			HeifHardwareLane.CpuDecodeStarted();
			try {
				return TryDecode(path, wantRgb, hardware: false, out result, timeoutMs);
			}
			finally {
				HeifHardwareLane.CpuDecodeEnded();
			}
		}

		static bool TryDecode(string path, bool wantRgb, bool hardware, out Result result, int timeoutMs) {
			result = default;
			long deadline = Stopwatch.GetTimestamp() + (long)(timeoutMs / 1000.0 * Stopwatch.Frequency);
			// Aborts blocking I/O once the deadline passes, as in VideoStreamDecoder.
			AVIOInterruptCB_callback interrupt = _ => Stopwatch.GetTimestamp() > deadline ? 1 : 0;

			AVFormatContext* format = null;
			AVCodecContext* codec = null;
			AVPacket* packet = null;
			AVFrame* frame = null;
			TileCanvas? canvas = null;
			try {
				format = ffmpeg.avformat_alloc_context();
				if (format == null)
					throw new FFInvalidExitCodeException("Failed to allocate AVFormatContext.");
				format->interrupt_callback = new AVIOInterruptCB { callback = interrupt };
				// avformat_open_input frees the context and nulls the pointer on failure.
				ffmpeg.avformat_open_input(&format, path, null, null).ThrowExceptionIfError();
				// No avformat_find_stream_info: the HEIF demuxer fills every tile's parameters from
				// the item properties, and probing would decode each of the 50-odd tiles once more.

				AVStreamGroup* group = FindPrimaryTileGrid(format);
				if (group == null)
					return false;
				AVStreamGroupTileGrid* grid = group->@params.tile_grid;
				if (grid == null || grid->nb_tiles == 0 || grid->coded_width <= 0 || grid->coded_height <= 0 || grid->width <= 0 || grid->height <= 0)
					throw new FFInvalidExitCodeException("Invalid HEIF tile grid.");

				int tileCount = (int)grid->nb_tiles;
				var offsets = new (int X, int Y)[tileCount];
				// Which tile each stream carries; every other stream (the HDR gain map's tiles,
				// Exif, depth maps) is discarded so the demuxer skips its data.
				var tileOfStream = new int[format->nb_streams];
				Array.Fill(tileOfStream, -1);
				AVCodecParameters* tilePar = null;
				for (int t = 0; t < tileCount; t++) {
					var offset = grid->offsets[t];
					if (offset.idx >= group->nb_streams)
						throw new FFInvalidExitCodeException($"Tile {t} references stream {offset.idx} outside the group.");
					AVStream* stream = group->streams[offset.idx];
					AVCodecParameters* par = stream->codecpar;
					if (tilePar == null)
						tilePar = par;
					else if (!SameCoding(par, tilePar))
						throw new FFInvalidExitCodeException("HEIF tiles are coded differently.");
					// Even offsets keep every tile on whole 4:2:0 chroma samples.
					if (offset.horizontal < 0 || offset.vertical < 0 || offset.horizontal % 2 != 0 || offset.vertical % 2 != 0 ||
						offset.horizontal + par->width > grid->coded_width || offset.vertical + par->height > grid->coded_height)
						throw new FFInvalidExitCodeException($"Tile {t} does not fit the grid ({offset.horizontal},{offset.vertical}).");
					offsets[t] = (offset.horizontal, offset.vertical);
					tileOfStream[stream->index] = t;
				}
				for (int s = 0; s < format->nb_streams; s++)
					format->streams[s]->discard = tileOfStream[s] >= 0 ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;

				int tileWidth = tilePar->width, tileHeight = tilePar->height;
				if (tileWidth <= 0 || tileHeight <= 0 || tileWidth % 2 != 0 || tileHeight % 2 != 0)
					throw new FFInvalidExitCodeException($"Unsupported tile size {tileWidth}x{tileHeight}.");
				int factor = CanvasFactor(grid->width, grid->height, tileWidth, tileHeight, offsets);

				AVCodec* decoder = ffmpeg.avcodec_find_decoder(tilePar->codec_id);
				if (decoder == null)
					throw new FFInvalidExitCodeException($"No decoder for {ffmpeg.avcodec_get_name(tilePar->codec_id)} tiles.");
				// The hardware lane's decoder stays open between photos and belongs to the lane;
				// a CPU decoder is this photo's own (freed below).
				AVCodecContext* tiles;
				if (hardware) {
					tiles = HeifHardwareLane.RentDecoder(decoder, tilePar);
				}
				else {
					codec = ffmpeg.avcodec_alloc_context3(decoder);
					if (codec == null)
						throw new FFInvalidExitCodeException("Failed to allocate AVCodecContext.");
					ffmpeg.avcodec_parameters_to_context(codec, tilePar).ThrowExceptionIfError();
					// The tiles are independent intra pictures, so frame threading decodes as many
					// at once as there are cores (0 = FFmpeg's automatic count).
					codec->thread_count = 0;
					codec->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
					ffmpeg.avcodec_open2(codec, decoder, null).ThrowExceptionIfError();
					tiles = codec;
				}

				packet = ffmpeg.av_packet_alloc();
				frame = ffmpeg.av_frame_alloc();
				if (packet == null || frame == null)
					throw new FFInvalidExitCodeException("Failed to allocate AVPacket/AVFrame.");

				canvas = new TileCanvas(offsets, factor, tileWidth, tileHeight,
					(grid->coded_width + factor - 1) / factor, (grid->coded_height + factor - 1) / factor);

				int readError;
				while ((readError = ffmpeg.av_read_frame(format, packet)) >= 0) {
					try {
						int t = packet->stream_index < tileOfStream.Length ? tileOfStream[packet->stream_index] : -1;
						if (t < 0)
							continue;
						// The tile number rides along as the timestamp: frame threading hands frames
						// back in its own time, and all tiles carry the same pts otherwise.
						packet->pts = packet->dts = t;
						int sendError;
						while ((sendError = ffmpeg.avcodec_send_packet(tiles, packet)) == ffmpeg.AVERROR(ffmpeg.EAGAIN))
							canvas.PlaceDecodedTiles(tiles, frame);
						sendError.ThrowExceptionIfError();
						canvas.PlaceDecodedTiles(tiles, frame);
					}
					finally {
						ffmpeg.av_packet_unref(packet);
					}
				}
				if (readError != ffmpeg.AVERROR_EOF)
					readError.ThrowExceptionIfError();
				ffmpeg.avcodec_send_packet(tiles, null).ThrowExceptionIfError();
				canvas.PlaceDecodedTiles(tiles, frame, drain: true);
				if (canvas.PlacedCount != tileCount)
					throw new FFInvalidExitCodeException($"Only {canvas.PlacedCount} of {tileCount} tiles decoded.");

				var crop = CropWindow(grid->horizontal_offset, grid->vertical_offset, grid->width, grid->height, factor, canvas.Width, canvas.Height);
				if (crop.Width <= 0 || crop.Height <= 0)
					throw new FFInvalidExitCodeException("Empty crop window.");
				AVFrame picture = canvas.View(crop.X, crop.Y, crop.Width, crop.Height);
				AVPixelFormat pixelFormat = canvas.PixelFormat;

				// irot/imir: the grid carries the turn as a display matrix, as a phone video does.
				FrameOrientation orientation = FrameOrientation.None;
				AVPacketSideData* matrix = ffmpeg.av_packet_side_data_get(grid->coded_side_data, grid->nb_coded_side_data,
					AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
				if (matrix != null && matrix->size >= 9 * sizeof(int))
					orientation = FrameOrientation.FromDisplayMatrix(new ReadOnlySpan<int>(matrix->data, 9));

				// The AI frame in the same bicubic step GetThumbnail(Rgb224) takes for any other picture,
				// and the gray bytes from it, as for every photo.
				int side = AI.OnnxEmbedder.InputSide;
				byte[] rgb;
				using (var toRgb = new VideoFrameConverter(new Size(crop.Width, crop.Height), pixelFormat, new Size(side, side), AVPixelFormat.AV_PIX_FMT_RGB24)) {
					// A pooled buffer, like every other AI frame; turning makes a new one.
					byte[] pooled = CopyPacked(toRgb.Convert(picture), 3, AI.FramePool.Shared.Rent());
					rgb = orientation.Apply(pooled, side, side, 3);
					if (!ReferenceEquals(rgb, pooled))
						AI.FramePool.Shared.Return(pooled);
				}
				byte[] gray = Utils.GrayBytesUtils.FromRgb224(rgb);
				if (!wantRgb) {
					AI.FramePool.Shared.Return(rgb);
					result = new Result(gray, null, grid->width, grid->height);
				}
				else
					result = new Result(gray, rgb, grid->width, grid->height);
				return true;
			}
			catch when (hardware) {
				// Mid-photo failure: the lane's decoder is in an unknown state, start the next photo fresh.
				HeifHardwareLane.CloseDecoder();
				throw;
			}
			finally {
				canvas?.Dispose();
				if (frame != null)
					ffmpeg.av_frame_free(&frame);
				if (packet != null)
					ffmpeg.av_packet_free(&packet);
				if (codec != null)
					ffmpeg.avcodec_free_context(&codec);
				if (format != null)
					ffmpeg.avformat_close_input(&format);
				GC.KeepAlive(interrupt);
			}
		}

		/// <summary>
		/// The tile grid of the primary picture: the one marked default, as the command line's
		/// [0:g:0] resolves it on Apple photos, else the first. Later grids are auxiliary
		/// pictures such as the HDR gain map.
		/// </summary>
		static AVStreamGroup* FindPrimaryTileGrid(AVFormatContext* format) {
			AVStreamGroup* first = null;
			for (int g = 0; g < format->nb_stream_groups; g++) {
				AVStreamGroup* group = format->stream_groups[g];
				if (group->type != AVStreamGroupParamsType.AV_STREAM_GROUP_PARAMS_TILE_GRID)
					continue;
				if ((group->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0)
					return group;
				if (first == null)
					first = group;
			}
			return first;
		}

		/// <summary>One decoder serves every tile, so all must share codec, size and parameter sets.</summary>
		static bool SameCoding(AVCodecParameters* a, AVCodecParameters* b) {
			if (a->codec_id != b->codec_id || a->width != b->width || a->height != b->height || a->extradata_size != b->extradata_size)
				return false;
			return a->extradata_size == 0 ||
				new ReadOnlySpan<byte>(a->extradata, a->extradata_size).SequenceEqual(new ReadOnlySpan<byte>(b->extradata, b->extradata_size));
		}

		/// <summary>Copies a converted frame into a tightly packed buffer, dropping swscale's row padding.</summary>
		static byte[] CopyPacked(AVFrame converted, int bytesPerPixel, byte[]? into) {
			int rowBytes = converted.width * bytesPerPixel;
			byte[] packed = into ?? new byte[rowBytes * converted.height];
			if (packed.Length != rowBytes * converted.height)
				throw new FFInvalidExitCodeException($"Buffer of {packed.Length} bytes for a {converted.width}x{converted.height} frame.");
			fixed (byte* dst = packed) {
				for (int row = 0; row < converted.height; row++)
					Buffer.MemoryCopy(converted.data[0] + row * converted.linesize[0], dst + row * rowBytes, rowBytes, rowBytes);
			}
			return packed;
		}

		/// <summary>
		/// The shrunk picture the tiles are scaled onto, and which of them have arrived. The canvas
		/// and its scaler are created with the first decoded tile, whose pixel format they take.
		/// </summary>
		sealed class TileCanvas : IDisposable {
			readonly (int X, int Y)[] offsets;
			readonly bool[] placed;
			readonly int factor, tileWidth, tileHeight;
			AVFrame* canvas;
			SwsContext* scaler;
			AVFrame* downloaded; // a hardware-decoded tile, copied to system memory
			AVPixelFormat tileFormat = AVPixelFormat.AV_PIX_FMT_NONE;
			// Hardware-decoded tiles not copied out yet. Copying waits for the GPU, so the lane keeps
			// a few tiles in flight and copies each out only once the next ones are queued on it.
			readonly Queue<nint> inFlight = new();

			public TileCanvas((int X, int Y)[] offsets, int factor, int tileWidth, int tileHeight, int width, int height) {
				this.offsets = offsets;
				placed = new bool[offsets.Length];
				this.factor = factor;
				this.tileWidth = tileWidth;
				this.tileHeight = tileHeight;
				Width = width;
				Height = height;
			}

			public int Width { get; }
			public int Height { get; }
			public int PlacedCount { get; private set; }
			public AVPixelFormat PixelFormat { get; private set; } = AVPixelFormat.AV_PIX_FMT_NONE;

			/// <summary>
			/// Takes every frame the decoder has ready and scales each into its place; hardware
			/// frames once <see cref="HeifHardwareLane.TilesInFlight"/> newer ones are queued, or all
			/// of them when <paramref name="drain"/> is set after the last packet.
			/// </summary>
			public void PlaceDecodedTiles(AVCodecContext* codec, AVFrame* frame, bool drain = false) {
				while (true) {
					int ret = ffmpeg.avcodec_receive_frame(codec, frame);
					if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
						break;
					ret.ThrowExceptionIfError();
					try {
						if (frame->hw_frames_ctx == null) {
							Place(frame);
							continue;
						}
						AVFrame* held = ffmpeg.av_frame_clone(frame);
						if (held == null)
							throw new FFInvalidExitCodeException("Failed to reference a decoded tile.");
						inFlight.Enqueue((nint)held);
						while (inFlight.Count > HeifHardwareLane.TilesInFlight)
							PlaceOldestInFlight();
					}
					finally {
						ffmpeg.av_frame_unref(frame);
					}
				}
				if (drain)
					while (inFlight.Count > 0)
						PlaceOldestInFlight();
			}

			void PlaceOldestInFlight() {
				AVFrame* held = (AVFrame*)inFlight.Dequeue();
				try {
					Place(held);
				}
				finally {
					ffmpeg.av_frame_free(&held);
				}
			}

			void Place(AVFrame* tile) {
				long t = tile->pts;
				if (t < 0 || t >= placed.Length || placed[t])
					throw new FFInvalidExitCodeException($"Decoder returned an unexpected tile ({t}).");
				if (tile->hw_frames_ctx != null) {
					// Hardware lane: the tile lives in video memory; copy it out (NV12).
					if (downloaded == null && (downloaded = ffmpeg.av_frame_alloc()) == null)
						throw new FFInvalidExitCodeException("Failed to allocate AVFrame.");
					ffmpeg.av_frame_unref(downloaded);
					ffmpeg.av_hwframe_transfer_data(downloaded, tile, 0).ThrowExceptionIfError();
					ffmpeg.av_frame_copy_props(downloaded, tile).ThrowExceptionIfError();
					tile = downloaded;
				}
				if (tile->width != tileWidth || tile->height != tileHeight)
					throw new FFInvalidExitCodeException($"Tile {t} decoded as {tile->width}x{tile->height}, expected {tileWidth}x{tileHeight}.");
				if (canvas == null)
					Allocate(tile);
				else if (tile->format != (int)tileFormat)
					throw new FFInvalidExitCodeException($"Tile {t} has a different pixel format.");

				int x = offsets[t].X / factor, y = offsets[t].Y / factor; // even, see CanvasFactor
				byte* dstY = canvas->data[0] + y * canvas->linesize[0] + x;
				byte* dstU = canvas->data[1] + y / 2 * canvas->linesize[1] + x / 2;
				byte* dstV = canvas->data[2] + y / 2 * canvas->linesize[2] + x / 2;
				if (scaler == null) {
					ffmpeg.av_image_copy_plane(dstY, canvas->linesize[0], tile->data[0], tile->linesize[0], tileWidth, tileHeight);
					ffmpeg.av_image_copy_plane(dstU, canvas->linesize[1], tile->data[1], tile->linesize[1], tileWidth / 2, tileHeight / 2);
					ffmpeg.av_image_copy_plane(dstV, canvas->linesize[2], tile->data[2], tile->linesize[2], tileWidth / 2, tileHeight / 2);
				}
				else {
					var dst = new byte*[] { dstY, dstU, dstV, null };
					var dstStride = new[] { canvas->linesize[0], canvas->linesize[1], canvas->linesize[2], 0 };
					ffmpeg.sws_scale(scaler, tile->data, tile->linesize, 0, tileHeight, dst, dstStride).ThrowExceptionIfError();
				}
				placed[t] = true;
				PlacedCount++;
			}

			void Allocate(AVFrame* tile) {
				// 8-bit 4:2:0 only, which is what phones write (NV12 from the hardware lane); the
				// process path takes the rest.
				tileFormat = (AVPixelFormat)tile->format;
				if (tileFormat != AVPixelFormat.AV_PIX_FMT_YUVJ420P && tileFormat != AVPixelFormat.AV_PIX_FMT_YUV420P && tileFormat != AVPixelFormat.AV_PIX_FMT_NV12)
					throw new FFInvalidExitCodeException($"Unsupported tile pixel format {ffmpeg.av_get_pix_fmt_name(tileFormat)}.");
				// The canvas holds the decoded values unchanged, labelled with their range: iPhone
				// tiles are full range, yuvj420p from the CPU decoder and NV12 tagged "pc" from the
				// hardware one, so both lanes hand the AI frame conversion the same picture.
				bool fullRange = tileFormat == AVPixelFormat.AV_PIX_FMT_YUVJ420P || tile->color_range == AVColorRange.AVCOL_RANGE_JPEG;
				PixelFormat = fullRange ? AVPixelFormat.AV_PIX_FMT_YUVJ420P : AVPixelFormat.AV_PIX_FMT_YUV420P;
				canvas = ffmpeg.av_frame_alloc();
				if (canvas == null)
					throw new FFInvalidExitCodeException("Failed to allocate the canvas.");
				canvas->format = (int)PixelFormat;
				canvas->width = Width;
				canvas->height = Height;
				ffmpeg.av_frame_get_buffer(canvas, 0).ThrowExceptionIfError();
				// Black where no tile lands (grids normally cover their whole coded area).
				new Span<byte>(canvas->data[0], canvas->linesize[0] * Height).Clear();
				int chromaRows = (Height + 1) / 2;
				new Span<byte>(canvas->data[1], canvas->linesize[1] * chromaRows).Fill(128);
				new Span<byte>(canvas->data[2], canvas->linesize[2] * chromaRows).Fill(128);
				if (factor == 1 && tileFormat != AVPixelFormat.AV_PIX_FMT_NV12)
					return; // plain plane copies
				// Area averaging by a whole factor: every canvas pixel is the mean of a square of
				// tile pixels, so the tiles meet without seams. Scaling into the tile's own range
				// family (yuvj420p to yuvj420p, NV12 to yuv420p) leaves the values' range alone.
				AVPixelFormat target = tileFormat == AVPixelFormat.AV_PIX_FMT_YUVJ420P ? AVPixelFormat.AV_PIX_FMT_YUVJ420P : AVPixelFormat.AV_PIX_FMT_YUV420P;
				scaler = ffmpeg.sws_getContext(tileWidth, tileHeight, tileFormat,
					tileWidth / factor, tileHeight / factor, target, (int)SwsFlags.SWS_AREA, null, null, null);
				if (scaler == null)
					throw new FFInvalidExitCodeException("Could not initialize the tile scaler.");
			}

			/// <summary>A frame header over part of the canvas (x and y even); valid until Dispose.</summary>
			public AVFrame View(int x, int y, int width, int height) {
				if (canvas == null)
					throw new FFInvalidExitCodeException("No tile was decoded.");
				AVFrame view = *canvas;
				view.width = width;
				view.height = height;
				view.data[0] = canvas->data[0] + y * canvas->linesize[0] + x;
				view.data[1] = canvas->data[1] + y / 2 * canvas->linesize[1] + x / 2;
				view.data[2] = canvas->data[2] + y / 2 * canvas->linesize[2] + x / 2;
				return view;
			}

			public void Dispose() {
				if (scaler != null) {
					ffmpeg.sws_freeContext(scaler);
					scaler = null;
				}
				if (downloaded != null) {
					AVFrame* frame = downloaded;
					ffmpeg.av_frame_free(&frame);
					downloaded = null;
				}
				while (inFlight.Count > 0) {
					AVFrame* held = (AVFrame*)inFlight.Dequeue();
					ffmpeg.av_frame_free(&held);
				}
				if (canvas != null) {
					AVFrame* frame = canvas;
					ffmpeg.av_frame_free(&frame);
					canvas = null;
				}
			}
		}
	}
}
