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

using System.Net;
using System.Net.Sockets;

namespace HEI.Agent.Tests;

/// <summary>
/// Installing or uninstalling stops the installed copy's processes only. A hei.exe run from anywhere else is
/// another copy: a reinstall that stopped every hei.exe lost a test build's scan halfway (2026-10-02).
/// </summary>
[Collection(AgentHomeCollection.Name)]
public sealed class OtherCopiesTests : IDisposable {
	const string InstallDir = @"C:\Users\me\AppData\Local\Programs\Heiward";
	const int Self = 100;

	static readonly (int Id, string? Exe)[] Running = {
		(1, InstallDir + @"\hei.exe"),                                    // the installed copy's page or scan
		(2, @"C:\USERS\ME\APPDATA\LOCAL\PROGRAMS\HEIWARD\HEI.EXE"),       // the same, as another case
		(3, InstallDir + @"\bin\ffmpeg.exe"),                             // its scan's FFmpeg
		(4, @"C:\Users\me\AppData\Local\Temp\claude\scratchpad\hei.exe"), // a test build with its own HEIWARD_HOME
		(5, @"E:\Heiward\hei.exe"),                                       // a USB drive
		(6, @"C:\Users\me\Downloads\Heiward-1.4.0-arm64\hei.exe"),        // an unzipped release
		(7, @"C:\Users\me\AppData\Local\Programs\Heiward-old\hei.exe"),   // a folder whose name starts the same
		(8, @"C:\Program Files\WindowsApps\TheNexus.Heiward_1.3.0.0_arm64__mcanr0hfqkj1g\hei.exe"), // the Store version
		(9, null),                                                        // one whose exe couldn't be read
		(Self, InstallDir + @"\hei.exe"),                                 // this process: the installed copy's uninstall
	};

	[Fact]
	public void OnlyTheInstalledCopysProcesses_AreStopped() =>
		Assert.Equal(new[] { 1, 2, 3 }, Installer.RunningFrom(Running, InstallDir, Self).Select(p => p.Id));

	[Fact]
	public void TheInstallFolder_MayEndInASeparator() =>
		Assert.Equal(new[] { 1, 2, 3 }, Installer.RunningFrom(Running, InstallDir + @"\", Self).Select(p => p.Id));

	[Theory]
	[InlineData(InstallDir + @"\..\Heiward\hei.exe", true)]
	[InlineData(InstallDir + @"\..\Heiward-old\hei.exe", false)]
	[InlineData(InstallDir, false)] // the folder itself
	[InlineData("", false)]         // a page from a build whose ping doesn't name its exe
	public void WhereAnExeRuns_GoesByItsFullPath(string exe, bool inside) => Assert.Equal(inside, Installer.RunsFrom(exe, InstallDir));

	[Fact]
	public void TheInstaller_FindsTheToken_InThePageServed() {
		using Stream html = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/index.html")!;
		string page = new StreamReader(html).ReadToEnd().Replace("__AGENT_TOKEN__", "0123456789abcdef");
		Assert.Equal("0123456789abcdef", ReviewServer.TokenIn(page));
	}

	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-copies-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public OtherCopiesTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>Another copy's page on the port: it names its exe, and closes when the installer asks, as its own page would.</summary>
	[Fact]
	public async Task AnotherCopysPage_SaysWhoseItIs_AndClosesWhenAsked() {
		int port;
		using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
			probe.Start();
			port = ((IPEndPoint)probe.LocalEndpoint).Port;
		}
		// Scans only on Scan now: the page starts none of its own.
		var cfg = new AgentConfig { Port = port, ScanEveryMinutes = 0 };
		Task<int> page = ReviewServer.RunAsync(cfg, openBrowser: false, CancellationToken.None);
		try {
			for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(port); i++) await Task.Delay(250);
			Assert.Equal(Environment.ProcessPath, await ReviewServer.PageExeAsync(port));

			Assert.True(await ReviewServer.AskToCloseAsync(port));
			Assert.Same(page, await Task.WhenAny(page, Task.Delay(TimeSpan.FromSeconds(10))));
			Assert.Null(await ReviewServer.PageExeAsync(port));
		}
		finally {
			if (!page.IsCompleted) await ReviewServer.AskToCloseAsync(port);
		}
	}
}
