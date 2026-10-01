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

using System.Text.Json;
using HEI.Agent;

namespace HEI.Agent.Tests;

/// <summary>Scans make way for games and 3D programs, and use more memory unless told otherwise.</summary>
public class ThreeDWatchTests {
	const int Self = 4000, Dwm = 1200;

	static (string, double) Engine(int pid, int engine, double percent) =>
		($"pid_{pid}_luid_0x00000000_0x0000D1F1_phys_0_eng_{engine}_engtype_3D", percent);

	[Fact]
	public void AGame_KeepingA3DEngineBusy_IsFound() =>
		Assert.Equal(9876, ThreeDWatch.Busiest(new[] { Engine(9876, 0, 87.5), Engine(9876, 1, 3), Engine(5555, 0, 4) }, Self, Dwm));

	[Fact]
	public void TheBusiestProgramIsNamed() =>
		Assert.Equal(7777, ThreeDWatch.Busiest(new[] { Engine(9876, 0, 35), Engine(7777, 0, 64) }, Self, Dwm));

	[Fact]
	public void TheDesktop_Heiward_AndLightUse_DontCount() =>
		Assert.Null(ThreeDWatch.Busiest(new[] {
			Engine(Dwm, 0, 95),   // the compositor draws every window
			Engine(Self, 0, 80),  // our own GPU work (DirectML)
			Engine(5555, 0, 12),  // a browser playing a video
			("_Total", 99),       // not a process
			Engine(0, 0, 50),
		}, Self, Dwm));

	[Fact]
	public void TheWatch_RunsOnThisPc() {
		// Whatever this machine is doing, asking must not throw (no counters, no shell, a VM).
		using var watch = new ThreeDWatch();
		_ = watch.Busy();
		_ = watch.Busy();
	}

	[Fact]
	public void MoreMemory_IsOnUnlessTurnedOff() {
		Assert.True(new AgentConfig().MoreMemory);
		var off = JsonSerializer.Deserialize<AgentConfig>("""{ "moreMemory": false }""", AgentConfig.Json)!;
		Assert.False(off.MoreMemory);
		Assert.Contains("\"moreMemory\": false", JsonSerializer.Serialize(off, AgentConfig.Json));
	}
}
