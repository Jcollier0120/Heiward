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
using System.Security;

namespace HEI.Agent {
	/// <summary>
	/// Two per-user Task Scheduler tasks (no admin rights), both only while the user is logged on,
	/// least privilege, low priority, one instance at a time:
	/// <list type="bullet">
	/// <item><see cref="ScanTask"/>: a scan every <see cref="AgentConfig.ScanEveryMinutes"/> minutes, catching
	/// up after the PC was off. On battery it runs only if allowed, and the scan itself steps aside in
	/// Battery Saver or below <see cref="AgentConfig.MinBatteryPercent"/>.</item>
	/// <item><see cref="OpenTask"/>: at sign-in, opens the review page in the default browser, at most once a
	/// day and only when something waits for review.</item>
	/// </list>
	/// Both run through conhost --headless: the console app gets its console, the user no window.
	/// </summary>
	static class Scheduler {
		public const string Folder = @"Heiward";
		public const string ScanTask = Folder + @"\Scan";
		public const string OpenTask = Folder + @"\Open review page";
		/// <summary>One run of the uninstall, which <see cref="Installer.Uninstall"/> hands over to Task Scheduler.</summary>
		public const string UninstallTask = Folder + @"\Uninstall";
		/// <summary>One run of reg.exe for the Store version, which removes a GitHub copy (<see cref="Installer.RemoveGitHubCopy"/>).</summary>
		public const string RemoveKeysTask = Folder + @"\Remove GitHub copy";

		static string Conhost => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");
		static string Reg => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
		static string Schtasks => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
		static string Cmd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

		static string X(string s) => SecurityElement.Escape(s)!;

		/// <param name="removeWhenGone">The Store version: <paramref name="agentExe"/> is the "hei" alias (<see cref="StorePackage.Alias"/>).</param>
		public static string ScanXml(AgentConfig cfg, string agentExe, bool removeWhenGone = false) {
			int every = Math.Max(15, cfg.ScanEveryMinutes);
			DateTime start = DateTime.Now.AddMinutes(10);
			string battery = (!cfg.ScanOnBattery).ToString().ToLowerInvariant();
			return Task(
				"Looks for likely duplicate photos and videos and lists them for review. Deletes nothing on its own.",
				$"""
				    <TimeTrigger>
				      <StartBoundary>{start:yyyy-MM-ddTHH:mm:ss}</StartBoundary>
				      <Repetition><Interval>PT{every}M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
				    </TimeTrigger>
				""",
				$"""
				    <DisallowStartIfOnBatteries>{battery}</DisallowStartIfOnBatteries>
				    <StopIfGoingOnBatteries>{battery}</StopIfGoingOnBatteries>
				    <ExecutionTimeLimit>PT4H</ExecutionTimeLimit>
				""",
				Action(ScanTask, agentExe, "scan --notify --scheduled", removeWhenGone));
		}

		public static string OpenXml(string agentExe, bool removeWhenGone = false) => Task(
			"Opens the duplicate review page once a day at sign-in when something waits for review.",
			$"""
			    <LogonTrigger>
			      <UserId>{X(Environment.UserDomainName + "\\" + Environment.UserName)}</UserId>
			      <Delay>PT1M</Delay>
			    </LogonTrigger>
			""",
			"""
			    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
			    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
			    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
			""",
			Action(OpenTask, agentExe, "open --if-pending --once-a-day", removeWhenGone));

		/// <summary>
		/// <c>hei uninstall</c> in a window, run once by <see cref="RunNow"/>: Task Scheduler starts it outside the
		/// package environment of whatever started the uninstall.
		/// </summary>
		public static string UninstallXml(string agentExe, bool purge) => Task(
			"Uninstalls Heiward, as Settings > Apps asked.",
			"",
			"""
			    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
			    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
			    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
			""",
			purge ? "uninstall --purge" : "uninstall",
			command: agentExe);

		/// <summary>
		/// Deletes the HKCU <paramref name="keys"/> with reg.exe, then the task itself, run once by <see cref="RunNow"/>:
		/// Task Scheduler starts it outside the package, where the user's keys are.
		/// </summary>
		public static string DeleteKeysXml(IEnumerable<string> keys) => Task(
			"Removes the registry entries of the copy of Heiward from GitHub, which the Microsoft Store version replaces.",
			"",
			"""
			    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
			    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
			    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
			""",
			DeleteKeysAction(keys));

