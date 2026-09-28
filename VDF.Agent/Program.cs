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

using System.CommandLine;
using System.Runtime.InteropServices;
using VDF.Agent;
using VDF.Core.AI;
using VDF.Core.FFTools;
using VDF.Core.Utils;

// hei (Heiward): finds likely duplicate photos and videos in the background (on the NPU when there
// is one), and stale developer files once a day, and lists them on a local review page. It never deletes anything on its own; files the user
// ticks go to the Recycle Bin. Run without arguments, it installs itself (or, once installed, opens
// the review page), so the one exe is also the installer.
var root = new RootCommand("hei — Heiward finds duplicate photos and videos, and stale developer files, and lists them for review");
root.SetAction(async (_, ct) => {
	if (Installer.RunningInstalled) {
		await OpenReviewPageAsync(AgentConfig.Load(), ct);
		return 0;
	}
	int code = await Installer.InstallAsync(dryRun: false, assumeYes: false, device: null, ct);
	if (!Console.IsInputRedirected) {
		Console.WriteLine("Press Enter to close.");
		Console.ReadLine();
	}
	return code;
});

var notify = new Option<bool>("--notify") { Description = "Show a Windows notification when the scan finds new duplicates." };
var open = new Option<bool>("--open") { Description = "Open the review page (it shows the scan's progress)." };
var scheduled = new Option<bool>("--scheduled") { Description = "Started by Task Scheduler: on battery, step aside in Battery Saver or below the configured charge." };
var scan = new Command("scan", "Scan the configured folders now and update the report.") { notify, open, scheduled };
scan.SetAction(async (r, ct) => {
	var cfg = AgentConfig.Load();
	if (r.GetValue(scheduled)) {
		if (Power.ShouldSkip(cfg, out string why)) {
			AgentPaths.AppendLog("scheduled scan skipped: " + why);
			return 0;
		}
		// Background scans take their time: Windows runs them on efficient cores at low clocks.
		AgentPaths.AppendLog(Power.EnterEfficiencyMode() ? "scheduled scan in efficiency mode" : "scheduled scan: efficiency mode unavailable");
	}
	if (r.GetValue(open)) {
		// Open first: the page shows the scan's progress, and the first scan of a library takes a while.
		_ = OpenReviewPageAsync(cfg, ct);
	}
	return await AgentScanner.RunAsync(cfg, r.GetValue(notify), ct);
});
root.Subcommands.Add(scan);

var noBrowser = new Option<bool>("--no-browser") { Description = "Don't open a browser tab." };
var serve = new Command("serve", "Serve the review page on 127.0.0.1 until it sits unused.") { noBrowser };
serve.SetAction((r, ct) => ReviewServer.RunAsync(AgentConfig.Load(), !r.GetValue(noBrowser), ct));
root.Subcommands.Add(serve);

var ifPending = new Option<bool>("--if-pending") { Description = "Only when something waits for review." };
var onceADay = new Option<bool>("--once-a-day") { Description = "At most once per day (the sign-in task uses this)." };
var openCmd = new Command("open", "Open the review page in the default browser (starting it if needed).") { ifPending, onceADay };
openCmd.SetAction(async (r, ct) => {
	var cfg = AgentConfig.Load();
	if (r.GetValue(ifPending) && PendingCount() == 0) return 0;
	string stamp = Path.Combine(AgentPaths.Home, "last-opened.txt");
	string today = DateTime.Now.ToString("yyyy-MM-dd");
	if (r.GetValue(onceADay)) {
		try { if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == today) return 0; } catch { }
	}
	await OpenReviewPageAsync(cfg, ct);
	try { AgentPaths.WriteAtomic(stamp, today); } catch { }
	return 0;
});
root.Subcommands.Add(openCmd);

var reuseFrom = new Option<string[]>("--reuse-from") {
	Description = "A folder where another Heiward or Video Duplicate Finder keeps its bin\\ and ai\\ folders: copy FFmpeg and the AI components from it instead of downloading them. Repeatable.",
};
var setup = new Command("setup", "Get FFmpeg and the AI components (and the NPU pack on Snapdragon PCs), copied from --reuse-from folders when they have them, then report what this PC will use.") { reuseFrom };
setup.SetAction(async (r, ct) => {
	try {
		await Installer.EnsurePrerequisitesAsync(ComponentReuse.Sources(r.GetValue(reuseFrom), CoreUtils.StateFolder), dryRun: false, ct);
		using var embedder = OnnxEmbedder.Create(AiDevice.Auto);
		Console.WriteLine($"Ready. AI matching runs on the {embedder.DeviceName}.");
		AiStatus.Record(AgentConfig.Load(), embedder.DeviceName, "setup");
		return 0;
	}
	catch (Exception e) when (e is not OperationCanceledException) {
		Console.Error.WriteLine($"Setup failed: {e.Message}");
		return 1;
	}
});
root.Subcommands.Add(setup);

