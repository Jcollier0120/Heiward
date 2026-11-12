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
	/// <summary>Live progress for the review page while a scan runs (scan-status.json), written when <see cref="StatusPace"/> says.</summary>
	/// <param name="Roots">The drives and folders this scan reads, whose cards show its progress: not those scanned only when asked, nor those where nothing changed.</param>
	/// <param name="Phase"><see cref="ScanPhase"/>: what the scan is doing now.</param>
	/// <param name="Listed">Of <paramref name="Roots"/>, those whose files are listed (while <paramref name="Phase"/> is "listing").</param>
	/// <param name="Drives">Each drive's files checked so far, from "checking" on: only drives with files in this scan.</param>
	/// <param name="AiDevice">Where the scan's AI model last ran ("NPU", "GPU", "CPU"), with <paramref name="AiRanUtc"/>; null before it has.</param>
	/// <param name="AiRanUtc">When it last ran: the page says "Running on the NPU" only just after, not for the whole scan.</param>
	sealed record ScanStatus(int Pid, DateTime StartedUtc, string Stage, int Position, int Max, bool FullSpeed = false, IReadOnlyList<string>? Roots = null,
		string Phase = ScanPhase.Listing, IReadOnlyList<string>? Listed = null, IReadOnlyList<DriveCheck>? Drives = null,
		string? AiDevice = null, DateTime? AiRanUtc = null);

	/// <summary>One drive's part of checking the files: done of total, as files and as bytes.</summary>
	sealed record DriveCheck(string Root, int Done, int Total, long DoneBytes, long TotalBytes);

	/// <summary>A scan's steps, in order, as <see cref="ScanStatus.Phase"/> names them.</summary>
	static class ScanPhase {
		/// <summary>Finding the photos and videos: each root is walked, or listed from its change journal.</summary>
		public const string Listing = "listing";
		/// <summary>Reading new and changed files (frames, fingerprints, AI), each drive at its own pace.</summary>
		public const string Checking = "checking";
		/// <summary>Comparing every file with the others, across drives: <see cref="ScanStatus.Stage"/> says which pass.</summary>
		public const string Comparing = "comparing";
		/// <summary>Making the report (each set sorted into the file to keep, its copies and look-alikes; the copies checked byte for
		/// byte, the videos' sound), then the checks that follow a scan: <see cref="ScanStatus.Stage"/> says which.</summary>
		public const string Finishing = "finishing";
	}

	/// <summary>
	/// One scan of the configured folders: VDF's engine (photos through WIC, embeddings on the NPU
	/// when there is one, cloud-only files never read), then a fresh <see cref="Report"/>. Only one
	/// scan runs at a time machine-wide for this user (scan.lock).
	/// </summary>
	static class AgentScanner {
		/// <summary>The status while the report is made, with how many of the sets' files are done.</summary>
		internal const string SortingSets = "Making the report: sorting each set into the file to keep, its copies and look-alikes";

		/// <param name="scheduled">Started by Task Scheduler: in the background unless the review page is open (<see cref="ScanPace"/>).</param>
		/// <param name="drives">Drives scanned only when asked (<see cref="AgentConfig.OnRequestDrives"/>) that this scan reads: from the drive's own page.</param>
		/// <param name="waitTurn">The install's first scan: waits its turn among the agents' first rounds as a scheduled one does (<see cref="FirstRound"/>).</param>
		/// <param name="bumpedSlotUtc">The scheduled scan this one was brought forward from (<see cref="ScanBump"/>): skipped once this one goes through.</param>
		/// <param name="folders">
		/// Scan now on a folder, from the review page: only these folders are read, each with the folders in it (<see cref="ListingPlan.Make"/>).
		/// It isn't a drive's scan: no drive's "Last scanned" is set by it (<see cref="DriveScans"/>), and it never stands in for a scheduled one.
		/// </param>
		/// <param name="only">
		/// Scan this drive now, on a drive's page ("D:\"), one scheduled scans read or one scanned only when asked: only that drive
		/// is read, whole, as any scan reads it, and every other drive rests (<see cref="ListingPlan.Make"/>). Its "Last scanned"
		/// alone is set; it never stands in for a scheduled scan, which still reads every drive it reads at its time.
		/// </param>
		public static async Task<int> RunAsync(AgentConfig cfg, bool notify, bool scheduled, CancellationToken ct, IReadOnlyCollection<string>? drives = null, bool waitTurn = false,
			DateTime? bumpedSlotUtc = null, IReadOnlyCollection<string>? folders = null, IReadOnlyCollection<string>? only = null) {
			Directory.CreateDirectory(AgentPaths.Home);
			using FileStream? scanLock = TryLock();
			if (scanLock == null) {
				Console.Error.WriteLine("A scan is already running.");
				return 0;
			}
			// A first scan waits its turn among the agents' first rounds (FirstRound), before it counts as started.
			IDisposable? firstTurn;
			try { firstTurn = FirstRound.Take(scheduled || waitTurn, Manor.Load(), AgentPaths.AppendLog); }
			catch (TimeoutException) {
				AgentPaths.AppendLog("first scan: no turn among the agents' first rounds yet; the next scheduled scan waits again");
				return 0;
			}
			using var turn = firstTurn;
			var started = DateTime.UtcNow;
			try { AgentPaths.WriteAtomic(AgentPaths.ScanStarted, started.ToString("O")); }
			catch { /* only the page's scan-when-due check and /api/ping's runningSince read it */ }
			// How it ended, written before scan.lock is let go: /api/ping's last run (RunTimes).
			int? code = null;
			Exception? error = null;
			try { return (code = await ScanAsync(cfg, notify, scheduled, drives, folders, only, started, firstTurn != null, ct)).Value; }
			catch (Exception e) { error = e; throw; }
			finally {
				AfterScan(LastScan.Record(started, DateTime.UtcNow, code, error), bumpedSlotUtc, cfg.ScanEveryMinutes,
					folderScan: folders is { Count: > 0 }, driveScan: only is { Count: > 0 });
			}
		}

		/// <summary>
		/// The roots a scan records as scanned (<see cref="DriveScans"/>, each drive's "Last scanned"): those it read whole. A
		/// scan of folders alone (Scan now on a folder) records none, and one of a drive alone (Scan this drive now) that drive's
		/// roots alone, the others resting; with no plan (the journal unread), every root was walked.
		/// </summary>
		internal static List<string> ScannedWhole(ListingPlan? plan, IEnumerable<string> walked) => plan?.ReadWhole ?? walked.ToList();

		/// <summary>
		/// The drives scanned only when asked that the developer check after this scan reads now, due or not, so their developer
		/// items catch up with their photos and videos: those asked for (<c>--drive</c>), or the one Scan this drive now read
		/// (<c>--only</c>) when it's scanned only when asked. Null for none: the check runs only when it's due, as after any scan.
		/// </summary>
		internal static IReadOnlyCollection<string>? DevDrives(AgentConfig cfg, IReadOnlyCollection<string>? drives, IReadOnlyCollection<string>? only) {
			if (drives is { Count: > 0 }) return drives;
			List<string> asked = (only ?? Array.Empty<string>()).Select(cfg.OnRequestDriveOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			return asked.Count > 0 ? asked : null;
		}

		/// <summary>
		/// Why <c>scan --only</c> (<paramref name="only"/>, Scan this drive now) can't run with the rest it was given, or null. It's
		/// a scan someone starts, of one drive: never a scheduled one, nor one brought forward (<c>--bump</c>), which stand for
		/// every drive. A scan of folders (<c>--folder</c>) or of drives scanned only when asked besides (<c>--drive</c>) is another.
		/// </summary>
		internal static string? OnlyRefusal(IReadOnlyCollection<string>? only, bool scheduled, bool bumped, bool folders, bool drives) =>
			only is not { Count: > 0 } ? null
			: scheduled || bumped ? "--only is for a scan you start, not a scheduled one or one brought forward (--bump): those read every drive."
			: folders || drives ? "--only reads just that drive: it doesn't go with --folder or --drive."
			: null;

		/// <summary>
		/// Once a scan brought forward from the scheduled one at <paramref name="bumpedSlotUtc"/> has ended: that scheduled scan
		/// is skipped only if this one went through (<see cref="LastScan.Ok"/>). One that failed, or was stopped, skips nothing.
		/// </summary>
		/// <param name="folderScan">Scan now on a folder: it reads one folder, so it never stands in for a scheduled scan of every drive.</param>
		/// <param name="driveScan">Scan this drive now (<c>scan --only</c>): it reads one drive, so it never stands in for one either.</param>
		internal static ScanBump? AfterScan(LastScan last, DateTime? bumpedSlotUtc, int everyMinutes, bool folderScan = false, bool driveScan = false) {
			if (bumpedSlotUtc is not { } slot) return null;
			if (folderScan || driveScan) {
				AgentPaths.AppendLog($"a {(folderScan ? "folder's" : "drive's")} scan doesn't stand in for the scheduled scan at {slot.ToLocalTime():g}: it runs as usual");
				return null;
			}
			if (!last.Ok) {
				AgentPaths.AppendLog($"scan brought forward didn't go through: the scheduled scan at {slot.ToLocalTime():g} runs as usual");
				return null;
			}
			return ScanBump.Record(slot, last.StartedUtc, everyMinutes);
		}

		/// <summary>
		/// The scan itself, holding scan.lock since <paramref name="started"/>. <paramref name="firstTurn"/>: a first scan in its
		/// turn among the agents' first rounds (FirstRound), at full speed since nothing else heavy runs then; a game still
		/// has it make way.
		/// </summary>
		static async Task<int> ScanAsync(AgentConfig cfg, bool notify, bool scheduled, IReadOnlyCollection<string>? drives, IReadOnlyCollection<string>? folders,
			IReadOnlyCollection<string>? only, DateTime started, bool firstTurn, CancellationToken ct) {
			var timer = Stopwatch.StartNew();
			var notes = new List<string>();
			bool fullSpeed = firstTurn || ScanPace.FullSpeed(cfg, scheduled);
			var settings = BuildSettings(cfg, notes, fullSpeed);
			if (settings.IncludeList.Count == 0) {
				Console.Error.WriteLine("None of the configured folders exist. Edit " + AgentPaths.Config);
				return 2;
			}
			double cpuCap = cfg.BackgroundCpuCap(Environment.ProcessorCount);
			bool paced = Power.SetPace(fullSpeed, cpuCap);
			Pace.MoreMemory = cfg.MoreMemory;
			// What changed on each drive since the last scan, from its change journal: a drive with nothing
			// new isn't walked, and one with something new has only those folders listed again.
			ListingPlan? plan = null;
			try { plan = ListingPlan.Make(settings, cfg, started, ct, drives, folders, only); }
			catch (Exception e) when (e is not OperationCanceledException && (folders is { Count: > 0 } || only is { Count: > 0 })) {
				// Asked for one folder or drive: walking every drive instead would be a far longer scan than the one asked for.
				string asked = string.Join(", ", folders is { Count: > 0 } ? folders : only!);
				AgentPaths.AppendLog($"scan of {asked} not started, the last listing not read: " + e.Message);
				Console.Error.WriteLine($"The scan of {asked} couldn't start: " + e.Message);
				return 1;
			}
			catch (Exception e) when (e is not OperationCanceledException) {
				AgentPaths.AppendLog("change journal not read, walking every folder: " + e.Message);
				// Drives scanned only when asked stay unread all the same: left out of this scan.
				foreach (string root in settings.IncludeList.Where(r => cfg.IsOnRequest(r) && !AgentConfig.IsOnDrive(r, drives)).ToList())
					settings.IncludeList.Remove(root);
			}
			// None of the folders asked for is there now, or each is left out of scans: there's nothing to read.
			if (folders is { Count: > 0 } && plan != null && plan.Roots.All(r => r.Mode == ListingMode.Resting)) {
				AgentPaths.AppendLog($"scan of {string.Join(", ", folders)} not started: no such folder among the scanned ones now");
				Console.Error.WriteLine("None of those folders is scanned: " + string.Join(", ", folders));
				return 1;
			}
			// Scan this drive now, and Heiward doesn't scan that drive (any more): there's nothing to read.
			if (only is { Count: > 0 } && plan != null && plan.Roots.All(r => r.Mode == ListingMode.Resting)) {
				AgentPaths.AppendLog($"scan of {string.Join(", ", only)} not started: Heiward doesn't scan it now");
				Console.Error.WriteLine("Heiward doesn't scan " + string.Join(", ", only));
				return 1;
			}
			// Each drive's "Last scanned" as the page gave it, from the last scan, while scans didn't keep each drive's yet: a
			// drive this scan leaves alone keeps it once they do.
			var shownBefore = DriveScans.Load() == null ? DriveScans.Before(ScanIndex.Load(), settings.IncludeList) : null;
			// Nothing a scan would see changed since the last one, which made this report with these
			// settings: a scheduled scan has nothing to do, and no drive is read, nor anything compared.
			if (scheduled && plan is { NothingChanged: true, SameScanAsLast: true } && Report.Load() != null) {
				plan.SaveSkipped();
				DriveScans.Record(ScannedWhole(plan, settings.IncludeList), started, shownBefore);
				AgentPaths.AppendLog("scan skipped, nothing new: " + plan.Describe());
				await NotifyAsync(cfg, notify, Housekeeping(cfg, ct), new());
				return 0;
			}
			AgentPaths.AppendLog($"scan started {(fullSpeed ? "at full speed" : $"in the background, at most {cpuCap:0.#}% of the processor")} ({settings.MaxDegreeOfParallelism} at once{(paced ? "" : ", pace not fully set")}): " +
				(plan?.Describe() ?? string.Join("; ", settings.IncludeList)));
			// The NPU, with a driver or plugin it hasn't been checked with, is checked first in its own process: a plugin that
			// ends its process while it compiles the model then ends the check's, not the scan's. Auto with no working NPU
			// takes the graphics card once it has passed a check: one it hasn't, checked now, in its own process too (this
			// one's ONNX Runtime must stay unloaded until the scan picks its device).
			if (settings.UseAiMatching)
				await NpuCheck.EnsureAsync(settings.AiDevice, AgentPaths.AppendLog, ct);
			if (settings.UseAiMatching && settings.AiDevice == AiDevice.Auto)
				await GpuCheck.EnsureAsync(cfg, AgentPaths.AppendLog, ct);
			// The card the scan's GPU work runs on, whose 3D engines a game would compete for.
			GpuAdapter? card = GpuAdapters.InUse(cfg.Gpu, GpuAdapters.List());
			using var stopPacing = CancellationTokenSource.CreateLinkedTokenSource(ct);
			Task pacing = FollowPageAsync(scheduled, fullSpeed, now => fullSpeed = now, card, stopPacing.Token, firstTurn);
			// Stop scan on the review page (or hei stop, or a pause) ends the scan as Ctrl+C would. It's heard until the scan
			// ends, while the report is made and the checks after it run too: once the engine was done it went unheard, for hours.
			ScanStop.Clear();
			using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
			using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(ct);
			Task watching = ScanStop.WatchAsync(started, stopped, stopWatching.Token);

			Func<string, bool>? mayRead = plan?.MayRead;
			List<string> reading = plan?.Roots.Where(r => r.Mode != ListingMode.Resting).Select(r => r.Root).ToList() ?? settings.IncludeList.ToList();
			var engine = new ScanEngine { Settings = settings, ListRoot = plan == null ? null : plan.ListingFor, MayRead = mayRead };
			int files = 0;
			string phase = ScanPhase.Listing;
			ScanProgressSnapshot? latest = null;
			// The drives' last counts, kept after the checking (the snapshots that follow have none): its
			// last snapshot, all done, can come and go between two writes.
			DriveProgress[]? driveCounts = null;
			engine.Progress += (_, e) => {
				if (phase == ScanPhase.Checking) files = Math.Max(files, e.MaxPosition);
				latest = e;
				if (e.Drives != null) driveCounts = e.Drives;
			};
			engine.FilesEnumerated += (_, _) => { latest = null; phase = ScanPhase.Checking; };
			// What the finishing is doing, and how far (files of the sets sorted, while the report is made).
			string finishing = "Finishing";
			int finishedFiles = 0, finishingFiles = 0;
			void Finish(string stage, int done = 0, int total = 0) { finishing = stage; finishedFiles = done; finishingFiles = total; }
			// Looked at once a second, whether or not a file finished (the listing reports nothing, and a drive's last
			// files can take minutes), and written when it's due (StatusPace).
			ScanStatus Now() {
				ScanProgressSnapshot? e = latest;
				bool finishingNow = phase == ScanPhase.Finishing;
				string stage = !string.IsNullOrEmpty(e?.CurrentStage) ? e.CurrentStage : phase switch {
					ScanPhase.Listing => "Finding files", ScanPhase.Checking => "Checking files", ScanPhase.Comparing => "Comparing", _ => finishing,
				};
				var ai = engine.AiLastRun;
				return new ScanStatus(Environment.ProcessId, started, stage, e?.CurrentPosition ?? (finishingNow ? finishedFiles : 0),
					e?.MaxPosition ?? (finishingNow ? finishingFiles : 0), fullSpeed, reading,
					phase, phase == ScanPhase.Listing ? engine.ListingTimes.Keys.ToList() : null,
					driveCounts?.Select(x => new DriveCheck(x.Root, x.DoneFiles, x.TotalFiles, x.DoneBytes, x.TotalBytes)).ToList(),
					ai?.Device, ai?.Utc);
			}
			await using var status = new StatusFile(Now, ScanPace.PageOpen);
			Report? previous;
			string device;
			List<ReportGroup> groups;
			Report report;
			AutoRun? auto;
			try {
				try {
					await RunEngineAsync(engine, () => { latest = null; phase = ScanPhase.Comparing; }, stopped.Token);
				}
				finally {
					stopPacing.Cancel();
					await pacing;
				}
				latest = null;
				phase = ScanPhase.Finishing;

				// The photos and videos the listing found (the progress counts the database's entries, a deleted file's too),
				// less the text files with a video's extension (TypeScript .ts files).
				List<(string Path, long Size)> media = engine.FoundMedia();
				if (media.Count > 0) files = media.Count;
				// Whichever build made it: after an update, the sets it already listed aren't new.
				previous = Report.LoadAny();
				// The device the embeddings actually ran on, after any fallback (the engine knows; a guess could say NPU for a CPU run).
				device = !settings.UseAiMatching ? "off" : engine.AiDeviceUsed ?? NpuComponents.DeviceFor(settings.AiDevice);
				string? cacheKey = engine.AiDeviceUsed != null ? engine.AiCacheKeyUsed : NpuComponents.CacheKeyFor(settings.AiDevice);
				var fingerprints = new ScanFingerprints(cacheKey, settings.UseAiMatching, stopped.Token) { MayRead = mayRead, TextDevice = settings.AiDevice };
				var hashes = ReportBuilder.ContentHashes.Load();
				Finish(SortingSets);
				try {
					groups = ReportBuilder.Build(engine.Duplicates, fingerprints, hashes, mayRead, engine.FoundFiles.Select(f => f.Path),
						(done, total) => Finish(SortingSets, done, total), stopped.Token);
				}
				finally {
					// The text reader's sessions (the NPU's graphs) are let go as soon as the sets are sorted.
					if (fingerprints.FinishReading() is { } read) AgentPaths.AppendLog(read);
				}
				Finish("Saving the report");
				try { hashes.Save(); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AgentPaths.AppendLog("saving the content hashes failed: " + e.Message); }
				// The report fingerprints the sound of the videos in it, once: the next scan reuses them.
				if (fingerprints.AudioAdded)
					try { DatabaseUtils.SaveDatabase(); }
					catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AgentPaths.AppendLog("saving the audio fingerprints failed: " + e.Message); }
				AiStatus.Record(cfg, device, "scan", engine.AiAcceleratorUsed, engine.AiCardUsed, engine.AiFallback);
				report = new Report(Report.CurrentVersion, started, timer.Elapsed.TotalSeconds, device, files,
					settings.IncludeList.ToList(), settings.ExcludedExtensions.OrderBy(e => e).ToList(), notes, groups, AppBuild.Current);
				report.Save();
				ScanIndex.Build(started, settings.IncludeList, media, engine.ListingTimes, engine.AnalysisTimes).Save();
				// This listing, and where the journal was read up to: the next scan starts from them.
				try { plan?.Save(engine.FoundFiles, fingerprints.ModifiedUtc, started); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AgentPaths.AppendLog("saving the listing failed: " + e.Message); }
				// Each drive this scan read, for its page's "Last scanned"; one left alone keeps its time. Whole roots only: a scan
				// that reads just some folders of a drive doesn't make the drive's "Last scanned", so it records nothing here.
				DriveScans.Record(ScannedWhole(plan, reading), started, shownBefore);
				// The report stands from here: Stop now leaves out what's still to come after it.
				try { auto = Housekeeping(cfg, stopped.Token, DevDrives(cfg, drives, only), stage => Finish(stage)); }
				catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
					AgentPaths.AppendLog("scan stopped after its report was saved: the developer check, the games check and automatic cleanup wait for the next scan");
					auto = null;
				}
			}
			catch (OperationCanceledException) {
				AgentPaths.AppendLog("scan aborted");
				Console.Error.WriteLine("Scan aborted.");
				return 130;
			}
			finally {
				stopWatching.Cancel();
				await watching;
				ScanStop.Clear();
			}
			await status.DisposeAsync();

			var decisions = DecisionStore.Load();
			var known = new HashSet<string>(previous?.Groups.Select(g => g.Key) ?? Enumerable.Empty<string>());
			// New sets that need the user; automatic cleanup takes care of the others without a word.
			var waiting = AutoCleaner.WaitingForUser(cfg, report, decisions, AutoCleanState.Load(), DateTime.UtcNow).Select(g => g.Key).ToHashSet();
			var fresh = groups.Where(g => !known.Contains(g.Key) && waiting.Contains(g.Key)).ToList();
			string summary = $"{groups.Count} group(s), {fresh.Count} new to review; {files:N0} files in {timer.Elapsed.TotalSeconds:N0} s, AI on {device}" +
				(engine.AiCardUsed != null ? $" ({engine.AiCardUsed})" : "");
			// Where the time went, so a slow scan says which part was slow.
			if (engine.DecodeSummary is { } decoding) AgentPaths.AppendLog("  " + decoding);
			if (engine.AiSummary is { } ai) AgentPaths.AppendLog("  " + ai);
			AgentPaths.AppendLog("scan done: " + summary);
			Console.Error.WriteLine("Scan done: " + summary);
			if (auto is { DidSomething: true }) Console.Error.WriteLine("Automatic cleanup: " + auto.Describe("; "));
			foreach (string n in notes) Console.Error.WriteLine("  note: " + n);
			await NotifyAsync(cfg, notify, auto, fresh.Where(AutoCleaner.Announced).ToList());
			return 0;
		}

		/// <summary>
		/// What runs on its own clock after every scheduled scan, whether the scan had anything to do: the
		/// developer check and the games check when each is due, and automatic cleanup of what has waited its days. Developer and game mode are each read once,
		/// fresh: Manor's settings may have changed since the last scan (<see cref="DevMode"/>, <see cref="GameMode"/>).
		/// </summary>
		/// <param name="drives">Drives scanned only when asked that this scan read (Scan this drive now): the developer check reads
		/// them too, now, due or not, so their developer items are brought up to date with their photos and videos.</param>
		/// <param name="stage">What it's doing now, for the scan's status on the page.</param>
		static AutoRun? Housekeeping(AgentConfig cfg, CancellationToken ct, IReadOnlyCollection<string>? drives = null, Action<string>? stage = null) {
			bool devChecked = false, gamesChecked = false;
			DevMode devMode = DevMode.Now(cfg);
			bool asked = devMode.On && drives is { Count: > 0 };
			if (asked || DevScan.Due(devMode)) {
				stage?.Invoke("Checking your developer files");
				try {
					devChecked = DevScan.RunAndSave(cfg, ct, drives) != null;
					if (asked && !devChecked)
						AgentPaths.AppendLog($"developer check already running, so the developer items on {string.Join(", ", drives!)} wait for the next Scan this drive now");
				}
				catch (Exception e) when (e is not OperationCanceledException) { AgentPaths.AppendLog("developer check failed: " + e.Message); }
			}
			// Game mode: the games check once a day too (GameMode, read fresh as developer mode is).
			GameMode gameMode = GameMode.Now(cfg);
			if (GameScan.Due(gameMode)) {
				stage?.Invoke("Checking your games");
				try { gamesChecked = GameScan.RunAndSave(cfg, ct) != null; }
				catch (Exception e) when (e is not OperationCanceledException) { AgentPaths.AppendLog("games check failed: " + e.Message); }
			}
			// Stopped by now: nothing is removed either.
			ct.ThrowIfCancellationRequested();
			stage?.Invoke("Cleaning up automatically");
			try { return AutoCleaner.RunAndSave(cfg, devMode, devChecked, new CleanupActions(cfg, automatic: true), gameMode, gamesChecked); }
			catch (Exception e) when (e is not OperationCanceledException) { AgentPaths.AppendLog("automatic cleanup failed: " + e.Message); }
			return null;
		}

		/// <summary>The notifications: what automatic cleanup did, and new duplicates to review (look-alikes go unannounced). Nothing new, nothing shown.</summary>
		static async Task NotifyAsync(AgentConfig cfg, bool notify, AutoRun? auto, List<ReportGroup> fresh) {
			// The review page this may start outlives the scan.
			Power.LiftCpuCap();
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
				Gpu = cfg.Gpu,
				SkipCloudPlaceholders = true,
				// The listing just saw every file; asking the disk about each one again could wake a sleeping drive.
				ListingProvesExistence = true,
				// And whatever it didn't see in the folders it listed is gone, or in a folder scans now leave out: forgotten,
				// so the database holds what's there to compare, not every file a scan ever saw.
				ForgetUnlisted = true,
				UseWindowsImageDecoder = true,
				// A hard link, or a folder seen again through a drive letter mapped onto it, is the same file under two
				// paths: no copy, and removing it frees nothing (or takes the other path with it).
				ExcludeHardLinks = true,
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
					UseShellExecute = false, CreateNoWindow = ChildConsole.OwnConsole, RedirectStandardOutput = true, RedirectStandardError = true,
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
		/// switches apply at once). The number of files decoded at once stays as the scan started.
		/// And it makes way for games and 3D programs (<see cref="ThreeDWatch"/>) on <paramref name="card"/>, the one its GPU
		/// work runs on: while one runs, the scan runs in the background and with less memory (<see cref="Pace.MoreMemory"/>),
		/// whatever the settings say, until it's gone. Two checks in a row either way, so a moment's 3D work doesn't flip it.
		/// </summary>
		static async Task FollowPageAsync(bool scheduled, bool current, Action<bool> changed, GpuAdapter? card, CancellationToken ct, bool firstTurn = false) {
			using var watch = new ThreeDWatch(card);
			string? makingWayFor = null;
			int busyChecks = 0, freeChecks = 0;
			while (true) {
				try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
				catch (OperationCanceledException) { return; }
				var cfg = AgentConfig.Load();
				if (watch.Busy() is { } busy) {
					freeChecks = 0;
					if (++busyChecks >= 2 && makingWayFor == null) {
						makingWayFor = busy;
						AgentPaths.AppendLog($"scan: making way for {busy}, in the background with less memory until it's gone");
					}
				}
				else {
					busyChecks = 0;
					if (makingWayFor != null && ++freeChecks >= 2) {
						AgentPaths.AppendLog($"scan: {makingWayFor} is gone, back to the usual pace");
						makingWayFor = null;
					}
				}
				Pace.MoreMemory = cfg.MoreMemory && makingWayFor == null;
				bool wanted = (firstTurn || ScanPace.FullSpeed(cfg, scheduled)) && makingWayFor == null;
				if (wanted == current) continue;
				current = wanted;
				Power.SetPace(wanted, cfg.BackgroundCpuCap(Environment.ProcessorCount));
				changed(wanted);
				if (makingWayFor == null)
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

		internal static FileStream? TryLock() {
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

		/// <summary>When the last scan started, whether it finished, was stopped or failed; null before the first.</summary>
		public static DateTime? LastStartedUtc() {
			try {
				return File.Exists(AgentPaths.ScanStarted) && DateTime.TryParse(File.ReadAllText(AgentPaths.ScanStarted).Trim(),
					System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t)
					? t.ToUniversalTime() : null;
			}
			catch { return null; }
		}

		public static ScanStatus? ReadStatus() {
			try {
				return File.Exists(AgentPaths.ScanStatus)
					? JsonSerializer.Deserialize<ScanStatus>(File.ReadAllText(AgentPaths.ScanStatus), AgentConfig.Json)
					: null;
			}
			catch { return null; }
		}
	}

	/// <summary>
	/// scan-status.json while a scan runs: written now, then looked at once a second on one thread, and written again
	/// when <see cref="StatusPace"/> says so, until disposed, which writes what hasn't landed yet and deletes it.
	/// </summary>
	sealed class StatusFile : IAsyncDisposable {
		readonly CancellationTokenSource stop = new();
		readonly Task loop;
		readonly Func<ScanStatus> now;
		readonly Action<string> write;
		readonly Action delete;
		readonly StatusPace pace;

		/// <param name="now">The scan's status as it is now.</param>
		/// <param name="watched">The review page is open and showing (<see cref="ScanPace.PageOpen"/>).</param>
		/// <param name="write">Writes the status's JSON; scan-status.json unless a test says otherwise.</param>
		/// <param name="delete">Deletes it once the scan is done; scan-status.json unless a test says otherwise.</param>
		/// <param name="time">The clock and the second's tick: the system's, unless a test moves them itself.</param>
		public StatusFile(Func<ScanStatus> now, Func<bool> watched, Action<string>? write = null, Action? delete = null, TimeProvider? time = null) {
			time ??= TimeProvider.System;
			this.now = now;
			this.write = write ?? (json => AgentPaths.WriteAtomic(AgentPaths.ScanStatus, json));
			this.delete = delete ?? (() => File.Delete(AgentPaths.ScanStatus));
			pace = new StatusPace(watched, time);
			Look();
			// The second's tick starts now, not when the thread gets going.
			var tick = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
			loop = Task.Run(async () => {
				using (tick) {
					try {
						while (await tick.WaitForNextTickAsync(stop.Token))
							Look();
					}
					catch (OperationCanceledException) { }
				}
			});
		}

		/// <summary>The status now, written if it's due (or, <paramref name="last"/>, if it hasn't landed).</summary>
		void Look(bool last = false) {
			try {
				ScanStatus status = now();
				if (pace.Due(status, last) is not { } json) return;
				write(json);
				pace.Wrote(status, json);
			}
			catch { /* progress is cosmetic */ }
		}

		public async ValueTask DisposeAsync() {
			if (stop.IsCancellationRequested) return;
			stop.Cancel();
			await loop;
			// The last state lands, whatever the pace held back.
			Look(last: true);
			try { delete(); } catch { }
		}
	}

	/// <summary>
	/// When scan-status.json is written again. Only the review page reads it, every 1.5 s while it's showing (app.js,
	/// PROGRESS_EVERY), and it reads nothing while it's hidden or closed. So a new step (<see cref="ScanStatus.Phase"/>),
	/// a new stage of comparing or finishing, or a change of pace is written at once; the counts moving alone, at most
	/// every <see cref="Watched"/> while the page is showing, and every <see cref="Alone"/> while it isn't: a page opened
	/// then marks itself seen with its first look, and the next second's look writes. A status the same as the last
	/// written isn't written at all. Once a second it had been, a temporary file and a replace each time, for as long as
	/// a scan ran.
	/// </summary>
	sealed class StatusPace(Func<bool> watched, TimeProvider time) {
		/// <summary>
		/// While the page shows: every second of its looks sees counts that moved, and the AI's last run is never older than
		/// the page's "Running on the NPU" allows (AI_RUNNING_MS, 10 s).
		/// </summary>
		internal static readonly TimeSpan Watched = TimeSpan.FromSeconds(3);
		/// <summary>While nobody looks: the page's first look after it opens is at most this far behind, and the next one isn't.</summary>
		internal static readonly TimeSpan Alone = TimeSpan.FromSeconds(30);

		ScanStatus? last;
		string? written;
		long writtenAt;

		/// <summary>The status's JSON when it's due to be written now, else null. <paramref name="final"/>: whenever it hasn't landed.</summary>
		public string? Due(ScanStatus status, bool final = false) {
			string json = JsonSerializer.Serialize(status, AgentConfig.Json);
			if (json == written) return null;
			if (final || last == null || status.Phase != last.Phase || status.FullSpeed != last.FullSpeed ||
				status.Stage != last.Stage && status.Phase != ScanPhase.Checking) // checking names each file's step: the page shows the counts
				return json;
			TimeSpan since = time.GetElapsedTime(writtenAt);
			return since >= Alone || since >= Watched && watched() ? json : null;
		}

		/// <summary>It's written.</summary>
		public void Wrote(ScanStatus status, string json) {
			last = status;
			written = json;
			writtenAt = time.GetTimestamp();
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
