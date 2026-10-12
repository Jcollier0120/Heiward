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
using HEI.Core.AI;
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <param name="Device">npu, gpu or cpu; null lets the installer pick (the NPU when there is one).</param>
	/// <param name="OnDemand">No scheduled scans (asked when the AI runs on the GPU or CPU).</param>
	/// <param name="ScanSpeed">background, full or auto (<see cref="AgentConfig.ScanSpeed"/>).</param>
	/// <param name="RemoveGitHubCopy">Remove Heiward installed from GitHub, when there is one.</param>
	/// <param name="Gpu">The graphics card for GPU work, on a PC with more than one (<see cref="AgentConfig.Gpu"/>); null lets the installer pick.</param>
	sealed record SetupRequest(string? Device, bool OnDemand, string? ScanSpeed, bool RemoveGitHubCopy = false, string? Gpu = null);

	/// <summary>
	/// The setup's questions on the review page, then <c>hei install --yes</c> with the answers in its own process,
	/// its output kept for the page. Two first runs use it:
	/// <list type="bullet">
	/// <item>the Store version's, which has no console: the install writes <see cref="AgentPaths.StoreSetUp"/> when it's done;</item>
	/// <item>the GitHub exe's, double-clicked: it copies itself to the install folder and starts that copy's page with
	/// <c>serve --setup</c> in a window of its own (<see cref="Installer.OpenSetupWindowAsync"/>); <see cref="FirstRun"/>
	/// holds until the install succeeds.</item>
	/// </list>
	/// </summary>
	static class StoreSetup {
		static readonly object gate = new();
		static readonly List<string> output = new();
		static bool running, failed, installedHere;
		static DateTime lastSeenUtc = DateTime.UtcNow;

		/// <summary>This page was started by the GitHub exe's setup window (<c>serve --setup</c>), to set Heiward up.</summary>
		public static bool FirstRun { get; private set; }

		/// <summary>Folders the install may copy FFmpeg and the AI components from: the downloaded exe's.</summary>
		static IReadOnlyList<string> reuseFrom = Array.Empty<string>();

		/// <summary>The GitHub exe's setup window: the page asks the questions until the install has succeeded.</summary>
		public static void BeginFirstRun(IReadOnlyList<string>? reuse) {
			FirstRun = true;
			reuseFrom = reuse ?? Array.Empty<string>();
		}

		public static bool Needed => StorePackage.IsPackaged ? !File.Exists(AgentPaths.StoreSetUp) : FirstRun && !installedHere;

		public static bool Running { get { lock (gate) return running; } }

		/// <summary>The setup window's page asked for the state: it's still open.</summary>
		public static void Seen() { lock (gate) lastSeenUtc = DateTime.UtcNow; }

		/// <summary>
		/// The GitHub exe's setup window was closed before the install started: nothing asks for this page, so it can
		/// stop rather than stay up for a Heiward that isn't installed. The next double-click starts it again.
		/// </summary>
		public static bool Abandoned(DateTime nowUtc) {
			lock (gate) return Abandoned(FirstRun, installedHere, running, lastSeenUtc, nowUtc);
		}

		internal static bool Abandoned(bool firstRun, bool installed, bool running, DateTime lastSeenUtc, DateTime nowUtc) =>
			firstRun && !installed && !running && nowUtc - lastSeenUtc > TimeSpan.FromMinutes(3);

		/// <summary>What runs the install: the Store version's console alias, or this copy, the installed exe.</summary>
		static string SetupExe => StorePackage.IsPackaged ? StorePackage.ConsoleExe : Environment.ProcessPath ?? Installer.InstalledExe;

		/// <summary>
		/// For the page's state poll: whether to show the questions, the NPU it found (the page names it and
		/// advises against the GPU or CPU next to it), a copy from GitHub it can remove, and the install's progress.
		/// </summary>
		public static object View() {
			bool needed = Needed;
			bool npu = needed && NpuComponents.IsSupportedPlatform;
			IReadOnlyList<GpuAdapter>? cards = needed ? GpuAdapters.List() : null;
			lock (gate) {
				return new {
					needed,
					// The GitHub exe's setup window, with its welcome and the version it installs; the Store version's
					// setup, on its review page, goes straight to the questions.
					firstRun = FirstRun,
					version = AppBuild.Current.Split('+')[0],
					// Castellan's site, for the setup's links to the staff Heiward is part of.
					site = ManorCard.ManorSiteUrl,
					running,
					failed,
					output = needed ? output.ToList() : new List<string>(),
					npu,
					npuName = NpuComponents.NpuName,
					npuHardware = npu ? HardwareName(NpuHardware.Name) : "",
					// An NPU this version can't use yet, named so the page can say why the AI runs elsewhere.
					unsupportedNpu = needed && !npu && NpuHardware.Vendor != NpuVendor.None ? HardwareName(NpuHardware.Name) : "",
					// Only the Store version replaces a copy from GitHub: the GitHub exe's own setup is that copy.
					gitHubCopy = needed && !running && StorePackage.IsPackaged ? Installer.GitHubCopyVersion() : null,
					// More than one graphics card: the page asks which one does the GPU work, suggesting the recommended one.
					gpus = cards?.Select(g => new { key = g.Key, memory = (long)g.DedicatedMemory }).ToList(),
					recommendedGpu = cards != null ? GpuAdapters.Recommended(cards)?.Key : null,
				};
			}
		}

		/// <summary>"Snapdragon(R) X2 Elite Extreme - X2E94100 - Qualcomm(R) Hexagon(TM) NPU" without the trademark marks.</summary>
		internal static string HardwareName(string name) =>
			System.Text.RegularExpressions.Regex.Replace(name.Replace("(R)", "").Replace("(TM)", "").Replace("®", "").Replace("™", ""), @"\s{2,}", " ").Trim();

		/// <summary>Starts the install; the reason when the answers don't add up or it's running already.</summary>
		/// <param name="setUp">Runs once the install succeeded (the review page reloads the settings it wrote).</param>
		public static string? Start(SetupRequest request, Action setUp) {
			if (Arguments(request) is not { } args) return "Those answers aren't ones the page offers.";
			lock (gate) {
				if (running) return "Setup is already running.";
				output.Clear();
				failed = false;
				var psi = new ProcessStartInfo(SetupExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
				foreach (string a in args) psi.ArgumentList.Add(a);
				var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
				DataReceivedEventHandler keep = (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (gate) output.Add(e.Data.Trim()); };
				p.OutputDataReceived += keep;
				p.ErrorDataReceived += keep;
				p.Exited += (_, _) => {
					p.WaitForExit(); // the last lines of output
					lock (gate) {
						running = false;
						failed = p.ExitCode != 0;
						if (!failed && FirstRun) installedHere = true;
					}
					AgentPaths.AppendLog(p.ExitCode == 0 ? "set up from the review page" : $"setup from the review page failed (exit {p.ExitCode})");
					if (p.ExitCode == 0) setUp();
					p.Dispose();
				};
				p.Start();
				p.BeginOutputReadLine();
				p.BeginErrorReadLine();
				running = true;
			}
			AgentPaths.AppendLog("setup started from the review page: " + string.Join(' ', args));
			return null;
		}

		/// <summary>The install's command line for the page's answers, or null when they aren't valid.</summary>
		/// <param name="gpus">The cards the answer may name; null: this PC's.</param>
		/// <param name="reuse">The folders the install may copy the components from; null: the setup window's.</param>
		internal static List<string>? Arguments(SetupRequest request, IReadOnlyList<GpuAdapter>? gpus = null, IReadOnlyList<string>? reuse = null) {
			if (request.ScanSpeed is null || !AgentConfig.ScanSpeeds.Contains(request.ScanSpeed)) return null;
			if (request.Device is not (null or "npu" or "gpu" or "cpu")) return null;
			var args = new List<string> { "install", "--yes", "--no-browser", "--scan-speed", request.ScanSpeed };
			if (request.Device != null) args.AddRange(new[] { "--device", request.Device });
			if (request.OnDemand) args.Add("--on-demand");
			if (request.RemoveGitHubCopy) args.Add("--remove-github-copy");
			foreach (string folder in reuse ?? reuseFrom) args.AddRange(new[] { "--reuse-from", folder });
			if (!string.IsNullOrWhiteSpace(request.Gpu)) {
				if (GpuAdapters.Find(request.Gpu, gpus ?? GpuAdapters.List()) is not { } card) return null;
				args.AddRange(new[] { "--gpu", card.Key });
			}
			return args;
		}
	}
}
