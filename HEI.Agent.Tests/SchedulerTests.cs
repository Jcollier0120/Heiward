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

namespace HEI.Agent.Tests;

public sealed class SchedulerTests {
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
	[InlineData("gpu", false, "background", "install --yes --no-browser --scan-speed background --device gpu")]
	[InlineData("cpu", true, "full", "install --yes --no-browser --scan-speed full --device cpu --on-demand")]
	[InlineData(null, false, "full", "install --yes --no-browser --scan-speed full")]
	public void StoreSetup_RunsTheInstall_WithThePagesAnswers(string? device, bool onDemand, string speed, string expected) =>
		Assert.Equal(expected, string.Join(' ', StoreSetup.Arguments(new SetupRequest(device, onDemand, speed))!));

	[Theory]
	[InlineData("npu", "full")]    // the page never offers the NPU: the installer picks it by itself
	[InlineData("gpu", "fast")]
	[InlineData("gpu", null)]
	public void StoreSetup_RefusesAnswersThePageDoesntOffer(string? device, string? speed) =>
		Assert.Null(StoreSetup.Arguments(new SetupRequest(device, false, speed)));
}
