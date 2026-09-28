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
using System.Text;
using Microsoft.Win32;

namespace VDF.Agent {
	/// <summary>
	/// A Windows notification through Windows PowerShell's WinRT projection: no extra package. The install
	/// registers an AppUserModelID (a registry key, no shortcut needed) so notifications say "Heiward";
	/// a copy that isn't installed sends them under PowerShell's own. Clicking one opens the review page.
	/// </summary>
	static class Toast {
		const string PowerShellAumid = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";
		const string AppId = "Heiward";
		public const string AppIdKey = @"Software\Classes\AppUserModelId\" + AppId;

		public static void Register(string displayName) {
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(AppIdKey);
			key.SetValue("DisplayName", displayName);
		}

		public static void Unregister() {
			try { Registry.CurrentUser.DeleteSubKeyTree(AppIdKey, throwOnMissingSubKey: false); } catch { }
		}

		static string SenderId {
			get {
				using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AppIdKey);
				return key != null ? AppId : PowerShellAumid;
			}
		}

		const string Script = """
			$ErrorActionPreference = 'Stop'
			[void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
			[void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
			function E([string]$s) { [System.Security.SecurityElement]::Escape($s) }
			$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
			$xml.LoadXml("<toast activationType=""protocol"" launch=""$(E $env:VDF_TOAST_LAUNCH)""><visual><binding template=""ToastGeneric""><text>$(E $env:VDF_TOAST_TITLE)</text><text>$(E $env:VDF_TOAST_BODY)</text></binding></visual></toast>")
			[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:VDF_TOAST_APPID).Show([Windows.UI.Notifications.ToastNotification]::new($xml))
			""";

		public static async Task ShowAsync(string title, string body, string launchUrl) {
			try {
				string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
				var psi = new ProcessStartInfo(ps) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
				foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
					psi.ArgumentList.Add(a);
				psi.Environment["VDF_TOAST_TITLE"] = title.Length > 120 ? title[..120] : title;
				psi.Environment["VDF_TOAST_BODY"] = body.Length > 400 ? body[..400] : body;
				psi.Environment["VDF_TOAST_APPID"] = SenderId;
				psi.Environment["VDF_TOAST_LAUNCH"] = launchUrl;
				using var p = Process.Start(psi)!;
				string err = await p.StandardError.ReadToEndAsync();
				await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
				if (p.ExitCode != 0)
					AgentPaths.AppendLog($"toast failed (exit {p.ExitCode}): {err.Trim()[..Math.Min(300, err.Trim().Length)]}");
			}
			catch (Exception e) {
				AgentPaths.AppendLog($"toast failed: {e.Message}");
			}
		}
	}
}
