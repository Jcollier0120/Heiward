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

namespace HEI.Agent {
	/// <param name="Device">gpu or cpu, asked only without an NPU; null lets the installer use the NPU.</param>
	/// <param name="OnDemand">No scheduled scans (asked only without an NPU).</param>
	/// <param name="ScanSpeed">background, full or auto (<see cref="AgentConfig.ScanSpeed"/>).</param>
	sealed record SetupRequest(string? Device, bool OnDemand, string? ScanSpeed);

	/// <summary>
	/// The Store version's first run, which has no console: the review page asks what the GitHub exe asks
	/// in its window, then this runs <c>hei install --yes</c> with the answers in its own process and keeps
	/// its output for the page. The install writes <see cref="AgentPaths.StoreSetUp"/> when it's done.
	/// </summary>
	static class StoreSetup {
		static readonly object gate = new();
		static readonly List<string> output = new();
		static bool running, failed;

		public static bool Needed => StorePackage.IsPackaged && !File.Exists(AgentPaths.StoreSetUp);

		public static bool Running { get { lock (gate) return running; } }

		/// <summary>For the page's state poll: whether to show the questions, and the install's progress.</summary>
		public static object View() {
			bool needed = Needed;
			lock (gate) {
				return new {
					needed,
					running,
					failed,
					output = needed ? output.ToList() : new List<string>(),
					npu = needed && NpuComponents.IsSupportedPlatform,
					npuName = NpuComponents.NpuName,
				};
			}
		}

		/// <summary>Starts the install; the reason when the answers don't add up or it's running already.</summary>
		/// <param name="setUp">Runs once the install succeeded (the review page reloads the settings it wrote).</param>
		public static string? Start(SetupRequest request, Action setUp) {
			if (Arguments(request) is not { } args) return "Those answers aren't ones the page offers.";
			lock (gate) {
				if (running) return "Setup is already running.";
				output.Clear();
				failed = false;
				var psi = new ProcessStartInfo(StorePackage.ConsoleExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
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
		internal static List<string>? Arguments(SetupRequest request) {
			if (request.ScanSpeed is null || !AgentConfig.ScanSpeeds.Contains(request.ScanSpeed)) return null;
			if (request.Device is not (null or "gpu" or "cpu")) return null;
			var args = new List<string> { "install", "--yes", "--no-browser", "--scan-speed", request.ScanSpeed };
			if (request.Device != null) args.AddRange(new[] { "--device", request.Device });
			if (request.OnDemand) args.Add("--on-demand");
			return args;
		}
	}
}