var dryRun = new Option<bool>("--dry-run") { Description = "Print every step without changing anything." };
var yes = new Option<bool>("--yes", "-y") { Description = "Answer yes to questions (unattended install)." };
var deviceOpt = new Option<AiDevice?>("--device") { Description = "Where the AI runs: npu, gpu or cpu. Default: the NPU if there is one, otherwise ask (GPU or CPU)." };
var onDemandOpt = new Option<bool>("--on-demand") { Description = "No scheduled scans: scan only when you press Scan now. Default without an NPU: ask (every 6 hours or on demand)." };
var install = new Command("install", "Install for this user (no admin): prerequisites, scheduled scans (hourly on an NPU, every 6 hours on a GPU or CPU), sign-in review page, Start menu, Apps & Features.") { dryRun, yes, deviceOpt, onDemandOpt, reuseFrom };
install.SetAction((r, ct) => Installer.InstallAsync(r.GetValue(dryRun), r.GetValue(yes), r.GetValue(deviceOpt), ct,
	r.GetResult(onDemandOpt) != null ? r.GetValue(onDemandOpt) : null, r.GetValue(reuseFrom)));

// Opens a session on one device and reports where the model actually runs (the installer's GPU
// check runs this in its own process: a process can only load one ONNX Runtime).
var probeDevice = new Option<AiDevice>("--device") { Description = "npu, gpu or cpu.", DefaultValueFactory = _ => AiDevice.Auto };
var probe = new Command("probe", "Check where the AI model runs on this PC.") { probeDevice };
probe.Hidden = true;
probe.SetAction(r => {
	AiDevice wanted = r.GetValue(probeDevice);
	using var embedder = OnnxEmbedder.Create(wanted);
	embedder.EmbedBatch(new[] { new byte[OnnxEmbedder.InputSide * OnnxEmbedder.InputSide * 3] });
	Console.WriteLine($"The AI model runs on the {embedder.DeviceName}.");
	return wanted is AiDevice.Auto || string.Equals(embedder.DeviceName, wanted.ToString(), StringComparison.OrdinalIgnoreCase) ? 0 : 1;
});
root.Subcommands.Add(probe);
root.Subcommands.Add(install);

var purge = new Option<bool>("--purge") { Description = "Also delete settings, the report and caches." };
var uninstall = new Command("uninstall", "Remove Heiward, its tasks and shortcuts. Keeps the report and settings unless --purge.") { purge, dryRun };
uninstall.SetAction(r => Installer.Uninstall(r.GetValue(purge), r.GetValue(dryRun)));
root.Subcommands.Add(uninstall);

var status = new Command("status", "Show the settings, the last report and the schedule.");
status.SetAction(_ => {
	var cfg = AgentConfig.Load();
	Console.WriteLine($"Installed: {(File.Exists(Installer.InstalledExe) ? Installer.InstallDir : "no")}");
	Console.WriteLine($"Settings: {AgentPaths.Config}{(File.Exists(AgentPaths.Config) ? "" : " (defaults; not saved yet)")}");
	Console.WriteLine($"Scans: {string.Join("; ", ScanScope.Roots(cfg))}{(cfg.ScanAllDrives ? " (every fixed drive, minus system, app and game folders: 'hei scope')" : "")}");
	if (cfg.ExcludeExtensions.Count > 0) Console.WriteLine($"Skipped types: {string.Join(" ", cfg.ExcludeExtensions)}");
	Console.WriteLine($"Schedule: {Scheduler.Describe(cfg)}{(cfg.ScanEveryMinutes > 0 && cfg.ScanOnBattery ? $", on battery too above {cfg.MinBatteryPercent}% unless Battery Saver is on" : "")}");
	if (AiStatus.Load() is { } ai) Console.WriteLine($"AI: {ai.Describe()} (checked by the {ai.Source}, {ai.CheckedAtUtc.ToLocalTime():g})");
	var report = Report.Load();
	if (report == null) Console.WriteLine("No scan yet: run 'hei scan'.");
	else {
		Console.WriteLine($"Last scan: {report.ScannedAtUtc.ToLocalTime():g}, {report.FilesScanned:N0} files in {report.DurationSec:N0} s, AI on {report.Device}");
		var decisions = DecisionStore.Load();
		var pending = report.Groups.Where(g => !decisions.ContainsKey(g.Key)).ToList();
		Console.WriteLine($"To review: {pending.Count} set(s), up to {Format.Bytes(pending.Sum(g => g.ReclaimBytes))} to free");
		foreach (string n in report.Notes) Console.WriteLine("  note: " + n);
	}
	Console.WriteLine($"Next scheduled scan: {Scheduler.NextRun() ?? "not scheduled (run 'hei install')"}");
	Console.WriteLine($"NPU lock shared with: {NpuLock.LockDirectory ?? "(no other NPU tool found)"}");
	Console.WriteLine($"Scan running: {(AgentScanner.IsRunning() ? "yes" : "no")}");
	return 0;
});
root.Subcommands.Add(status);

