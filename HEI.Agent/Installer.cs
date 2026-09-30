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
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using HEI.Core.AI;
using HEI.Core.FFTools;
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <summary>
	/// The whole install, per user and without admin rights, from the one self-contained exe:
	/// <list type="number">
	/// <item>copy the exe to %LOCALAPPDATA%\Programs\Heiward (its downloads land next to it);</item>
	/// <item>fetch FFmpeg, the AI runtime and model, and the pack for the PC's NPU; probe the NPU;</item>
	/// <item>write settings.json: hourly scans on an NPU; without one, only if the user agrees, daily on the CPU;</item>
	/// <item>register the scan task and the sign-in "open the review page" task;</item>
	/// <item>add Start menu and desktop shortcuts to the review page, the name and icon its notifications show, and an Apps &amp; Features entry (so Windows can uninstall it);</item>
	/// <item>start the first scan and open the review page.</item>
	/// </list>
	/// <c>--dry-run</c> prints every step without changing anything.
	/// The Store version (<see cref="StorePackage"/>) is installed already: it skips the copy, the shortcuts, the
	/// notification name and Apps &amp; Features, which its package has, and its tasks run the "hei" alias. Its
	/// review page asks the questions and runs this with the answers (<c>install --yes --device ...</c>).
	/// </summary>
	static class Installer {
		const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Heiward";
		const string DisplayName = "Heiward";

		public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Heiward");
		public static string InstalledExe => Path.Combine(InstallDir, "hei.exe");
		static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Heiward.lnk");
		static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Heiward.lnk");
		/// <summary>The mark as a picture, for notifications (Windows shows the exe's icon everywhere else).</summary>
		static string IconPng => Path.Combine(InstallDir, "heiward.png");
		static string CurrentExe => StorePackage.IsPackaged ? StorePackage.ConsoleExe : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "hei.exe");
		public static bool RunningInstalled => StorePackage.IsPackaged || StorePackage.InPackageFolder || string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);
		/// <summary>What the scheduled tasks run: the installed exe, or the Store version's alias.</summary>
		static string TaskExe => StorePackage.IsPackaged ? StorePackage.Alias : InstalledExe;

		/// <summary>Scheduled scans without an NPU: every 6 hours, on AC power.</summary>
		internal const int GpuCpuScanMinutes = 6 * 60;

		/// <param name="device">Force the AI device (unattended installs); null = the NPU if there is one, else ask.</param>
		/// <param name="onDemand">True: no scheduled scans, only "Scan now". Null: ask when there's no NPU.</param>
		/// <param name="reuseFrom">Folders whose bin\ and ai\ may already hold the prerequisites (<see cref="ComponentReuse"/>).</param>
		/// <param name="scanSpeed">How hard scans work (<see cref="AgentConfig.ScanSpeed"/>); null: ask.</param>
		/// <param name="openPage">Open the review page once the first scan starts (the page itself runs the Store version's setup).</param>
		/// <param name="removeGitHubCopy">The Store version: remove a copy installed from GitHub (<see cref="RemoveGitHubCopy"/>).</param>
		public static async Task<int> InstallAsync(bool dryRun, bool assumeYes, AiDevice? device, CancellationToken ct, bool? onDemand = null, IReadOnlyList<string>? reuseFrom = null,
			string? scanSpeed = null, bool openPage = true, bool removeGitHubCopy = false) {
			void Step(string s) => Console.WriteLine((dryRun ? "[dry run] " : "") + s);
			Console.WriteLine($"{DisplayName} setup");
			if (StorePackage.InPackageFolder && !StorePackage.IsPackaged) {
				Console.WriteLine("This is the Microsoft Store version, started by its path: open Heiward from the Start menu, or run \"hei\".");
				return 1;
			}
			// The Store installs the package built for the PC.
			if (!StorePackage.IsPackaged && !WrongBuildConfirmed(assumeYes)) {
				Console.WriteLine("Nothing installed.");
				return 3;
			}

			if (!RunningInstalled) {
				Step($"Copy {CurrentExe} -> {InstalledExe}");
				if (!dryRun) {
					Directory.CreateDirectory(InstallDir);
					StopRunningAgents();
					File.Copy(CurrentExe, InstalledExe, overwrite: true);
					// Continue from the installed copy, so the downloads land next to it (its folder is
					// VDF's state folder) and everything it registers points at it.
					var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false };
					foreach (string a in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(a);
					if (!psi.ArgumentList.Contains("install")) psi.ArgumentList.Insert(0, "install");
					// The installed copy starts empty: it can copy what the folder it came from already has.
					psi.ArgumentList.Add("--reuse-from");
					psi.ArgumentList.Add(Path.GetDirectoryName(Path.GetFullPath(CurrentExe))!);
					using var p = Process.Start(psi)!;
					await p.WaitForExitAsync(ct);
					return p.ExitCode;
				}
			}

			// Prerequisites: copied from a copy that already has them, else downloaded. No admin rights needed.
			bool arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
			// The Store version brings FFmpeg along, and can copy the rest from a GitHub copy's folder.
			var sources = StorePackage.IsPackaged ? ComponentReuse.Sources((reuseFrom ?? Array.Empty<string>()).Append(InstallDir), CoreUtils.StateFolder)
				: ComponentReuse.Sources(RunningInstalled ? reuseFrom : (reuseFrom ?? Array.Empty<string>()).Append(Path.GetDirectoryName(Path.GetFullPath(CurrentExe))!), InstallDir);
			Step("Prerequisites: FFmpeg, ONNX Runtime + DINOv2 model" + (NpuComponents.IsSupportedPlatform ? $", {NpuComponents.NpuName} pack" : ""));
			await EnsurePrerequisitesAsync(sources, dryRun, ct);

			bool npu = device is null or AiDevice.Auto or AiDevice.Npu && (!dryRun ? NpuComponents.WillUseNpu(AiDevice.Auto) : NpuComponents.IsSupportedPlatform);
			Step(npu ? $"NPU found: AI matching runs on the {NpuComponents.NpuName}."
				: device is AiDevice.Gpu or AiDevice.Cpu && NpuComponents.IsSupportedPlatform ? $"AI matching set to the {device.Value.ToString().ToUpperInvariant()}, although this PC has a {NpuComponents.NpuName}."
				: NpuHardware.Vendor == NpuVendor.None ? $"No NPU on this PC ({(arm64 ? "ARM64" : RuntimeInformation.OSArchitecture.ToString())})."
				: NpuComponents.IsSupportedPlatform ? $"The {NpuComponents.NpuName} could not run the model here."
				: $"This build does not support this PC's NPU yet ({NpuHardware.Name}).");
			var cfg = File.Exists(AgentPaths.Config) ? AgentConfig.Load() : new AgentConfig();
			cfg.AiDevice = "auto";
			if (!npu) {
				AiDevice choice = device is AiDevice.Gpu or AiDevice.Cpu ? device.Value : AskDevice(assumeYes);
				if (choice == AiDevice.Auto) {
					Console.WriteLine("Nothing installed.");
					return 3;
				}
				if (choice == AiDevice.Gpu) {
					Step("GPU pack: ONNX Runtime DirectML + DirectML");
					if (dryRun && ComponentReuse.Find(ComponentReuse.GpuPack, sources) is string gpuFrom)
						Console.WriteLine($"  would be copied from {gpuFrom}");
					if (!dryRun) {
						// A process loads one ONNX Runtime, so the GPU check runs in its own process.
						string? from = GpuComponents.IsInstalled ? null : await ComponentReuse.TryCopyAsync(ComponentReuse.GpuPack, sources, CoreUtils.StateFolder,
							async () => GpuComponents.IsInstalled && await ProbeDeviceAsync("gpu", ct));
						if (from != null)
							Console.WriteLine($"  copied from {from}");
						else {
							Console.WriteLine("  downloading (~215 MB)...");
							await GpuComponents.DownloadAsync(null, ct);
							if (!await ProbeDeviceAsync("gpu", ct)) {
								Console.WriteLine("  The GPU could not run the model here; using the CPU instead.");
								choice = AiDevice.Cpu;
							}
						}
					}
				}
				cfg.AiDevice = choice == AiDevice.Gpu ? "gpu" : "cpu";
				cfg.ScanOnBattery = false;
				// Without an NPU a scan costs real power: every 6 hours on AC power, or only on demand.
				cfg.ScanEveryMinutes = (onDemand ?? AskOnDemand(assumeYes)) ? 0 : GpuCpuScanMinutes;
				Step($"AI matching runs on the {(choice == AiDevice.Gpu ? "GPU (DirectML)" : "CPU")}.");
			}
			else if (onDemand == true) {
				cfg.ScanEveryMinutes = 0;
			}
			cfg.ScanSpeed = scanSpeed ?? AskSpeed(assumeYes, cfg.ScanSpeed);
			Step($"Settings: {AgentPaths.Config} ({Scheduler.Describe(cfg)}, {SpeedText(cfg)}; {(cfg.ScanAllDrives ? "every fixed drive, minus system, app and game folders" : "folders: " + string.Join("; ", cfg.Folders))})");
			if (!dryRun) {
				cfg.Save();
				// What the install found, for the review page's badge until the first scan says otherwise.
				AiStatus.Record(cfg, npu ? "NPU" : cfg.AiDevice == "gpu" ? "GPU" : "CPU", "install");
			}

			// After the prerequisites, which may have been copied from its folder; before the tasks, which have its tasks' names.
			if (removeGitHubCopy && StorePackage.IsPackaged && Directory.Exists(InstallDir)) {
				Step($"Remove the copy from GitHub: {InstallDir}, its shortcuts, its notification name and its Apps & Features entry");
				if (!dryRun) RemoveGitHubCopy();
			}

			Step(cfg.ScanEveryMinutes > 0
				? $"Task Scheduler: '{Scheduler.ScanTask}' {Scheduler.Describe(cfg)}, '{Scheduler.OpenTask}' at sign-in"
				: $"Task Scheduler: no scan task (scans run when you press Scan now), '{Scheduler.OpenTask}' at sign-in");
			bool fromStore = StorePackage.IsPackaged;
			if (dryRun) {
				if (cfg.ScanEveryMinutes > 0) Console.WriteLine(Scheduler.ScanXml(cfg, TaskExe, fromStore));
				Console.WriteLine(Scheduler.OpenXml(TaskExe, fromStore));
			}
			else {
				if (cfg.ScanEveryMinutes > 0) Scheduler.Register(Scheduler.ScanTask, Scheduler.ScanXml(cfg, TaskExe, fromStore));
				else Scheduler.Remove(Scheduler.ScanTask);
				if (cfg.OpenPageAtSignIn) Scheduler.Register(Scheduler.OpenTask, Scheduler.OpenXml(TaskExe, fromStore));
			}

			if (fromStore) {
				Step("Start menu, notifications and Apps & Features: the Store package's own");
				if (!dryRun) AgentPaths.WriteAtomic(AgentPaths.StoreSetUp, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
			}
			else {
				Step($"Start menu and desktop: {StartMenuShortcut}, {DesktopShortcut} (open the review page)");
				if (!dryRun) {
					CreateShortcut(StartMenuShortcut);
					CreateShortcut(DesktopShortcut);
				}
				Step($"Notifications: shown as {DisplayName}, with its icon (HKCU\\{Toast.AppIdKey})");
				if (!dryRun) Toast.Register(DisplayName, WriteIconPng());
				Step($"Apps & Features entry: HKCU\\{UninstallKey}");
				if (!dryRun) RegisterUninstall();
			}

			if (dryRun) return 0;
			Console.WriteLine();
			Console.WriteLine(openPage ? "Installed. The first scan starts now; the review page opens in your browser and shows its progress." : "Installed. The first scan starts now.");
			Console.WriteLine("Later scans only look at new files. Nothing is ever deleted unless you choose it on the page.");
			if (openPage) StartDetached("scan", "--open");
			else StartDetached("scan");
			return 0;
		}

		public static int Uninstall(bool purge, bool dryRun) {
			void Step(string s) => Console.WriteLine((dryRun ? "[dry run] " : "") + s);
			Step($"Remove tasks '{Scheduler.ScanTask}' and '{Scheduler.OpenTask}'");
			if (!dryRun) { Scheduler.Remove(Scheduler.ScanTask); Scheduler.Remove(Scheduler.OpenTask); }
			// The shortcuts, the notification name, Apps & Features and the folder are a GitHub copy's.
			if (!StorePackage.IsPackaged) {
				Step($"Remove {StartMenuShortcut}, {DesktopShortcut}, the notification name and the Apps & Features entry");
				if (!dryRun) {
					DeleteOwnShortcut(StartMenuShortcut);
					DeleteOwnShortcut(DesktopShortcut);
					Toast.Unregister();
					try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
					StopRunningAgents();
				}
			}
			if (purge) {
				Step($"Delete settings, report and caches: {AgentPaths.Home}");
				if (!dryRun) try { Directory.Delete(AgentPaths.Home, recursive: true); } catch { }
			}
			else Console.WriteLine($"Kept settings and the report in {AgentPaths.Home} (add --purge to delete them).");
			if (StorePackage.IsPackaged) {
				if (!dryRun) try { File.Delete(AgentPaths.StoreSetUp); } catch { }
				Console.WriteLine(dryRun ? "" : "Scans are off. To remove Heiward itself: Settings > Apps > Installed apps > Heiward > Uninstall.");
				return 0;
			}
			if (Directory.Exists(InstallDir)) {
				Step($"Delete {InstallDir}");
				// The running exe can't delete itself: a detached cmd does it once this process exits.
				if (!dryRun)
					Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
						$"/d /c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"") { UseShellExecute = false, CreateNoWindow = true });
			}
			Console.WriteLine(dryRun ? "" : "Uninstalled. Files you reviewed stay where they are; recycled ones are in the Recycle Bin.");
			return 0;
		}

		/// <summary>
		/// True unless this is the Intel/AMD build on an Arm PC and the user declines. That build runs there,
		/// emulated, but slower and without the NPU; the Arm64 download is the one for the PC.
		/// </summary>
		static bool WrongBuildConfirmed(bool assumeYes) {
			if (RuntimeInformation.OSArchitecture != Architecture.Arm64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return true;
			Console.WriteLine("  This is the Intel/AMD build, and this PC has an Arm processor. It would run emulated: slower,");
			Console.WriteLine("  and without the NPU. The Arm64 download (Heiward-...-arm64.exe) is the one for this PC.");
			if (assumeYes || Console.IsInputRedirected) return true;
			Console.Write("  Install this build anyway? [y/N]: ");
			return (Console.ReadLine()?.Trim().ToLowerInvariant() ?? "").StartsWith('y');
		}

		/// <summary>GPU or CPU for a PC without an NPU; Auto means "cancel". --yes (and no console) picks the GPU.</summary>
		static AiDevice AskDevice(bool assumeYes) {
			Console.WriteLine("  The AI step can run on your graphics card or on the processor:");
			Console.WriteLine("    [G] GPU (recommended if you have a graphics card; any DirectX 12 GPU): fast and light on power");
			Console.WriteLine("    [C] CPU: works everywhere, uses more power");
			Console.WriteLine("    [N] Don't install");
			if (assumeYes || Console.IsInputRedirected) return AiDevice.Gpu;
			Console.Write("  Your choice [G/c/n]: ");
			string answer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
			return answer.StartsWith('n') ? AiDevice.Auto : answer.StartsWith('c') ? AiDevice.Cpu : AiDevice.Gpu;
		}

		/// <summary>Without an NPU: scheduled scans every 6 hours, or only on demand. --yes picks the schedule.</summary>
		static bool AskOnDemand(bool assumeYes) {
			Console.WriteLine("  When should it look for new duplicates?");
			Console.WriteLine("    [S] Every 6 hours, only on AC power, in Windows' efficiency mode (recommended)");
			Console.WriteLine("    [D] Only when I press \"Scan now\" on the review page");
			if (assumeYes || Console.IsInputRedirected) return false;
			Console.Write("  Your choice [S/d]: ");
			return (Console.ReadLine()?.Trim().ToLowerInvariant() ?? "").StartsWith('d');
		}

		/// <summary>How hard scans work: in the background, or at full speed. --yes keeps what settings.json has.</summary>
		static string AskSpeed(bool assumeYes, string current) {
			Console.WriteLine("  How hard should scans work?");
			Console.WriteLine("    [B] In the background: low power, in Windows' efficiency mode, and on the NPU where there is one. Slower (recommended)");
			Console.WriteLine("    [F] At full speed: as many cores as it takes, at normal priority, to finish as fast as possible");
			if (assumeYes || Console.IsInputRedirected) return current;
			Console.Write("  Your choice [B/f]: ");
			return (Console.ReadLine()?.Trim().ToLowerInvariant() ?? "").StartsWith('f') ? "full" : "background";
		}

		static string SpeedText(AgentConfig cfg) =>
			cfg.AlwaysFullSpeed ? "at full speed" : cfg.AlwaysInBackground ? "in the background" : "at full speed when you're here";

		/// <summary>
		/// FFmpeg, ONNX Runtime and the model, and the pack for the PC's NPU, next to this exe: each copied
		/// from one of <paramref name="sources"/> when a copy there passes the same check (see
		/// <see cref="ComponentReuse"/>), otherwise downloaded. Downloads are SHA-256 pinned. Shared by
		/// install and setup.
		/// </summary>
		internal static async Task EnsurePrerequisitesAsync(IReadOnlyList<string> sources, bool dryRun, CancellationToken ct) {
			if (dryRun) {
				var parts = new List<ComponentReuse.Part> { ComponentReuse.AiRuntime };
				// The Store version brings FFmpeg along.
				if (!File.Exists(Path.Combine(CoreUtils.CurrentFolder, "bin", "ffmpeg.exe"))) parts.Insert(0, ComponentReuse.Ffmpeg);
				if (NpuComponents.IsSupportedPlatform) parts.Add(ComponentReuse.NpuPack);
				foreach (var part in parts)
					Console.WriteLine(ComponentReuse.Find(part, sources) is string from ? $"  {Capital(part.Name)}: would be copied from {from}" : $"  {Capital(part.Name)}: would be downloaded, unless already here");
				return;
			}

			if (File.Exists(Path.Combine(CoreUtils.CurrentFolder, "bin", "ffmpeg.exe"))) { /* already here */ }
			else if (await ComponentReuse.TryCopyAsync(ComponentReuse.Ffmpeg, sources, CoreUtils.CurrentFolder,
				() => Task.FromResult(Starts(Path.Combine(CoreUtils.CurrentFolder, "bin", "ffmpeg.exe")) && Starts(Path.Combine(CoreUtils.CurrentFolder, "bin", "ffprobe.exe")))) is string ffFrom)
				Console.WriteLine($"  FFmpeg: copied from {ffFrom}");
			else if (FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFmpeg) == null || FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFProbe) == null)
				Console.WriteLine($"  FFmpeg -> {await FfmpegDownloader.DownloadAndInstallAsync(null, ct)}");

			if (!AiComponents.IsReady) {
				if (await ComponentReuse.TryCopyAsync(ComponentReuse.AiRuntime, sources, CoreUtils.StateFolder, () => Task.FromResult(AiComponents.IsReady)) is string aiFrom)
					Console.WriteLine($"  AI components: copied from {aiFrom}");
				else {
					Console.WriteLine($"  AI components (ONNX Runtime {AiComponents.RuntimeVersion} + model, ~100 MB)...");
					await AiComponents.DownloadAsync(null, ct);
				}
			}

			if (NpuComponents.IsSupportedPlatform && !NpuComponents.IsInstalled) {
				// The pack carries no version marker, so a copy has to run the model on the NPU, in its own process.
				if (await ComponentReuse.TryCopyAsync(ComponentReuse.NpuPack, sources, CoreUtils.StateFolder,
					async () => NpuComponents.IsInstalled && await ProbeDeviceAsync("npu", ct)) is string npuFrom)
					Console.WriteLine($"  NPU pack: copied from {npuFrom}");
				else {
					Console.WriteLine($"  NPU pack ({NpuComponents.PackDescription})...");
					await NpuComponents.DownloadAsync(null, ct);
				}
			}
		}

		static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

		/// <summary>The tool starts and prints its version: an FFmpeg build that crashes on load (as some ARM64 ones do) fails this.</summary>
		static bool Starts(string exe) {
			try {
				using var p = Process.Start(new ProcessStartInfo(exe, "-hide_banner -version") {
					UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
				});
				if (p == null) return false;
				p.StandardOutput.ReadToEnd();
				return p.WaitForExit(15_000) && p.ExitCode == 0;
			}
			catch { return false; }
		}

		/// <summary>Runs "hei probe --device {device}" in its own process: true when the model runs there.</summary>
		static async Task<bool> ProbeDeviceAsync(string device, CancellationToken ct) {
			var psi = new ProcessStartInfo(CurrentExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
			psi.ArgumentList.Add("probe");
			psi.ArgumentList.Add("--device");
			psi.ArgumentList.Add(device);
			using var p = Process.Start(psi)!;
			// ONNX Runtime writes its warnings to stderr: they go to the log when the probe fails, not the window.
			Task<string> errors = p.StandardError.ReadToEndAsync(ct);
			string output = await p.StandardOutput.ReadToEndAsync(ct);
			await p.WaitForExitAsync(ct);
			Console.WriteLine("  " + output.Trim());
			if (p.ExitCode != 0) AgentPaths.AppendLog($"probe --device {device} failed: {(await errors).Trim()}");
			return p.ExitCode == 0;
		}

		/// <summary>
		/// The Store version, with a copy from GitHub installed too: removes that copy so there aren't two
		/// Heiwards. Its tasks have the Store version's task names, which the install registers next. Settings,
		/// the report and the history in %LOCALAPPDATA%\Heiward stay: the Store version uses them.
		/// </summary>
		static void RemoveGitHubCopy() {
			StopGitHubCopy();
			DeleteOwnShortcut(StartMenuShortcut);
			DeleteOwnShortcut(DesktopShortcut);
			// Through reg.exe: the package's own registry changes stay in the package (its view of HKCU
			// shows them gone while the user's keys stay), a program outside the package reaches the real ones.
			DeleteUserKey(Toast.AppIdKey);
			bool entryLeft = !DeleteUserKey(UninstallKey);
			// A process that just stopped can hold its exe for a moment.
			for (int i = 0; i < 5 && Directory.Exists(InstallDir); i++) {
				try { Directory.Delete(InstallDir, recursive: true); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(1000); }
			}
			if (Directory.Exists(InstallDir) || entryLeft) {
				Console.WriteLine("  Some of it is still there: uninstall the other Heiward in Settings > Apps > Installed apps.");
				AgentPaths.AppendLog($"removing the copy from GitHub left {(entryLeft ? "its Apps & Features entry" : InstallDir)}");
			}
			else AgentPaths.AppendLog("removed the copy from GitHub");
		}

		/// <summary>
		/// Deletes a Heiward.lnk only if it's the GitHub copy's: the Store version's desktop shortcut has the
		/// same name and must survive the GitHub copy's removal or uninstall.
		/// </summary>
		static void DeleteOwnShortcut(string lnk) {
			try {
				if (File.Exists(lnk) && PointsAt(File.ReadAllBytes(lnk), InstallDir)) File.Delete(lnk);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		/// <summary>A .lnk file names <paramref name="folder"/> (its target and arguments are stored as UTF-16 or ANSI text).</summary>
		internal static bool PointsAt(byte[] lnk, string folder) =>
			System.Text.Encoding.Unicode.GetString(lnk).Contains(folder, StringComparison.OrdinalIgnoreCase) ||
			lnk.Length > 1 && System.Text.Encoding.Unicode.GetString(lnk, 1, lnk.Length - 1).Contains(folder, StringComparison.OrdinalIgnoreCase) ||
			System.Text.Encoding.Latin1.GetString(lnk).Contains(folder, StringComparison.OrdinalIgnoreCase);

		/// <summary>Deletes an HKCU key with reg.exe; true when the user's registry doesn't have it any more.</summary>
		static bool DeleteUserKey(string key) {
			string reg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
			int Run(params string[] args) {
				var psi = new ProcessStartInfo(reg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
				foreach (string a in args) psi.ArgumentList.Add(a);
				using var p = Process.Start(psi)!;
				p.StandardOutput.ReadToEnd();
				p.StandardError.ReadToEnd();
				p.WaitForExit(10_000);
				return p.ExitCode;
			}
			try {
				Run("delete", @"HKCU\" + key, "/f");
				return Run("query", @"HKCU\" + key) != 0;
			}
			catch { return false; }
		}

		/// <summary>
		/// Stops the GitHub copy's processes (its review page or a scan), found by their exe's path: the Store
		/// version's own are hei.exe too.
		/// </summary>
		public static void StopGitHubCopy() {
			foreach (var p in Process.GetProcessesByName("hei")) {
				try {
					if (string.Equals(ImagePath(p), InstalledExe, StringComparison.OrdinalIgnoreCase)) {
						p.Kill(entireProcessTree: true);
						p.WaitForExit(5000);
					}
				}
				catch { /* gone already, or not ours to stop */ }
				p.Dispose();
			}
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

		/// <summary>A process's exe: works for an emulated x64 process too, unlike its MainModule.</summary>
		static string? ImagePath(Process p) {
			var name = new System.Text.StringBuilder(1024);
			int size = name.Capacity;
			return QueryFullProcessImageName(p.Handle, 0, name, ref size) ? name.ToString() : null;
		}

		/// <summary>The GitHub copy's version ("1.2.2") when one is installed, else null.</summary>
		public static string? GitHubCopyVersion() {
			if (!File.Exists(InstalledExe)) return null;
			try { return FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion?.Split('+')[0] ?? ""; }
			catch { return ""; }
		}

		/// <summary>Stops other hei processes (a review page or a scan) so the exe can be replaced or removed.</summary>
		static void StopRunningAgents() {
			foreach (var p in Process.GetProcessesByName("hei").Where(p => p.Id != Environment.ProcessId)) {
				try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
				p.Dispose();
			}
		}

		/// <summary>Writes the mark's picture next to the installed exe (from the page's files, which are compiled in); null if it can't.</summary>
		static string? WriteIconPng() {
			try {
				using Stream? png = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/heiward.png");
				if (png == null) return null;
				using (var file = File.Create(IconPng)) png.CopyTo(file);
				return IconPng;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return null;
			}
		}

		/// <summary>
		/// A .lnk through WScript.Shell (always present): conhost --headless runs the console exe without a
		/// window, and "open" starts the review page if it isn't running. The icon is the exe's own.
		/// </summary>
		static void CreateShortcut(string lnk) {
			string conhost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");
			string ps = $"""
				$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{lnk.Replace("'", "''")}')
				$s.TargetPath = '{conhost}'
				$s.Arguments = '--headless "{InstalledExe.Replace("'", "''")}" open'
				$s.IconLocation = '{InstalledExe.Replace("'", "''")},0'
				$s.Description = 'Review duplicate photos and videos, and stale developer files'
				$s.WorkingDirectory = '{InstallDir.Replace("'", "''")}'
				$s.Save()
				""";
			RunPowerShell(ps);
		}

		static void RegisterUninstall() {
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey);
			string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
			key.SetValue("DisplayName", DisplayName);
			key.SetValue("DisplayVersion", version);
			key.SetValue("Publisher", "Heiward (based on Video Duplicate Finder)");
			key.SetValue("DisplayIcon", InstalledExe);
			key.SetValue("InstallLocation", InstallDir);
			key.SetValue("UninstallString", $"\"{InstalledExe}\" uninstall");
			key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" uninstall");
			key.SetValue("NoModify", 1, RegistryValueKind.DWord);
			key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
			try {
				long kb = Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
				key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
			}
			catch { }
		}

		static void RunPowerShell(string script) {
			string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
			var psi = new ProcessStartInfo(ps) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
			foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
				psi.ArgumentList.Add(a);
			using var p = Process.Start(psi)!;
			string err = p.StandardError.ReadToEnd();
			p.WaitForExit(30_000);
			if (p.ExitCode != 0) throw new InvalidOperationException($"PowerShell failed: {err.Trim()}");
		}

		public static void StartDetached(params string[] args) {
			// The Store version runs its own exe, even with a GitHub copy installed too.
			string exe = !StorePackage.IsPackaged && File.Exists(InstalledExe) ? InstalledExe : CurrentExe;
			var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
			foreach (string a in args) psi.ArgumentList.Add(a);
			Process.Start(psi);
		}
	}
}
