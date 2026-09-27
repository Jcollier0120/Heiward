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

namespace VDF.Core.Utils {
	/// <summary>
	/// Decodes photos with the Windows Imaging Component instead of an FFmpeg process: the 32×32
	/// gray frame and the 224×224 RGB embedding frame both come out of ONE decode, in-process.
	/// <list type="bullet">
	/// <item>Faster: JPEG (and other codecs that implement IWICBitmapSourceTransform) decode
	/// straight to a reduced size — the IDCT and colour conversion never touch the full 12 MP.
	/// Measured on a Snapdragon X2: ~15 ms per 12 MP JPEG, against ~42 ms for a full decode
	/// and ~150 ms for an FFmpeg process per photo.</item>
	/// <item>Native everywhere Windows runs, including ARM64 (where the FFmpeg builds VDF
	/// downloads crash on load on Snapdragon X2), and it reads HEIC/HEIF, WebP, AVIF and camera
	/// RAW through the codecs Windows already has.</item>
	/// </list>
	/// Raw vtable calls (no COM interop marshalling), so it stays Native-AOT safe. Any failure
	/// returns false and the caller falls back to FFmpeg.
	/// </summary>
	internal static unsafe class WicImageDecoder {
		const int GrayOut = GrayBytesUtils.Side;
		const int RgbOut = AI.OnnxEmbedder.InputSide;

		static readonly Guid CLSID_WICImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
		static readonly Guid IID_IWICImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
		static readonly Guid IID_IWICBitmapSourceTransform = new("3b16811b-6a43-4ec9-b713-3d5a0c13b940");
		static readonly Guid GUID_WICPixelFormat24bppBGR = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
		static readonly Guid GUID_WICPixelFormat8bppGray = new("6fddc324-4e03-4bfe-b185-3d77768dc908");
		static readonly Guid GUID_ContainerFormatHeif = new("e1e62521-6787-405b-a339-500715b5763f");

		const uint GENERIC_READ = 0x80000000;
		const int WICDecodeMetadataCacheOnDemand = 0;
		const int WICBitmapInterpolationModeFant = 3;
		const int WICBitmapDitherTypeNone = 0;
		const int WICBitmapPaletteTypeCustom = 0;
		const ushort VT_UI2 = 18;

		// vtable slots (IUnknown = 0..2)
		const int Factory_CreateDecoderFromFilename = 3, Factory_CreateFormatConverter = 10, Factory_CreateBitmapScaler = 11,
			Factory_CreateBitmapFromMemory = 20;
		const int Decoder_GetContainerFormat = 5, Decoder_GetFrame = 13;
		const int Source_GetSize = 3, Source_CopyPixels = 7;
		const int Frame_GetMetadataQueryReader = 8;
		const int Transform_CopyPixels = 3, Transform_GetClosestSize = 4;
		const int Query_GetMetadataByName = 5;
		const int Scaler_Initialize = 8, Converter_Initialize = 8;

		[ThreadStatic] static nint threadFactory;
		[ThreadStatic] static string? lastFailure;

		/// <summary>Why the last <see cref="TryDecode"/> on this thread returned false (stage and HRESULT), for logs.</summary>
		internal static string? LastFailure => lastFailure;

		static bool Fail(string stage, int hr = 0) {
			lastFailure = hr == 0 ? stage : $"{stage} failed, HRESULT 0x{hr:X8}";
			return false;
		}

		[DllImport("ole32.dll")] static extern int CoInitializeEx(nint reserved, uint coInit);
		[DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint ppv);
		[DllImport("ole32.dll")] static extern int PropVariantClear(void* pvar);
		[DllImport("ole32.dll")] static extern int CreateStreamOnHGlobal(nint hGlobal, int deleteOnRelease, out nint stream);
		[DllImport("ole32.dll")] static extern int GetHGlobalFromStream(nint stream, out nint hGlobal);
		[DllImport("kernel32.dll")] static extern nint GlobalLock(nint hMem);
		[DllImport("kernel32.dll")] static extern int GlobalUnlock(nint hMem);