var devScan = new Option<bool>("--scan") { Description = "Check again now (otherwise: show the last check)." };
var pruneBranches = new Option<string?>("--prune-branches") { Description = "Delete the repository's local branches already merged into its remote's main/master (fetches first; never main, master, develop or a checked-out branch)." };
var dev = new Command("dev", "Developer mode: build outputs, worktrees, caches, emulators and temp files that tools recreate.") { devScan, pruneBranches };
dev.SetAction(r => {
	var cfg = AgentConfig.Load();
	if (r.GetValue(pruneBranches) is { } repoPath) {
		PruneResult pruned = BranchPruner.Prune(Path.GetFullPath(repoPath));
		if (pruned.Error != null) { Console.Error.WriteLine(pruned.Error); return 1; }
		if (!pruned.Fetched) Console.WriteLine("Couldn't fetch; used the last fetched state.");
		Console.WriteLine(pruned.Deleted.Count == 0 ? "No merged branches to delete." : $"Deleted {pruned.Deleted.Count}: {string.Join(", ", pruned.Deleted)}");
		foreach (PruneKept k in pruned.Kept) Console.WriteLine($"Kept {k.Branch}: {k.Reason}");
		return 0;
	}
	DevReport? report = r.GetValue(devScan) ? DevScan.RunAndSave(cfg) ?? DevReport.Load() : DevReport.Load();
	if (report == null) {
		Console.WriteLine("No developer check yet: run 'hei dev --scan'.");
		return 0;
	}
	Console.WriteLine($"Checked {report.ScannedAtUtc.ToLocalTime():g} in {report.DurationSec:N0} s.");
	foreach (DevCategory c in report.Categories) {
		Console.WriteLine($"{c.Title}: {Format.Bytes(c.Items.Sum(i => i.Bytes))}, {Format.Bytes(c.Items.Where(i => i.Suggested).Sum(i => i.Bytes))} ticked");
		foreach (DevItem i in c.Items.Take(8))
			Console.WriteLine($"  {(i.Suggested ? "[x]" : i.Blocked != null ? "[-]" : "[ ]")} {Format.Bytes(i.Bytes),9}  {i.Name}  {i.Detail}{(i.Blocked != null ? $" ({i.Blocked})" : "")}");
	}
	return 0;
});
root.Subcommands.Add(dev);

