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

using System.Runtime.InteropServices;

namespace HEI.Agent.Tests;

public sealed class GitExeTests {
	const string Launcher = @"C:\Program Files\Git\cmd\git.exe";
	static readonly string[] Builds = [@"C:\Program Files\Git\clangarm64\bin\git.exe", @"C:\Program Files\Git\mingw64\bin\git.exe"];

	[Theory]
	[InlineData(Architecture.Arm64, @"C:\Program Files\Git\clangarm64\bin\git.exe")]
	[InlineData(Architecture.X64, @"C:\Program Files\Git\mingw64\bin\git.exe")]
	public void Git_StartsTheRealGit_BehindGitForWindowsLauncher(Architecture arch, string real) {
		// The launcher only starts the real one: every git was two processes, three with a console host of its own.
		Assert.Equal(real, Git.RealBehind(Launcher, arch, p => Builds.Contains(p, StringComparer.OrdinalIgnoreCase)));
		Assert.Equal(real, Git.RealBehind(@"C:\Program Files\Git\CMD\git.exe", arch, p => Builds.Contains(p, StringComparer.OrdinalIgnoreCase)));
	}

	[Fact]
	public void Git_IsLeftAsItIs_WhenItIsNotTheLauncher_OrNothingIsBehindIt() {
		Assert.Equal(@"D:\tools\git.exe", Git.RealBehind(@"D:\tools\git.exe", Architecture.X64, _ => true));
		Assert.Equal(Launcher, Git.RealBehind(Launcher, Architecture.Arm64, _ => false));
		Assert.Equal(@"C:\cmd\git.exe", Git.RealBehind(@"C:\cmd\git.exe", Architecture.X64, _ => false));
	}

	[Fact]
	public void Git_TheOneFound_Runs() {
		if (Git.Exe == null) return; // no git on this PC
		var (code, output) = Git.Run(AppContext.BaseDirectory, "--version");
		Assert.Equal(0, code);
		Assert.StartsWith("git version", output);
	}
}
