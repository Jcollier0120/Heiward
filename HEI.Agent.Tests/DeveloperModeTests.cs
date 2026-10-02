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

/// <summary>Developer mode is off until it's turned on; settings from before the switch keep what they did where it mattered.</summary>
public class DeveloperModeTests {
	[Fact]
	public void IsOff_UntilTurnedOn() {
		Assert.False(new AgentConfig().DeveloperModeOn);
		Assert.False(AgentConfig.FromJson("{}").DeveloperModeOn);
		Assert.True(AgentConfig.FromJson("""{ "developerMode": "on" }""").DeveloperModeOn);
		Assert.False(AgentConfig.FromJson("""{ "developerMode": "off" }""").DeveloperModeOn);
	}

	[Fact]
	public void OldAuto_StaysOn_OnlyWhereItCleansUpByItself() {
		AgentConfig plain = AgentConfig.FromJson("""{ "developerMode": "auto" }""");
		Assert.False(plain.DeveloperModeOn);
		Assert.Equal("off", plain.DeveloperMode);
		AgentConfig cleaning = AgentConfig.FromJson("""{ "developerMode": "auto", "autoClean": { "developer": true } }""");
		Assert.True(cleaning.DeveloperModeOn);
		Assert.Equal("on", cleaning.DeveloperMode);
	}
}
