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

using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using HEI.Core.Utils;

namespace HEI.Core.FFTools.FFmpegNative {
	/// <summary>
	/// Videos whose bytes are on another PC (Heiward's scan of another PC over the Estate): the scan sees each one as a
	/// file of its own size and dates, with nothing in it yet, and its decoder reads the parts it needs through
	/// <see cref="Open"/>, a stream that fetches them from the other PC as they're asked for. A video's header and the frame
	/// sampled are all that's fetched. ffprobe (a process, which can't read through a stream) is answered by
	/// <see cref="Probe"/> instead, and the FFmpeg process isn't tried after a failed decode.
	/// </summary>
	public static class RemoteMedia {
		static Func<string, bool>? covers;
		static Func<string, Stream?>? open;

		/// <summary>From here on, the files <paramref name="isRemote"/> says are read through <paramref name="opener"/>.</summary>
		public static void Use(Func<string, bool> isRemote, Func<string, Stream?> opener) {
			covers = isRemote;
			open = opener;
		}

		/// <summary>Back to reading every file from the disk.</summary>
		public static void Stop() {
			covers = null;
			open = null;
		}

		/// <summary>Whether <paramref name="path"/> is read through <see cref="Open"/>.</summary>
		public static bool Handles(string path) => covers?.Invoke(path) == true;

		/// <summary>A stream of <paramref name="path"/>'s bytes, or null when it isn't one of them (or can't be had now).</summary>
		public static Stream? Open(string path) => Handles(path) ? open?.Invoke(path) : null;

		/// <summary>
		/// What ffprobe would say of a remote video (<see cref="FFProbeEngine.GetMediaInfo(string, bool, out bool)"/>), from
		/// its header read through <see cref="Open"/>: null when it isn't a video FFmpeg reads, or it couldn't be fetched.
		/// <paramref name="noMedia"/>: it was read, and holds nothing FFmpeg can use.
		/// </summary>
		public static unsafe MediaInfo? Probe(string path, out bool noMedia) {
			noMedia = false;
			using Stream? stream = Open(path);
			if (stream == null) return null;
			using var io = new StreamIo(stream);
			AVFormatContext* format = ffmpeg.avformat_alloc_context();
			if (format == null) return null;
			io.Attach(format);
			try {
				if (ffmpeg.avformat_open_input(&format, null, null, null) < 0) {
					noMedia = !io.Failed;
					return null;
				}
				if (ffmpeg.avformat_find_stream_info(format, null) < 0) {
					noMedia = !io.Failed;
					return null;
				}
				return Describe(format);
			}
			catch (Exception e) {
				Logger.Instance.Warn($"Failed to retrieve media info from: {path}: {e.Message}");
				return null;
			}
			finally {
				if (format != null) ffmpeg.avformat_close_input(&format);
			}
		}

		/// <summary>An open file's streams and duration, as <see cref="FFProbeJsonReader"/> reads ffprobe's JSON of them.</summary>
		internal static unsafe MediaInfo Describe(AVFormatContext* format) {
			var info = new MediaInfo { Streams = new MediaInfo.StreamInfo[format->nb_streams] };
			if (format->duration > 0)
				info.Duration = TimeSpan.FromSeconds(format->duration / (double)ffmpeg.AV_TIME_BASE).TrimMiliseconds();
			bool foundBitRate = false;
			for (int i = 0; i < format->nb_streams; i++) {
				AVStream* s = format->streams[i];
				AVCodecParameters* p = s->codecpar;
				var stream = new MediaInfo.StreamInfo {
					Index = s->index.ToString(),
					CodecName = ffmpeg.avcodec_get_name(p->codec_id) ?? "",
					CodecType = ffmpeg.av_get_media_type_string(p->codec_type) ?? "",
					Width = p->width,
					Height = p->height,
					Channels = p->ch_layout.nb_channels,
					IsAttachedPicture = (s->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0,
					Language = Tag(s->metadata, "language") is string language ? FFProbeJsonReader.NormalizeLanguageTag(language) : string.Empty,
				};
				AVCodecDescriptor* descriptor = ffmpeg.avcodec_descriptor_get(p->codec_id);
				stream.CodecLongName = descriptor != null ? Marshal.PtrToStringUTF8((IntPtr)descriptor->long_name) ?? "" : "";
				if (p->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
					stream.PixelFormat = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)p->format) ?? "";
				if (p->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO) {
					stream.SampleRate = p->sample_rate;
					stream.ChannelLayout = ChannelLayout(&p->ch_layout);
				}
				if (p->bit_rate > 0) {
					foundBitRate = true;
					stream.BitRate = p->bit_rate;
				}
				AVRational rate = s->r_frame_rate;
				if (rate.den != 0 || rate.num != 0)
					stream.FrameRate = rate.num > 0 && rate.den > 0 ? rate.num / (float)rate.den : -1f;
				stream.HdrFormat = HdrFormat(p);
				info.Streams[i] = stream;
			}
			if (!foundBitRate && info.Streams.Length > 0 && format->bit_rate > 0)
				info.Streams[0].BitRate = format->bit_rate;
			return info;
		}

