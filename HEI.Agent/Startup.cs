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

using System.Reflection;
using Microsoft.Win32;

namespace HEI.Agent {
	/// <summary>
	/// Heiward at sign-in, as Windows lists it in Startup apps (Settings > Apps > Startup, and Task Manager's): a value of
	/// HKCU's Run that starts Heiward.exe (HEI.Launcher), which starts the review page without a window and, with
	/// <see cref="AgentConfig.OpenPageAtSignIn"/>, opens it once a day when something waits. Turned off there, it stays
	/// off: Windows keeps that choice apart (StartupApproved), and Heiward only ever rewrites its own value. It replaces
	/// the sign-in task (<see cref="Scheduler.OpenTask"/>), which Startup apps never showed. The Store version keeps
	/// that task: its package has no exe without a console.
	/// </summary>
	static class Startup {
		public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
		public const string ValueName = "Heiward";

		/// <summary>Heiward.exe, beside the installed hei.exe.</summary>
		public static string LauncherExe => Path.Combine(Installer.InstallDir, "Heiward.exe");

		/// <summary>The Run value: Heiward.exe with the sign-in task's arguments.</summary>
		internal static string Command(string launcher, bool openPage) =>
			$"\"{launcher}\" {(openPage ? "open --if-pending --once-a-day" : "open --no-browser")}";

		/// <summary>Writes Heiward.exe (when it changed) and the Run value; the sign-in task of an older Heiward goes.</summary>
		public static void Register(bool openPage) {
			WriteLauncher(LauncherExe);
			using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
				key.SetValue(ValueName, Command(LauncherExe, openPage));
			Scheduler.Remove(Scheduler.OpenTask);
		}

		/// <summary>Takes Heiward out of Startup apps (the uninstall; the folder, Heiward.exe with it, goes on its own).</summary>
		public static void Remove() {
			try {
				using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
				key?.DeleteValue(ValueName, throwOnMissingValue: false);
			}
			catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) {
				AgentPaths.AppendLog("uninstall: Heiward stays in Startup apps: " + e.Message);
			}
		}

		/// <summary>Heiward.exe from hei.exe's own resources (HEI.Agent.csproj embeds it), written only when it differs.</summary>
		internal static void WriteLauncher(string path) {
			using Stream exe = Assembly.GetExecutingAssembly().GetManifestResourceStream("Heiward.exe")
				?? throw new InvalidOperationException("This build of Heiward has no Heiward.exe to start it at sign-in.");
			using var bytes = new MemoryStream();
			exe.CopyTo(bytes);
			if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))) return;
			string temp = path + ".new";
			File.WriteAllBytes(temp, bytes.ToArray());
			File.Move(temp, path, overwrite: true);
		}
	}
}