		/// <summary>cmd's quotes around it all: a command that starts with a quote loses its first and last one.</summary>
		internal static string DeleteKeysAction(IEnumerable<string> keys) =>
			$"--headless \"{Cmd}\" /d /c \"{string.Concat(keys.Select(k => $"\"{Reg}\" delete \"HKCU\\{k}\" /f >nul 2>&1 & "))}\"{Schtasks}\" /Delete /TN \"{RemoveKeysTask}\" /F >nul\"";

		/// <summary>
		/// What conhost --headless runs. Uninstalling the Store version removes the alias and runs none of
		/// Heiward's code, so there the task checks for the alias first, and deletes itself once it's gone
		/// rather than failing at every trigger.
		/// </summary>
		internal static string Action(string task, string agentExe, string args, bool removeWhenGone) => !removeWhenGone
			? $"--headless \"{agentExe}\" {args}"
			: $"--headless \"{Cmd}\" /d /c if exist \"{agentExe}\" (\"{agentExe}\" {args}) else \"{Schtasks}\" /Delete /TN \"{task}\" /F";

		/// <param name="command">What the task runs: conhost --headless, unless it should have a window.</param>
		static string Task(string description, string trigger, string power, string action, string? command = null) => $"""
			<?xml version="1.0" encoding="UTF-16"?>
			<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
			  <RegistrationInfo><Description>{X(description)}</Description></RegistrationInfo>
			  <Triggers>
			{trigger}  </Triggers>
			  <Principals>
			    <Principal id="Author">
			      <LogonType>InteractiveToken</LogonType>
			      <RunLevel>LeastPrivilege</RunLevel>
			    </Principal>
			  </Principals>
			  <Settings>
			    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
			{power}    <StartWhenAvailable>true</StartWhenAvailable>
			    <Priority>7</Priority>
			  </Settings>
			  <Actions Context="Author">
			    <Exec>
			      <Command>{X(command ?? Conhost)}</Command>
			      <Arguments>{X(action)}</Arguments>
			    </Exec>
			  </Actions>
			</Task>
			""";

		public static void Register(string name, string xml) {
			string file = Path.Combine(Path.GetTempPath(), $"heiward-task-{Guid.NewGuid():N}.xml");
			File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
			try {
				(int code, string output) = Run("/Create", "/TN", name, "/XML", file, "/F");
				if (code != 0) throw new InvalidOperationException($"Task Scheduler refused '{name}': {output.Trim()}");
			}
			finally { File.Delete(file); }
		}

		public static void Remove(string name) => Run("/Delete", "/TN", name, "/F");

		/// <summary>Starts a task now; false when Task Scheduler wouldn't.</summary>
		public static bool RunNow(string name) => Run("/Run", "/TN", name).Item1 == 0;

		public static bool Exists(string name) => Run("/Query", "/TN", name).Item1 == 0;

		/// <summary>"every hour", "every 6 hours on AC power", "only when you press Scan now".</summary>
		public static string Describe(AgentConfig cfg) {
			if (cfg.ScanEveryMinutes <= 0) return "scans only when you press Scan now";
			int m = Math.Max(15, cfg.ScanEveryMinutes);
			string every = m % 60 != 0 ? $"every {m} min" : m == 60 ? "every hour" : $"every {m / 60} hours";
			return "scans " + every + (cfg.ScanOnBattery ? "" : " on AC power");
		}

		static (DateTime At, string? Next) cachedQuery;

		/// <summary>
		/// "Next Run Time" of the scan task, or null when it isn't registered or someone disabled it in Task
		/// Scheduler: either way no scheduled scan comes. Cached for a minute.
		/// </summary>
		public static string? NextRun() {
			if (DateTime.UtcNow - cachedQuery.At < TimeSpan.FromMinutes(1)) return cachedQuery.Next;
			string? next = null;
			try {
				(int code, string output) = Run("/Query", "/TN", ScanTask, "/FO", "LIST", "/V");
				if (code == 0) {
					var lines = output.Split('\n').Select(l => l.Trim()).ToList();
					string? Field(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();
					if (!string.Equals(Field("Scheduled Task State"), "Disabled", StringComparison.OrdinalIgnoreCase))
						next = Field("Next Run Time") ?? "scheduled";
				}
			}
			catch { }
			cachedQuery = (DateTime.UtcNow, next);
			return next;
		}

		/// <summary>The next <see cref="NextRun"/> asks Task Scheduler again (the tasks just changed).</summary>
		public static void Forget() => cachedQuery = default;

		static (int, string) Run(params string[] args) {
			var psi = new ProcessStartInfo(Schtasks) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
			foreach (string a in args) psi.ArgumentList.Add(a);
			using var p = Process.Start(psi)!;
			string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
			p.WaitForExit(30_000);
			return (p.ExitCode, output);
		}
	}
}