		static unsafe string? Tag(AVDictionary* tags, string key) {
			AVDictionaryEntry* entry = ffmpeg.av_dict_get(tags, key, null, 0);
			return entry == null ? null : Marshal.PtrToStringUTF8((IntPtr)entry->value);
		}

		static unsafe string ChannelLayout(AVChannelLayout* layout) {
			const int Size = 128;
			byte* text = stackalloc byte[Size];
			return ffmpeg.av_channel_layout_describe(layout, text, Size) > 0 ? Marshal.PtrToStringUTF8((IntPtr)text) ?? "" : "";
		}

		/// <summary>As ffprobe's color_transfer and side data say it (FFProbeJsonReader's ComputeHdrFormat).</summary>
		static unsafe string HdrFormat(AVCodecParameters* p) {
			if (p->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67) return "HLG";
			if (p->color_trc != AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084) return string.Empty;
			for (int i = 0; i < p->nb_coded_side_data; i++) {
				AVPacketSideDataType type = p->coded_side_data[i].type;
				if (type == AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF) return "Dolby Vision";
				if (type == AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS) return "HDR10+";
			}
			return "HDR10";
		}
	}

	/// <summary>
	/// A stream as FFmpeg's I/O (an AVIOContext of our own, AVFMT_FLAG_CUSTOM_IO): its reads and seeks call the stream's.
	/// Attach it to a format context before avformat_open_input; dispose it after avformat_close_input.
	/// </summary>
	internal sealed unsafe class StreamIo : IDisposable {
		const int BufferSize = 64 * 1024;
		readonly Stream stream;
		readonly avio_alloc_context_read_packet read;
		readonly avio_alloc_context_seek seek;
		AVIOContext* io;
		readonly byte[] scratch = new byte[BufferSize];

		/// <summary>A read or seek failed (the other PC didn't answer): what FFmpeg says of the file then says nothing of it.</summary>
		public bool Failed { get; private set; }

		public StreamIo(Stream stream) {
			this.stream = stream;
			// Kept in fields: FFmpeg holds only their function pointers, which must outlive it.
			read = Read;
			seek = Seek;
			byte* buffer = (byte*)ffmpeg.av_malloc(BufferSize);
			if (buffer == null) throw new OutOfMemoryException("av_malloc");
			io = ffmpeg.avio_alloc_context(buffer, BufferSize, 0, null, read, default, seek);
			if (io == null) {
				ffmpeg.av_free(buffer);
				throw new OutOfMemoryException("avio_alloc_context");
			}
		}

		public void Attach(AVFormatContext* format) {
			format->pb = io;
			format->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
		}

		int Read(void* opaque, byte* buf, int size) {
			try {
				int n = stream.Read(scratch, 0, Math.Min(size, scratch.Length));
				if (n <= 0) return ffmpeg.AVERROR_EOF;
				new ReadOnlySpan<byte>(scratch, 0, n).CopyTo(new Span<byte>(buf, n));
				return n;
			}
			catch (Exception) {
				Failed = true;
				return ffmpeg.AVERROR(5); // EIO
			}
		}

		long Seek(void* opaque, long offset, int whence) {
			try {
				if ((whence & ffmpeg.AVSEEK_SIZE) != 0) return stream.Length;
				SeekOrigin origin = (whence & 3) switch { 1 => SeekOrigin.Current, 2 => SeekOrigin.End, _ => SeekOrigin.Begin };
				return stream.Seek(offset, origin);
			}
			catch (Exception) {
				Failed = true;
				return -1;
			}
		}

		public void Dispose() {
			if (io != null) {
				// FFmpeg may have swapped the buffer for one of its own: free whichever it holds.
				ffmpeg.av_freep(&io->buffer);
				AVIOContext* context = io;
				ffmpeg.avio_context_free(&context);
				io = null;
			}
			stream.Dispose();
		}
	}
}
