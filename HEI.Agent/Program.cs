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
using HEI.Agent;
using HEI.Core.AI;
using HEI.Core.FFTools;
using HEI.Core.Utils;

// hei (Heiward): finds likely duplicate photos and videos in the background (on the NPU when there
// is one), and stale developer files once a day, and lists them on a local review page. It deletes
// nothing on its own unless the user turns on automatic cleanup; copies go to the Recycle Bin. Run
// without arguments, it installs itself (or, once installed, opens the review page), so the one exe
// is also the installer.
// The Store version keeps the AI components in its package's storage: its own folder is read-only, and
// Windows removes that storage with the app.
if (StorePackage.IsPackaged) CoreUtils.UseStateFolder(Path.Combine(StorePackage.Storage, "components"));

// A heiward: link arrives as an argument. heiward://start is the review page's "Start Heiward", when it
// can't reach Heiward: start it in the background, the page reloads by itself. Any other opens the page.
if (args.FirstOrDefault(a => a.StartsWith("heiward:", StringComparison.OrdinalIgnoreCase)) is string link)
	args = link.TrimEnd('/').EndsWith("start", StringComparison.OrdinalIgnoreCase) ? new[] { "serve", "--no-browser" } : new[] { "open" };

var root = new RootCommand("hei — Heiward finds duplicate photos and videos, and stale developer files, and lists them for review");
root.SetAction(async (_, ct) => {
	// From the Store package's folder but without its identity, as its desktop shortcut starts it: the packaged
	// app takes over, as from the Start menu.
	if (StorePackage.InPackageFolder && !StorePackage.IsPackaged && StorePackage.ActivateFromFolder()) return 0;
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
var scheduled = new Option<bool>("--scheduled") { Description = "Started by Task Scheduler: on battery, step aside in Battery Saver or below the configured charge; run in the background unless the review page is open." };
var drive = new Option<string[]>("--drive") { Description = "Also read these drives that are scanned only when you ask (onRequestDrives), e.g. --drive D:\\. Without it they're left alone." };
var waitTurn = new Option<bool>("--wait-turn") { Description = "A first scan the person didn't start by hand (the install's): waits its turn among the manor's agents' first rounds, as a scheduled one does." };
var scan = new Command("scan", "Scan the configured folders now and update the report.") { notify, open, scheduled, drive, waitTurn };
scan.SetAction(async (r, ct) => {
	var cfg = AgentConfig.Load();
	if (r.GetValue(scheduled)) {
		// The page stays up, paused or not: should it have gone (a crash, a stopped install), it's back within a
		// scan's interval. Before the scan sets its pace, so the page doesn't start capped or at a low priority.
		ReviewServer.EnsureRunningInBackground(cfg);
		if (AgentPause.Load() is { } pause) {
			AgentPaths.AppendLog("scheduled scan skipped: scans are paused " + pause.Describe(DateTime.UtcNow));
			return 0;
		}
		if (Power.ShouldSkip(cfg, out string why)) {
			AgentPaths.AppendLog("scheduled scan skipped: " + why);
			return 0;
		}
	}
	if (r.GetValue(open)) {
		// Open first: the page shows the scan's progress, and the first scan of a library takes a while.
		_ = OpenReviewPageAsync(cfg, ct);
	}
	return await AgentScanner.RunAsync(cfg, r.GetValue(notify), r.GetValue(scheduled), ct, r.GetValue(drive), r.GetValue(waitTurn));
});
root.Subcommands.Add(scan);

var pauseMinutes = new Option<int?>("--minutes") { Description = $"How long, 1 to {AgentPause.MaxMinutes}. Without it: until 'hei resume'." };
var pauseCmd = new Command("pause", "Pause scheduled scans and stop the one running: for --minutes, or until 'hei resume'. Scan now still works.") { pauseMinutes };
pauseCmd.SetAction(r => {
	var pause = AgentPause.Start(r.GetValue(pauseMinutes), DateTime.UtcNow);
	if (AgentScanner.IsRunning()) ScanStop.Request();
	Console.WriteLine($"Scheduled scans are paused {pause.Describe(DateTime.UtcNow)}.");
	return 0;
});
root.Subcommands.Add(pauseCmd);

var resumeCmd = new Command("resume", "Resume scheduled scans after 'hei pause'.");
resumeCmd.SetAction(_ => {
	AgentPause.Resume();
	Console.WriteLine("Scheduled scans are back on.");
	return 0;
});
root.Subcommands.Add(resumeCmd);

var stopCmd = new Command("stop", "Stop the scan that's running (scheduled scans carry on; 'hei pause' stops those too).");
stopCmd.SetAction(_ => {
	if (!AgentScanner.IsRunning()) {
		Console.WriteLine("No scan is running.");
		return 0;
	}
	ScanStop.Request();
	Console.WriteLine("The scan stops within a few seconds.");
	return 0;
});
root.Subcommands.Add(stopCmd);

var noBrowser = new Option<bool>("--no-browser") { Description = "Don't open a browser tab." };
var serve = new Command("serve", "Serve the review page on 127.0.0.1, in this window (what open starts in the background).") { noBrowser };
serve.SetAction((r, ct) => ReviewServer.RunAsync(AgentConfig.Load(), !r.GetValue(noBrowser), ct));
root.Subcommands.Add(serve);

var ifPending = new Option<bool>("--if-pending") { Description = "Open the browser only when duplicates wait for review (look-alikes don't count)." };
var onceADay = new Option<bool>("--once-a-day") { Description = "Open the browser at most once per day (the sign-in task uses this)." };
var openNoBrowser = new Option<bool>("--no-browser") { Description = "Only make sure the page is up." };
var openCmd = new Command("open", "Make sure the review page is up, and open it in the default browser.") { ifPending, onceADay, openNoBrowser };
openCmd.SetAction(async (r, ct) => {
	var cfg = AgentConfig.Load();
	string stamp = Path.Combine(AgentPaths.Home, "last-opened.txt");
	string today = DateTime.Now.ToString("yyyy-MM-dd");
	bool browser = !r.GetValue(openNoBrowser) && !(r.GetValue(ifPending) && PendingCount(stamp) == 0);
	if (browser && r.GetValue(onceADay)) {
		try { if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == today) browser = false; } catch { }
	}
	// The page is up whether or not the browser opens: the sign-in task is how it comes back after a restart.
	if (!browser) {
		await EnsurePageUpAsync(cfg, ct);
		return 0;
	}
	await OpenReviewPageAsync(cfg, ct);
	try { AgentPaths.WriteAtomic(stamp, today); } catch { }
	return 0;
});
root.Subcommands.Add(openCmd);

var reuseFrom = new Option<string[]>("--reuse-from") {
	Description = "A folder that already holds FFmpeg and the AI components in bin\\ and ai\\, such as another copy of Heiward's: copy them from it instead of downloading them. Repeatable.",
};
var setup = new Command("setup", "Get FFmpeg and the AI components (and the pack for the PC's NPU), copied from --reuse-from folders or the installed copy when they have them, then report what this PC will use.") { reuseFrom };
setup.SetAction(async (r, ct) => {
	try {
		// The installed copy's folder too: a development build starts empty, and the installed Heiward has them.
		var sources = ComponentReuse.Sources((r.GetValue(reuseFrom) ?? Array.Empty<string>()).Append(Installer.InstallDir), CoreUtils.StateFolder);
		await Installer.EnsurePrerequisitesAsync(sources, dryRun: false, ct);
		var cfg = AgentConfig.Load();
		AiDevice setting = Enum.TryParse(cfg.AiDevice, ignoreCase: true, out AiDevice d) ? d : AiDevice.Auto;
		// The card the settings name, as a scan takes it; and, for Auto without a working NPU, checked first if it hasn't been.
		GpuAdapters.Choose(cfg.Gpu);
		if (setting == AiDevice.Auto) await GpuCheck.EnsureAsync(cfg, line => Console.WriteLine("  " + line), ct);
		using var embedder = OnnxEmbedder.Create(setting);
		Console.WriteLine($"Ready. AI matching runs on the {embedder.DeviceName}{(embedder.Card != null ? $" ({embedder.Card.Key})" : "")}.");
		if (embedder.Fallback != null) Console.WriteLine("  It fell back: " + embedder.Fallback);
		AiStatus.Record(cfg, embedder.DeviceName, "setup", embedder.AcceleratorId, embedder.Card?.Key, embedder.Fallback);
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
var speedOpt = new Option<string?>("--scan-speed") { Description = "How hard scans work: background (efficiency mode, slower), full (as fast as possible), or auto (full speed while you're on the review page). Default: ask." };
speedOpt.AcceptOnlyFromAmong(AgentConfig.ScanSpeeds);
var installNoBrowser = new Option<bool>("--no-browser") { Description = "Don't open the review page when done." };
var removeGitHubOpt = new Option<bool>("--remove-github-copy") { Description = "The Store version: remove Heiward installed from GitHub (its shortcuts, Apps & Features entry and folder). Settings and history stay." };
var gpuOpt = new Option<string?>("--gpu") { Description = "On a PC with more than one graphics card, the one for GPU work (AI matching on the GPU, decoding videos and iPhone photos): its number as the installer lists them, its name, or default (Windows' default). Default: ask; with --yes, the one with the most memory of its own." };
var install = new Command("install", "Install for this user (no admin): prerequisites, scheduled scans (hourly on an NPU, every 6 hours on a GPU or CPU), sign-in review page, Start menu and desktop shortcuts, Apps & Features.") { dryRun, yes, deviceOpt, onDemandOpt, speedOpt, installNoBrowser, removeGitHubOpt, reuseFrom, gpuOpt };
install.SetAction((r, ct) => Installer.InstallAsync(r.GetValue(dryRun), r.GetValue(yes), r.GetValue(deviceOpt), ct,
	r.GetResult(onDemandOpt) != null ? r.GetValue(onDemandOpt) : null, r.GetValue(reuseFrom), r.GetValue(speedOpt), openPage: !r.GetValue(installNoBrowser),
	removeGitHubCopy: r.GetValue(removeGitHubOpt), gpu: r.GetValue(gpuOpt)));

// Opens a session on one device and reports where the model actually runs (the installer's GPU
// check runs this in its own process: a process can only load one ONNX Runtime). A GPU check is
// recorded for the card (GpuChecks: Auto takes a card that passed one), and a card that fails it gets
// Heiward's own failure marker, which its Auto skips for 10 minutes.
var probeDevice = new Option<AiDevice>("--device") { Description = "npu, gpu or cpu.", DefaultValueFactory = _ => AiDevice.Auto };
var probeGpu = new Option<string?>("--gpu") { Description = "The graphics card, by name (settings.json's gpu). Default: Windows' default." };
var probe = new Command("probe", "Check where the AI model runs on this PC.") { probeDevice, probeGpu };
probe.Hidden = true;
probe.SetAction(r => {
	AiDevice wanted = r.GetValue(probeDevice);
	GpuAdapters.Choose(r.GetValue(probeGpu));
	// Only a card with the GPU pack here is checked: without the pack there's nothing to learn about the card.
	GpuAdapter? card = wanted == AiDevice.Gpu && GpuComponents.IsInstalled ? GpuAdapters.InUseNow() : null;
	try {
		using var embedder = OnnxEmbedder.Create(wanted);
		embedder.EmbedBatch(new[] { new byte[OnnxEmbedder.InputSide * OnnxEmbedder.InputSide * 3] });
		Console.WriteLine($"The AI model runs on the {embedder.DeviceName}{(embedder.Card != null ? $" ({embedder.Card.Key})" : "")}.");
		bool ran = wanted is AiDevice.Auto || string.Equals(embedder.DeviceName, wanted.ToString(), StringComparison.OrdinalIgnoreCase);
		if (card != null) GpuChecks.Record(card, ran, embedder.FallbackReason);
		return ran ? 0 : 1;
	}
	catch (Exception e) {
		Console.WriteLine($"The AI model couldn't run: {Accelerators.OneLine(e.Message)}");
		if (card != null) {
			GpuChecks.Record(card, passed: false, e.Message);
			Accelerators.MarkFailed(card.AcceleratorId, $"the {card.Key} failed its check: {e.Message}");
		}
		return 1;
	}
});
root.Subcommands.Add(probe);
root.Subcommands.Add(install);

var purge = new Option<bool>("--purge") { Description = "Also delete settings, the report and caches." };
var uninstall = new Command("uninstall", "Remove Heiward, its tasks and shortcuts. Keeps the report and settings unless --purge.") { purge, dryRun };
uninstall.SetAction(r => Installer.Uninstall(r.GetValue(purge), r.GetValue(dryRun)));
root.Subcommands.Add(uninstall);

var statusJson = new Option<bool>("--json") { Description = "Print one JSON object instead, for scripts and other tools: running (not paused), stoppedSince, pausedUntil, scheduled, nextScan, scanning, lastScan, toReview, page {url, up}, summary." };
var status = new Command("status", "Show the settings, the last report and the schedule.") { statusJson };
status.SetAction(async (r, _) => {
	var cfg = AgentConfig.Load();
	if (r.GetValue(statusJson)) {
		Console.WriteLine((await AgentStatus.NowAsync(cfg)).ToJson());
		return 0;
	}
	Console.WriteLine($"Installed: {(StorePackage.IsPackaged ? $"from the Microsoft Store ({StorePackage.FamilyName}){(File.Exists(AgentPaths.StoreSetUp) ? "" : ", not set up yet: open Heiward from the Start menu")}" : File.Exists(Installer.InstalledExe) ? Installer.InstallDir : "no")}");
	if (DevBuild.Current)
		Console.WriteLine("This copy: a development build, with no scheduled tasks" +
			(AgentPaths.Separate ? $", its own data and its own page ({ReviewServer.PageUrl(cfg.Port)}), apart from the installed copy's" : ""));
	Console.WriteLine($"Settings: {AgentPaths.Config}{(File.Exists(AgentPaths.Config) ? "" : " (defaults; not saved yet)")}");
	Console.WriteLine($"Scans: {string.Join("; ", ScanScope.Roots(cfg))}{(cfg.ScanAllDrives ? " (every fixed drive, minus system, app and game folders: 'hei scope')" : "")}");
	if (cfg.ExcludeExtensions.Count > 0) Console.WriteLine($"Skipped types: {string.Join(" ", cfg.ExcludeExtensions)}");
	Console.WriteLine($"Schedule: {Scheduler.Describe(cfg)}{(cfg.ScanEveryMinutes > 0 && cfg.ScanOnBattery ? $", on battery too above {cfg.MinBatteryPercent}% unless Battery Saver is on" : "")}");
	Console.WriteLine($"Developer mode: {DevMode.Now(cfg).Describe()}");
	Console.WriteLine($"Game mode: {GameMode.Now(cfg).Describe()}");
	if (AiStatus.Load() is { } ai) Console.WriteLine($"AI: {ai.Describe()} ({(ai.Accelerator != null ? ai.Accelerator + ", " : "")}checked by the {ai.Source}, {ai.CheckedAtUtc.ToLocalTime():g})");
	IReadOnlyList<GpuAdapter> cards = GpuAdapters.List();
	foreach (var f in AiStatus.Failures(cards))
		Console.WriteLine($"Failed for Heiward: {f.Name} ({f.Id}) at {f.SinceUtc.ToLocalTime():t}, skipped until {f.UntilUtc.ToLocalTime():t}: {f.Reason}");
	bool gpuSet = !string.IsNullOrWhiteSpace(cfg.Gpu);
	if (cards.Count > 1 || gpuSet)
		Console.WriteLine($"Graphics card: {(gpuSet ? cfg.Gpu : "Windows' default")}" +
			(gpuSet && GpuAdapters.Find(cfg.Gpu, cards) == null ? " (not on this PC now: Windows' default instead)" : "") +
			$"; this PC has {(cards.Count == 0 ? "none Windows lists" : string.Join(", ", cards.Select(c => c.Key)))}");
	var report = Report.Load();
	if (report == null) Console.WriteLine(Report.IsStale() ? "Heiward was updated: the next scan finds the sets again with this version ('hei scan')." : "No scan yet: run 'hei scan'.");
	else {
		Console.WriteLine($"Last scan: {report.ScannedAtUtc.ToLocalTime():g}, {report.FilesScanned:N0} files in {report.DurationSec:N0} s, AI on {report.Device}");
		var decisions = DecisionStore.Load();
		var pending = report.Groups.Where(g => !decisions.ContainsKey(g.Key)).ToList();
		Console.WriteLine($"To review: {pending.Count} set(s), up to {Format.Bytes(pending.Sum(g => g.ReclaimBytes))} to free");
		foreach (string n in report.Notes) Console.WriteLine("  note: " + n);
	}
	Console.WriteLine($"Next scheduled scan: {Scheduler.NextRun() ?? (DevBuild.Current ? "none in a development build" : "not scheduled (run 'hei install')")}");
	if (AgentPause.Load() is { } paused) Console.WriteLine($"Paused: scheduled scans skip themselves {paused.Describe(DateTime.UtcNow)} ('hei resume').");
	Console.WriteLine($"NPU lock shared with: {NpuLock.LockDirectory ?? "(no other NPU tool found)"}");
	if (NpuLock.LockDirectory != null && GpuAdapters.InUse(cfg.Gpu, cards) is { } lockCard)
		Console.WriteLine($"Graphics card lock (AI on the GPU): {NpuLock.LockDirectoryFor(lockCard.AcceleratorId)}");
	Console.WriteLine($"Scan running: {(AgentScanner.IsRunning() ? "yes" : "no")}");
	PrintAutoClean(cfg, detail: false);
	return 0;
});
root.Subcommands.Add(status);

var autoDuplicates = new Option<string?>("--duplicates") { Description = "on or off: move plain copies of photos, and byte-identical videos, to the Recycle Bin by itself." };
autoDuplicates.AcceptOnlyFromAmong("on", "off");
var autoDeveloper = new Option<string?>("--developer") { Description = "on or off: delete developer leftovers by itself (merged branches, temp files and crash dumps, build outputs and worktrees of stale projects, unused emulator images)." };
autoDeveloper.AcceptOnlyFromAmong("on", "off");
var autoGames = new Option<string?>("--games") { Description = "on or off: move what games leave behind to the Recycle Bin by itself, with game mode on (crash dumps, launchers' download caches, leftovers and shader caches of uninstalled games)." };
autoGames.AcceptOnlyFromAmong("on", "off");
var autoDays = new Option<int?>("--after-days") { Description = $"Days something is listed before it's cleaned (0 to {AutoCleanConfig.MaxAfterDays}; default 3)." };
var autoCmd = new Command("auto", "Automatic cleanup: show what it will clean and when, or turn it on or off (the review page has the same switches).") { autoDuplicates, autoDeveloper, autoGames, autoDays };
autoCmd.SetAction(r => {
	var cfg = AgentConfig.Load();
	string? duplicates = r.GetValue(autoDuplicates), developer = r.GetValue(autoDeveloper), games = r.GetValue(autoGames);
	int? days = r.GetValue(autoDays);
	if (duplicates != null || developer != null || games != null || days != null) {
		var next = new AutoCleanConfig {
			Duplicates = duplicates != null ? duplicates == "on" : cfg.AutoClean.Duplicates,
			Developer = developer != null ? developer == "on" : cfg.AutoClean.Developer,
			DeveloperKinds = cfg.AutoClean.DeveloperKinds,
			Games = games != null ? games == "on" : cfg.AutoClean.Games,
			GameKinds = cfg.AutoClean.GameKinds,
			AfterDays = days ?? cfg.AutoClean.AfterDays,
		}.Normalized();
		cfg.AutoClean = next;
		cfg.Save();
		AutoCleanState.Update(s => AutoCleaner.SyncSince(next, s, DateTime.UtcNow));
		AgentPaths.AppendLog($"automatic cleanup set from the command line: duplicates {(next.Duplicates ? "on" : "off")}, developer {(next.Developer ? "on" : "off")}, " +
			$"games {(next.Games ? "on" : "off")}, after {next.AfterDays} day(s)");
	}
	PrintAutoClean(cfg, detail: true);
	return 0;
});
root.Subcommands.Add(autoCmd);

var devScan = new Option<bool>("--scan") { Description = "Check again now (otherwise: show the last check)." };
var pruneBranches = new Option<string?>("--prune-branches") { Description = "Delete the repository's local branches already merged into its remote's main/master (fetches first; never main, master, develop or a checked-out branch)." };
var dev = new Command("dev", "Developer mode: build outputs, worktrees, caches, emulators and temp files that tools recreate.") { devScan, pruneBranches };
dev.SetAction(r => {
	var cfg = AgentConfig.Load();
	if (r.GetValue(pruneBranches) is { } repoPath) {
		// At a manor with Reeve, merged branches are Reeve's to delete (ManorRoles).
		if (ManorRoles.Now().Worktrees is { } reeve) {
			Console.Error.WriteLine($"{reeve.Note}: Heiward leaves branches to it ({reeve.Url}).");
			return 1;
		}
		PruneResult pruned = BranchPruner.Prune(Path.GetFullPath(repoPath));
		if (pruned.Error != null) { Console.Error.WriteLine(pruned.Error); return 1; }
		if (!pruned.Fetched) Console.WriteLine("Couldn't fetch; used the last fetched state.");
		Console.WriteLine(pruned.Deleted.Count == 0 ? "No merged branches to delete." : $"Deleted {pruned.Deleted.Count} {(pruned.Deleted.Count == 1 ? "branch" : "branches")}:");
		foreach (string b in pruned.Deleted) Console.WriteLine("  " + b);
		foreach (PruneKept k in pruned.Kept) Console.WriteLine($"Kept {k.Branch}: {k.Reason}");
		return 0;
	}
	// Manor's Developer options, when Manor is installed and they say; else Heiward's own switch.
	if (DevMode.Now(cfg) is { On: false } off) {
		Console.Error.WriteLine(off.CommandOffText);
		return 1;
	}
	ManorRoles roles = ManorRoles.Now();
	DevReport? report = roles.View(r.GetValue(devScan) ? DevScan.RunAndSave(cfg) ?? DevReport.Load() : DevReport.Load());
	if (report == null) {
		Console.WriteLine("No developer check yet: run 'hei dev --scan'.");
		return 0;
	}
	Console.WriteLine($"Checked {report.ScannedAtUtc.ToLocalTime():g} in {report.DurationSec:N0} s.");
	foreach (ManorRole? role in new[] { roles.Worktrees, roles.PullRequests })
		if (role != null) Console.WriteLine($"{role.Note} ({role.Url}).");
	foreach (DevCategory c in report.Categories) {
		Console.WriteLine($"{c.Title}: {Format.Bytes(c.Items.Sum(i => i.Bytes))}, {Format.Bytes(c.Items.Where(i => i.Suggested).Sum(i => i.Bytes))} ticked");
		foreach (DevItem i in c.Items.Take(8))
			Console.WriteLine($"  {(i.Suggested ? "[x]" : i.Blocked != null ? "[-]" : "[ ]")} {Format.Bytes(i.Bytes),9}  {i.Name}  {i.Detail}{(i.Blocked != null ? $" ({i.Blocked})" : "")}");
	}
	return 0;
});
root.Subcommands.Add(dev);

var gamesScan = new Option<bool>("--scan") { Description = "Check again now (otherwise: show the last check)." };
var gamesCmd = new Command("games", "Game mode: installed games, and what games and their launchers leave behind (leftovers, download caches, shader caches, crash dumps).") { gamesScan };
gamesCmd.SetAction(r => {
	var cfg = AgentConfig.Load();
	// Manor, when its settings say "gameMode"; else Heiward's own switch.
	if (GameMode.Now(cfg) is { On: false } off) {
		Console.Error.WriteLine(off.CommandOffText);
		return 1;
	}
	GameReport? report = r.GetValue(gamesScan) ? GameScan.RunAndSave(cfg) ?? GameReport.Load() : GameReport.Load();
	if (report == null) {
		Console.WriteLine("No games check yet: run 'hei games --scan'.");
		return 0;
	}
	Console.WriteLine($"Checked {report.ScannedAtUtc.ToLocalTime():g} in {report.DurationSec:N0} s: {report.Games.Count} installed game(s), " +
		$"in {(report.Launchers.Count == 0 ? "no launcher" : string.Join(", ", report.Launchers.Select(GameLaunchers.Name)))}.");
	foreach (GameInstall g in report.Games.Take(10))
		Console.WriteLine($"  {Format.Bytes(g.Bytes),9}  {g.Name} ({GameLaunchers.Name(g.Launcher)}){(g.LastPlayedUtc is { } p ? $", last played {p.ToLocalTime():d}" : "")}");
	foreach (GameCategory c in report.Categories) {
		Console.WriteLine($"{c.Title}: {Format.Bytes(c.Items.Sum(i => i.Bytes))}, {Format.Bytes(c.Items.Where(i => i.Suggested).Sum(i => i.Bytes))} ticked");
		foreach (GameItem i in c.Items.Take(8))
			Console.WriteLine($"  {(i.Info ? "   " : i.Suggested ? "[x]" : i.Blocked != null ? "[-]" : "[ ]")} {Format.Bytes(i.Bytes),9}  {i.Name}  {i.Detail}{(i.Blocked != null ? $" ({i.Blocked})" : "")}");
	}
	return 0;
});
root.Subcommands.Add(gamesCmd);

var count = new Option<bool>("--count") { Description = "List the drives as a scan would (every disk at once, slowest first; names and attributes only, no file is opened), with each disk's time, and count the photos and videos per folder." };
var scope = new Command("scope", "Show what a scan looks at and what it leaves out.") { count };
scope.SetAction(r => {
	var cfg = AgentConfig.Load();
	var notes = new List<string>();
	var settings = AgentScanner.BuildSettings(cfg, notes);
	Console.WriteLine("Scanned, with subfolders:");
	foreach (string rootFolder in settings.IncludeList) {
		// A folder from settings.json inside a built-in exclusion is scanned anyway: say so, since it's listed below.
		string why = ScanScope.BuiltInExclusionOver(rootFolder) is { } over
			? $"  (in settings.json, so scanned although {(over.Folder.Equals(rootFolder, StringComparison.OrdinalIgnoreCase) ? "it" : over.Folder)} is left out by default: {over.Rule.Reason})"
			: "";
		Console.WriteLine("  " + rootFolder + why);
	}
	Console.WriteLine("Left out below those (with everything inside):");
	foreach (string excluded in settings.SubfolderBlackList) Console.WriteLine("  " + excluded);
	Console.WriteLine($"  folders holding {string.Join(", ", settings.SkipFoldersContaining)} (code repositories), folder links, cloud-only files");
	if (settings.BlackList.Count > 0) {
		Console.WriteLine("Left out everywhere (excludeFolders in settings.json; wins over folders):");
		foreach (string excluded in settings.BlackList) Console.WriteLine("  " + excluded);
	}
	foreach (string n in notes) Console.WriteLine("note: " + n);
	if (!r.GetValue(count)) return 0;

	// As a scan lists them: every disk at once, slowest first, a disk's own folders one after another.
	// Each disk's time, and the total against one disk after another, show what walking them together saves.
	var disks = HEI.Core.Utils.DriveScanPlanner.GroupRootsByDisk(settings.IncludeList);
	var excludedFolders = settings.BlackList.Concat(settings.SubfolderBlackList).ToList();
	var timer = System.Diagnostics.Stopwatch.StartNew();
	var walked = Task.WhenAll(disks.Select(diskRoots => Task.Run(() => diskRoots.Select(rootFolder => {
		var walk = System.Diagnostics.Stopwatch.StartNew();
		var files = HEI.Core.Utils.FileUtils.GetFilesRecursive(rootFolder, settings.IgnoreReadOnlyFolders, settings.IgnoreReparsePoints,
			recursive: true, settings.IncludeImages, excludedFolders, CancellationToken.None, settings.SkipCloudPlaceholders, settings.ExcludedExtensions,
			settings.SkipFoldersContaining, settings.SkipFolderLinks);
		return (Root: rootFolder, Files: files, Time: walk.Elapsed);
	}).ToList()))).GetAwaiter().GetResult();
	TimeSpan wall = timer.Elapsed;

	Console.WriteLine($"Listed {disks.Count} disk(s) at once, slowest first:");
	foreach (var disk in walked) {
		string first = disk[0].Root;
		string kind = HEI.Core.Utils.DriveScanPlanner.IsNetworkRoot(Path.GetPathRoot(first) ?? first) ? "network share"
			: HEI.Core.Utils.DriveScanPlanner.QueryHasSeekPenalty(Path.GetPathRoot(first) ?? first) switch { true => "hard disk", false => "SSD", null => "disk" };
		Console.WriteLine($"  {kind,-13} {disk.Sum(w => w.Files.Count),9:N0} files {disk.Sum(w => w.Time.TotalSeconds),7:N1} s  {string.Join(", ", disk.Select(w => w.Root))}");
	}
	var perFolder = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
	int total = 0;
	foreach (var (rootFolder, files, _) in walked.SelectMany(d => d)) {
		total += files.Count;
		foreach (FileInfo f in files) {
			// Grouped three levels below the drive: C:\Users\me\Pictures, D:\Photos\2019.
			string rel = Path.GetRelativePath(rootFolder, f.DirectoryName ?? rootFolder);
			string key = Path.Combine(rootFolder, string.Join(Path.DirectorySeparatorChar, rel.Split(Path.DirectorySeparatorChar).Take(3)));
			perFolder.TryGetValue(key, out var c);
			perFolder[key] = (c.Files + 1, c.Bytes + f.Length);
		}
	}
	double oneByOne = walked.SelectMany(d => d).Sum(w => w.Time.TotalSeconds);
	Console.WriteLine($"{total:N0} photos and videos found in {wall.TotalSeconds:N1} s" +
		(disks.Count > 1 ? $" (one disk after another: about {oneByOne:N1} s)" : "") + ". By folder:");
	foreach (var (folder, c) in perFolder.OrderByDescending(kv => kv.Value.Files).Take(40))
		Console.WriteLine($"  {c.Files,8:N0}  {Format.Bytes(c.Bytes),9}  {folder}");
	return 0;
});
root.Subcommands.Add(scope);

return await root.Parse(args).InvokeAsync();

/// <summary>
/// Sets that wait for the user and call for them (<see cref="AutoCleaner.Announced"/>: not look-alikes). With
/// automatic cleanup of duplicates on, only those listed since the page last opened this way: the sign-in page
/// shouldn't open every day for sets the user leaves for later.
/// </summary>
static int PendingCount(string stamp) {
	var report = Report.Load();
	if (report == null) return 0;
	var cfg = AgentConfig.Load();
	var s = AutoCleanState.Load();
	var waiting = AutoCleaner.WaitingForUser(cfg, report, DecisionStore.Load(), s, DateTime.UtcNow).Where(AutoCleaner.Announced).ToList();
	if (!cfg.AutoClean.Duplicates || !File.Exists(stamp)) return waiting.Count;
	DateTime lastOpened = File.GetLastWriteTimeUtc(stamp);
	return waiting.Count(g => !s.FirstSeenUtc.TryGetValue("g:" + g.Key, out DateTime seen) || seen > lastOpened);
}

/// <summary>Automatic cleanup's settings and what's coming; with <paramref name="detail"/>, thing by thing.</summary>
static void PrintAutoClean(AgentConfig cfg, bool detail) {
	AutoCleanConfig a = cfg.AutoClean;
	if (!a.Duplicates && !a.Developer && !a.Games) {
		Console.WriteLine("Automatic cleanup: off" + (detail ? " ('hei auto --duplicates on', '--developer on', '--games on', or the switches on the review page)" : ""));
		return;
	}
	Console.WriteLine($"Automatic cleanup: duplicates {(a.Duplicates ? "on" : "off")}, developer {(a.Developer ? $"on ({string.Join(", ", a.DeveloperKinds)})" : "off")}, " +
		$"games {(a.Games ? $"on ({string.Join(", ", a.GameKinds)})" : "off")}; things wait {a.AfterDays} day(s) after they're first listed");
	DateTime now = DateTime.UtcNow;
	Report? report = Report.Load();
	DevReport? dev = DevMode.Now(cfg).On ? ManorRoles.Now().View(DevReport.Load()) : null;
	GameReport? games = GameMode.Now(cfg).On ? GameReport.Load() : null;
	var decisions = DecisionStore.Load();
	AutoCleanState s = AutoCleanState.Load();
	AutoPlan plan = AutoCleaner.Plan(cfg, report, dev, decisions, s, now, games);
	string When(DateTime due) => due <= now ? "due now" : "from " + due.ToLocalTime().ToString("ddd d MMM HH:mm");
	void Summary(string what, IEnumerable<AutoPlanEntry> entries) {
		var list = entries.ToList();
		if (list.Count == 0) return;
		var due = list.Where(e => e.DueUtc != null).ToList();
		var dueNow = due.Where(e => e.DueUtc <= now).ToList();
		long bytes = dueNow.Sum(e => e.Bytes);
		Console.WriteLine($"  {what}: {dueNow.Count} due now{(bytes > 0 ? $" ({Format.Bytes(bytes)})" : "")}, {due.Count - dueNow.Count} later" +
			(due.Count > dueNow.Count ? $" (next {When(due.Where(e => e.DueUtc > now).Min(e => e.DueUtc!.Value))})" : "") +
			$", {list.Count - due.Count} left for you");
	}
	Summary("Sets of copies", plan.Groups.Values);
	Summary("Developer items", plan.DevItems.Values);
	Summary("Repositories with merged branches", plan.Repos.Values);
	Summary("Game leftovers", plan.GameItems.Values);
	if (detail) {
		foreach (var (key, e) in plan.Groups.OrderBy(kv => kv.Value.DueUtc ?? DateTime.MaxValue).Take(30)) {
			ReportGroup g = report!.Groups.First(x => x.Key == key);
			string keep = g.Items.First(i => i.Keep).Name;
			Console.WriteLine($"    {(e.DueUtc is { } d ? When(d) : e.Held ? "held" : "waits"),-22} {keep}: " +
				(e.DueUtc != null ? $"{e.Count} cop{(e.Count == 1 ? "y" : "ies")}, {Format.Bytes(e.Bytes)}" : e.Held ? "you said leave it" : e.Reason));
		}
		if (plan.Groups.Count > 30) Console.WriteLine($"    and {plan.Groups.Count - 30} more sets");
		var items = dev?.Categories.SelectMany(c => c.Items).ToDictionary(i => i.Id) ?? new();
		foreach (var (id, e) in plan.DevItems.OrderBy(kv => kv.Value.DueUtc ?? DateTime.MaxValue).Take(30))
			Console.WriteLine($"    {(e.DueUtc is { } d ? When(d) : e.Held ? "held" : "waits"),-22} {items[id].Name} ({items[id].Kind}, {Format.Bytes(e.Bytes)})" +
				(e.DueUtc == null ? ": " + (e.Held ? "you said leave it" : e.Reason) : ""));
		foreach (var (id, e) in plan.Repos) {
			RepoBranches r = dev!.Repositories.First(x => x.Id == id);
			Console.WriteLine($"    {(e.DueUtc is { } d ? When(d) : e.Held ? "held" : "waits"),-22} {r.Name}: {e.Count} merged branch(es)" +
				(e.DueUtc == null ? ": " + (e.Held ? "you said leave it" : e.Reason) : ""));
		}
		if (plan.DevItems.Count > 0 || plan.Repos.Count > 0)
			Console.WriteLine("  Developer items are cleaned right after the daily developer check once they're due.");
		var gameItems = games?.Items.ToDictionary(i => i.Id) ?? new();
		foreach (var (id, e) in plan.GameItems.OrderBy(kv => kv.Value.DueUtc ?? DateTime.MaxValue).Take(30))
			Console.WriteLine($"    {(e.DueUtc is { } d ? When(d) : e.Held ? "held" : "waits"),-22} {gameItems[id].Name} ({gameItems[id].Kind}, {Format.Bytes(e.Bytes)})" +
				(e.DueUtc == null ? ": " + (e.Held ? "you said leave it" : e.Reason) : ""));
		if (plan.GameItems.Count > 0)
			Console.WriteLine("  Game leftovers go to the Recycle Bin right after the daily games check once they're due.");
	}
	if (s.Runs.FirstOrDefault() is { } last)
		Console.WriteLine($"  Last run: {last.AtUtc.ToLocalTime():g}: {last.Describe("; ")}");
}

/// <summary>Starts the review page in the background if needed, waits until it answers, opens it.</summary>
static async Task OpenReviewPageAsync(AgentConfig cfg, CancellationToken ct) {
	await EnsurePageUpAsync(cfg, ct);
	ReviewServer.OpenBrowser(cfg.Port);
}

/// <summary>Starts the review page in the background if needed, and waits until it answers.</summary>
static async Task EnsurePageUpAsync(AgentConfig cfg, CancellationToken ct) {
	// Both versions use the same port: a GitHub copy's page there would stand in for the Store version's own
	// (and its setup), so the Store version stops it first.
	if (StorePackage.IsPackaged && await ReviewServer.IsUpAsync(cfg.Port) && !await ReviewServer.IsUpAsync(cfg.Port, fromStore: true)) {
		Installer.StopGitHubCopy();
		for (int i = 0; i < 20 && await ReviewServer.IsUpAsync(cfg.Port); i++) await Task.Delay(250, ct);
	}
	ReviewServer.EnsureRunningInBackground(cfg);
	for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(cfg.Port); i++)
		await Task.Delay(250, ct);
}

namespace HEI.Agent {
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

		[DllImport("ntdll.dll")]
		static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int size);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref CpuRateControl info, int size);

		[DllImport("kernel32.dll")]
		static extern bool CloseHandle(IntPtr handle);

		/// <summary>JOBOBJECT_CPU_RATE_CONTROL_INFORMATION; the rate is in hundredths of a percent of the whole processor.</summary>
		[StructLayout(LayoutKind.Sequential)]
		struct CpuRateControl {
			public uint ControlFlags, CpuRate;
		}

		/// <summary>The job this process joined for <see cref="CapCpu"/>; kept for the process's lifetime.</summary>
		static IntPtr cpuJob;

		/// <summary>
		/// In the background: EcoQoS, the power half of Task Manager's "Efficiency mode", so Windows runs
		/// the process on efficient cores at low clocks and a scan takes longer and costs little power.
		/// Priority stays below normal rather than idle: the scan holds the shared NPU lock for up to
		/// two seconds at a time, and an idle-priority thread starved while holding it would make
		/// other NPU tools wait. At full speed: normal priority, and throttling explicitly off, so
		/// Windows doesn't guess that a windowless process may run slowly.
		/// The disk, too: in the background the scan's reads (listing folders, reading new files,
		/// hashing copies) go at very low I/O priority, as the search indexer's and defrag's do, so
		/// whatever else is using the drive goes first and a hard disk isn't kept seeking for it. Only
		/// the disk: Windows' own background mode would lower the CPU priority to idle as well.
		/// And in the background, a cap on the processor (<see cref="CapCpu"/>): a low priority only gives
		/// way to other work, so an idle PC's processor was the scan's, efficiency mode or not.
		/// The NPU and the GPU's video decoder follow the same pace from their next piece of work on (<see cref="Pace"/>).
		/// </summary>
		/// <param name="cpuCap">In the background, the most of the whole processor the scan uses, in percent (<see cref="AgentConfig.BackgroundCpuCap"/>).</param>
		public static bool SetPace(bool fullSpeed, double cpuCap) {
			Pace.FullSpeed = fullSpeed;
			const int ProcessPowerThrottling = 4, ProcessIoPriority = 33, IoPriorityVeryLow = 0, IoPriorityNormal = 2;
			const uint ExecutionSpeed = 0x1, BelowNormalPriorityClass = 0x4000, NormalPriorityClass = 0x20;
			var state = new ProcessPowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = fullSpeed ? 0 : ExecutionSpeed };
			bool qos = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
			bool priority = SetPriorityClass(GetCurrentProcess(), fullSpeed ? NormalPriorityClass : BelowNormalPriorityClass);
			int io = fullSpeed ? IoPriorityNormal : IoPriorityVeryLow;
			bool disk = NtSetInformationProcess(GetCurrentProcess(), ProcessIoPriority, ref io, sizeof(int)) == 0;
			bool cpu = CapCpu(fullSpeed ? 0 : cpuCap);
			return qos && priority && disk && cpu;
		}

		/// <summary>
		/// Caps the processor time of this process, and of the programs it starts (FFmpeg, FFprobe), at
		/// <paramref name="percent"/> of the whole processor; 0 lifts the cap. The hard cap of a job object,
		/// which the process joins the first time: Windows holds the scan's threads back once they have
		/// used their share, however idle the PC. A program started while the cap is lifted is in the
		/// job too, but nothing caps it unless the cap comes back: <see cref="LiftCpuCap"/> before
		/// starting anything that outlives the scan.
		/// </summary>
		static bool CapCpu(double percent) {
			const int JobObjectCpuRateControlInformation = 15;
			const uint Enable = 0x1, HardCap = 0x4;
			if (cpuJob == IntPtr.Zero) {
				if (percent <= 0) return true; // never capped
				IntPtr job = CreateJobObject(IntPtr.Zero, null);
				if (job == IntPtr.Zero) return false;
				// Inside Task Scheduler's own job too: since Windows 8 jobs nest.
				if (!AssignProcessToJobObject(job, GetCurrentProcess())) {
					AgentPaths.AppendLog($"no cap on the processor: joining a job failed ({Marshal.GetLastWin32Error()})");
					CloseHandle(job);
					return false;
				}
				cpuJob = job;
			}
			var rate = percent > 0
				? new CpuRateControl { ControlFlags = Enable | HardCap, CpuRate = (uint)Math.Clamp(Math.Round(percent * 100), 1, 10_000) }
				: new CpuRateControl();
			return SetInformationJobObject(cpuJob, JobObjectCpuRateControlInformation, ref rate, Marshal.SizeOf<CpuRateControl>());
		}

		/// <summary>The scan's work is done: what it starts from here, the review page among them, must not stay capped.</summary>
		public static void LiftCpuCap() => CapCpu(0);

		[DllImport("ntdll.dll")]
		static extern int NtQueryInformationProcess(IntPtr process, int infoClass, out int info, int size, out int returned);

		/// <summary>The process's I/O priority (0 very low, 1 low, 2 normal), as <see cref="SetPace"/> set it; null if Windows won't say.</summary>
		internal static int? IoPriority() =>
			NtQueryInformationProcess(GetCurrentProcess(), 33, out int io, sizeof(int), out _) == 0 ? io : null;

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
