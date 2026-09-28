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

using System.Security.Cryptography;
using System.Text;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VDF.Agent {
	/// <summary>
	/// JPEG thumbnails for the review page, cached on disk by path + size + modified time (a changed
	/// file gets a new one). Photos through WIC (~15 ms), videos as a frame at 20% through FFmpeg.
	/// </summary>
	static class Thumbnails {
		const int MaxSide = 360;

		public static byte[]? Get(ReportItem item, bool isImage) {
			var fi = new FileInfo(item.Path);
			if (!fi.Exists) return null;
			string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{MaxSide}")));
			string cached = Path.Combine(AgentPaths.Thumbnails, key[..2], key + ".jpg");
			try {
				if (File.Exists(cached)) return File.ReadAllBytes(cached);
			}
			catch (IOException) { }

			// Photos through WIC, except HEIC: Windows' HEIF codec takes one photo at a time, FFmpeg
			// processes run in parallel (the page asks for many thumbnails at once).
			bool heif = FileUtils.IsHeifImageFile(fi.FullName);
			byte[]? jpeg = null;
			if (isImage && !heif && WicImageDecoder.TryThumbnailJpeg(fi.FullName, MaxSide, out byte[]? wic))
				jpeg = wic;
			if (jpeg == null) {
				var at = TimeSpan.FromSeconds(isImage ? 0 : Math.Max(0, item.DurationSec * 0.2));
				jpeg = FfmpegEngine.ExtractThumbnailJpeg(fi.FullName, at, MaxSide);
			}
			if (jpeg == null && heif && WicImageDecoder.TryThumbnailJpeg(fi.FullName, MaxSide, out byte[]? heifWic))
				jpeg = heifWic;
			if (jpeg == null) return null;
			try {
				Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
				File.WriteAllBytes(cached, jpeg);
			}
			catch (IOException) { }
			return jpeg;
		}
	}
}
