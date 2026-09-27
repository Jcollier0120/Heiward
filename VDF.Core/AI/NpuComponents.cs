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
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using VDF.Core.Utils;

namespace VDF.Core.AI {
	/// <summary>
	/// Where AI embeddings run. Auto = the NPU when this machine has one and the NPU pack is installed,
	/// else the CPU. Gpu = DirectML (<see cref="GpuComponents"/>), chosen explicitly, never by Auto.
	/// </summary>
	public enum AiDevice { Auto, Cpu, Npu, Gpu }

	/// <summary>
	/// The NPU pack: what AI matching needs to run on a Qualcomm Hexagon NPU (Snapdragon X / X2,
	/// Windows on ARM). Downloaded on demand like the rest of the AI components, never bundled:
	/// <list type="bullet">
	/// <item>Qualcomm's QNN execution provider plugin (NuGet package Qualcomm.ML.OnnxRuntime.QNN,
	/// SHA256-pinned), of which only the win-arm64 HTP files are kept, in <c>{ai}/qnn</c>.</item>
	/// <item>DINOv2-small in FP32 (the same Xenova export VDF's int8 model was quantized from,
	/// SHA256-pinned). The NPU runs it in FP16, which tracks FP32 far closer than the dynamic int8
	/// model does on the CPU: measured cosine to FP32 ≥ 0.997 versus ≥ 0.92.</item>
	/// </list>
	/// The plugin needs ONNX Runtime ≥ 1.24, which is why <see cref="AiComponents.RuntimeVersion"/>
	/// is newer on Windows ARM64. The HTP needs static shapes: the stock export's dynamic
	/// dimensions are pinned with free-dimension overrides (batch <see cref="NpuBatch"/>, 3×224×224)
	/// and the compiled graph is cached, so later sessions start in a fraction of a second.
	/// </summary>
	public static class NpuComponents {
		public const string EpName = "QNNExecutionProvider";
		public const string QnnPackageVersion = "2.6.0";
		const string QnnPackageSha256 = "c2fe66eeaf92a0cb89faef1d4d05c23b939896aeb9dcfa2261d8fe70b54b8103";
		const string QnnPackageUrl = "https://api.nuget.org/v3-flatcontainer/qualcomm.ml.onnxruntime.qnn/2.6.0/qualcomm.ml.onnxruntime.qnn.2.6.0.nupkg";
		const string QnnPackageNativeDir = "runtimes/win-arm64/native/";

		/// <summary>
		/// The HTP backend's files. Genie (LLMs), the GPU backend and the x64 builds in the
		/// package are not needed. V73 = Snapdragon X, V81 = Snapdragon X2.
		/// </summary>
		static readonly string[] QnnFiles = {
			"onnxruntime_providers_qnn.dll", "QnnHtp.dll", "QnnHtpPrepare.dll", "QnnSystem.dll", "QnnHtpNetRunExtensions.dll",
			"QnnHtpV73Stub.dll", "libQnnHtpV73Skel.so", "libqnnhtpv73.cat",
			"QnnHtpV81Stub.dll", "libQnnHtpV81Skel.so", "libqnnhtpv81.cat",
		};

		public const string ModelFileName = "dinov2-small-fp32.onnx";
		const string ModelSha256 = "83141175ec78b4ff9a2bb58a4c7c264ba0054d1c2e122e5a8114b79a8d4179ea";
		const string ModelUrl = "https://huggingface.co/Xenova/dinov2-small/resolve/main/onnx/model.onnx";

		/// <summary>The NPU graph's fixed batch size; <see cref="OnnxEmbedder"/> pads the last chunk.</summary>
		public const int NpuBatch = 8;
		/// <summary>Cache key of NPU embeddings: they are FP16 DINOv2, not VDF's int8 model, so they get their own sidecars.</summary>
		public const string ModelKey = "dinov2s-fp16";

		public static string QnnFolder => Path.Combine(AiComponents.AiFolder, "qnn");
		public static string ModelPath => Path.Combine(AiComponents.AiFolder, ModelFileName);
		/// <summary>Compiled HTP graphs, keyed by the QNN build that compiled them.</summary>
		internal static string ContextCacheFolder => Path.Combine(AiComponents.AiFolder, $"qnn-cache-{QnnPackageVersion}");

		/// <summary>Only Windows on ARM64 has a Hexagon NPU this pack can drive.</summary>
		public static bool IsSupportedPlatform =>
			CoreUtils.IsWindows && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

		public static bool IsInstalled =>
			IsSupportedPlatform && File.Exists(ModelPath) && QnnFiles.All(f => File.Exists(Path.Combine(QnnFolder, f)));

		static readonly object registerLock = new();
		static bool registered;
		static IReadOnlyList<OrtEpDevice>? npuDevices;