var count = new Option<bool>("--count") { Description = "List the drives as a scan would (names and attributes only; no file is opened) and count the photos and videos per folder." };
var scope = new Command("scope", "Show what a scan looks at and what it leaves out.") { count };
scope.SetAction(r => {
	var cfg = AgentConfig.Load();
	var notes = new List<string>();
	var settings = AgentScanner.BuildSettings(cfg, notes);
	Console.WriteLine("Scanned, with subfolders:");
	foreach (string rootFolder in settings.IncludeList) Console.WriteLine("  " + rootFolder);
	Console.WriteLine("Left out (with everything inside):");
	foreach (string excluded in settings.BlackList) Console.WriteLine("  " + excluded);
	Console.WriteLine($"  folders holding {string.Join(", ", settings.SkipFoldersContaining)} (code repositories), folder links, cloud-only files");
	foreach (string n in notes) Console.WriteLine("note: " + n);
	if (!r.GetValue(count)) return 0;

	var perFolder = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
	var timer = System.Diagnostics.Stopwatch.StartNew();
	int total = 0;
	foreach (string rootFolder in settings.IncludeList) {
		var files = VDF.Core.Utils.FileUtils.GetFilesRecursive(rootFolder, settings.IgnoreReadOnlyFolders, settings.IgnoreReparsePoints,
			recursive: true, settings.IncludeImages, settings.BlackList.ToList(), CancellationToken.None, settings.SkipCloudPlaceholders, settings.ExcludedExtensions,
			settings.SkipFoldersContaining, settings.SkipFolderLinks);
		total += files.Count;
		foreach (FileInfo f in files) {
			// Grouped three levels below the drive: C:\Users\me\Pictures, D:\Photos\2019.
			string rel = Path.GetRelativePath(rootFolder, f.DirectoryName ?? rootFolder);
			string key = Path.Combine(rootFolder, string.Join(Path.DirectorySeparatorChar, rel.Split(Path.DirectorySeparatorChar).Take(3)));
			perFolder.TryGetValue(key, out var c);
			perFolder[key] = (c.Files + 1, c.Bytes + f.Length);
		}
	}
	Console.WriteLine($"{total:N0} photos and videos found in {timer.Elapsed.TotalSeconds:N1} s. By folder:");
	foreach (var (folder, c) in perFolder.OrderByDescending(kv => kv.Value.Files).Take(40))
		Console.WriteLine($"  {c.Files,8:N0}  {Format.Bytes(c.Bytes),9}  {folder}");
	return 0;
});
root.Subcommands.Add(scope);

return await root.Parse(args).InvokeAsync();

static int PendingCount() {
	var report = Report.Load();
	if (report == null) return 0;
	var decisions = DecisionStore.Load();
	return report.Groups.Count(g => !decisions.ContainsKey(g.Key));
}

/// <summary>Starts the review page in the background if needed, waits until it answers, opens it.</summary>
static async Task OpenReviewPageAsync(AgentConfig cfg, CancellationToken ct) {
	ReviewServer.EnsureRunningInBackground(cfg);
	for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(cfg.Port); i++)
		await Task.Delay(250, ct);
	ReviewServer.OpenBrowser(cfg.Port);
}

namespace VDF.Agent {
	/// <summary>Battery checks for scheduled scans (GetSystemPowerStatus).</summary>
	static class Power {
		[StructLayout(LayoutKind.Sequential)]
		struct SystemPowerStatus {
			public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
			public int BatteryLifeTime, BatteryFullLifeTime;
		}

		[DllImport("kernel32.dll")]
		static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

		[StructLayout(LayoutKind.Sequential)]
		struct ProcessPowerThrottlingState {
			public uint Version, ControlMask, StateMask;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool SetProcessInformation(IntPtr process, int infoClass, ref ProcessPowerThrottlingState info, int size);

		[DllImport("kernel32.dll")]
		static extern IntPtr GetCurrentProcess();

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

		/// <summary>
		/// EcoQoS, the power half of Task Manager's "Efficiency mode": Windows runs the process on
		/// efficient cores at low clocks, so a background scan takes longer and costs little power.
		/// Priority stays below normal rather than idle: the scan holds the shared NPU lock for up to
		/// two seconds at a time, and an idle-priority thread starved while holding it would make
		/// other NPU tools wait.
		/// </summary>
		public static bool EnterEfficiencyMode() {
			const int ProcessPowerThrottling = 4;
			const uint ExecutionSpeed = 0x1, BelowNormalPriorityClass = 0x4000;
			var state = new ProcessPowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = ExecutionSpeed };
			bool eco = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
			bool low = SetPriorityClass(GetCurrentProcess(), BelowNormalPriorityClass);
			return eco && low;
		}

		public static bool ShouldSkip(AgentConfig cfg, out string why) {
			why = "";
			if (!GetSystemPowerStatus(out var s) || s.ACLineStatus != 0 || (s.BatteryFlag & 128) != 0)
				return false; // on AC, unknown, or no battery
			if (!cfg.ScanOnBattery) { why = "on battery (scanOnBattery is off)"; return true; }
			if ((s.SystemStatusFlag & 1) != 0) { why = "Battery Saver is on"; return true; }
			if (s.BatteryLifePercent != 255 && s.BatteryLifePercent < cfg.MinBatteryPercent) { why = $"battery at {s.BatteryLifePercent}%"; return true; }
			return false;
		}
	}
}
