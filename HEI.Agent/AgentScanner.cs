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
using HEI.Core;
using HEI.Core.AI;
using HEI.Core.FFTools;
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <summary>Live progress for the review page, rewritten about once a second while a scan runs.</summary>
	sealed record ScanStatus(int Pid, DateTime StartedUtc, string Stage, int Position, int Max, bool FullSpeed = false);

	/// <summary>
	/// One scan of the configured folders: VDF's engine (photos through WIC, embeddings on the NPU
	/// when there is one, cloud-only files never read), then a fresh <see cref="Report"/>. Only one
	/// scan runs at a time machine-wide for this user (scan.lock).
	/// </summary>
	static class AgentScanner {
		/// <param name="scheduled">Started by Task Scheduler: in the background unless the review page is open (<see cref="ScanPace"/>).</param>
		/// <param name="drives">Drives scanned only when asked (<see cref="AgentConfig.OnRequestDrives"/>) that this scan reads: from the drive's own page.</param>
		public static async Task<int> RunAsync(AgentConfig cfg, bool notify, bool scheduled, CancellationToken ct, IReadOnlyCollection<string>? drives = null) {
			Directory.CreateDirectory(AgentPaths.Home);
			using FileStream? scanLock = TryLock();
			if (scanLock == null) {
				Console.Error.WriteLine("A scan is already running.");
				return 0;
			}
			var started = DateTime.UtcNow;
			var timer = Stopwatch.StartNew();
			var notes = new List<string>();
			bool fullSpeed = ScanPace.FullSpeed(cfg, scheduled);
			var settings = BuildSettings(cfg, notes, fullSpeed);
			if (settings.IncludeList.Count == 0) {
				Console.Error.WriteLine("None of the configured folders exist. Edit " + AgentPaths.Config);
				return 2;
			}
			bool paced = Power.SetPace(fullSpeed);
			// What changed on each drive since the last scan, from its change journal: a drive with nothing
			// new isn't walked, and one with something new has only those folders listed again.
			ListingPlan? plan = null;
			try { plan = ListingPlan.Make(settings, cfg, started, ct, drives); }
			catch (Exception e) when (e is not OperationCanceledException) {
				AgentPaths.AppendLog("change journal not read, walking every folder: " + e.Message);
				// Drives scanned only when asked stay unread all the same: left out of this scan.
				foreach (string root in settings.IncludeList.Where(r => cfg.IsOnRequest(r) && !(drives ?? Array.Empty<string>()).Any(d => AgentConfig.DriveOf(d) == AgentConfig.DriveOf(r))).ToList())
					settings.IncludeList.Remove(root);
			}
			// Nothing a scan would see changed since the last one, which made this report with these
			// settings: a scheduled scan has nothing to do, and no drive is read, nor anything compared.
			if (scheduled && plan is { NothingChanged: true, SameScanAsLast: true } && Report.Load() != null) {
				plan.SaveSkipped();
				AgentPaths.AppendLog("scan skipped, nothing new: " + plan.Describe());
				await NotifyAsync(cfg, notify, Housekeeping(cfg, ct), new());
				return 0;
			}
			AgentPaths.AppendLog($"scan started {(fullSpeed ? "at full speed" : "in the background")} ({settings.MaxDegreeOfParallelism} at once{(paced ? "" : ", priority unchanged")}): " +
				(plan?.Describe() ?? string.Join("; ", settings.IncludeList)));
			using var stopPacing = CancellationTokenSource.CreateLinkedTokenSource(ct);
			Task pacing = FollowPageAsync(scheduled, fullSpeed, now => fullSpeed = now, stopPacing.Token);
			// Stop scan on the review page (or hei stop, or a pause) ends the scan as Ctrl+C would.
			ScanStop.Clear();
			using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
			Task watching = ScanStop.WatchAsync(started, stopped, stopPacing.Token);

			Func<string, bool>? mayRead = plan?.MayRead;
			var engine = new ScanEngine { Settings = settings, ListRoot = plan == null ? null : plan.ListingFor, MayRead = mayRead };
			int files = 0;
			string stage = "Finding files";
			long lastWrite = 0;
			engine.Progress += (_, e) => {
				if (stage == "Checking files") files = Math.Max(files, e.MaxPosition);
				long now = Stopwatch.GetTimestamp();
				if (Stopwatch.GetElapsedTime(lastWrite, now) < TimeSpan.FromSeconds(1)) return;
				lastWrite = now;
				WriteStatus(new ScanStatus(Environment.ProcessId, started, string.IsNullOrEmpty(e.CurrentStage) ? stage : e.CurrentStage, e.CurrentPosition, e.MaxPosition, fullSpeed));
			};
			engine.FilesEnumerated += (_, _) => stage = "Checking files";
			WriteStatus(new ScanStatus(Environment.ProcessId, started, stage, 0, 0, fullSpeed));
			try {
				await RunEngineAsync(engine, () => stage = "Comparing", stopped.Token);
			}
			catch (OperationCanceledException) {
				AgentPaths.AppendLog("scan aborted");
				Console.Error.WriteLine("Scan aborted.");
				return 130;
			}
			finally {
				stopPacing.Cancel();
				await pacing;
				await watching;
				ScanStop.Clear();
				try { File.Delete(AgentPaths.ScanStatus); } catch { }
			}

			// The files the listing found (the progress counts the database's entries, a deleted file's too).
			if (engine.FoundFiles.Count > 0) files = engine.FoundFiles.Count;
			// Whichever build made it: after an update, the sets it already listed aren't new.
			Report? previous = Report.LoadAny();
			// The device the embeddings actually ran on, after any fallback (the engine knows; a guess could say NPU for a CPU run).
			string device = !settings.UseAiMatching ? "off" : engine.AiDeviceUsed ?? NpuComponents.DeviceFor(settings.AiDevice);
			string? cacheKey = engine.AiDeviceUsed != null ? engine.AiCacheKeyUsed : NpuComponents.CacheKeyFor(settings.AiDevice);
			var fingerprints = new ScanFingerprints(cacheKey, settings.UseAiMatching, ct) { MayRead = mayRead };
			var hashes = ReportBuilder.ContentHashes.Load();
			var groups = ReportBuilder.Build(engine.Duplicates, fingerprints, hashes, mayRead, engine.FoundFiles.Select(f => f.Path));
			try { hashes.Save(); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AgentPaths.AppendLog("saving the content hashes failed: " + e.Message); }
			// The report fingerprints the sound of the videos in it, once: the next scan reuses them.
			if (fingerprints.AudioAdded)
				try { DatabaseUtils.SaveDatabase(); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AgentPaths.AppendLog("saving the audio fingerprints failed: " + e.Message); }
			AiStatus.Record(cfg, device, "scan");
			var report = new Report(Report.CurrentVersion, started, timer.Elapsed.TotalSeconds, device, files,
				settings.IncludeList.ToList(), settings.ExcludedExtensions.OrderBy(e => e).ToList(), notes, groups, AppBuild.Current);
			report.Save();
			ScanIndex.Build(started, settings.IncludeList, engine.FoundFiles, engine.ListingTimes, engine.AnalysisTimes).Save();
			// This listing, and where the journal was read up to: the next scan starts from them.
			try { plan?.Save(engine.FoundFiles, fingerprints.ModifiedUtc, started); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AgentPaths.AppendLog("saving the listing failed: " + e.Message); }
			AutoRun? auto = Housekeeping(cfg, ct);

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
			await NotifyAsync(cfg, notify, auto, fresh);
			return 0;
		}

		/// <summary>
		/// What runs on its own clock after every scheduled scan, whether the scan had anything to do: the
		/// developer check when it's due, and automatic cleanup of what has waited its days.
		/// </summary>
		static AutoRun? Housekeeping(AgentConfig cfg, CancellationToken ct) {
			bool devChecked = false;
			if (DevScan.Due(cfg)) {
				try { devChecked = DevScan.RunAndSave(cfg, ct) != null; }
				catch (Exception e) when (e is not OperationCanceledException) { AgentPaths.AppendLog("developer check failed: " + e.Message); }
			}
			try { return AutoCleaner.RunAndSave(cfg, devChecked, new CleanupActions(cfg, automatic: true)); }
			catch (Exception e) when (e is not OperationCanceledException) { AgentPaths.AppendLog("automatic cleanup failed: " + e.Message); }
			return null;
		}

		/// <summary>The notifications: what automatic cleanup did, and new sets to review. Nothing new, nothing shown.</summary>
		static async Task NotifyAsync(AgentConfig cfg, bool notify, AutoRun? auto, List<ReportGroup> fresh) {
			if (!notify || !cfg.Toast || (fresh.Count == 0 && auto is not { DidSomething: true })) return;
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

		internal static Settings BuildSettings(AgentConfig cfg, List<string> notes, bool fullSpeed = false) {
			var s = new Settings {
				IncludeImages = true,
				IncludeSubDirectories = true,
				UseAiMatching = true,
				AiDevice = Enum.TryParse(cfg.AiDevice, ignoreCase: true, out AiDevice d) ? d : AiDevice.Auto,
				SkipCloudPlaceholders = true,
				// The listing just saw every file; asking the disk about each one again could wake a sleeping drive.
				ListingProvesExistence = true,
				UseWindowsImageDecoder = true,
				MaxDegreeOfParallelism = cfg.ParallelismFor(fullSpeed),
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

		/// <summary>
		/// Follows the review page while the scan runs: open it and a background scan speeds up, close it
		/// and a scheduled scan steps back (checked every 5 s; settings.json is read fresh, so the page's
		/// switch applies at once). The number of files decoded at once stays as the scan started.
		/// </summary>
		static async Task FollowPageAsync(bool scheduled, bool current, Action<bool> changed, CancellationToken ct) {
			while (true) {
				try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
				catch (OperationCanceledException) { return; }
				bool wanted = ScanPace.FullSpeed(AgentConfig.Load(), scheduled);
				if (wanted == current) continue;
				current = wanted;
				Power.SetPace(wanted);
				changed(wanted);
				AgentPaths.AppendLog(wanted ? "scan: full speed (the review page is open)" : "scan: back in the background");
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

	/// <summary>
	/// How hard a scan works (<see cref="AgentConfig.ScanSpeed"/>). "auto": at full speed when someone
	/// is waiting for it (they started it, or the review page is open and showing), in the background
	/// otherwise. "background": always in the background, as scheduled scans always ran before. "full":
	/// always at full speed.
	/// </summary>
	static class ScanPace {
		/// <summary>The page polls every 2–15 s while it shows; hidden tabs don't report.</summary>
		static readonly TimeSpan PageFresh = TimeSpan.FromSeconds(45);
		static long lastMarked;

		internal static bool FullSpeed(AgentConfig cfg, bool scheduled, bool pageOpen) =>
			cfg.AlwaysFullSpeed || !cfg.AlwaysInBackground && (!scheduled || pageOpen);

		public static bool FullSpeed(AgentConfig cfg, bool scheduled) => FullSpeed(cfg, scheduled, PageOpen());

		public static bool PageOpen() {
			try { return File.Exists(AgentPaths.PageSeen) && DateTime.UtcNow - File.GetLastWriteTimeUtc(AgentPaths.PageSeen) < PageFresh; }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
		}

		/// <summary>The page is open and showing; the file is touched at most every 5 s.</summary>
		public static void MarkPageSeen() {
			long now = Environment.TickCount64;
			long last = Interlocked.Read(ref lastMarked);
			if (last != 0 && now - last < 5000 || Interlocked.CompareExchange(ref lastMarked, now, last) != last) return;
			try {
				Directory.CreateDirectory(AgentPaths.Home);
				File.WriteAllText(AgentPaths.PageSeen, "");
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* then scans don't speed up */ }
		}
	}

	static class Format {
		public static string Bytes(long b) =>
			b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" :
			b >= 1L << 20 ? $"{b / (double)(1L << 20):0.#} MB" :
			b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} bytes";
	}
}
