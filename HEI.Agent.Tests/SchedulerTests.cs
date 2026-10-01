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

namespace HEI.Agent.Tests;

public sealed class SchedulerTests {
	[Fact]
	public async Task Uninstall_DeletesTheFolder_OnceAFileInItIsLetGo() {
		// Regression: uninstalling tried once, three seconds on, and a file still held then (an antivirus
		// scan, an FFmpeg a scan started) left the folder behind. Now it tries every two seconds for half a minute.
		string dir = Directory.CreateTempSubdirectory("hei-uninstall-test").FullName;
		string held = Path.Combine(dir, "bin", "avcodec.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(held)!);
		File.WriteAllBytes(held, new byte[64]);
		Process cmd;
		using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.Read)) {
			cmd = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Installer.DeleteFolderLater(dir)) { UseShellExecute = false, CreateNoWindow = true })!;
			await Task.Delay(TimeSpan.FromSeconds(3)); // past its first try
			Assert.True(Directory.Exists(dir));
		}
		await cmd.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
		Assert.False(Directory.Exists(dir));
	}

	[Theory]
	[InlineData(60, true, "scans every hour")]
	[InlineData(360, false, "scans every 6 hours on AC power")]
	[InlineData(90, false, "scans every 90 min on AC power")]
	[InlineData(0, false, "scans only when you press Scan now")]
	public void Describe_SaysWhenScansRun(int minutes, bool onBattery, string expected) =>
		Assert.Equal(expected, Scheduler.Describe(new AgentConfig { ScanEveryMinutes = minutes, ScanOnBattery = onBattery }));

	[Fact]
	public void ScanTask_WithoutAnNpu_RepeatsEverySixHours_AndNotOnBattery() {
		string xml = Scheduler.ScanXml(new AgentConfig { ScanEveryMinutes = Installer.GpuCpuScanMinutes, ScanOnBattery = false }, @"C:\x\hei.exe");
		Assert.Contains("<Interval>PT360M</Interval>", xml);
		Assert.Contains("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", xml);
		Assert.Contains("scan --notify --scheduled", xml);
	}

	[Fact]
	public void UninstallTask_RunsTheExe_InAWindow_OnlyWhenStarted() {
		string xml = Scheduler.UninstallXml(@"C:\x\hei.exe", purge: true);
		Assert.Contains(@"<Command>C:\x\hei.exe</Command>", xml);
		Assert.Contains("<Arguments>uninstall --purge</Arguments>", xml);
		Assert.DoesNotContain("Trigger>", xml);
		Assert.True(System.Xml.Linq.XDocument.Parse(xml).Root != null);
	}

	[Fact]
	public void RemoveKeysTask_DeletesEachKey_ThenItself_InOnePairOfOuterQuotes() {
		string action = Scheduler.DeleteKeysAction(new[] { @"Software\Classes\heiward", @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Heiward" });
		Assert.Matches(@"^--headless "".+\\cmd\.exe"" /d /c """".+\\reg\.exe"" delete ""HKCU\\Software\\Classes\\heiward"" /f >nul 2>&1 & " +
			@""".+\\reg\.exe"" delete ""HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Heiward"" /f >nul 2>&1 & " +
			@""".+\\schtasks\.exe"" /Delete /TN ""Heiward\\Remove GitHub copy"" /F >nul""$", action);
	}

	[Fact]
	public void Task_OfTheGitHubCopy_RunsTheExe() =>
		Assert.Equal("--headless \"C:\\x\\hei.exe\" open", Scheduler.Action(Scheduler.OpenTask, @"C:\x\hei.exe", "open", removeWhenGone: false));

	[Fact]
	public void Task_OfTheStoreVersion_RunsTheAlias_AndDeletesItselfOnceTheAliasIsGone() {
		string action = Scheduler.Action(Scheduler.ScanTask, @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\hei.exe", "scan --notify --scheduled", removeWhenGone: true);
		Assert.Matches(@"^--headless "".+\\cmd\.exe"" /d /c if exist ""C:\\Users\\me\\AppData\\Local\\Microsoft\\WindowsApps\\hei\.exe"" " +
			@"\(""C:\\Users\\me\\AppData\\Local\\Microsoft\\WindowsApps\\hei\.exe"" scan --notify --scheduled\) " +
			@"else "".+\\schtasks\.exe"" /Delete /TN ""Heiward\\Scan"" /F$", action);
	}

	[Fact]
	public void TaskXml_OfTheStoreVersion_EscapesTheAction() {
		string xml = Scheduler.OpenXml(@"C:\a&b\hei.exe", removeWhenGone: true);
		Assert.Contains("if exist &quot;C:\\a&amp;b\\hei.exe&quot;", xml);
		Assert.Contains("/TN &quot;Heiward\\Open review page&quot;", xml);
	}

	[Theory]
	[InlineData("npu", false, false, "background", "install --yes --no-browser --scan-speed background --device npu")]
	[InlineData("gpu", false, false, "background", "install --yes --no-browser --scan-speed background --device gpu")]
	[InlineData("cpu", true, false, "full", "install --yes --no-browser --scan-speed full --device cpu --on-demand")]
	[InlineData(null, false, true, "full", "install --yes --no-browser --scan-speed full --remove-github-copy")]
	public void StoreSetup_RunsTheInstall_WithThePagesAnswers(string? device, bool onDemand, bool removeGitHub, string speed, string expected) =>
		Assert.Equal(expected, string.Join(' ', StoreSetup.Arguments(new SetupRequest(device, onDemand, speed, removeGitHub))!));

	[Theory]
	[InlineData("tpu", "full")]
	[InlineData("gpu", "fast")]
	[InlineData("gpu", null)]
	public void StoreSetup_RefusesAnswersThePageDoesntOffer(string? device, string? speed) =>
		Assert.Null(StoreSetup.Arguments(new SetupRequest(device, false, speed)));

	[Theory]
	[InlineData(0, true)]   // the target and arguments as UTF-16, at an even or odd offset
	[InlineData(1, true)]
	[InlineData(2, false)]  // ANSI
	public void GitHubShortcut_IsTellsApart_ByTheFolderItNames(int layout, bool unicode) {
		const string dir = @"C:\Users\me\AppData\Local\Programs\Heiward";
		byte[] path = unicode ? System.Text.Encoding.Unicode.GetBytes(@"--headless """ + dir + @"\hei.exe"" open") : System.Text.Encoding.Latin1.GetBytes(dir + @"\hei.exe");
		byte[] lnk = new byte[] { 0x4C, 0, 0, 0 }.Concat(new byte[layout == 1 ? 1 : 0]).Concat(path).Concat(new byte[] { 0, 0 }).ToArray();
		Assert.True(Installer.PointsAt(lnk, dir));
		// The Store version's desktop shortcut runs the package's Heiward.exe.
		Assert.False(Installer.PointsAt(System.Text.Encoding.Unicode.GetBytes(@"C:\Program Files\WindowsApps\TheNexus.Heiward_1.3.0.0_x64__mcanr0hfqkj1g\Heiward.exe"), dir));
	}

	[Fact]
	public void StorePackage_WorksOutThePackageFamily_FromThePublisher() =>
		// Partner Center's package family for Heiward is TheNexus.Heiward_mcanr0hfqkj1g.
		Assert.Equal("mcanr0hfqkj1g", StorePackage.PublisherId("CN=71D8D20A-F4D5-405B-9F54-12741B793F6D"));

	[Theory]
	// Settings > Apps starts the GitHub copy's uninstall with Settings' own package identity: that is no Store version.
	[InlineData("windows.immersivecontrolpanel_cw5n1h2txyewy", false, null)]
	[InlineData("windows.immersivecontrolpanel_cw5n1h2txyewy", true, null)]
	// Heiward's identity, passed on to an exe outside the package's folder (the GitHub copy's).
	[InlineData("TheNexus.Heiward_mcanr0hfqkj1g", false, null)]
	[InlineData("TheNexus.Heiward_mcanr0hfqkj1g", true, "TheNexus.Heiward_mcanr0hfqkj1g")]
	[InlineData(null, true, null)]
	public void StorePackage_IsTheStoreVersion_OnlyWithHeiwardsIdentity_InItsPackagesFolder(string? identity, bool inPackageFolder, string? expected) =>
		Assert.Equal(expected, StorePackage.Own(identity, inPackageFolder));

	[Theory]
	[InlineData("Snapdragon(R) X2 Elite Extreme - X2E94100 - Qualcomm(R) Hexagon(TM) NPU", "Snapdragon X2 Elite Extreme - X2E94100 - Qualcomm Hexagon NPU")]
	[InlineData("Intel® AI Boost", "Intel AI Boost")]
	public void StoreSetup_NamesTheNpu_WithoutTrademarkMarks(string windows, string shown) =>
		Assert.Equal(shown, StoreSetup.HardwareName(windows));
}
