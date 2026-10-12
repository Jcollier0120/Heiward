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

namespace HEI.Agent {
	static partial class Installer {
		/// <summary>
		/// Whether the uninstall asks in a window, as the setup does: a person started it from Settings > Apps (no console
		/// window, nothing reading its output). Never for a script, Castellan's uninstall (which reads what it says),
		/// --no-window (Apps &amp; Features' quiet uninstall), the Store version, or a run inside a package's environment,
		/// which hands over to Task Scheduler first (<see cref="HandOver"/>): the run that task starts asks.
		/// </summary>
		static bool UninstallWindowWanted() =>
			Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected
			&& !StorePackage.IsPackaged && StorePackage.Identity == null && !DevBuild.Current
			&& Environment.GetEnvironmentVariable("HEIWARD_SETUP") != "console";

		/// <summary>
		/// Heiward's page at #/uninstall, in Edge as an app window like the setup's (<see cref="OpenAppWindow"/>): it asks
		/// whether to keep the settings and the report, then the page's own process uninstalls (<see cref="UninstallFromPage"/>).
		/// </summary>
		/// <returns>False when the page can't ask (it didn't start, or another copy's has the port): the uninstall goes ahead here.</returns>
		static bool OpenUninstallWindow(bool purge) {
			try { return OpenUninstallWindowAsync(purge, CancellationToken.None).GetAwaiter().GetResult(); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or HttpRequestException or TaskCanceledException) {
				Log("uninstall window: " + e.Message + "; the uninstall goes ahead without it");
				return false;
			}
		}

		static async Task<bool> OpenUninstallWindowAsync(bool purge, CancellationToken ct) {
			if (!File.Exists(InstalledExe)) return false;
			int port = AgentConfig.Load().Port;
			if (!await MakeWayForPageAsync(port, ct)) return false;
			if (!await PageUninstallsAsync(port)) {
				// No page, or one from before it had #/uninstall: the installed copy's, started now.
				if (await ReviewServer.IsUpAsync(port)) await ReviewServer.AskToCloseAsync(port);
				var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, CreateNoWindow = true };
				foreach (string a in new[] { "serve", "--no-browser" }) psi.ArgumentList.Add(a);
				KeepStdHandlesToSelf();
				using (Process.Start(psi)) { }
				for (int i = 0; i < 60 && !await PageUninstallsAsync(port); i++) await Task.Delay(250, ct);
				if (!await PageUninstallsAsync(port)) {
					Log("uninstall window: Heiward's page didn't start, so the uninstall goes ahead without it");
					return false;
				}
			}
			OpenAppWindow(UninstallPageUrl(port, purge));
			return true;
		}

		/// <summary>Whether the page on <paramref name="port"/> is the installed copy's, with #/uninstall (its ping says uninstallPage).</summary>
		static async Task<bool> PageUninstallsAsync(int port) {
			try {
				using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
				using var ping = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/ping"));
				JsonElement root = ping.RootElement;
				return root.TryGetProperty("uninstallPage", out var page) && page.ValueKind == JsonValueKind.True
					&& root.TryGetProperty("exe", out var exe) && exe.ValueKind == JsonValueKind.String && RunsFrom(exe.GetString(), InstallDir);
			}
			catch { return false; }
		}

		internal static string UninstallPageUrl(int port, bool purge) => ReviewServer.PageUrl(port) + (purge ? "#/uninstall/purge" : "#/uninstall");

		/// <summary>Whether this process's page may uninstall Heiward: it's the installed copy's, from GitHub.</summary>
		internal static bool PageCanUninstall => !StorePackage.IsPackaged && !DevBuild.Current && RunsFrom(Environment.ProcessPath, InstallDir);

		/// <summary>
		/// The uninstall the page's window asked for (POST /api/uninstall), run by the page's own process: what it did, for
		/// the page's Details. The page's process stops once it has answered, and its folder goes after it.
		/// </summary>
		public static (bool Ok, List<string> Lines) UninstallFromPage(bool purge) {
			var lines = new List<string>();
			uninstallLog = true;
			Log($"uninstall started on Heiward's page ({AppBuild.Current}, from {Environment.ProcessPath}{(purge ? ", deleting the settings and report" : "")})");
			try {
				UninstallSteps(purge, dryRun: false, lines.Add, fromPage: true);
				Log("uninstall done");
				return (true, lines);
			}
			catch (Exception e) {
				Log("uninstall failed: " + e);
				lines.Add("Heiward couldn't finish uninstalling: " + e.Message);
				return (false, lines);
			}
		}
	}
}
