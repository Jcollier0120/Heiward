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

using HEI.Core.AI;

namespace HEI.Agent.Tests;

/// <summary>
/// The copy from GitHub updating itself (SelfUpdate): who updates it, which release is newer, which file is this PC's and
/// its SHA-256 from SHA256SUMS.txt, and what an update keeps from the settings (Installer.Kept). Nothing here downloads.
/// </summary>
public sealed class SelfUpdateTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-update-" + Guid.NewGuid().ToString("N"));

	public SelfUpdateTests() => Directory.CreateDirectory(dir);

	public void Dispose() {
		try { Directory.Delete(dir, true); } catch { }
	}

	[Fact]
	public void OnlyTheInstalledGitHubCopyWithTheSwitchOnUpdatesItself() {
		Assert.Null(SelfUpdate.WhyNot(autoUpdate: true, packaged: false, devBuild: false, runningInstalled: true, manorEmploys: false));
		Assert.Equal("The Microsoft Store keeps it up to date.", SelfUpdate.WhyNot(true, packaged: true, false, true, false));
		Assert.Equal("Manor keeps it up to date.", SelfUpdate.WhyNot(true, false, false, true, manorEmploys: true));
		Assert.Equal("A development build doesn't update itself.", SelfUpdate.WhyNot(true, false, devBuild: true, true, false));
		Assert.Equal("Only the installed copy updates itself.", SelfUpdate.WhyNot(true, false, false, runningInstalled: false, false));
		Assert.Equal("Updating by itself is off in Settings.", SelfUpdate.WhyNot(autoUpdate: false, false, false, true, false));
		// The Store and Manor update it whatever the switch says.
		Assert.Equal("Manor keeps it up to date.", SelfUpdate.WhyNot(autoUpdate: false, false, false, true, manorEmploys: true));
	}

	[Theory]
	[InlineData("1.9.10", "1.9.9", true)]
	[InlineData("v1.10.0", "1.9.9", true)]
	[InlineData("2.0.0", "1.99.99", true)]
	[InlineData("1.9.9", "1.9.9", false)]
	[InlineData("1.9.8", "1.9.9", false)]
	[InlineData("1.9.10+abc1234", "1.9.9", true)]
	[InlineData("not a version", "1.9.9", false)]
	public void NewerComparesVersionsByNumber(string candidate, string current, bool newer) =>
		Assert.Equal(newer, SelfUpdate.Newer(candidate, current));

	[Fact]
	public void TheExeIsThisProcessorsBuild() {
		Assert.Equal("Heiward-1.9.10-arm64.exe", SelfUpdate.AssetName("1.9.10", arm64: true));
		Assert.Equal("Heiward-1.9.10-x64.exe", SelfUpdate.AssetName("1.9.10", arm64: false));
	}

	[Fact]
	public void SumsAreReadByFileName() {
		string a = new('a', 64), b = new('B', 64);
		var sums = SelfUpdate.ParseSums($"{a}  Heiward-1.9.10-arm64.exe\r\n{b} *Heiward-1.9.10-x64.exe\n\nnot a line\n{new string('c', 63)}  short.exe\n");
		Assert.Equal(2, sums.Count);
		Assert.Equal(a, sums["Heiward-1.9.10-arm64.exe"]);
		Assert.Equal(new string('b', 64), sums["heiward-1.9.10-x64.exe"]);
	}

	string Manor(string? agents, bool installed = true) {
		string folder = Path.Combine(dir, "manor-" + Guid.NewGuid().ToString("N")[..6]);
		Directory.CreateDirectory(folder);
		if (installed) {
			File.WriteAllText(Path.Combine(folder, "settings.json"), "{}");
			Directory.CreateDirectory(Path.Combine(folder, "app"));
		}
		if (agents != null) File.WriteAllText(Path.Combine(folder, "agents.json"), agents);
		return folder;
	}

	[Fact]
	public void ManorUpdatesHeiwardOnlyWhenItEmploysIt() {
		Assert.True(SelfUpdate.ManorEmploys(Manor("""{"agents":[{"id":"reeve"},{"id":"heiward","name":"Heiward"}]}""")));
		Assert.False(SelfUpdate.ManorEmploys(Manor("""{"agents":[{"id":"reeve"}]}""")));
		Assert.False(SelfUpdate.ManorEmploys(Manor(null)));
		Assert.False(SelfUpdate.ManorEmploys(Manor("not json")));
		Assert.False(SelfUpdate.ManorEmploys(Manor("""{"agents":{"id":"heiward"}}""")));
		// A folder left by an uninstalled Manor: no app\, so no Manor.
		Assert.False(SelfUpdate.ManorEmploys(Manor("""{"agents":[{"id":"heiward"}]}""", installed: false)));
		Assert.False(SelfUpdate.ManorEmploys(null));
	}

	[Fact]
	public void AnUpdateKeepsWhereTheAiRunsTheScheduleAndTheCard() {
		var cpu = Installer.Kept(new AgentConfig { AiDevice = "cpu", ScanEveryMinutes = 120, Gpu = "" });
		Assert.Equal((AiDevice?)AiDevice.Cpu, cpu.Device);
		Assert.False(cpu.OnDemand);
		Assert.Equal("default", cpu.Gpu);

		var gpu = Installer.Kept(new AgentConfig { AiDevice = "GPU", ScanEveryMinutes = 0, Gpu = "NVIDIA GeForce RTX 4070" });
		Assert.Equal((AiDevice?)AiDevice.Gpu, gpu.Device);
		Assert.True(gpu.OnDemand);
		Assert.Equal("NVIDIA GeForce RTX 4070", gpu.Gpu);

		// Auto: the NPU where there is one, as a first install picks it.
		Assert.Null(Installer.Kept(new AgentConfig { AiDevice = "auto" }).Device);
	}
}
