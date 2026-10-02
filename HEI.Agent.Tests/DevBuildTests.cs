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
/// A development build keeps apart from the installed Heiward: its own data folder and port, no scheduled
/// tasks. Installing from one installs the installed copy, in the installed copy's places.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME, and which build this is, are process-wide
public sealed class DevBuildTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-dev-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

	public DevBuildTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", null);
	}

	public void Dispose() {
		DevBuild.Override = null;
		AgentPaths.ActingAsInstalled = false;
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>An exe's folder <paramref name="depth"/> folders below the test folder, which holds .git when <paramref name="git"/> says so.</summary>
	string ExeFolder(int depth, string? git) {
		string exe = Path.Combine(new[] { dir }.Concat(Enumerable.Range(1, depth).Select(i => "d" + i)).ToArray());
		Directory.CreateDirectory(exe);
		if (git == "folder") Directory.CreateDirectory(Path.Combine(dir, ".git"));
		if (git == "file") File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: C:/somewhere/.git/worktrees/x");
		return exe;
	}

	[Theory]
	[InlineData(0, "folder", true)]
	[InlineData(5, "folder", true)]  // bin\Release\net10.0-windows\win-arm64\publish
	[InlineData(5, "file", true)]    // in a git worktree, .git is a file
	[InlineData(8, "folder", true)]
	[InlineData(9, "folder", false)] // too far above to be this exe's checkout
	[InlineData(9, null, false)]     // nine deep: the walk never leaves the test folder
	public void ACheckout_IsAFolderAboveTheExe_HoldingGit(int depth, string? git, bool expected) =>
		Assert.Equal(expected, DevBuild.IsCheckout(ExeFolder(depth, git)));

	[Fact]
	public void ADevelopmentBuild_HasItsOwnDataFolder_AndPort() {
		DevBuild.Override = true;
		Assert.True(AgentPaths.Separate);
		Assert.Equal(Path.Combine(Local, "Heiward-dev"), AgentPaths.Home);
		Assert.Equal(Path.Combine(Local, "Heiward-dev", "settings.json"), AgentPaths.Config);
		Assert.Equal(28484, new AgentConfig().Port);
		Assert.Equal("http://heiward.localhost:28484/", ReviewServer.PageUrl(new AgentConfig().Port));
	}

	[Fact]
	public void TheInstalledCopy_KeepsItsDataFolder_AndPort() {
		DevBuild.Override = false;
		Assert.False(AgentPaths.Separate);
		Assert.Equal(Path.Combine(Local, "Heiward"), AgentPaths.Home);
		Assert.Equal(18484, new AgentConfig().Port);
		Assert.Equal(18484, AgentConfig.FromJson("{}").Port);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void HeiwardHome_OverridesTheDataFolder_AsBefore(bool devBuild) {
		DevBuild.Override = devBuild;
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
		Assert.False(AgentPaths.Separate);
		Assert.Equal(dir, AgentPaths.Home);
		Assert.Equal(dir, AgentPaths.InstalledHome);
		Assert.Equal(18484, new AgentConfig().Port);
	}

	[Fact]
	public void ADevelopmentBuild_KeepsAPortSettingsSets_ButNeverTheInstalledCopys() {
		DevBuild.Override = true;
		Assert.Equal(28484, AgentConfig.FromJson("{}").Port);
		Assert.Equal(30000, AgentConfig.FromJson("""{ "port": 30000 }""").Port);
		// A settings.json copied over from the installed copy.
		Assert.Equal(28484, AgentConfig.FromJson("""{ "port": 18484 }""").Port);

		DevBuild.Override = false;
		Assert.Equal(30000, AgentConfig.FromJson("""{ "port": 30000 }""").Port);
	}

	[Fact]
	public void InstallingFromADevelopmentBuild_TargetsTheInstalledCopy() {
		DevBuild.Override = true;
		AgentPaths.ActAsInstalled();
		Assert.False(AgentPaths.Separate);
		Assert.Equal(Path.Combine(Local, "Heiward"), AgentPaths.Home);
		Assert.Equal(Path.Combine(Local, "Heiward", "settings.json"), AgentPaths.Config);
		Assert.Equal(18484, new AgentConfig().Port);
		Assert.Equal(Path.Combine(Local, "Programs", "Heiward"), Installer.InstallDir);
		Assert.Equal(Path.Combine(Local, "Programs", "Heiward", "hei.exe"), Installer.InstalledExe);
	}

	[Fact]
	public void ADevelopmentBuild_HasNoScheduledTasks_AndRegistersNone() {
		DevBuild.Override = true;
		Assert.Null(Scheduler.NextRun());
		Assert.NotNull(Installer.RegisterTasks(new AgentConfig()));
	}

	[Fact]
	public async Task TheInstallsPlan_FromADevelopmentBuild_IsTheInstalledCopys() {
		DevBuild.Override = true;
		var output = new StringWriter();
		TextWriter before = Console.Out;
		Console.SetOut(TextWriter.Synchronized(output));
		int code;
		try {
			code = await Installer.InstallAsync(dryRun: true, assumeYes: true, AiDevice.Cpu, CancellationToken.None, onDemand: false,
				scanSpeed: "background", openPage: false);
		}
		finally {
			Console.SetOut(before);
		}
		string plan = output.ToString();
		Assert.Equal(0, code);
		Assert.Contains("-> " + Installer.InstalledExe, plan);
		Assert.Contains("Settings: " + Path.Combine(Local, "Heiward", "settings.json"), plan);
		Assert.Contains(Installer.InstalledExe + "&quot; scan --notify --scheduled", plan); // the scan task's XML
		Assert.Contains(Installer.InstalledExe + "&quot; open --if-pending", plan);       // the sign-in task's
		Assert.DoesNotContain("Heiward-dev", plan);
		// And so does the rest of the process: the install's log, should it write one.
		Assert.Equal(Path.Combine(Local, "Heiward"), AgentPaths.Home);
	}
}
