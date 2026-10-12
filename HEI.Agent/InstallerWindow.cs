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
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HEI.Agent {
	static partial class Installer {
		/// <summary>
		/// The GitHub exe, double-clicked: the setup in a window of its own rather than the console. It copies itself
		/// to <see cref="InstallDir"/>, starts that copy's review page with <c>serve --setup</c> (the page asks the
		/// questions and runs the install, <see cref="StoreSetup"/>) and opens it in Edge as an app window, which stays
		/// open as Heiward's own once it's set up. Edge comes with Windows; without it, the default browser.
		/// </summary>
		/// <returns>
		/// False, with nothing changed but the copied exe, when the console is the better place: a script or another
		/// program started it, it's a development build or the wrong one for the PC (the console asks about that), or
		/// another copy's page holds the port. The install then runs in the console as before.
		/// </returns>
		public static async Task<bool> OpenSetupWindowAsync(CancellationToken ct) {
			if (Environment.GetEnvironmentVariable("HEIWARD_SETUP") == "console") return false;
			if (StorePackage.IsPackaged || StorePackage.InPackageFolder || DevBuild.Current) return false;
			if (Console.IsInputRedirected || !ConsoleShown()) return false;
			if (RuntimeInformation.OSArchitecture == Architecture.Arm64 && RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return false;
			IntPtr console = GetConsoleWindow();
			ShowWindow(console, SwHide);
			try {
				int port = AgentConfig.Load().Port;
				Directory.CreateDirectory(InstallDir);
				StopRunningAgents();
				if (await ReviewServer.IsUpAsync(port)) {
					// Another copy's page (the Store version's, or one run from elsewhere): the console says what to do.
					AgentPaths.AppendLog($"setup window: port {port} is another copy's page, so the setup runs in the console");
					ShowWindow(console, SwShow);
					return false;
				}
				File.Copy(CurrentExe, InstalledExe, overwrite: true);
				var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, CreateNoWindow = true };
				foreach (string a in SetupServeArguments(Path.GetDirectoryName(Path.GetFullPath(CurrentExe))!)) psi.ArgumentList.Add(a);
				KeepStdHandlesToSelf();
				using (Process.Start(psi)) { }
				for (int i = 0; i < 60 && !await ReviewServer.IsUpAsync(port); i++) await Task.Delay(250, ct);
				if (!await ReviewServer.IsUpAsync(port)) {
					AgentPaths.AppendLog("setup window: the page didn't start, so the setup runs in the console");
					ShowWindow(console, SwShow);
					return false;
				}
				AgentPaths.AppendLog($"setup window: opened from {CurrentExe}");
				OpenAppWindow(ReviewServer.PageUrl(port));
				return true;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {
				AgentPaths.AppendLog("setup window: " + e.Message + "; the setup runs in the console");
				ShowWindow(console, SwShow);
				return false;
			}
		}

		/// <summary>The installed copy's page in setup mode, allowed to copy what the downloaded exe's folder already has.</summary>
		internal static string[] SetupServeArguments(string downloadedFrom) => ["serve", "--setup", "--no-browser", "--reuse-from", downloadedFrom];

		/// <summary>
		/// The page in an Edge app window: no tabs nor address bar, Heiward's icon and title, as Castellan's own page opens.
		/// Whatever the default browser is; that browser only when Edge isn't there.
		/// </summary>
		internal static void OpenAppWindow(string url) {
			if (EdgePath() is string edge) {
				try {
					var psi = new ProcessStartInfo(edge) { UseShellExecute = false };
					foreach (string a in EdgeArguments(url)) psi.ArgumentList.Add(a);
					KeepStdHandlesToSelf();
					using (Process.Start(psi)) { }
					return;
				}
				catch (System.ComponentModel.Win32Exception) { }
			}
			ReviewServer.OpenInDefaultBrowser(url);
		}

		internal static string[] EdgeArguments(string url) => [$"--app={url}", "--window-size=1180,820", "--no-first-run"];

		/// <summary>Edge, as Windows registers it (App Paths), else where it's installed; null when it isn't.</summary>
		static string? EdgePath() {
			foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine }) {
				try {
					using RegistryKey? key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
					if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
				}
				catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
			}
			foreach (Environment.SpecialFolder f in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }) {
				string path = Path.Combine(Environment.GetFolderPath(f), "Microsoft", "Edge", "Application", "msedge.exe");
				if (File.Exists(path)) return path;
			}
			return null;
		}

		const int SwHide = 0, SwShow = 5;

		[DllImport("user32.dll")]
		static extern bool ShowWindow(IntPtr window, int command);
	}
}
