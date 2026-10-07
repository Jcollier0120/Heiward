// /*
//     Copyright (C) 2026 0x90d
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

namespace HEI.GUI.Utils;

static class DesktopNotificationHelper {
	const string AppName = "Video Duplicate Finder";

	public static void Notify(string title, string message) {
		try {
			if (OperatingSystem.IsWindows())
				NotifyWindows(title, message);
			else if (OperatingSystem.IsMacOS())
				NotifyMacOS(title, message);
			else if (OperatingSystem.IsLinux())
				NotifyLinux(title, message);
		}
		catch { }
	}

	const string AppId = "VideoDuplicateFinder";

	static void NotifyWindows(string title, string message) {
		var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(WindowsScript(title, message)));
		Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}") {
			CreateNoWindow = true
		});
	}

	/// <summary>
	/// Windows 10/11 requires the AppId to be registered in HKCU before CreateToastNotifier will show anything
	/// (unregistered ids fail silently), so the script registers it (HKCU, no elevation needed). The script goes in
	/// as -EncodedCommand, so the command line needs no quoting; the title and message sit in a single-quoted
	/// PowerShell string, which <see cref="EscapeXml"/> keeps them from ending.
	/// </summary>
	internal static string WindowsScript(string title, string message) => $$"""
			[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
			[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null
			$id = '{{AppId}}'
			$rp = "HKCU:\Software\Classes\AppUserModelId\$id"
			if (-not (Test-Path $rp)) {
			    New-Item -Path $rp -Force | Out-Null
			    New-ItemProperty -Path $rp -Name DisplayName -Value '{{AppName}}' -PropertyType ExpandString -Force | Out-Null
			}
			$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
			$xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text>{{EscapeXml(title)}}</text><text>{{EscapeXml(message)}}</text></binding></visual></toast>')
			[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($id).Show([Windows.UI.Notifications.ToastNotification]::new($xml))
			""";

	static void NotifyMacOS(string title, string message) {
		// ArgumentList avoids shell interpretation — no shell escaping needed,
		// only AppleScript string escaping inside the -e expression.
		var psi = new ProcessStartInfo("osascript") { CreateNoWindow = true };
		psi.ArgumentList.Add("-e");
		psi.ArgumentList.Add($"display notification \"{EscapeAppleScript(message)}\" with title \"{EscapeAppleScript(title)}\"");
		Process.Start(psi);
	}

	static void NotifyLinux(string title, string message) {
		// notify-send is available on virtually all Linux desktops (libnotify).
		// ArgumentList passes title and message as separate argv entries — no shell escaping needed.
		// Silently fails if notify-send is not installed.
		var psi = new ProcessStartInfo("notify-send") { CreateNoWindow = true };
		psi.ArgumentList.Add(title);
		psi.ArgumentList.Add(message);
		Process.Start(psi);
	}

	/// <summary>
	/// XML text that is also safe inside the script's single-quoted PowerShell string: ' becomes &amp;apos;, and the curly
	/// quotes ‘ ’ ‚ ‛ (which PowerShell ends a single-quoted string at too) become character references.
	/// </summary>
	internal static string EscapeXml(string s) => s
		.Replace("&", "&amp;")
		.Replace("<", "&lt;")
		.Replace(">", "&gt;")
		.Replace("\"", "&quot;")
		.Replace("'", "&apos;")
		.Replace("‘", "&#x2018;")
		.Replace("’", "&#x2019;")
		.Replace("‚", "&#x201A;")
		.Replace("‛", "&#x201B;");

	static string EscapeAppleScript(string s) => s
		.Replace("\\", "\\\\")
		.Replace("\"", "\\\"");
}
