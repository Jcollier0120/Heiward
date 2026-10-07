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
using HEI.GUI.Utils;

namespace HEI.GUI.Tests {
	/// <summary>
	/// A Windows notification's title and message sit in a single-quoted PowerShell string: PowerShell ends one at the
	/// curly quotes ‘ ’ ‚ ‛ as well as at ', so none of them may reach it as it is.
	/// </summary>
	public class NotificationScriptTests {
		[Fact]
		public void Quotes_of_every_kind_become_character_references() {
			Assert.Equal("O&#x2019;Brien &apos;&#x2018;&#x201A;&#x201B;", DesktopNotificationHelper.EscapeXml("O’Brien '‘‚‛"));
		}

		[Fact]
		public void A_title_or_message_with_curly_quotes_stays_text() {
			if (!OperatingSystem.IsWindows()) return;
			string script = DesktopNotificationHelper.WindowsScript("x’; Write-Output INJECTED; ’y", "x'; Write-Output INJECTED; ‘y");
			Assert.DoesNotContain("’", script);
			Assert.DoesNotContain("‘", script);
			// Parsed by PowerShell, never run: its commands are its own.
			string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
			var psi = new ProcessStartInfo(ps) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
			const string Parse = """
				$ast = [System.Management.Automation.Language.Parser]::ParseInput($env:HEI_TEST_SCRIPT, [ref]$null, [ref]$null)
				[Console]::Out.Write((@($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Sort-Object -Unique) -join ','))
				""";
			foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Parse)) }) psi.ArgumentList.Add(a);
			psi.Environment["HEI_TEST_SCRIPT"] = script;
			using var p = Process.Start(psi)!;
			string commands = p.StandardOutput.ReadToEnd();
			Assert.True(p.WaitForExit(60_000));
			Assert.Equal("New-Item,New-ItemProperty,New-Object,Out-Null,Test-Path", commands);
		}
	}
}
