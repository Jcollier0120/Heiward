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

namespace HEI.Agent.Tests;

/// <summary>
/// The installer's PowerShell: a path is always text. PowerShell ends a single-quoted string at the curly quotes
/// ‘ ’ ‚ ‛ as well as at ', so a profile like O’Brien must not end one early.
/// </summary>
public sealed class PowerShellQuoteTests {
	/// <summary>Runs a script in Windows PowerShell 5.1, with `env` in its environment; what it printed.</summary>
	internal static string Run(string script, IDictionary<string, string>? env = null) {
		string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
		var psi = new ProcessStartInfo(ps) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
		foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
			Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false\n" + script)) })
			psi.ArgumentList.Add(a);
		foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
		using var p = Process.Start(psi)!;
		string output = p.StandardOutput.ReadToEnd();
		Assert.True(p.WaitForExit(60_000));
		Assert.Equal(0, p.ExitCode);
		return output;
	}

	/// <summary>The commands a script holds, parsed by PowerShell and never run, sorted and without repeats.</summary>
	internal static string CommandsIn(string script) => Run("""
		$ast = [System.Management.Automation.Language.Parser]::ParseInput($env:HEI_TEST_SCRIPT, [ref]$null, [ref]$null)
		[Console]::Out.Write((@($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Sort-Object -Unique) -join ','))
		""", new Dictionary<string, string> { ["HEI_TEST_SCRIPT"] = script });

	[Fact]
	public void Every_quote_is_doubled_the_curly_ones_too() {
		Assert.Equal("'it''s'", Installer.PsQuote("it's"));
		Assert.Equal("'O\u2019\u2019Brien'", Installer.PsQuote("O\u2019Brien"));
		Assert.Equal("'\u2018\u2018\u201A\u201A\u201B\u201B'", Installer.PsQuote("\u2018\u201A\u201B"));
	}

	[Fact]
	public void PowerShell_reads_back_exactly_the_text() {
		string[] values = [
			"'", "\u2018", "\u2019", "\u201A", "\u201B", "''", "'\u2018\u2019\u201A\u201B'",
			"x\u2019; Write-Output INJECTED; \u2019y", "x'; Write-Output INJECTED; 'y",
			@"C:\Users\O" + "\u2019" + @"Brien\AppData\Local\Programs\Heiward", "a $(Write-Output INJECTED) b $env:USERNAME", "tick `n `\" `$x`",
		];
		string output = Run(string.Join("\n", values.Select(v => $"[Console]::Out.Write('<' + {Installer.PsQuote(v)} + \">`n\")")));
		Assert.Equal(values.Select(v => $"<{v}>"), output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
	}

	[Fact]
	public void The_shortcut_script_for_a_profile_with_curly_quotes_runs_only_its_own_commands() {
		string home = "C:\\Users\\x\u2019; Write-Output INJECTED; \u2019y";
		string script = Installer.ShortcutScript(home + @"\Desktop\Heiward.lnk", @"C:\Windows\System32\conhost.exe",
			home + @"\AppData\Local\Programs\Heiward\hei.exe", home + @"\AppData\Local\Programs\Heiward");
		Assert.Equal("New-Object", CommandsIn(script));
		Assert.Contains("'--headless \"C:\\Users\\x\u2019\u2019; Write-Output INJECTED; \u2019\u2019y\\AppData", script);
	}
}
