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

namespace VDF.Agent.Tests;

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
		string xml = Scheduler.ScanXml(new AgentConfig { ScanEveryMinutes = Installer.GpuCpuScanMinutes, ScanOnBattery = false }, @"C:\x\vdf-agent.exe");
		Assert.Contains("<Interval>PT360M</Interval>", xml);
		Assert.Contains("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", xml);
		Assert.Contains("scan --notify --scheduled", xml);
	}
}
