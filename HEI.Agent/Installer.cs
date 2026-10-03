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
		/// <param name="gpu">The graphics card for GPU work on a PC with more than one: its number as <see cref="AskGpu"/> lists them, or its name. Null: ask.</param>
		/// <returns>0 once installed (or for a dry run); otherwise not 0, with the reason on stderr.</returns>
		public static async Task<int> InstallAsync(bool dryRun, bool assumeYes, AiDevice? device, CancellationToken ct, bool? onDemand = null, IReadOnlyList<string>? reuseFrom = null,
			string? scanSpeed = null, bool openPage = true, bool removeGitHubCopy = false, string? gpu = null) {
			// A development build installs the installed copy: its settings, tasks and folders, not its own.
			AgentPaths.ActAsInstalled();
			try {
				return await InstallStepsAsync(dryRun, assumeYes, device, ct, onDemand, reuseFrom, scanSpeed, openPage, removeGitHubCopy, gpu);
			}
			catch (Exception e) when (e is not OperationCanceledException) {
				AgentPaths.AppendLog("install failed: " + e);
				Console.Error.WriteLine($"Heiward couldn't finish installing: {e.Message}");
				return 1;
			}
		}

		static async Task<int> InstallStepsAsync(bool dryRun, bool assumeYes, AiDevice? device, CancellationToken ct, bool? onDemand, IReadOnlyList<string>? reuseFrom,
			string? scanSpeed, bool openPage, bool removeGitHubCopy, string? gpu) {
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
			// The graphics cards, and --gpu checked against them before anything is downloaded.
			IReadOnlyList<GpuAdapter> gpus = GpuAdapters.List();
			string? gpuKey = null;
			if (gpu != null) {
				gpuKey = GpuArgument(gpu, gpus);
				if (gpuKey == null) {
					Console.WriteLine($"  No graphics card \"{gpu}\" on this PC." + (gpus.Count == 0 ? "" : " It has:"));
					for (int i = 0; i < gpus.Count; i++) Console.WriteLine($"    {i + 1}. {gpus[i].Key}");
					Console.WriteLine("Nothing installed.");
					return 3;
				}
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
					// Unattended (--yes, or run by a script or another program): its input is closed, so no question can
					// wait for an answer, and without a window here it gets none either. Its output is this one's.
					bool windowless = GetConsoleWindow() == IntPtr.Zero;
					if (assumeYes || windowless || Console.IsInputRedirected) {
						psi.RedirectStandardInput = true;
						psi.CreateNoWindow = windowless;
					}
					using var p = Process.Start(psi)!;
					if (psi.RedirectStandardInput) p.StandardInput.Close();
					await p.WaitForExitAsync(ct);
					return p.ExitCode;
				}
			}

			// Prerequisites: copied from a copy that already has them, else downloaded. No admin rights needed.
			bool arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
			// The Store version brings FFmpeg along, and can copy the rest from a GitHub copy's folder.
			var sources = StorePackage.IsPackaged ? ComponentReuse.Sources((reuseFrom ?? Array.Empty<string>()).Append(InstallDir), CoreUtils.StateFolder)
				: ComponentReuse.Sources(RunningInstalled ? reuseFrom : (reuseFrom ?? Array.Empty<string>()).Append(Path.GetDirectoryName(Path.GetFullPath(CurrentExe))!), InstallDir);
			Step("Prerequisites: FFmpeg, ONNX Runtime + DINOv2 model" + (NpuComponents.IsSupportedPlatform ? $", {NpuComponents.NpuName} pack" : "") +
				(dryRun && !RunningInstalled ? $", in {InstallDir}" : ""));
			await EnsurePrerequisitesAsync(sources, dryRun, ct, RunningInstalled ? null : InstallDir);

			bool npu = device is null or AiDevice.Auto or AiDevice.Npu && (!dryRun ? NpuComponents.WillUseNpu(AiDevice.Auto) : NpuComponents.IsSupportedPlatform);
			Step(npu ? $"NPU found: AI matching runs on the {NpuComponents.NpuName}."
				: device is AiDevice.Gpu or AiDevice.Cpu && NpuComponents.IsSupportedPlatform ? $"AI matching set to the {device.Value.ToString().ToUpperInvariant()}, although this PC has a {NpuComponents.NpuName}."
				: NpuHardware.Vendor == NpuVendor.None ? $"No NPU on this PC ({(arm64 ? "ARM64" : RuntimeInformation.OSArchitecture.ToString())})."
				: NpuComponents.IsSupportedPlatform ? $"The {NpuComponents.NpuName} could not run the model here."
				: $"This build does not support this PC's NPU yet ({NpuHardware.Name}).");
			var cfg = File.Exists(AgentPaths.Config) ? AgentConfig.Load() : new AgentConfig();
			cfg.AiDevice = "auto";
			// With more than one graphics card, which one does the GPU work: asked once, before the GPU check, which runs on it.
			bool gpuPicked = false;
			void PickGpu(bool forAi) {
				if (gpuPicked) return;
				gpuPicked = true;
				cfg.Gpu = gpuKey ?? AskGpu(gpus, cfg.Gpu, assumeYes, forAi);
				if (gpus.Count > 1 || cfg.Gpu.Length > 0)
					Step($"Graphics card: {(cfg.Gpu.Length > 0 ? "the " + cfg.Gpu : "Windows' default")}.");
			}
			if (!npu) {
				AiDevice choice = device is AiDevice.Gpu or AiDevice.Cpu ? device.Value : AskDevice(assumeYes);
				if (choice == AiDevice.Auto) {
					Console.WriteLine("Nothing installed.");
					return 3;
				}
				if (choice == AiDevice.Gpu) {
					PickGpu(forAi: true);
					Step("GPU pack: ONNX Runtime DirectML + DirectML");
					if (dryRun && ComponentReuse.Find(ComponentReuse.GpuPack, sources) is string gpuFrom)
						Console.WriteLine($"  would be copied from {gpuFrom}");
					if (!dryRun) {
						// A process loads one ONNX Runtime, so the GPU check runs in its own process.
						string? from = GpuComponents.IsInstalled ? null : await ComponentReuse.TryCopyAsync(ComponentReuse.GpuPack, sources, CoreUtils.StateFolder,
							async () => GpuComponents.IsInstalled && await ProbeDeviceAsync("gpu", ct, cfg.Gpu));
						if (from != null)
							Console.WriteLine($"  copied from {from}");
						else {
							Console.WriteLine("  downloading (~215 MB)...");
							await GpuComponents.DownloadAsync(null, ct);
							if (!await ProbeDeviceAsync("gpu", ct, cfg.Gpu)) {
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
			// On the NPU or the CPU, the graphics card still decodes videos and iPhone photos.
			PickGpu(forAi: false);
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
				Console.WriteLine(Scheduler.OpenXml(TaskExe, cfg.OpenPageAtSignIn, fromStore));
			}
			else if (RegisterTasks(cfg) is string why) throw new InvalidOperationException(why);

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
				Step($"heiward: links start the review page (its \"Start Heiward\" button): HKCU\\{ProtocolKey}");
				if (!dryRun) RegisterProtocol();
			}

			if (dryRun) return 0;
			// Opening another copy's page would show that copy's report as this one's.
			bool pageFree = fromStore || await MakeWayForPageAsync(cfg.Port, ct);
			if (!pageFree) openPage = false;
			Console.WriteLine();
			Console.WriteLine(openPage ? "Installed. The first scan starts now; the review page opens in your browser and shows its progress." : "Installed. The first scan starts now.");
			Console.WriteLine("Later scans only look at new files. Nothing is ever deleted unless you choose it on the page.");
			if (openPage) StartDetached("scan", "--open");
			else {
				// The page stays up, browser or not: an update (which stopped the old copy's) or an unattended install
				// mustn't leave Heiward without one until the next sign-in.
				if (pageFree) StartDetached("serve", "--no-browser");
				StartDetached("scan");
			}
			return 0;
		}

		/// <summary>
		/// From Settings > Apps (Apps &amp; Features runs <c>hei uninstall</c> in a console window of its own),
		/// or the command line. Every step goes to heiward.log, which uninstalling keeps. Started in a window
		/// of its own, it says how it went before the window closes: an error stays until Enter, so it can be
		/// read (a first build's uninstall closed at once, and left no trace of why it did nothing).
		/// </summary>
		public static int Uninstall(bool purge, bool dryRun) {
			// From a development build too, it's the installed copy that goes, with its data; the build's own stays.
			AgentPaths.ActAsInstalled();
			bool ownWindow = !Console.IsInputRedirected && !Console.IsOutputRedirected;
			uninstallLog = !dryRun;
			Log($"uninstall started ({AppBuild.Current}, from {Environment.ProcessPath}{(StorePackage.Identity is string id ? $", with the package identity {id}" : "")})");
			try {
				if (!dryRun && StorePackage.Identity != null && !StorePackage.IsPackaged && HandOver(purge)) {
					Console.WriteLine("Uninstalling Heiward in a new window.");
					return 0;
				}
				int code = UninstallSteps(purge, dryRun);
				Log("uninstall done");
				if (ownWindow && !dryRun) {
					Console.WriteLine();
					Console.WriteLine("Heiward is uninstalled. This window closes in a few seconds.");
					WaitForKey(TimeSpan.FromSeconds(8));
				}
				return code;
			}
			catch (Exception e) {
				Log("uninstall failed: " + e);
				Console.Error.WriteLine();
				Console.Error.WriteLine("Heiward couldn't finish uninstalling: " + e.Message);
				Console.Error.WriteLine("The details are in " + AgentPaths.Log + ". Running uninstall again picks up where it stopped.");
				if (ownWindow) {
					Console.Error.WriteLine("Press Enter to close.");
					Console.ReadLine();
				}
				return 1;
			}
		}

		/// <summary>
		/// Settings > Apps starts the uninstall inside its own package's environment (<see cref="StorePackage.Identity"/>),
		/// which keeps registry changes, and those of any program started from here, in that package's view of
		/// HKCU: the Apps &amp; Features entry would look gone to the uninstall and stay in Settings. Task Scheduler
		/// starts its tasks outside any, so a one-time task runs the uninstall again, in a window of its own.
		/// </summary>
		/// <returns>False when Task Scheduler wouldn't: then the uninstall goes ahead here.</returns>
		static bool HandOver(bool purge) {
			// A development build's uninstall hands over to the installed copy's: no task ever runs a development build.
			string exe = DevBuild.Current ? InstalledExe : Environment.ProcessPath ?? InstalledExe;
			if (!File.Exists(exe)) return false;
			try {
				Scheduler.Register(Scheduler.UninstallTask, Scheduler.UninstallXml(exe, purge));
				if (Scheduler.RunNow(Scheduler.UninstallTask)) {
					Log("uninstall: handed over to Task Scheduler");
					return true;
				}
				Log("uninstall: Task Scheduler didn't start the uninstall");
			}
			catch (Exception e) { Log("uninstall: couldn't hand over to Task Scheduler: " + e.Message); }
			return false;
		}

		/// <summary>Uninstall's steps go to heiward.log; not in a dry run, nor once --purge has deleted its folder.</summary>
		static bool uninstallLog;
		static void Log(string line) {
			if (uninstallLog) AgentPaths.AppendLog(line);
		}

		/// <summary>Waits until a key is pressed or the time is up.</summary>
		static void WaitForKey(TimeSpan most) {
			var until = DateTime.UtcNow + most;
			try {
				while (DateTime.UtcNow < until && !Console.KeyAvailable) Thread.Sleep(100);
			}
			catch (InvalidOperationException) { } // no console to read keys from
		}

		static int UninstallSteps(bool purge, bool dryRun) {
			void Step(string s) {
				Console.WriteLine((dryRun ? "[dry run] " : "") + s);
				Log("uninstall: " + s);
			}
			Step($"Remove tasks '{Scheduler.ScanTask}' and '{Scheduler.OpenTask}'");
			if (!dryRun) {
				Scheduler.Remove(Scheduler.ScanTask);
				Scheduler.Remove(Scheduler.OpenTask);
				Scheduler.Remove(Scheduler.UninstallTask); // what ran this uninstall, if anything did: it runs on regardless
			}
			// The shortcuts, the notification name, Apps & Features and the folder are a GitHub copy's.
			if (!StorePackage.IsPackaged) {
				Step($"Remove {StartMenuShortcut}, {DesktopShortcut}, the notification name and the Apps & Features entry");
				if (!dryRun) {
					DeleteOwnShortcut(StartMenuShortcut);
					DeleteOwnShortcut(DesktopShortcut);
					Toast.Unregister();
					try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
					catch (Exception e) { Log("uninstall: the Apps & Features entry stays: " + e.Message); }
					try { Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false); } catch { }
					StopRunningAgents();
					// Anything else still running from the folder (an FFmpeg a scan started) would keep it from going.
					StopProcessesIn(InstallDir);
				}
			}
			if (purge) {
				Step($"Delete settings, report and caches: {AgentPaths.Home}");
				if (!dryRun) {
					uninstallLog = false; // the log goes with the folder; writing to it would make the folder again
					try { Directory.Delete(AgentPaths.Home, recursive: true); } catch { }
				}
			}
			else Console.WriteLine($"Kept settings and the report in {AgentPaths.Home} (add --purge to delete them).");
			if (StorePackage.IsPackaged) {
				if (!dryRun) try { File.Delete(AgentPaths.StoreSetUp); } catch { }
				Console.WriteLine(dryRun ? "" : "Scans are off. To remove Heiward itself: Settings > Apps > Installed apps > Heiward > Uninstall.");
				return 0;
			}
			if (Directory.Exists(InstallDir)) {
				Step($"Delete {InstallDir}");
				// The running exe can't delete itself: a detached cmd deletes the folder once this process has
				// exited. It tries every two seconds for half a minute, while anything else (an antivirus scan,
				// a file still closing) holds a file in it; whatever goes first, goes.
				if (!dryRun)
					Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
						DeleteFolderLater(InstallDir)) { UseShellExecute = false, CreateNoWindow = true });
			}
			Console.WriteLine(dryRun ? "" : "Uninstalled. Files you reviewed stay where they are; recycled ones are in the Recycle Bin.");
			return 0;
		}

		/// <summary>cmd's arguments that delete <paramref name="folder"/>, trying again every two seconds for half a minute.</summary>
		internal static string DeleteFolderLater(string folder) =>
			$"/d /c for /l %i in (1,1,15) do @(if exist \"{folder}\" (ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{folder}\" 2>nul))";

		/// <summary>
		/// Stops every process whose program is in <paramref name="folder"/> (only those named <paramref name="name"/>,
		/// when given), but this one. Which they are goes by where their exe is (<see cref="RunningFrom"/>).
		/// </summary>
		static void StopProcessesIn(string folder, string? name = null) {
			Process[] running = name == null ? Process.GetProcesses() : Process.GetProcessesByName(name);
			try {
				var byId = running.ToDictionary(p => p.Id);
				foreach (var (id, exe) in RunningFrom(running.Select(p => (p.Id, ImagePathOrNull(p))), folder, Environment.ProcessId)) {
					try {
						Log($"uninstall: stopping {Path.GetFileName(exe)} ({id})");
						byId[id].Kill(entireProcessTree: true);
						byId[id].WaitForExit(5000);
					}
					catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
				}
			}
			finally {
				foreach (Process p in running) p.Dispose();
			}
		}

		/// <summary>
		/// The processes in <paramref name="processes"/> (id, exe) whose exe is in <paramref name="folder"/>, or a folder
		/// inside it, but <paramref name="self"/>. One whose exe can't be read (null) isn't: it can't be shown to be the
		/// installed copy's.
		/// </summary>
		internal static IEnumerable<(int Id, string Exe)> RunningFrom(IEnumerable<(int Id, string? Exe)> processes, string folder, int self) {
			foreach (var (id, exe) in processes)
				if (id != self && RunsFrom(exe, folder)) yield return (id, exe!);
		}

		/// <summary>True when <paramref name="exe"/> is in <paramref name="folder"/>, or a folder inside it.</summary>
		internal static bool RunsFrom(string? exe, string folder) {
			if (string.IsNullOrEmpty(exe)) return false;
			try {
				string inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
				return Path.GetFullPath(exe).StartsWith(inside, StringComparison.OrdinalIgnoreCase);
			}
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
		}

		/// <summary>
		/// The scan task (when <see cref="AgentConfig.ScanEveryMinutes"/> asks for one) and the sign-in task, for
		/// the installed copy or the Store version. Also the review page's "Turn scheduled scans back on", after
		/// someone deleted or disabled them in Task Scheduler.
		/// </summary>
		/// <returns>Why they can't be registered, or null.</returns>
		public static string? RegisterTasks(AgentConfig cfg) {
			// The tasks are the installed copy's, with its settings: a development build's own aren't those.
			if (DevBuild.Current) return "A development build has no scheduled scans: build a release and install it to try them.";
			// A copy run from Downloads has nothing a task could run once it's gone: install it first.
			if (!StorePackage.IsPackaged && !File.Exists(InstalledExe)) return "Heiward isn't installed on this PC: run the downloaded Heiward once to install it.";
			bool fromStore = StorePackage.IsPackaged;
			if (cfg.ScanEveryMinutes > 0) Scheduler.Register(Scheduler.ScanTask, Scheduler.ScanXml(cfg, TaskExe, fromStore));
			else Scheduler.Remove(Scheduler.ScanTask);
			// Always: it's what brings the page back after a restart. The setting decides only whether the browser opens.
			Scheduler.Register(Scheduler.OpenTask, Scheduler.OpenXml(TaskExe, cfg.OpenPageAtSignIn, fromStore));
			Scheduler.Forget();
			return null;
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
			if (assumeYes || Console.IsInputRedirected) return AiDevice.Gpu; // nobody to ask: no question either
			Console.WriteLine("  The AI step can run on your graphics card or on the processor:");
			Console.WriteLine("    [G] GPU (recommended if you have a graphics card; any DirectX 12 GPU): fast and light on power");
			Console.WriteLine("    [C] CPU: works everywhere, uses more power");
			Console.WriteLine("    [N] Don't install");
			Console.Write("  Your choice [G/c/n]: ");
			string answer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
			return answer.StartsWith('n') ? AiDevice.Auto : answer.StartsWith('c') ? AiDevice.Cpu : AiDevice.Gpu;
		}

		/// <summary>
		/// With more than one graphics card, which one does the GPU work: its <see cref="GpuAdapter.Key"/>. Suggested: the one
		/// <paramref name="current"/> names, else the one with the most memory of its own, which --yes (and no console) takes.
		/// One card or none: "", Windows' default.
		/// </summary>
		/// <param name="forAi">The AI runs on the graphics card too, not only the decoding.</param>
		internal static string AskGpu(IReadOnlyList<GpuAdapter> gpus, string? current, bool assumeYes, bool forAi) {
			if (gpus.Count < 2) return "";
			GpuAdapter recommended = GpuAdapters.Recommended(gpus)!;
			GpuAdapter suggested = GpuAdapters.Find(current, gpus) ?? recommended;
			if (assumeYes || Console.IsInputRedirected) return suggested.Key;
			Console.WriteLine($"  This PC has {gpus.Count} graphics cards. Which should Heiward use {(forAi ? "for AI matching, and " : "")}to decode videos and iPhone photos?");
			for (int i = 0; i < gpus.Count; i++)
				Console.WriteLine($"    [{i + 1}] {GpuText(gpus[i])}{(gpus[i] == recommended ? " (recommended)" : "")}");
			Console.WriteLine("  You can choose another one later, in Settings on the review page.");
			int number = gpus.ToList().IndexOf(suggested) + 1;
			Console.Write($"  Your choice [{number}]: ");
			string answer = Console.ReadLine()?.Trim() ?? "";
			return int.TryParse(answer, out int n) && n >= 1 && n <= gpus.Count ? gpus[n - 1].Key : suggested.Key;
		}

		/// <summary>"NVIDIA GeForce RTX 4070, 12 GB of its own memory", or "..., shares the PC's memory" for a graphics chip.</summary>
		internal static string GpuText(GpuAdapter gpu) =>
			gpu.Key + (gpu.DedicatedMemory >= 512UL << 20 ? $", {Format.Bytes((long)gpu.DedicatedMemory)} of its own memory" : ", shares the PC's memory");

		/// <summary>
		/// install --gpu: a card's number as <see cref="AskGpu"/> lists them, or its name (<see cref="GpuAdapter.Key"/>, any case);
		/// "default" for Windows' default (""). Null when this PC has no such card.
		/// </summary>
		internal static string? GpuArgument(string value, IReadOnlyList<GpuAdapter> gpus) {
			value = value.Trim();
			if (value.Equals("default", StringComparison.OrdinalIgnoreCase)) return "";
			if (int.TryParse(value, out int n)) return n >= 1 && n <= gpus.Count ? gpus[n - 1].Key : null;
			return GpuAdapters.Find(value, gpus)?.Key;
		}

		/// <summary>Without an NPU: scheduled scans every 6 hours, or only on demand. --yes picks the schedule.</summary>
		static bool AskOnDemand(bool assumeYes) {
			if (assumeYes || Console.IsInputRedirected) return false;
			Console.WriteLine("  When should it look for new duplicates?");
			Console.WriteLine("    [S] Every 6 hours, only on AC power, in Windows' efficiency mode (recommended)");
			Console.WriteLine("    [D] Only when I press \"Scan now\" on the review page");
			Console.Write("  Your choice [S/d]: ");
			return (Console.ReadLine()?.Trim().ToLowerInvariant() ?? "").StartsWith('d');
		}

		/// <summary>How hard scans work: in the background, or at full speed. --yes keeps what settings.json has.</summary>
		static string AskSpeed(bool assumeYes, string current) {
			if (assumeYes || Console.IsInputRedirected) return current;
			Console.WriteLine("  How hard should scans work?");
			Console.WriteLine("    [B] In the background: low power, in Windows' efficiency mode, and on the NPU where there is one. Slower (recommended)");
			Console.WriteLine("    [F] At full speed: as many cores as it takes, at normal priority, to finish as fast as possible");
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
		/// <param name="dryRunTarget">A dry run's folder they'd go to, when that isn't this exe's (the installed copy's).</param>
		internal static async Task EnsurePrerequisitesAsync(IReadOnlyList<string> sources, bool dryRun, CancellationToken ct, string? dryRunTarget = null) {
			if (dryRun) {
				var parts = new List<ComponentReuse.Part> { ComponentReuse.AiRuntime };
				// The Store version brings FFmpeg along.
				if (!File.Exists(Path.Combine(dryRunTarget ?? CoreUtils.CurrentFolder, "bin", "ffmpeg.exe"))) parts.Insert(0, ComponentReuse.Ffmpeg);
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

		/// <summary>
		/// Runs "hei probe --device {device}" in its own process: true when the model runs there. A GPU probe records the
		/// card's check (<see cref="GpuChecks"/>), and a failing one marks the card failed (Heiward's own marker).
		/// </summary>
		/// <param name="gpu">The graphics card to check, by name; null or empty: Windows' default.</param>
		/// <param name="say">Where the probe's answer goes; null: this window.</param>
		internal static async Task<bool> ProbeDeviceAsync(string device, CancellationToken ct, string? gpu = null, Action<string>? say = null) {
			var psi = new ProcessStartInfo(CurrentExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
			psi.ArgumentList.Add("probe");
			psi.ArgumentList.Add("--device");
			psi.ArgumentList.Add(device);
			if (!string.IsNullOrEmpty(gpu)) {
				psi.ArgumentList.Add("--gpu");
				psi.ArgumentList.Add(gpu);
			}
			using var p = Process.Start(psi)!;
			// ONNX Runtime writes its warnings to stderr: they go to the log when the probe fails, not the window.
			Task<string> errors = p.StandardError.ReadToEndAsync(ct);
			string output = await p.StandardOutput.ReadToEndAsync(ct);
			await p.WaitForExitAsync(ct);
			(say ?? (line => Console.WriteLine("  " + line)))(output.Trim());
			if (p.ExitCode != 0) AgentPaths.AppendLog($"probe --device {device}{(string.IsNullOrEmpty(gpu) ? "" : $" --gpu \"{gpu}\"")} failed: {(await errors).Trim()}");
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
			// The heiward: links' too: the Store version's own come with its package.
			bool entryLeft = !DeleteUserKeysOutside(Toast.AppIdKey, ProtocolKey, UninstallKey);
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

		/// <summary>
		/// Deletes HKCU keys from outside the package. Its registry changes stay in its own view of HKCU, which
		/// shows them gone while the user's keys stay, and so do those of every program it starts, reg.exe
		/// included. Task Scheduler starts its tasks outside any package: a one-time task deletes them.
		/// </summary>
		/// <returns>True once the task has run; this view can't tell whether the keys are gone.</returns>
		static bool DeleteUserKeysOutside(params string[] keys) {
			try {
				Scheduler.Register(Scheduler.RemoveKeysTask, Scheduler.DeleteKeysXml(keys));
				if (Scheduler.RunNow(Scheduler.RemoveKeysTask))
					// The task deletes itself last.
					for (var until = DateTime.UtcNow.AddSeconds(15); DateTime.UtcNow < until; Thread.Sleep(250))
						if (!Scheduler.Exists(Scheduler.RemoveKeysTask)) return true;
				Scheduler.Remove(Scheduler.RemoveKeysTask);
			}
			catch (Exception e) { AgentPaths.AppendLog("Task Scheduler couldn't remove the GitHub copy's registry keys: " + e.Message); }
			return false;
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

		/// <summary><see cref="ImagePath"/>, or null for a process this one may not look at, or that has exited.</summary>
		static string? ImagePathOrNull(Process p) {
			try { return ImagePath(p); }
			catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
		}

		/// <summary>The GitHub copy's version ("1.2.2") when one is installed, else null.</summary>
		public static string? GitHubCopyVersion() {
			if (!File.Exists(InstalledExe)) return null;
			try { return FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion?.Split('+')[0] ?? ""; }
			catch { return ""; }
		}

		/// <summary>
		/// Stops the installed copy's hei processes (its review page or a scan) so its exe can be replaced or removed.
		/// Only those whose exe is in <see cref="InstallDir"/>: a hei.exe run from anywhere else (a development build,
		/// a USB drive, an unzipped release, a test build with its own HEIWARD_HOME) is another copy, and stopping it
		/// would lose its scan. Such a copy's review page can still hold the port; <see cref="MakeWayForPageAsync"/>
		/// sees to that (a development build's has a port of its own, <see cref="AgentPaths.DevPort"/>).
		/// </summary>
		static void StopRunningAgents() => StopProcessesIn(InstallDir, "hei");

		/// <summary>
		/// Every copy's review page uses the same port (<see cref="AgentPaths.InstalledPort"/>), but a development
		/// build's without HEIWARD_HOME, and "open" opens whatever Heiward page answers there: another copy's (one run
		/// from elsewhere, or the Store version) would stand in for the installed copy's, with that copy's report. That page is asked to close, as its own page would ask
		/// (<see cref="ReviewServer.AskToCloseAsync"/>). Only the page closes: its copy's scans run in processes of
		/// their own, and carry on.
		/// </summary>
		/// <returns>True when the port is free, or the installed copy's page has it.</returns>
		static async Task<bool> MakeWayForPageAsync(int port, CancellationToken ct) {
			string? exe = await ReviewServer.PageExeAsync(port);
			if (exe == null || RunsFrom(exe, InstallDir)) return true;
			string whose = exe.Length > 0 ? exe : "another copy of Heiward";
			if (await ReviewServer.AskToCloseAsync(port)) {
				AgentPaths.AppendLog($"install: asked the review page of {whose} to close, for this copy's on port {port}");
				for (int i = 0; i < 20; i++) {
					if (!await ReviewServer.IsUpAsync(port)) return true;
					await Task.Delay(250, ct);
				}
			}
			// A build from before /api/quit, or one that's busy with the Store version's setup.
			Console.WriteLine($"  The review page on port {port} is {whose}'s, and it stays up.");
			Console.WriteLine("  Close that copy (end its hei.exe in Task Manager); then the Heiward shortcut opens this copy's.");
			AgentPaths.AppendLog($"install: the review page of {whose} kept port {port}");
			return false;
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

		/// <summary>heiward: links, which the review page offers when it can't reach Heiward ("Start Heiward").</summary>
		const string ProtocolKey = @"Software\Classes\heiward";

		static void RegisterProtocol() {
			string conhost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(ProtocolKey);
			key.SetValue("", "URL:Heiward");
			key.SetValue("URL Protocol", "");
			using (RegistryKey icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{InstalledExe}\",0");
			using RegistryKey command = key.CreateSubKey(@"shell\open\command");
			// The link itself is the argument: heiward://start starts Heiward, any other opens the page (Program.cs).
			command.SetValue("", $"\"{conhost}\" --headless \"{InstalledExe}\" \"%1\"");
		}

		static void RegisterUninstall() {
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey);
			string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
			key.SetValue("DisplayName", DisplayName);
			key.SetValue("DisplayVersion", version);
			key.SetValue("Publisher", "The Nexus"); // as the Microsoft Store lists it
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
			KeepStdHandlesToSelf();
			Process.Start(psi);
		}

		[DllImport("kernel32.dll")]
		static extern IntPtr GetConsoleWindow();

		[DllImport("kernel32.dll")]
		static extern IntPtr GetStdHandle(int which);

		[DllImport("kernel32.dll")]
		static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

		/// <summary>
		/// A process started here inherits every handle this one lets it, its input and output among them. A scan
		/// outlives the install that starts it: holding the install's output open, it would keep whatever reads that
		/// (a script, another program) waiting for the end of a first scan, which takes a while.
		/// </summary>
		internal static void KeepStdHandlesToSelf() {
			const int StdInput = -10, StdOutput = -11, StdError = -12;
			const uint HandleFlagInherit = 1;
			foreach (int which in new[] { StdInput, StdOutput, StdError }) {
				IntPtr handle = GetStdHandle(which);
				if (handle != IntPtr.Zero && handle != new IntPtr(-1)) SetHandleInformation(handle, HandleFlagInherit, 0);
			}
		}
	}
}