		static readonly Guid GUID_ContainerFormatJpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
		const int Factory_CreateEncoder = 8, Factory_CreateBitmapFlipRotator = 13;
		const int Encoder_Initialize = 3, Encoder_CreateNewFrame = 10, Encoder_Commit = 11;
		const int FrameEncode_Initialize = 3, FrameEncode_SetSize = 4, FrameEncode_SetPixelFormat = 6, FrameEncode_WriteSource = 11, FrameEncode_Commit = 12;
		const int FlipRotator_Initialize = 8, Stream_Seek = 5;
		const int WICBitmapEncoderNoCache = 2;

		/// <summary>
		/// A display thumbnail (JPEG, longest side <paramref name="maxSide"/>, upright), for the review
		/// page. Same reduced-size decode as <see cref="TryDecode"/>, so a 12 MP photo costs ~15 ms.
		/// </summary>
		internal static bool TryThumbnailJpeg(string path, int maxSide, out byte[]? jpeg) {
			jpeg = null;
			if (!IsAvailable) return false;
			nint factory = GetFactory();
			if (factory == 0) return false;
			nint decoder = 0, frame = 0, bitmap = 0, scaler = 0, rotator = 0, stream = 0, encoder = 0, frameEncode = 0, props = 0;
			try {
				fixed (char* p = path)
					if (Call(factory, Factory_CreateDecoderFromFilename, (nint)p, 0, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder) < 0)
						return false;
				if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(decoder, Decoder_GetFrame))(decoder, 0, &frame) < 0)
					return false;
				uint w, h;
				if (((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(frame, Source_GetSize))(frame, &w, &h) < 0 || w == 0 || h == 0)
					return false;
				Orientation orientation = IsHeif(decoder) ? Orientation.None : ReadOrientation(frame);
				if (!TryReducedDecode(frame, w, h, out byte[] reduced, out uint rw, out uint rh))
					return false;
				Guid bgr = GUID_WICPixelFormat24bppBGR;
				fixed (byte* pr = reduced)
					if (((delegate* unmanaged[Stdcall]<nint, uint, uint, Guid*, uint, uint, byte*, nint*, int>)Slot(factory, Factory_CreateBitmapFromMemory))(
							factory, rw, rh, &bgr, rw * 3, (uint)reduced.Length, pr, &bitmap) < 0)
						return false;
				double s = Math.Min(1.0, maxSide / (double)Math.Max(rw, rh));
				uint tw = Math.Max(1, (uint)Math.Round(rw * s)), th = Math.Max(1, (uint)Math.Round(rh * s));
				if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, Factory_CreateBitmapScaler))(factory, &scaler) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, int, int>)Slot(scaler, Scaler_Initialize))(scaler, bitmap, tw, th, WICBitmapInterpolationModeFant) < 0)
					return false;
				nint source = scaler;
				int transform = orientation switch {
					Orientation.FlipH => 8, Orientation.Rotate180 => 2, Orientation.FlipV => 16, Orientation.Transpose => 1 | 8,
					Orientation.Rotate90 => 1, Orientation.Transverse => 3 | 8, Orientation.Rotate270 => 3, _ => 0
				};
				if (transform != 0 &&
					((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, Factory_CreateBitmapFlipRotator))(factory, &rotator) >= 0 &&
					((delegate* unmanaged[Stdcall]<nint, nint, int, int>)Slot(rotator, FlipRotator_Initialize))(rotator, scaler, transform) >= 0) {
					source = rotator;
					if ((transform & 1) != 0) (tw, th) = (th, tw);
				}

				if (CreateStreamOnHGlobal(0, 1, out stream) < 0)
					return false;
				Guid jpegFormat = GUID_ContainerFormatJpeg;
				if (((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)Slot(factory, Factory_CreateEncoder))(factory, &jpegFormat, null, &encoder) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, nint, int, int>)Slot(encoder, Encoder_Initialize))(encoder, stream, WICBitmapEncoderNoCache) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, nint*, nint*, int>)Slot(encoder, Encoder_CreateNewFrame))(encoder, &frameEncode, &props) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(frameEncode, FrameEncode_Initialize))(frameEncode, props) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(frameEncode, FrameEncode_SetSize))(frameEncode, tw, th) < 0)
					return false;
				Guid pixelFormat = GUID_WICPixelFormat24bppBGR;
				if (((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(frameEncode, FrameEncode_SetPixelFormat))(frameEncode, &pixelFormat) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, nint, void*, int>)Slot(frameEncode, FrameEncode_WriteSource))(frameEncode, source, null) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, int>)Slot(frameEncode, FrameEncode_Commit))(frameEncode) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, int>)Slot(encoder, Encoder_Commit))(encoder) < 0)
					return false;

				ulong length;
				if (((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)Slot(stream, Stream_Seek))(stream, 0, 1 /* STREAM_SEEK_CUR */, &length) < 0 ||
					GetHGlobalFromStream(stream, out nint hGlobal) < 0)
					return false;
				nint data = GlobalLock(hGlobal);
				if (data == 0) return false;
				try {
					jpeg = new byte[checked((int)length)];
					Marshal.Copy(data, jpeg, 0, jpeg.Length);
				}
				finally { GlobalUnlock(hGlobal); }
				return true;
			}
			catch (Exception e) when (e is not OutOfMemoryException) {
				return false;
			}
			finally {
				Release(props);
				Release(frameEncode);
				Release(encoder);
				Release(stream);
				Release(rotator);
				Release(scaler);
				Release(bitmap);
				Release(frame);
				Release(decoder);
			}
		}

		/// <summary>Whether this platform has WIC. The decoder is still per-file fallible (unknown codec, corrupt file).</summary>
		internal static bool IsAvailable => OperatingSystem.IsWindows();

		/// <summary>
		/// The image's displayed size from its header, without decoding pixels. For tiled HEIC this is
		/// the whole picture (after clap crop and rotation), where FFprobe's largest stream is one tile.
		/// </summary>
		internal static bool TryGetSize(string path, out int width, out int height) {
			width = height = 0;
			if (!IsAvailable) return false;
			nint factory = GetFactory();
			if (factory == 0) return false;
			nint decoder = 0, frame = 0;
			try {
				fixed (char* p = path)
					if (Call(factory, Factory_CreateDecoderFromFilename, (nint)p, 0, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder) < 0)
						return false;
				if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(decoder, Decoder_GetFrame))(decoder, 0, &frame) < 0)
					return false;
				uint w, h;
				if (((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(frame, Source_GetSize))(frame, &w, &h) < 0 || w == 0 || h == 0)
					return false;
				width = (int)w;
				height = (int)h;
				return true;
			}
			catch { return false; }
			finally {
				Release(frame);
				Release(decoder);
			}
		}

		/// <summary>
		/// Decodes <paramref name="path"/> into VDF's 32×32 gray frame and, when
		/// <paramref name="wantRgb"/>, the 224×224 RGB24 embedding frame (both squashed to square
		/// like the FFmpeg path, orientation applied). <paramref name="width"/>/<paramref name="height"/>
		/// are the stored image dimensions.
		/// </summary>
		internal static bool TryDecode(string path, bool wantRgb, out byte[]? gray, out byte[]? rgb, out int width, out int height) {
			gray = null; rgb = null; width = height = 0;
			lastFailure = null;
			if (!IsAvailable) return Fail("not Windows");
			nint factory = GetFactory();
			if (factory == 0) return Fail("no WIC factory");
			nint decoder = 0, frame = 0, bitmap = 0;
			try {
				int hr;
				fixed (char* p = path)
					if ((hr = Call(factory, Factory_CreateDecoderFromFilename, (nint)p, 0, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder)) < 0)
						return Fail("no codec (CreateDecoderFromFilename)", hr);
				if ((hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(decoder, Decoder_GetFrame))(decoder, 0, &frame)) < 0)
					return Fail("GetFrame", hr);
				uint w, h;
				if ((hr = ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(frame, Source_GetSize))(frame, &w, &h)) < 0 || w == 0 || h == 0)
					return Fail("GetSize", hr);
				width = (int)w;
				height = (int)h;
				Orientation orientation = IsHeif(decoder) ? Orientation.None : ReadOrientation(frame); // the HEIF codec applies irot/imir itself

				// Reduced-size BGR copy, ≥ RgbOut on both sides, then two Fant (area-average) passes from it.
				if (!TryReducedDecode(frame, w, h, out byte[] reduced, out uint rw, out uint rh))
					return Fail("pixel decode");
				fixed (byte* pr = reduced) {
					Guid bgr = GUID_WICPixelFormat24bppBGR;
					if (((delegate* unmanaged[Stdcall]<nint, uint, uint, Guid*, uint, uint, byte*, nint*, int>)Slot(factory, Factory_CreateBitmapFromMemory))(
							factory, rw, rh, &bgr, rw * 3, (uint)reduced.Length, pr, &bitmap) < 0)
						return Fail("CreateBitmapFromMemory");
				}
				byte[]? g = Resample(factory, bitmap, GrayOut, GUID_WICPixelFormat8bppGray, 1);
				if (g == null) return Fail("gray resample");
				gray = Orient(g, GrayOut, 1, orientation);
				if (wantRgb) {
					byte[]? bgrOut = Resample(factory, bitmap, RgbOut, GUID_WICPixelFormat24bppBGR, 3);
					if (bgrOut != null) {
						for (int i = 0; i < bgrOut.Length; i += 3)
							(bgrOut[i], bgrOut[i + 2]) = (bgrOut[i + 2], bgrOut[i]); // BGR -> RGB
						rgb = Orient(bgrOut, RgbOut, 3, orientation);
					}
				}
				return true;
			}
			catch (Exception e) when (e is not OutOfMemoryException) {
				return Fail($"{e.GetType().Name}: {e.Message}");
			}
			finally {
				Release(bitmap);
				Release(frame);
				Release(decoder);
			}
		}

		/// <summary>
		/// The frame as 24bpp BGR at the smallest size the codec can produce natively that is still
		/// ≥ <see cref="RgbOut"/> on both sides (JPEG: 1/2, 1/4, 1/8 scaling inside the IDCT).
		/// Codecs without IWICBitmapSourceTransform go through a Fant scaler on the full decode.
		/// </summary>
		static bool TryReducedDecode(nint frame, uint w, uint h, out byte[] buffer, out uint rw, out uint rh) {
			double s = Math.Min(1.0, Math.Max(RgbOut / (double)w, RgbOut / (double)h) * 2); // 2× headroom for the area filter
			uint tw = Math.Max(1, (uint)Math.Ceiling(w * s)), th = Math.Max(1, (uint)Math.Ceiling(h * s));
			nint transform = 0;
			Guid iid = IID_IWICBitmapSourceTransform;
			if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(frame, 0))(frame, &iid, &transform) >= 0 && transform != 0) {
				try {
					uint cw = tw, ch = th;
					if (((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(transform, Transform_GetClosestSize))(transform, &cw, &ch) >= 0) {
						if (cw < RgbOut || ch < RgbOut) { cw = w; ch = h; } // never go below the output size
						buffer = new byte[checked((int)(cw * 3 * ch))];
						Guid fmt = GUID_WICPixelFormat24bppBGR;
						fixed (byte* pb = buffer)
							if (((delegate* unmanaged[Stdcall]<nint, void*, uint, uint, Guid*, int, uint, uint, byte*, int>)Slot(transform, Transform_CopyPixels))(
									transform, null, cw, ch, &fmt, 0, cw * 3, (uint)buffer.Length, pb) >= 0 && fmt == GUID_WICPixelFormat24bppBGR) {
								rw = cw; rh = ch;
								return true;
							}
					}
				}
				finally { Release(transform); }
			}
			// No native scaling: scale the full decode.
			byte[]? scaled = ScaleSource(GetFactory(), frame, tw, th, GUID_WICPixelFormat24bppBGR, 3);
			buffer = scaled ?? Array.Empty<byte>();
			rw = tw; rh = th;
			return scaled != null;
		}

		static byte[]? Resample(nint factory, nint source, int side, Guid format, int bytesPerPixel) =>
			ScaleSource(factory, source, (uint)side, (uint)side, format, bytesPerPixel);

		/// <summary>source → Fant scaler (w×h) → format converter → pixels.</summary>
		static byte[]? ScaleSource(nint factory, nint source, uint w, uint h, Guid format, int bytesPerPixel) {
			nint scaler = 0, converter = 0;
			try {
				if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, Factory_CreateBitmapScaler))(factory, &scaler) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, int, int>)Slot(scaler, Scaler_Initialize))(scaler, source, w, h, WICBitmapInterpolationModeFant) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, Factory_CreateFormatConverter))(factory, &converter) < 0 ||
					((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int, nint, double, int, int>)Slot(converter, Converter_Initialize))(
						converter, scaler, &format, WICBitmapDitherTypeNone, 0, 0.0, WICBitmapPaletteTypeCustom) < 0)
					return null;
				uint stride = w * (uint)bytesPerPixel;
				var pixels = new byte[checked((int)(stride * h))];
				fixed (byte* pp = pixels)
					if (((delegate* unmanaged[Stdcall]<nint, void*, uint, uint, byte*, int>)Slot(converter, Source_CopyPixels))(converter, null, stride, (uint)pixels.Length, pp) < 0)
						return null;
				return pixels;
			}
			finally {
				Release(converter);
				Release(scaler);
			}
		}

		enum Orientation : ushort { None = 1, FlipH = 2, Rotate180 = 3, FlipV = 4, Transpose = 5, Rotate90 = 6, Transverse = 7, Rotate270 = 8 }

		/// <summary>EXIF orientation (System.Photo.Orientation), 1 when absent or unreadable.</summary>
		static Orientation ReadOrientation(nint frame) {
			nint reader = 0;
			try {
				if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(frame, Frame_GetMetadataQueryReader))(frame, &reader) < 0 || reader == 0)
					return Orientation.None;
				byte* pv = stackalloc byte[24]; // PROPVARIANT
				new Span<byte>(pv, 24).Clear();
				try {
					fixed (char* name = "System.Photo.Orientation")
						if (((delegate* unmanaged[Stdcall]<nint, char*, byte*, int>)Slot(reader, Query_GetMetadataByName))(reader, name, pv) < 0)
							return Orientation.None;
					ushort vt = *(ushort*)pv;
					ushort value = *(ushort*)(pv + 8);
					return vt == VT_UI2 && value is >= 1 and <= 8 ? (Orientation)value : Orientation.None;
				}
				finally { PropVariantClear(pv); }
			}
			finally { Release(reader); }
		}

		static bool IsHeif(nint decoder) {
			Guid container;
			return ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(decoder, Decoder_GetContainerFormat))(decoder, &container) >= 0 &&
				container == GUID_ContainerFormatHeif;
		}

		/// <summary>
		/// Applies EXIF orientation to a square frame. The outputs are squashed to squares, and
		/// squashing commutes with 90° turns, so turning the small output equals turning the photo.
		/// </summary>
		static byte[] Orient(byte[] src, int side, int bpp, Orientation o) {
			if (o == Orientation.None) return src;
			var dst = new byte[src.Length];
			int last = side - 1;
			for (int y = 0; y < side; y++)
				for (int x = 0; x < side; x++) {
					(int sx, int sy) = o switch {
						Orientation.FlipH => (last - x, y),
						Orientation.Rotate180 => (last - x, last - y),
						Orientation.FlipV => (x, last - y),
						Orientation.Transpose => (y, x),
						Orientation.Rotate90 => (y, last - x),
						Orientation.Transverse => (last - y, last - x),
						Orientation.Rotate270 => (last - y, x),
						_ => (x, y)
					};
					Buffer.BlockCopy(src, (sy * side + sx) * bpp, dst, (y * side + x) * bpp, bpp);
				}
			return dst;
		}

		static nint GetFactory() {
			if (threadFactory != 0) return threadFactory;
			CoInitializeEx(0, 0 /* COINIT_MULTITHREADED; S_FALSE or RPC_E_CHANGED_MODE are fine */);
			if (CoCreateInstance(CLSID_WICImagingFactory, 0, 1 /* CLSCTX_INPROC_SERVER */, IID_IWICImagingFactory, out nint f) < 0)
				return 0;
			return threadFactory = f; // one per thread, kept for the thread's lifetime (pool threads are reused)
		}

		static void* Slot(nint obj, int index) => (*(void***)obj)[index];

		static int Call(nint obj, int slot, nint a, nint b, uint c, int d, nint* result) =>
			((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, int, nint*, int>)Slot(obj, slot))(obj, a, b, c, d, result);

		static void Release(nint obj) {
			if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);
		}
	}
}
