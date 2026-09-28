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

using System.Text.Json;
using VDF.Core;
using VDF.Core.ViewModels;

namespace VDF.CLI.Commands {
	/// <summary>Drives ScanEngine operations and bridges the async-void/event API to awaitable Tasks.</summary>
	internal static class ScanRunner {
		/// <summary>Runs StartSearch() then StartCompare() (the full pipeline).</summary>
		internal static async Task<HashSet<DuplicateItem>> RunScanAndCompareAsync(ScanEngine engine, CancellationToken ct) {
			await RunSearchAsync(engine, ct);
			return await RunCompareAsync(engine, ct);
		}

		/// <summary>
		/// Headless first-use download of the AI components (ONNX Runtime + model) when an
		/// AI option is enabled — the CLI counterpart of the GUI's download prompt. Must run
		/// before StartSearch, whose PrepareSearch fails fast on missing components.
		/// </summary>
		internal static async Task EnsureAiComponentsAsync(VDF.Core.Settings settings, CancellationToken ct) {
			if (!settings.NeedsAiComponents)
				return;
			// On Windows ARM64, auto/npu also want the NPU pack; elsewhere it does not apply.
			bool wantNpu = settings.AiDevice != VDF.Core.AI.AiDevice.Cpu && VDF.Core.AI.NpuComponents.IsSupportedPlatform;
			bool needNpu = wantNpu && !VDF.Core.AI.NpuComponents.IsInstalled;
			bool needGpu = settings.AiDevice == VDF.Core.AI.AiDevice.Gpu && VDF.Core.AI.GpuComponents.IsSupportedPlatform && !VDF.Core.AI.GpuComponents.IsInstalled;
			if (needGpu) needNpu = false;
			if (VDF.Core.AI.AiComponents.IsReady && !needNpu && !needGpu)
				return;
			// One line per ~5 MB, not per chunk — CI logs stay readable. Buckets are
			// per step: the runtime and model download concurrently, and a shared
			// counter would swallow whichever step reaches a bucket second.
			var lastReported = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
			var progress = new Progress<VDF.Core.AI.AiDownloadProgress>(p => {
				long bucket = p.BytesDone / (5 * 1024 * 1024);
				if (lastReported.TryGetValue(p.Step, out long prev) && prev == bucket) return;
				lastReported[p.Step] = bucket;
				Console.Error.WriteLine($"[scan]   {p.Step}: {p.BytesDone / (1024 * 1024)} MB{(p.BytesTotal.HasValue ? $" / {p.BytesTotal.Value / (1024 * 1024)} MB" : string.Empty)}");
			});
			if (!VDF.Core.AI.AiComponents.IsReady) {
				Console.Error.WriteLine($"[scan] Downloading AI components (ONNX Runtime {VDF.Core.AI.AiComponents.RuntimeVersion} + model, ~100 MB) to '{VDF.Core.AI.AiComponents.AiFolder}'...");
				await VDF.Core.AI.AiComponents.DownloadAsync(progress, ct);
				Console.Error.WriteLine("[scan] AI components ready.");
			}
			if (needGpu) {
				Console.Error.WriteLine($"[scan] Downloading the GPU pack (ONNX Runtime DirectML + DirectML {VDF.Core.AI.GpuComponents.DirectMLVersion}, ~215 MB) to '{VDF.Core.AI.AiComponents.AiFolder}'...");
				await VDF.Core.AI.GpuComponents.DownloadAsync(progress, ct);
				Console.Error.WriteLine("[scan] GPU pack ready.");
			}
			if (needNpu) {
				Console.Error.WriteLine($"[scan] Downloading the NPU pack ({VDF.Core.AI.NpuComponents.PackDescription}) to '{VDF.Core.AI.AiComponents.AiFolder}'...");
				try {
					await VDF.Core.AI.NpuComponents.DownloadAsync(progress, ct);
					Console.Error.WriteLine("[scan] NPU pack ready.");
				}
				catch (Exception e) when (e is not OperationCanceledException && settings.AiDevice == VDF.Core.AI.AiDevice.Auto) {
					// Auto only prefers the NPU: without the pack the scan still runs on the CPU.
					Console.Error.WriteLine($"[scan] NPU pack unavailable ({e.Message}); AI matching runs on the CPU.");
				}
			}
		}