		/// <summary>
		/// The NPU devices the QNN plugin reports, registering the plugin with ONNX Runtime on
		/// first use (once per process). Empty when the pack is missing, the platform has no
		/// NPU, or the plugin fails to load — callers then stay on the CPU.
		/// </summary>
		internal static IReadOnlyList<OrtEpDevice> GetNpuDevices() {
			// Tests pin the CPU test embedder: never let a dev machine's NPU pack leak into them.
			if (AiComponents.TestOverrideModelPath != null || !IsInstalled || !AiComponents.IsReady)
				return Array.Empty<OrtEpDevice>();
			lock (registerLock) {
				if (npuDevices != null)
					return npuDevices;
				try {
					AiComponents.EnsureResolverInstalled();
					OrtEnv env = OrtEnv.Instance();
					if (!registered) {
						env.RegisterExecutionProviderLibrary(EpName, Path.Combine(QnnFolder, "onnxruntime_providers_qnn.dll"));
						registered = true;
					}
					npuDevices = env.GetEpDevices()
						.Where(d => d.EpName == EpName && d.HardwareDevice.Type == OrtHardwareDeviceType.NPU)
						.ToList();
				}
				catch (Exception e) {
					Logger.Instance.Info($"NPU unavailable, AI matching stays on the CPU: {e.Message}");
					npuDevices = Array.Empty<OrtEpDevice>();
				}
				return npuDevices;
			}
		}

		/// <summary>
		/// Whether <paramref name="device"/> resolves to the NPU on this machine (without opening a session).
		/// Never for Gpu: probing the NPU loads ONNX Runtime, which would lock out the DirectML build.
		/// </summary>
		public static bool WillUseNpu(AiDevice device) =>
			device is AiDevice.Auto or AiDevice.Npu && GetNpuDevices().Count > 0;

		/// <summary>The device the given setting will run on: "NPU", "GPU" or "CPU" (without opening a session).</summary>
		public static string DeviceFor(AiDevice device) =>
			device == AiDevice.Gpu ? (GpuComponents.IsInstalled ? "GPU" : "CPU") : WillUseNpu(device) ? "NPU" : "CPU";

		/// <summary>The embedding-cache key the given device setting will produce (see <see cref="ModelKey"/>).</summary>
		public static string? CacheKeyFor(AiDevice device) => DeviceFor(device) switch {
			"NPU" => ModelKey,
			"GPU" => GpuComponents.ModelKey,
			_ => null,
		};

		/// <summary>The QNN HTP options: FP16 on the HTP, burst clocks while a scan runs.</summary>
		internal static Dictionary<string, string> ProviderOptions() => new() {
			["backend_path"] = Path.Combine(QnnFolder, "QnnHtp.dll"),
			["htp_performance_mode"] = "burst",
			["enable_htp_fp16_precision"] = "1",
			// File-mapped weights are not supported for this graph; skip the failing first attempt.
			["disable_file_mapped_weights"] = "1",
		};

		/// <summary>Downloads whatever part of the NPU pack is missing. Safe to call when installed.</summary>
		public static async Task DownloadAsync(IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			if (!IsSupportedPlatform)
				throw new PlatformNotSupportedException("The NPU pack needs Windows on ARM64 (Snapdragon X).");
			Directory.CreateDirectory(AiComponents.AiFolder);
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
			var downloads = new List<Func<CancellationToken, Task>>(2);
			if (!QnnFiles.All(f => File.Exists(Path.Combine(QnnFolder, f))))
				downloads.Add(ct => DownloadQnnAsync(http, progress, ct));
			if (!File.Exists(ModelPath))
				downloads.Add(ct => DownloadModelAsync(http, progress, ct));
			await AiComponents.RunDownloadsAsync(downloads, token);
		}

		static async Task DownloadQnnAsync(HttpClient http, IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			string tempRoot = Path.Combine(Path.GetTempPath(), $"VDF.NpuDownload.{Guid.NewGuid():N}");
			Directory.CreateDirectory(tempRoot);
			try {
				string package = Path.Combine(tempRoot, "qnn.nupkg");
				string step = $"Qualcomm QNN {QnnPackageVersion}";
				await DownloadVerifiedAsync(http, new Uri(QnnPackageUrl), package, QnnPackageSha256, step, progress, token);
				Directory.CreateDirectory(QnnFolder);
				using ZipArchive zip = ZipFile.OpenRead(package);
				foreach (string name in QnnFiles) {
					ZipArchiveEntry entry = zip.GetEntry(QnnPackageNativeDir + name)
						?? throw new IOException($"The QNN package has no {QnnPackageNativeDir}{name}.");
					string tmp = Path.Combine(QnnFolder, name + ".part");
					entry.ExtractToFile(tmp, overwrite: true);
					File.Move(tmp, Path.Combine(QnnFolder, name), overwrite: true);
				}
			}
			finally {
				try { Directory.Delete(tempRoot, true); } catch { }
			}
		}

		/// <summary>The FP32 model the NPU and the GPU both run (the GPU pack shares it).</summary>
		internal static Task DownloadModelAsync(HttpClient http, IProgress<AiDownloadProgress>? progress, CancellationToken token) =>
			DownloadVerifiedAsync(http, new Uri(ModelUrl), ModelPath, ModelSha256, "FP32 model", progress, token);

		internal static async Task DownloadVerifiedAsync(HttpClient http, Uri url, string destination, string sha256, string step,
			IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			string tempPath = destination + $".{Guid.NewGuid():N}.download";
			try {
				await DownloadUtils.DownloadFileAsync(http, url, tempPath, step,
					(done, total) => progress?.Report(new AiDownloadProgress(step, done, total)), token);
				string hash;
				await using (FileStream fs = File.OpenRead(tempPath))
					hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, token));
				if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
					throw new IOException($"{step} download failed the integrity check (SHA256 {hash}, expected {sha256}).");
				File.Move(tempPath, destination, overwrite: true);
			}
			finally {
				try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
			}
		}
	}
}
