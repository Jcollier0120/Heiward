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
using VDF.Core.AI;
using VDF.Core.FFTools;

namespace VDF.Agent {
	/// <summary>
	/// The whole install, per user and without admin rights, from the one self-contained exe:
	/// <list type="number">
	/// <item>copy the exe to %LOCALAPPDATA%\Programs\VDF Agent (its downloads land next to it);</item>
	/// <item>fetch FFmpeg, the AI runtime and model, and on Snapdragon PCs the NPU pack; probe the NPU;</item>
	/// <item>write agent.json: hourly scans on an NPU; without one, only if the user agrees, daily on the CPU;</item>
	/// <item>register the scan task and the sign-in "open the review page" task;</item>
	/// <item>add a Start menu entry and an Apps &amp; Features entry (so Windows can uninstall it);</item>
	/// <item>start the first scan and open the review page.</item>
	/// </list>
	/// <c>--dry-run</c> prints every step without changing anything.
	/// </summary>
	static class Installer {
		const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VDFAgent";
		const string DisplayName = "Duplicate check (VDF Agent)";

		public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VDF Agent");
		public static string InstalledExe => Path.Combine(InstallDir, "vdf-agent.exe");
		static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Duplicate check.lnk");
		static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "vdf-agent.exe");
		public static bool RunningInstalled => string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

		/// <summary>Scheduled scans without an NPU: every 6 hours, on AC power.</summary>
		internal const int GpuCpuScanMinutes = 6 * 60;

