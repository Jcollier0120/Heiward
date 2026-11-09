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

using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>
	/// The text-reading model (<see cref="OcrReader"/>): PaddleOCR's PP-OCRv6 tiny, its detector as published and its
	/// recognizer fine-tuned on screen and document text (Apache-2.0), about 4 MB in all. Downloaded with the AI
	/// components from its public release, each file pinned by size and SHA-256, into {ai}/text-v2 with its license and
	/// notice. It runs on ONNX Runtime, which the AI components bring.
	/// </summary>
	public static class TextComponents {
		public const string ModelVersion = "2";
		const string Release = "https://github.com/Jcollier0120/Manor-releases/releases/download/ocr-model-v" + ModelVersion;

		/// <summary>One file of the model: where it goes in its folder, where it's downloaded from, its size and SHA-256.</summary>
		internal sealed record Pinned(string Name, string Url, long Size, string Sha256);

		internal static readonly Pinned[] Files = {
			new("det.onnx", $"{Release}/det.onnx", 1_744_568, "caa7341cf77f0e379a1900874f2ec7bcea0b9ea90028cb075f8f29745ae3330c"),
			new("rec.onnx", $"{Release}/rec.onnx", 2_273_569, "a34d9770c3452e39a008c4a965cef56312448a66ced5ecba35695897bcd634fe"),
			new("charset.txt", $"{Release}/charset.txt", 298, "ffe49ab5748c825e01a765d3325e945a252067842e839f44e80021385b459ee4"),
			new(Path.Combine("notices", "LICENSE.txt"), $"{Release}/LICENSE.txt", 11_358, "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30"),
			new(Path.Combine("notices", "NOTICE.txt"), $"{Release}/NOTICE.txt", 1_212, "1e5026805ff919b2c41cc08ba986b587eb7c89fa5e60be85578ea3a5b1869723"),
		};

		/// <summary>About how much it downloads, for messages.</summary>
		public static string Description => $"text-reading model {ModelVersion}, ~{Math.Round(Files.Sum(f => f.Size) / 1e6)} MB";

		/// <summary>Tests and the benchmarks read the model from another folder (its files checked like a download's).</summary>
		internal static string? FolderOverride;

		public static string Folder => FolderOverride ?? Path.Combine(AiComponents.AiFolder, "text-v" + ModelVersion);

		/// <summary>Every file is there at its pinned size (its hash was checked when it came).</summary>
		public static bool IsInstalled => Files.All(f => new FileInfo(Path.Combine(Folder, f.Name)) is { Exists: true } file && file.Length == f.Size);

		/// <summary>The files in <paramref name="folder"/> are the pinned ones, hash for hash (a folder the benchmarks point at).</summary>
		internal static bool Verify(string folder, out string? why) {
			foreach (Pinned f in Files) {
				string path = Path.Combine(folder, f.Name);
				if (!File.Exists(path)) { why = $"{f.Name} is missing"; return false; }
				using FileStream fs = File.OpenRead(path);
				string hash = Convert.ToHexString(SHA256.HashData(fs));
				if (fs.Length != f.Size || !hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) { why = $"{f.Name} isn't the pinned file"; return false; }
			}
			why = null;
			return true;
		}

		/// <summary>Downloads whatever file is missing, each checked against its size and SHA-256 before it's put in place.</summary>
		public static async Task DownloadAsync(IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			Directory.CreateDirectory(Path.Combine(Folder, "notices"));
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
			foreach (Pinned f in Files) {
				string path = Path.Combine(Folder, f.Name);
				if (new FileInfo(path) is { Exists: true } have && have.Length == f.Size) continue;
				await NpuComponents.DownloadVerifiedAsync(http, new Uri(f.Url), path, f.Sha256, $"Text-reading model ({Path.GetFileName(f.Name)})", progress, token);
				if (new FileInfo(path).Length != f.Size) {
					File.Delete(path);
					throw new IOException($"The text-reading model's {f.Name} isn't {f.Size:N0} bytes.");
				}
			}
			Logger.Instance.Info($"Text-reading model {ModelVersion} is in {Folder}.");
		}
	}
}
