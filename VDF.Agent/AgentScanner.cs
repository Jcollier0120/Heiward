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

using System.Diagnostics;
using System.Text.Json;
using VDF.Core;
using VDF.Core.AI;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VDF.Agent {
	/// <summary>Live progress for the review page, rewritten about once a second while a scan runs.</summary>
	sealed record ScanStatus(int Pid, DateTime StartedUtc, string Stage, int Position, int Max);

	/// <summary>
	/// One scan of the configured folders: VDF's engine (photos through WIC, embeddings on the NPU
	/// when there is one, cloud-only files never read), then a fresh <see cref="Report"/>. Only one
	/// scan runs at a time machine-wide for this user (scan.lock).
	/// </summary>
	static class AgentScanner {
		public static async Task<int> RunAsync(AgentConfig cfg, bool notify, CancellationToken ct) {
			Directory.CreateDirectory(AgentPaths.Home);
			using FileStream? scanLock = TryLock();
			if (scanLock == null) {
				Console.Error.WriteLine("A scan is already running.");
				return 0;
			}
			var started = DateTime.UtcNow;
			var timer = Stopwatch.StartNew();
			var notes = new List<string>();
			var settings = BuildSettings(cfg, notes);
			if (settings.IncludeList.Count == 0) {
				Console.Error.WriteLine("None of the configured folders exist. Edit " + AgentPaths.Config);
				return 2;
			}
			AgentPaths.AppendLog($"scan started: {string.Join("; ", settings.IncludeList)}");

			var engine = new ScanEngine { Settings = settings };
			int files = 0;
			string stage = "Finding files";
			long lastWrite = 0;
			engine.Progress += (_, e) => {
				if (stage == "Checking files") files = Math.Max(files, e.MaxPosition);
				long now = Stopwatch.GetTimestamp();
				if (Stopwatch.GetElapsedTime(lastWrite, now) < TimeSpan.FromSeconds(1)) return;
				lastWrite = now;
				WriteStatus(new ScanStatus(Environment.ProcessId, started, string.IsNullOrEmpty(e.CurrentStage) ? stage : e.CurrentStage, e.CurrentPosition, e.MaxPosition));
			};
			engine.FilesEnumerated += (_, _) => stage = "Checking files";
			WriteStatus(new ScanStatus(Environment.ProcessId, started, stage, 0, 0));
			try {
				await RunEngineAsync(engine, () => stage = "Comparing", ct);
			}
			catch (OperationCanceledException) {
				AgentPaths.AppendLog("scan aborted");
				Console.Error.WriteLine("Scan aborted.");
				return 130;
			}
			finally {
				try { File.Delete(AgentPaths.ScanStatus); } catch { }
			}

			Report? previous = Report.Load();
			// The device the embeddings actually ran on, after any fallback (the engine knows; a guess could say NPU for a CPU run).
			string device = !settings.UseAiMatching ? "off" : engine.AiDeviceUsed ?? NpuComponents.DeviceFor(settings.AiDevice);
			string? cacheKey = engine.AiDeviceUsed != null ? engine.AiCacheKeyUsed : NpuComponents.CacheKeyFor(settings.AiDevice);
			var groups = ReportBuilder.Build(engine.Duplicates, new ScanFingerprints(cacheKey, settings.UseAiMatching));
			AiStatus.Record(cfg, device, "scan");
			var report = new Report(Report.CurrentVersion, started, timer.Elapsed.TotalSeconds, device, files,
				settings.IncludeList.ToList(), settings.ExcludedExtensions.OrderBy(e => e).ToList(), notes, groups);
			report.Save();
			ScanIndex.Build(started, settings.IncludeList, engine.FoundFiles, engine.ListingTimes, engine.AnalysisTimes).Save();
			bool devChecked = false;
			if (DevScan.Due(cfg)) {
				try { devChecked = DevScan.RunAndSave(cfg, ct) != null; }
				catch (Exception e) when (e is not OperationCanceledException) { AgentPaths.AppendLog("developer check failed: " + e.Message); }
			}
			AutoRun? auto = null;
			try { auto = AutoCleaner.RunAndSave(cfg, devChecked, new CleanupActions(cfg, automatic: true)); }
			catch (Exception e) when (e is not OperationCanceledException) { AgentPaths.AppendLog("automatic cleanup failed: " + e.Message); }

			var decisions = DecisionStore.Load();
			var known = new HashSet<string>(previous?.Groups.Select(g => g.Key) ?? Enumerable.Empty<string>());
			// New sets that need the user; automatic cleanup takes care of the others without a word.
			var waiting = AutoCleaner.WaitingForUser(cfg, report, decisions, AutoCleanState.Load(), DateTime.UtcNow).Select(g => g.Key).ToHashSet();
			var fresh = groups.Where(g => !known.Contains(g.Key) && waiting.Contains(g.Key)).ToList();
			string summary = $"{groups.Count} group(s), {fresh.Count} new to review; {files:N0} files in {timer.Elapsed.TotalSeconds:N0} s, AI on {device}";
			AgentPaths.AppendLog("scan done: " + summary);
			Console.Error.WriteLine("Scan done: " + summary);
			if (auto is { DidSomething: true }) Console.Error.WriteLine("Automatic cleanup: " + auto.Describe("; "));
			foreach (string n in notes) Console.Error.WriteLine("  note: " + n);

			if (notify && cfg.Toast && (fresh.Count > 0 || auto is { DidSomething: true })) {
				ReviewServer.EnsureRunningInBackground(cfg);
				if (auto is { DidSomething: true })
					await Toast.ShowAsync("Cleaned up automatically", auto.Describe() + ".", ReviewServer.PageUrl(cfg.Port));
				if (fresh.Count > 0) {
					long bytes = fresh.Sum(g => g.ReclaimBytes);
					await Toast.ShowAsync($"{fresh.Count} new set{(fresh.Count == 1 ? "" : "s")} of likely duplicates",
						bytes > 0 ? $"Review them to free up to {Format.Bytes(bytes)}. Nothing is deleted until you choose." : "Review them when you have a minute.",
						ReviewServer.PageUrl(cfg.Port));
				}
			}
			return 0;
		}

		internal static Settings BuildSettings(AgentConfig cfg, List<string> notes) {
			var s = new Settings {
				IncludeImages = true,
				IncludeSubDirectories = true,
				UseAiMatching = true,
				AiDevice = Enum.TryParse(cfg.AiDevice, ignoreCase: true, out AiDevice d) ? d : AiDevice.Auto,
				SkipCloudPlaceholders = true,
				UseWindowsImageDecoder = true,
				MaxDegreeOfParallelism = cfg.EffectiveParallelism,
				CustomDatabaseFolder = AgentPaths.Database,
			};
			Directory.CreateDirectory(AgentPaths.Database);
			ScanScope.Apply(s, cfg, notes);
			// In-process FFmpeg: an iPhone photo's tiles decode in one process instead of one ffmpeg.exe
			// per photo (~3x faster). Falls back to the process per file if the libraries don't load.
			s.UseNativeFfmpegBinding = ScanEngine.NativeFFmpegExists;
			foreach (string e in cfg.ExcludeExtensions) s.ExcludedExtensions.Add(e.StartsWith('.') ? e : "." + e);
			if (s.ExcludedExtensions.Contains(".heic"))
				notes.Add("HEIC/HEIF photos are skipped (settings.json, excludeExtensions).");

			if (!AiComponents.IsReady) {
				s.UseAiMatching = false;
				notes.Add("AI matching is off: run 'hei setup' to install it (finds cropped, edited and mirrored copies).");
			}
			if (!FfmpegWorks()) {
				// Photos decode through WIC without FFmpeg (HEIC too, slowly); videos can't. Leave them
				// out and say so, rather than failing every video one by one.
				foreach (string e in FileUtils.VideoExtensions) s.ExcludedExtensions.Add(e);
				notes.Add("Videos were skipped and HEIC photos decode slowly: FFmpeg is missing or doesn't start on this PC. Run 'hei setup'.");
			}
			return s;
		}

		/// <summary>FFmpeg and FFprobe exist and actually start (the ARM64 builds VDF used to fetch crash on load on Snapdragon X2).</summary>
		static bool FfmpegWorks() {
			string? ffmpeg = FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFmpeg), ffprobe = FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFProbe);
			if (ffmpeg == null || ffprobe == null) return false;
			try {
				using var p = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -version") {
					UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
				});
				if (p == null) return false;
				p.StandardOutput.ReadToEnd();
				return p.WaitForExit(15_000) && p.ExitCode == 0;
			}
			catch {
				return false;
			}
		}

		/// <summary>Search, then compare, as two awaited steps (the CLI's pattern: chaining them ran the compare twice, #803).</summary>
		static async Task RunEngineAsync(ScanEngine engine, Action onCompare, CancellationToken ct) {
			var searched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			void SearchDone(object? s, EventArgs e) => searched.TrySetResult();
			void Aborted(object? s, EventArgs e) => searched.TrySetCanceled();
			engine.BuildingHashesDone += SearchDone;
			engine.ScanAborted += Aborted;
			using (ct.Register(() => { engine.Stop(); searched.TrySetCanceled(); }))
			{
				engine.StartSearch(searchAndCompare: false);
				await searched.Task;
			}
			engine.BuildingHashesDone -= SearchDone;
			engine.ScanAborted -= Aborted;

			onCompare();
			var compared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			void CompareDone(object? s, EventArgs e) => compared.TrySetResult();
			void CompareAborted(object? s, EventArgs e) => compared.TrySetCanceled();
			engine.ScanDone += CompareDone;
			engine.ScanAborted += CompareAborted;
			using (ct.Register(() => { engine.Stop(); compared.TrySetCanceled(); }))
			{
				engine.StartCompare();
				await compared.Task;
			}
			engine.ScanDone -= CompareDone;
			engine.ScanAborted -= CompareAborted;
		}

		static FileStream? TryLock() {
			try {
				return new FileStream(AgentPaths.ScanLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
			}
			catch (IOException) {
				return null;
			}
		}

		/// <summary>True while another process holds scan.lock.</summary>
		public static bool IsRunning() {
			if (!File.Exists(AgentPaths.ScanLock)) return false;
			using FileStream? probe = TryLock();
			return probe == null;
		}

		public static ScanStatus? ReadStatus() {
			try {
				return File.Exists(AgentPaths.ScanStatus)
					? JsonSerializer.Deserialize<ScanStatus>(File.ReadAllText(AgentPaths.ScanStatus), AgentConfig.Json)
					: null;
			}
			catch { return null; }
		}

		static void WriteStatus(ScanStatus status) {
			try { AgentPaths.WriteAtomic(AgentPaths.ScanStatus, JsonSerializer.Serialize(status, AgentConfig.Json)); }
			catch { /* progress is cosmetic */ }
		}
	}

	static class Format {
		public static string Bytes(long b) =>
			b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" :
			b >= 1L << 20 ? $"{b / (double)(1L << 20):0.#} MB" :
			b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} bytes";
	}
}