		/// <param name="device">Force the AI device (unattended installs); null = the NPU if there is one, else ask.</param>
		/// <param name="onDemand">True: no scheduled scans, only "Scan now". Null: ask when there's no NPU.</param>
		public static async Task<int> InstallAsync(bool dryRun, bool assumeYes, AiDevice? device, CancellationToken ct, bool? onDemand = null) {
			void Step(string s) => Console.WriteLine((dryRun ? "[dry run] " : "") + s);
			Console.WriteLine($"{DisplayName} setup");

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
					using var p = Process.Start(psi)!;
					await p.WaitForExitAsync(ct);
					return p.ExitCode;
				}
			}

			// Prerequisites. Downloads are SHA-256 pinned; nothing here needs admin rights.
			bool arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
			Step("Prerequisites: FFmpeg, ONNX Runtime + DINOv2 model" + (NpuComponents.IsSupportedPlatform ? ", Qualcomm NPU pack" : ""));
			if (!dryRun) {
				if (FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFmpeg) == null || FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFProbe) == null)
					Console.WriteLine($"  FFmpeg -> {await FfmpegDownloader.DownloadAndInstallAsync(null, ct)}");
				if (!AiComponents.IsReady) {
					Console.WriteLine($"  AI components (ONNX Runtime {AiComponents.RuntimeVersion} + model, ~100 MB)...");
					await AiComponents.DownloadAsync(null, ct);
				}
				if (NpuComponents.IsSupportedPlatform && !NpuComponents.IsInstalled) {
					Console.WriteLine($"  NPU pack (Qualcomm QNN {NpuComponents.QnnPackageVersion} + model, ~230 MB)...");
					await NpuComponents.DownloadAsync(null, ct);
				}
			}

			bool npu = device is null or AiDevice.Auto or AiDevice.Npu && (!dryRun ? NpuComponents.WillUseNpu(AiDevice.Auto) : NpuComponents.IsSupportedPlatform);
			Step(npu ? "NPU found: AI matching runs on the Hexagon NPU." : $"No supported NPU on this PC ({(arm64 ? "ARM64" : RuntimeInformation.OSArchitecture.ToString())}).");
			var cfg = File.Exists(AgentPaths.Config) ? AgentConfig.Load() : new AgentConfig();
			cfg.AiDevice = "auto";
			if (!npu) {
				AiDevice choice = device is AiDevice.Gpu or AiDevice.Cpu ? device.Value : AskDevice(assumeYes);
				if (choice == AiDevice.Auto) {
					Console.WriteLine("Nothing installed.");
					return 3;
				}
				if (choice == AiDevice.Gpu) {
					Step("GPU pack: ONNX Runtime DirectML + DirectML (~215 MB)");
					if (!dryRun) {
						await GpuComponents.DownloadAsync(null, ct);
						// A process loads one ONNX Runtime, so the GPU check runs in its own process.
						if (!await ProbeDeviceAsync("gpu", ct)) {
							Console.WriteLine("  The GPU could not run the model here; using the CPU instead.");
							choice = AiDevice.Cpu;
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
			Step($"Settings: {AgentPaths.Config} ({Scheduler.Describe(cfg)}; {(cfg.ScanAllDrives ? "every fixed drive, minus system, app and game folders" : "folders: " + string.Join("; ", cfg.Folders))})");
			if (!dryRun) cfg.Save();

			Step(cfg.ScanEveryMinutes > 0
				? $"Task Scheduler: '{Scheduler.ScanTask}' {Scheduler.Describe(cfg)}, '{Scheduler.OpenTask}' at sign-in"
				: $"Task Scheduler: no scan task (scans run when you press Scan now), '{Scheduler.OpenTask}' at sign-in");
			if (dryRun) {
				if (cfg.ScanEveryMinutes > 0) Console.WriteLine(Scheduler.ScanXml(cfg, InstalledExe));
				Console.WriteLine(Scheduler.OpenXml(InstalledExe));
			}
			else {
				if (cfg.ScanEveryMinutes > 0) Scheduler.Register(Scheduler.ScanTask, Scheduler.ScanXml(cfg, InstalledExe));
				else Scheduler.Remove(Scheduler.ScanTask);
				if (cfg.OpenPageAtSignIn) Scheduler.Register(Scheduler.OpenTask, Scheduler.OpenXml(InstalledExe));
			}

			Step($"Start menu: {StartMenuShortcut}");
			if (!dryRun) CreateShortcut(StartMenuShortcut);
			Step($"Apps & Features entry: HKCU\\{UninstallKey}");
			if (!dryRun) RegisterUninstall();

			if (dryRun) return 0;
			Console.WriteLine();
			Console.WriteLine("Installed. The first scan starts now; the review page opens in your browser and shows its progress.");
			Console.WriteLine("Later scans only look at new files. Nothing is ever deleted unless you choose it on the page.");
			StartDetached("scan", "--open");
			return 0;
		}

		public static int Uninstall(bool purge, bool dryRun) {
			void Step(string s) => Console.WriteLine((dryRun ? "[dry run] " : "") + s);
			Step($"Remove tasks '{Scheduler.ScanTask}' and '{Scheduler.OpenTask}'");
			if (!dryRun) { Scheduler.Remove(Scheduler.ScanTask); Scheduler.Remove(Scheduler.OpenTask); }
			Step($"Remove {StartMenuShortcut} and the Apps & Features entry");
			if (!dryRun) {
				try { File.Delete(StartMenuShortcut); } catch { }
				try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
				StopRunningAgents();
			}
			if (purge) {
				Step($"Delete settings, report and caches: {AgentPaths.Home}");
				if (!dryRun) try { Directory.Delete(AgentPaths.Home, recursive: true); } catch { }
			}
			else Console.WriteLine($"Kept settings and the report in {AgentPaths.Home} (add --purge to delete them).");
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

		/// <summary>Runs "vdf-agent probe --device {device}" in its own process: true when the model runs there.</summary>
		static async Task<bool> ProbeDeviceAsync(string device, CancellationToken ct) {
			var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
			psi.ArgumentList.Add("probe");
			psi.ArgumentList.Add("--device");
			psi.ArgumentList.Add(device);
			using var p = Process.Start(psi)!;
			string output = await p.StandardOutput.ReadToEndAsync(ct);
			await p.WaitForExitAsync(ct);
			Console.WriteLine("  " + output.Trim());
			return p.ExitCode == 0;
		}

		/// <summary>Stops other vdf-agent processes (a review page or a scan) so the exe can be replaced or removed.</summary>
		static void StopRunningAgents() {
			foreach (var p in Process.GetProcessesByName("vdf-agent").Where(p => p.Id != Environment.ProcessId)) {
				try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
				p.Dispose();
			}
		}

		/// <summary>A .lnk through WScript.Shell (always present): conhost --headless runs the console exe without a window.</summary>
		static void CreateShortcut(string lnk) {
			string conhost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");
			string ps = $"""
				$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{lnk.Replace("'", "''")}')
				$s.TargetPath = '{conhost}'
				$s.Arguments = '--headless "{InstalledExe.Replace("'", "''")}" open'
				$s.IconLocation = '{InstalledExe.Replace("'", "''")},0'
				$s.Description = 'Review likely duplicate photos and videos'
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
			key.SetValue("Publisher", "VDF Agent (Video Duplicate Finder fork)");
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
			string exe = File.Exists(InstalledExe) ? InstalledExe : CurrentExe;
			var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
			foreach (string a in args) psi.ArgumentList.Add(a);
			Process.Start(psi);
		}
	}
}
