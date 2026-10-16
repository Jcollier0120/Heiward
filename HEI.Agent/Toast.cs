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
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace HEI.Agent {
	/// <summary>
	/// A Windows notification through Windows PowerShell's WinRT projection: no extra package. Notifications go
	/// under an AppUserModelID of Heiward's own (a registry key, no shortcut needed) so they say "Heiward", with
	/// its icon: the install registers it, and any other copy (Manor's, a download, a build) registers it the
	/// first time it notifies. Windows may leave the key's icon out beside the name, so the icon is on the
	/// notification itself too. Only should the registry refuse do they go under PowerShell's own. The Store
	/// version sends them under its package's app ID, which already has the name and logo. Clicking one opens
	/// the review page.
	/// </summary>
	static class Toast {
		const string PowerShellAumid = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";
		const string AppId = "Heiward";
		public const string AppIdKey = @"Software\Classes\AppUserModelId\" + AppId;

		/// <param name="iconPng">A picture notifications show as the app's icon; null leaves Windows' default.</param>
		public static void Register(string displayName, string? iconPng = null) {
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(AppIdKey);
			key.SetValue("DisplayName", displayName);
			if (iconPng != null) key.SetValue("IconUri", iconPng);
		}

		public static void Unregister() {
			try { Registry.CurrentUser.DeleteSubKeyTree(AppIdKey, throwOnMissingSubKey: false); } catch { }
		}

		/// <summary>Writes the mark's picture to <paramref name="path"/> (from the page's files, which are compiled in); null if it can't.</summary>
		internal static string? WriteIconPng(string path) {
			try {
				using Stream? png = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/heiward.png");
				if (png == null) return null;
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				using (var file = File.Create(path)) png.CopyTo(file);
				return path;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return null;
			}
		}

		/// <summary>
		/// Who a notification is from, and the icon to put on it: the Store package's app ID (its logo is its own), else
		/// Heiward's key, registered here with the name and icon when either is missing (the icon in Heiward's folder,
		/// where the install's is gone or was never written), else PowerShell's.
		/// </summary>
		internal static (string Id, string? Icon) Sender() {
			if (StorePackage.IsPackaged) return (StorePackage.AppUserModelId, null);
			try {
				string? icon;
				using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(AppIdKey)) {
					icon = key?.GetValue("IconUri") as string;
					if (key?.GetValue("DisplayName") is string && icon != null && File.Exists(icon)) return (AppId, icon);
				}
				icon = WriteIconPng(Path.Combine(AgentPaths.Home, "heiward.png"));
				Register(AppId, icon);
				return (AppId, icon);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) {
				return (PowerShellAumid, null);
			}
		}

		const string Script = """
			$ErrorActionPreference = 'Stop'
			[void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
			[void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
			function E([string]$s) { [System.Security.SecurityElement]::Escape($s) }
			$logo = ''
			if ($env:HEI_TOAST_LOGO) { $logo = "<image placement=""appLogoOverride"" hint-crop=""none"" src=""$(E $env:HEI_TOAST_LOGO)""/>" }
			$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
			$xml.LoadXml("<toast activationType=""protocol"" launch=""$(E $env:HEI_TOAST_LAUNCH)""><visual><binding template=""ToastGeneric""><text>$(E $env:HEI_TOAST_TITLE)</text><text>$(E $env:HEI_TOAST_BODY)</text>$logo</binding></visual></toast>")
			[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:HEI_TOAST_APPID).Show([Windows.UI.Notifications.ToastNotification]::new($xml))
			""";

		public static async Task ShowAsync(string title, string body, string launchUrl) {
			try {
				string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
				var psi = new ProcessStartInfo(ps) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
				foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
					psi.ArgumentList.Add(a);
				psi.Environment["HEI_TOAST_TITLE"] = title.Length > 120 ? title[..120] : title;
				psi.Environment["HEI_TOAST_BODY"] = body.Length > 400 ? body[..400] : body;
				(string id, string? icon) = Sender();
				psi.Environment["HEI_TOAST_APPID"] = id;
				psi.Environment["HEI_TOAST_LOGO"] = icon != null ? new Uri(icon).AbsoluteUri : "";
				psi.Environment["HEI_TOAST_LAUNCH"] = launchUrl;
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