		/// <summary>Runs StartSearch() only (enumerate files and build hashes).</summary>
		internal static async Task RunSearchAsync(ScanEngine engine, CancellationToken ct) {
			var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

			engine.BuildingHashesDone += OnDone;
			engine.ScanAborted += OnAborted;
			ct.Register(() => { engine.Stop(); tcs.TrySetCanceled(); });

			// searchAndCompare:false — the CLI drives comparison as a separate awaitable step
			// (RunCompareAsync). Letting StartSearch auto-chain into StartCompare would run the
			// comparison twice, and the two concurrent SaveDatabase calls crash on the temp
			// database file (#803).
			engine.StartSearch(searchAndCompare: false);
			await tcs.Task;

			engine.BuildingHashesDone -= OnDone;
			engine.ScanAborted -= OnAborted;

			void OnDone(object? s, EventArgs e) => tcs.TrySetResult();
			void OnAborted(object? s, EventArgs e) => tcs.TrySetCanceled();
		}

		/// <summary>Runs StartCompare() only (assumes database already populated by a prior scan).</summary>
		internal static async Task<HashSet<DuplicateItem>> RunCompareAsync(ScanEngine engine, CancellationToken ct) {
			var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

			engine.ScanDone += OnDone;
			engine.ScanAborted += OnAborted;
			ct.Register(() => { engine.Stop(); tcs.TrySetCanceled(); });

			engine.StartCompare();
			await tcs.Task;

			engine.ScanDone -= OnDone;
			engine.ScanAborted -= OnAborted;

			return engine.Duplicates;

			void OnDone(object? s, EventArgs e) => tcs.TrySetResult();
			void OnAborted(object? s, EventArgs e) => tcs.TrySetCanceled();
		}

		internal static void WireProgress(ScanEngine engine) {
			engine.FilesEnumerated += (_, _) =>
				Console.Error.WriteLine("[scan] File enumeration complete.");

			engine.BuildingHashesDone += (_, _) =>
				Console.Error.WriteLine("[scan] Hashing complete.");

			engine.Progress += (_, e) => {
				int pct = e.MaxPosition > 0 ? (int)(100L * e.CurrentPosition / e.MaxPosition) : 0;
				string eta = e.Remaining == TimeSpan.Zero ? "..." : e.Remaining.ToString(@"m\mss\s");
				string stage = string.IsNullOrEmpty(e.CurrentStage)
					? string.Empty
					: e.StageMax > 0 ? $"  ({e.CurrentStage} {e.StageCurrent}/{e.StageMax})" : $"  ({e.CurrentStage})";
				Console.Error.Write($"\r[{pct,3}%] {e.CurrentPosition}/{e.MaxPosition}  ETA {eta}  {TruncatePath(e.CurrentFile, 60)}{stage}    ");
			};

			engine.ScanDone += (_, _) => {
				Console.Error.WriteLine();
				Console.Error.WriteLine("[scan] Comparison complete.");
			};

			engine.ScanAborted += (_, _) => {
				Console.Error.WriteLine();
				Console.Error.WriteLine("[scan] Aborted.");
			};
		}


		internal static Settings LoadOrCreateSettings(FileInfo? settingsFile) {
			if (settingsFile == null || !settingsFile.Exists)
				return new Settings();

			try {
				var json = File.ReadAllText(settingsFile.FullName);
				return JsonSerializer.Deserialize(json, VDF.Core.Utils.CoreJsonContext.Default.Settings) ?? new Settings();
			}
			catch (Exception ex) {
				Console.Error.WriteLine($"Warning: could not load settings file '{settingsFile.FullName}': {ex.Message}");
				return new Settings();
			}
		}

		static string TruncatePath(string path, int maxLen) {
			if (path.Length <= maxLen) return path;
			return "..." + path[^(maxLen - 3)..];
		}
	}
}
