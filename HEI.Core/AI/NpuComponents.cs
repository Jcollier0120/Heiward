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
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>
	/// Where AI embeddings run. Auto = the NPU when this machine has one and the NPU pack is installed,
	/// else the CPU. Gpu = DirectML (<see cref="GpuComponents"/>), chosen explicitly, never by Auto.
	/// </summary>
	public enum AiDevice { Auto, Cpu, Npu, Gpu }

	/// <summary>
	/// The NPU pack: what AI matching needs to run on this PC's NPU, downloaded on demand like the rest of
	/// the AI components, never bundled. Which pack depends on who made the NPU (<see cref="NpuHardware"/>):
	/// <list type="bullet">
	/// <item>Qualcomm Hexagon (Snapdragon X / X2, Windows on ARM64): Qualcomm's QNN plugin from NuGet.</item>
	/// <item>Intel AI Boost (Core Ultra, Windows x64): Intel's OpenVINO plugin from NuGet, OpenVINO included.</item>
	/// <item>AMD Ryzen AI (Windows x64, Windows 11 24H2+): AMD's Vitis AI plugin, which Windows ML downloads
	/// and keeps updated (<see cref="WindowsMlCatalog"/>).</item>
	/// </list>
	/// All of them run DINOv2-small in FP32 (the same Xenova export VDF's int8 model was quantized from,
	/// SHA256-pinned) at lower precision on the NPU (FP16, or BF16 on AMD), which tracks FP32 far closer
	/// than the dynamic int8 model does on the CPU: measured cosine to FP32 ≥ 0.997 versus ≥ 0.92 (QNN).
	/// NPUs need static shapes: the export's dynamic dimensions are pinned with free-dimension overrides
	/// (batch <see cref="NpuBatch"/>, 3×224×224) and the compiled graph is cached. Each pack's vectors get
	/// their own embedding sidecars (<see cref="ModelKey"/>), since precisions differ slightly.
	/// </summary>
	public static class NpuComponents {
		public const string QnnPackageVersion = QnnPack.PackageVersion;
		public const string ModelFileName = "dinov2-small-fp32.onnx";
		const string ModelSha256 = "83141175ec78b4ff9a2bb58a4c7c264ba0054d1c2e122e5a8114b79a8d4179ea";
		const string ModelUrl = "https://huggingface.co/Xenova/dinov2-small/resolve/main/onnx/model.onnx";

		/// <summary>
		/// The NPU graph's fixed batch size (<see cref="NpuPack.Batch"/>); <see cref="OnnxEmbedder"/> pads the
		/// last chunk. HEI_NPU_BATCH overrides it, to measure others.
		/// </summary>
		public static int NpuBatch =>
			int.TryParse(Environment.GetEnvironmentVariable("HEI_NPU_BATCH"), out int batch) && batch is > 0 and <= 64 ? batch : Pack?.Batch ?? 8;

		public static string ModelPath => Path.Combine(AiComponents.AiFolder, ModelFileName);

		/// <summary>Run options for the NPU at either pace (<see cref="Pace"/>), or null when the pack has none.</summary>
		internal static Dictionary<string, string>? RunConfig(bool fullSpeed) => Pack?.RunConfig(fullSpeed);

		/// <summary>
		/// Developer test modes (environment variable HEI_NPU_TEST), which exercise a pack on a PC without
		/// that vendor's NPU: "openvino-cpu" runs the Intel pack on OpenVINO's CPU device (x64, or the x64
		/// build under emulation); "winml-qnn" gets Qualcomm's plugin through Windows ML on a Snapdragon PC.
		/// </summary>
		static readonly string? TestMode = Environment.GetEnvironmentVariable("HEI_NPU_TEST")?.Trim().ToLowerInvariant();

		static readonly Lazy<NpuPack?> chosen = new(ChoosePack);
		/// <summary>Tests pick the pack instead of the hardware.</summary>
		internal static NpuPack? PackOverride;
		internal static NpuPack? Pack => PackOverride ?? chosen.Value;

		static NpuPack? ChoosePack() {
			if (!CoreUtils.IsWindows) return null;
			Architecture arch = RuntimeInformation.ProcessArchitecture;
			switch (TestMode) {
				case "openvino-cpu": return arch == Architecture.X64 ? new OpenVinoPack(acceptCpu: true) : null;
				case "winml-qnn": return WindowsMlPack.QnnForTests();
			}
			return (NpuHardware.Vendor, arch) switch {
				(NpuVendor.Qualcomm, Architecture.Arm64) => new QnnPack(),
				(NpuVendor.Intel, Architecture.X64) => new OpenVinoPack(acceptCpu: false),
				(NpuVendor.Amd, Architecture.X64) when WindowsMlPack.OsSupported => WindowsMlPack.VitisAi(),
				_ => null,
			};
		}

		/// <summary>True when this PC has an NPU this build can drive (whether or not its pack is downloaded yet).</summary>
		public static bool IsSupportedPlatform => Pack != null;

		public static bool IsInstalled => Pack is { } p && File.Exists(ModelPath) && p.IsInstalled;

		public static string EpName => Pack?.EpName ?? QnnPack.QnnEpName;
		/// <summary>The NPU for messages: "Qualcomm Hexagon NPU", "Intel AI Boost NPU", "AMD Ryzen AI NPU".</summary>
		public static string NpuName => Pack?.DisplayName ?? "NPU";
		/// <summary>The pack for download messages: what it is and roughly how big.</summary>
		public static string PackDescription => Pack?.Description ?? "";
		/// <summary>Cache key of NPU embeddings: they are FP16/BF16 DINOv2, not VDF's int8 model, so they get their own sidecars.</summary>
		public static string ModelKey => Pack?.ModelKey ?? QnnPack.QnnModelKey;
		/// <summary>The pack's folders under {ai} (to copy one another copy already has), and a file that shows it's all there.</summary>
		public static IReadOnlyList<string> PackFolders => Pack?.Folders ?? Array.Empty<string>();
		public static string? PackKeyFile => Pack?.KeyFile;
		/// <summary>Compiled NPU graphs, keyed by the plugin build that compiled them.</summary>
		internal static string ContextCacheFolder => Path.Combine(AiComponents.AiFolder, Pack!.CacheFolderName);
		internal static bool UsesEpContextModel => Pack?.UsesEpContextModel ?? false;

		static readonly object registerLock = new();
		static bool registered;
		static IReadOnlyList<OrtEpDevice>? npuDevices;

		/// <summary>
		/// The NPU devices the pack's plugin reports, registering the plugin with ONNX Runtime on first
		/// use (once per process). Empty when the pack is missing, the platform has no NPU, or the plugin
		/// fails to load — callers then stay on the CPU.
		/// </summary>
		internal static IReadOnlyList<OrtEpDevice> GetNpuDevices() {
			// Tests pin the CPU test embedder: never let a dev machine's NPU pack leak into them.
			if (AiComponents.TestOverrideModelPath != null || !IsInstalled || !AiComponents.IsReady)
				return Array.Empty<OrtEpDevice>();
			lock (registerLock) {
				if (npuDevices != null)
					return npuDevices;
				NpuPack pack = Pack!;
				try {
					AiComponents.EnsureResolverInstalled();
					OrtEnv env = OrtEnv.Instance();
					if (!registered) {
						env.RegisterExecutionProviderLibrary(pack.EpName, pack.PluginLibraryPath());
						registered = true;
					}
					npuDevices = env.GetEpDevices()
						.Where(d => string.Equals(d.EpName, pack.EpName, StringComparison.OrdinalIgnoreCase) && pack.Accepts(d.HardwareDevice.Type))
						.ToList();
					if (npuDevices.Count == 0)
						// What the plugin does offer, so a report from an untested NPU says why.
						Logger.Instance.Info($"The {pack.DisplayName} pack offers no NPU. ONNX Runtime devices: " +
							string.Join("; ", env.GetEpDevices().Select(d => $"{d.EpName} {d.HardwareDevice.Type} ({d.HardwareDevice.Vendor})")));
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

		internal static Dictionary<string, string> ProviderOptions() => Pack!.ProviderOptions(ContextCacheFolder);

		/// <summary>Downloads whatever part of the NPU pack is missing. Safe to call when installed.</summary>
		public static async Task DownloadAsync(IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			NpuPack pack = Pack ?? throw new PlatformNotSupportedException("This PC has no NPU this build can drive.");
			Directory.CreateDirectory(AiComponents.AiFolder);
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
			var downloads = new List<Func<CancellationToken, Task>>(2);
			if (!pack.IsInstalled)
				downloads.Add(ct => pack.DownloadAsync(http, progress, ct));
			if (!File.Exists(ModelPath))
				downloads.Add(ct => DownloadModelAsync(http, progress, ct));
			await AiComponents.RunDownloadsAsync(downloads, token);
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

		/// <summary>
		/// Downloads a NuGet package (SHA256-pinned) to a temp folder and extracts the entries <paramref name="pick"/>
		/// maps to a file name into <paramref name="folder"/>, each written aside and moved into place.
		/// </summary>
		internal static async Task ExtractFromNuGetAsync(HttpClient http, string url, string sha256, string step, string folder,
			Func<string, string?> pick, IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			string tempRoot = Path.Combine(Path.GetTempPath(), $"HEI.NpuDownload.{Guid.NewGuid():N}");
			Directory.CreateDirectory(tempRoot);
			try {
				string package = Path.Combine(tempRoot, "pack.nupkg");
				await DownloadVerifiedAsync(http, new Uri(url), package, sha256, step, progress, token);
				Directory.CreateDirectory(folder);
				using ZipArchive zip = ZipFile.OpenRead(package);
				foreach (ZipArchiveEntry entry in zip.Entries) {
					if (pick(entry.FullName) is not string name) continue;
					string tmp = Path.Combine(folder, name + ".part");
					entry.ExtractToFile(tmp, overwrite: true);
					File.Move(tmp, Path.Combine(folder, name), overwrite: true);
				}
			}
			finally {
				try { Directory.Delete(tempRoot, true); } catch { }
			}
		}
	}

	/// <summary>One vendor's way onto its NPU (see <see cref="NpuComponents"/>).</summary>
	internal abstract class NpuPack {
		/// <summary>The execution provider name its devices report (also the name it is registered under).</summary>
		public abstract string EpName { get; }
		public abstract string DisplayName { get; }
		public abstract string Description { get; }
		public abstract string ModelKey { get; }
		/// <summary>Where compiled graphs are kept, under {ai}.</summary>
		public abstract string CacheFolderName { get; }
		/// <summary>
		/// True: compiled graphs are cached as an ONNX Runtime EP-context model (<see cref="OnnxEmbedder"/>).
		/// False: the plugin keeps its own cache in <see cref="CacheFolderName"/> (a provider option).
		/// </summary>
		public virtual bool UsesEpContextModel => false;
		/// <summary>
		/// Images per NPU run. NPUs only run the batch size their graph was compiled for, so a smaller last
		/// chunk is padded. 8 where it hasn't been measured.
		/// </summary>
		public virtual int Batch => 8;
		/// <summary>Per-run options that set how hard the NPU works at either pace; null to leave it to the plugin.</summary>
		public virtual Dictionary<string, string>? RunConfig(bool fullSpeed) => null;
		/// <summary>The pack's folders under {ai}, and one file there that shows the pack is complete.</summary>
		public abstract IReadOnlyList<string> Folders { get; }
		public abstract string KeyFile { get; }
		public abstract bool IsInstalled { get; }
		/// <summary>The plugin DLL to register with ONNX Runtime.</summary>
		public abstract string PluginLibraryPath();
		public abstract Dictionary<string, string> ProviderOptions(string cacheFolder);
		/// <summary>Which of the plugin's devices count: its NPU.</summary>
		public virtual bool Accepts(OrtHardwareDeviceType type) => type == OrtHardwareDeviceType.NPU;
		public abstract Task DownloadAsync(HttpClient http, IProgress<AiDownloadProgress>? progress, CancellationToken token);

		protected static string Ai(string relative) => Path.Combine(AiComponents.AiFolder, relative);
	}

	/// <summary>
	/// Qualcomm Hexagon (Snapdragon X / X2): Qualcomm's QNN execution provider plugin (NuGet package
	/// Qualcomm.ML.OnnxRuntime.QNN, SHA256-pinned), of which only the win-arm64 HTP files are kept, in
	/// {ai}/qnn. It runs the FP32 model in FP16 on the HTP; compiling takes ~5 s, loading the cached
	/// EP-context model ~0.2 s. Needs ONNX Runtime ≥ 1.24.
	/// </summary>
	internal sealed class QnnPack : NpuPack {
		public const string PackageVersion = "2.6.0";
		public const string QnnEpName = "QNNExecutionProvider";
		public const string QnnModelKey = "dinov2s-fp16";
		const string PackageSha256 = "c2fe66eeaf92a0cb89faef1d4d05c23b939896aeb9dcfa2261d8fe70b54b8103";
		const string PackageUrl = "https://api.nuget.org/v3-flatcontainer/qualcomm.ml.onnxruntime.qnn/2.6.0/qualcomm.ml.onnxruntime.qnn.2.6.0.nupkg";
		const string PackageNativeDir = "runtimes/win-arm64/native/";

		/// <summary>
		/// The HTP backend's files. Genie (LLMs), the GPU backend and the x64 builds in the
		/// package are not needed. V73 = Snapdragon X, V81 = Snapdragon X2.
		/// </summary>
		static readonly string[] QnnFiles = {
			"onnxruntime_providers_qnn.dll", "QnnHtp.dll", "QnnHtpPrepare.dll", "QnnSystem.dll", "QnnHtpNetRunExtensions.dll",
			"QnnHtpV73Stub.dll", "libQnnHtpV73Skel.so", "libqnnhtpv73.cat",
			"QnnHtpV81Stub.dll", "libQnnHtpV81Skel.so", "libqnnhtpv81.cat",
		};

		static string QnnFolder => Ai("qnn");

		public override string EpName => QnnEpName;
		public override string DisplayName => "Qualcomm Hexagon NPU";
		public override string Description => $"Qualcomm QNN {PackageVersion} + model, ~230 MB";
		public override string ModelKey => QnnModelKey;
		public override string CacheFolderName => $"qnn-cache-{PackageVersion}";
		public override bool UsesEpContextModel => true;
		/// <summary>
		/// One image per run: the HTP's per-run cost is small next to the model, and its compiler handles a
		/// single image best. On a Snapdragon X2: 2.12 ms per image at batch 1, 4.91 at 2, 2.96 at 4, 2.52 at
		/// 8, 2.64 at 16, 5.09 at 32, with identical embeddings. Batch 1 also never pads, and compiles in half the time.
		/// </summary>
		public override int Batch => 1;
		public override IReadOnlyList<string> Folders => new[] { "qnn", CacheFolderName };
		public override string KeyFile => Path.Combine("qnn", "onnxruntime_providers_qnn.dll");
		public override bool IsInstalled => QnnFiles.All(f => File.Exists(Path.Combine(QnnFolder, f)));
		public override string PluginLibraryPath() => Path.Combine(QnnFolder, "onnxruntime_providers_qnn.dll");

		/// <summary>
		/// The HTP's clocks follow the scan's pace, run by run: burst when someone waits, power_saver in the
		/// background. Measured on a Snapdragon X2 (batch 1): 2.12 ms per image in burst, 3.20 in power_saver,
		/// which still embeds 300 images a second, several times what the decoders hand it.
		/// </summary>
		public override Dictionary<string, string> RunConfig(bool fullSpeed) =>
			new() { ["qnn.htp_perf_mode"] = fullSpeed ? "burst" : "power_saver" };

		/// <summary>FP16 on the HTP; burst clocks until the first run says otherwise (<see cref="RunConfig"/>).</summary>
		public override Dictionary<string, string> ProviderOptions(string cacheFolder) => new() {
			["backend_path"] = Path.Combine(QnnFolder, "QnnHtp.dll"),
			["htp_performance_mode"] = "burst",
			["enable_htp_fp16_precision"] = "1",
			// File-mapped weights are not supported for this graph; skip the failing first attempt.
			["disable_file_mapped_weights"] = "1",
		};

		public override Task DownloadAsync(HttpClient http, IProgress<AiDownloadProgress>? progress, CancellationToken token) =>
			NpuComponents.ExtractFromNuGetAsync(http, PackageUrl, PackageSha256, $"Qualcomm QNN {PackageVersion}", QnnFolder,
				entry => entry.StartsWith(PackageNativeDir, StringComparison.Ordinal) && QnnFiles.Contains(entry[PackageNativeDir.Length..])
					? entry[PackageNativeDir.Length..] : null,
				progress, token);
	}

	/// <summary>
	/// Intel AI Boost (Core Ultra): Intel's OpenVINO execution provider plugin (NuGet package
	/// Intel.ML.OnnxRuntime.EP.OpenVINO, SHA256-pinned), which carries the whole OpenVINO runtime, NPU
	/// compiler included, in {ai}/openvino. OpenVINO caches the compiled NPU graph itself (cache_dir).
	/// </summary>
	internal sealed class OpenVinoPack(bool acceptCpu) : NpuPack {
		public const string PackageVersion = "1.7.0";
		const string PackageSha256 = "483c6ac2c268f8fc3dcbbd951fdb41ebf053456c6718d60941f50fb392dd794d";
		const string PackageUrl = "https://api.nuget.org/v3-flatcontainer/intel.ml.onnxruntime.ep.openvino/1.7.0/intel.ml.onnxruntime.ep.openvino.1.7.0.nupkg";
		const string PackageNativeDir = "runtimes/win-x64/native/";
		const string Marker = "pack.version";
		const string Plugin = "onnxruntime_providers_openvino_plugin.dll";

		static string Folder => Ai("openvino");

		public override string EpName => "OpenVINOExecutionProvider";
		public override string DisplayName => "Intel AI Boost NPU";
		public override string Description => $"Intel OpenVINO {PackageVersion} + model, ~210 MB";
		public override string ModelKey => "dinov2s-openvino-fp16";
		public override string CacheFolderName => $"openvino-cache-{PackageVersion}";
		public override IReadOnlyList<string> Folders => new[] { "openvino", CacheFolderName };
		public override string KeyFile => Path.Combine("openvino", Plugin);

		public override bool IsInstalled {
			get {
				try {
					return new[] { Plugin, "openvino.dll", "openvino_intel_npu_plugin.dll", "openvino_onnx_frontend.dll" }
						.All(f => File.Exists(Path.Combine(Folder, f))) &&
						File.ReadAllText(Path.Combine(Folder, Marker)).Trim() == PackageVersion;
				}
				catch { return false; }
			}
		}

		public override string PluginLibraryPath() => Path.Combine(Folder, Plugin);

		/// <summary>OpenVINO picks FP16 on the NPU by itself; the compiled graph goes to the cache folder.</summary>
		public override Dictionary<string, string> ProviderOptions(string cacheFolder) {
			Directory.CreateDirectory(cacheFolder);
			return new() { ["cache_dir"] = cacheFolder };
		}

		public override bool Accepts(OrtHardwareDeviceType type) => type == OrtHardwareDeviceType.NPU || acceptCpu && type == OrtHardwareDeviceType.CPU;

		public override async Task DownloadAsync(HttpClient http, IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			// Every native file: the plugin loads OpenVINO's device plugins and frontends by name.
			await NpuComponents.ExtractFromNuGetAsync(http, PackageUrl, PackageSha256, $"Intel OpenVINO {PackageVersion}", Folder,
				entry => entry.StartsWith(PackageNativeDir, StringComparison.Ordinal) && !entry.EndsWith('/') ? entry[PackageNativeDir.Length..] : null,
				progress, token);
			File.WriteAllText(Path.Combine(Folder, Marker), PackageVersion);
		}
	}

	/// <summary>
	/// A vendor plugin that Windows ML downloads and keeps updated (AMD's Vitis AI): Heiward downloads only
	/// Windows ML's catalog DLL (NuGet package Microsoft.Windows.AI.MachineLearning, SHA256-pinned) into
	/// {ai}/winml, and asks the catalog for the plugin. Windows 11 24H2 or later.
	/// </summary>
	internal sealed class WindowsMlPack : NpuPack {
		public const string PackageVersion = "2.4.89";
		const string PackageSha256 = "5c68ecfb947223267abf159a023f5192ad42725e4e9cc995e7c1470dd54dff63";
		const string PackageUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.ai.machinelearning/2.4.89/microsoft.windows.ai.machinelearning.2.4.89.nupkg";

		readonly string provider, epName, displayName, modelKey, cacheFolderName;
		readonly Func<string, Dictionary<string, string>> options;

		WindowsMlPack(string provider, string epName, string displayName, string modelKey, string cacheFolderName,
			Func<string, Dictionary<string, string>> options) {
			this.provider = provider;
			this.epName = epName;
			this.displayName = displayName;
			this.modelKey = modelKey;
			this.cacheFolderName = cacheFolderName;
			this.options = options;
		}

		/// <summary>AMD Ryzen AI: Windows ML runs the FP32 model in BF16 on the NPU; Vitis AI caches the compiled graph.</summary>
		public static WindowsMlPack VitisAi() => new("VitisAI", "VitisAIExecutionProvider", "AMD Ryzen AI NPU", "dinov2s-vitisai-bf16", "vitisai-cache",
			cache => {
				Directory.CreateDirectory(cache);
				return new() { ["cache_dir"] = cache, ["cache_key"] = $"dinov2s_b{NpuComponents.NpuBatch}" };
			});

		/// <summary>Test mode: Qualcomm's plugin through Windows ML, so a Snapdragon PC exercises this path.</summary>
		public static WindowsMlPack QnnForTests() => new("QNN", QnnPack.QnnEpName, "Qualcomm Hexagon NPU (Windows ML)", "dinov2s-winml-qnn-fp16", "winml-qnn-cache",
			_ => new() { ["htp_performance_mode"] = "burst", ["enable_htp_fp16_precision"] = "1" });

		/// <summary>Windows ML's downloadable providers need Windows 11 24H2 (build 26100) or later.</summary>
		public static bool OsSupported => CoreUtils.IsWindows && Environment.OSVersion.Version.Build >= 26100;

		static string Folder => Ai("winml");
		static string CatalogDll => Path.Combine(Folder, WindowsMlCatalog.DllName);
		static string PackageNativeDir => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "runtimes/win-arm64/native/" : "runtimes/win-x64/native/";

		public override string EpName => epName;
		public override string DisplayName => displayName;
		public override string Description => $"Windows ML {PackageVersion} catalog (the {provider} plugin comes from Windows) + model";
		public override string ModelKey => modelKey;
		public override string CacheFolderName => cacheFolderName;
		public override IReadOnlyList<string> Folders => new[] { "winml", cacheFolderName };
		public override string KeyFile => Path.Combine("winml", WindowsMlCatalog.DllName);

		/// <summary>The catalog DLL is here and Windows has the plugin (it may still need adding to this process).</summary>
		public override bool IsInstalled {
			get {
				if (!File.Exists(CatalogDll)) return false;
				try {
					using var catalog = new WindowsMlCatalog(CatalogDll);
					return catalog.Find(epName) is { } p && p.State != WindowsMlCatalog.ReadyState.NotPresent;
				}
				catch (Exception e) {
					Logger.Instance.Info($"Windows ML catalog unavailable: {e.Message}");
					return false;
				}
			}
		}

		public override string PluginLibraryPath() {
			using var catalog = new WindowsMlCatalog(CatalogDll);
			WindowsMlCatalog.Provider p = catalog.Find(epName) ?? throw new InvalidOperationException($"Windows ML offers no {provider} provider for this PC.");
			p.EnsureReady(); // quick once installed: adds the plugin's package to this process
			string path = p.LibraryPath;
			Logger.Instance.Info($"Windows ML {provider} plugin {p.Version}: {path}");
			return path;
		}

		public override Dictionary<string, string> ProviderOptions(string cacheFolder) => options(cacheFolder);

		public override async Task DownloadAsync(HttpClient http, IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			if (!File.Exists(CatalogDll))
				await NpuComponents.ExtractFromNuGetAsync(http, PackageUrl, PackageSha256, $"Windows ML {PackageVersion}", Folder,
					entry => entry == PackageNativeDir + WindowsMlCatalog.DllName ? WindowsMlCatalog.DllName : null, progress, token);
			await Task.Run(() => {
				using var catalog = new WindowsMlCatalog(CatalogDll);
				WindowsMlCatalog.Provider p = catalog.Find(epName)
					?? throw new InvalidOperationException($"Windows ML offers no {provider} provider for this PC (check the NPU driver).");
				p.EnsureReady(); // Windows downloads and installs it: seconds to minutes
			}, token);
		}
	}
}
