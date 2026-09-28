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

using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>
	/// The GPU pack, for PCs without an NPU: ONNX Runtime's DirectML build (any DirectX 12 GPU: NVIDIA,
	/// AMD, Intel, Qualcomm Adreno) and DirectML itself, both SHA256-pinned NuGet packages of which only
	/// the native DLLs are kept, in <c>{ai}/gpu</c>, plus the FP32 model the NPU uses. A process loads one
	/// ONNX Runtime, so GPU mode must be chosen before anything touches ONNX Runtime
	/// (<see cref="AiComponents.RuntimeFolderOverride"/>); <see cref="AiDevice.Auto"/> never picks the GPU.
	/// </summary>
	public static class GpuComponents {
		const string OrtDmlVersion = "1.24.4";
		const string OrtDmlSha256 = "57e9f11b73437bef7a309496135d4c1f96b1a8e9ddba60013fa27bfc1d788681";
		const string OrtDmlUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/1.24.4/microsoft.ml.onnxruntime.directml.1.24.4.nupkg";
		public const string DirectMLVersion = "1.15.4";
		const string DirectMLSha256 = "4e7cb7ddce8cf837a7a75dc029209b520ca0101470fcdf275c1f49736a3615b9";
		const string DirectMLUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg";

		/// <summary>Cache key of GPU embeddings: FP32 DINOv2 (see <see cref="UnionEmbeddingStore.PathFor"/>).</summary>
		public const string ModelKey = "dinov2s-fp32";

		public static string GpuFolder => Path.Combine(AiComponents.AiFolder, "gpu");

		/// <summary>DirectML ships for Windows on x64 and ARM64.</summary>
		public static bool IsSupportedPlatform =>
			CoreUtils.IsWindows && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

		static string Rid => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
		static string DmlArch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64-win" : "x64-win";
		static readonly string[] RuntimeFiles = { "onnxruntime.dll", "onnxruntime_providers_shared.dll" };

		public static bool IsInstalled =>
			IsSupportedPlatform && File.Exists(NpuComponents.ModelPath) &&
			RuntimeFiles.Append("DirectML.dll").All(f => File.Exists(Path.Combine(GpuFolder, f)));

		/// <summary>
		/// Makes this process load the DirectML runtime. False when ONNX Runtime is already loaded
		/// (another runtime got there first) or the pack is missing: the caller stays on the CPU.
		/// </summary>
		internal static bool TrySelectRuntime() {
			if (!IsInstalled) return false;
			if (AiComponents.RuntimeLoaded)
				return string.Equals(AiComponents.RuntimeFolderOverride, GpuFolder, StringComparison.OrdinalIgnoreCase);
			AiComponents.RuntimeFolderOverride = GpuFolder;
			return true;
		}

		/// <summary>Downloads whatever part of the GPU pack is missing (~215 MB the first time). Safe to call when installed.</summary>
		public static async Task DownloadAsync(IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			if (!IsSupportedPlatform)
				throw new PlatformNotSupportedException("The GPU pack needs Windows on x64 or ARM64.");
			Directory.CreateDirectory(GpuFolder);
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
			var downloads = new List<Func<CancellationToken, Task>>(3);
			if (!RuntimeFiles.All(f => File.Exists(Path.Combine(GpuFolder, f))))
				downloads.Add(ct => ExtractFromPackageAsync(http, OrtDmlUrl, OrtDmlSha256, $"ONNX Runtime DirectML {OrtDmlVersion}",
					RuntimeFiles.Select(f => ($"runtimes/{Rid}/native/{f}", f)).ToArray(), progress, ct));
			if (!File.Exists(Path.Combine(GpuFolder, "DirectML.dll")))
				downloads.Add(ct => ExtractFromPackageAsync(http, DirectMLUrl, DirectMLSha256, $"DirectML {DirectMLVersion}",
					new[] { ($"bin/{DmlArch}/DirectML.dll", "DirectML.dll") }, progress, ct));
			if (!File.Exists(NpuComponents.ModelPath))
				downloads.Add(ct => NpuComponents.DownloadModelAsync(http, progress, ct));
			await AiComponents.RunDownloadsAsync(downloads, token);
		}

		static async Task ExtractFromPackageAsync(HttpClient http, string url, string sha256, string step,
			(string Entry, string Name)[] files, IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			string tempRoot = Path.Combine(Path.GetTempPath(), $"HEI.GpuDownload.{Guid.NewGuid():N}");
			Directory.CreateDirectory(tempRoot);
			try {
				string package = Path.Combine(tempRoot, "package.nupkg");
				await NpuComponents.DownloadVerifiedAsync(http, new Uri(url), package, sha256, step, progress, token);
				using ZipArchive zip = ZipFile.OpenRead(package);
				foreach ((string entryName, string name) in files) {
					ZipArchiveEntry entry = zip.GetEntry(entryName) ?? throw new IOException($"{step}: the package has no {entryName}.");
					string tmp = Path.Combine(GpuFolder, name + ".part");
					entry.ExtractToFile(tmp, overwrite: true);
					File.Move(tmp, Path.Combine(GpuFolder, name), overwrite: true);
				}
			}
			finally {
				try { Directory.Delete(tempRoot, true); } catch { }
			}
		}
	}
}
