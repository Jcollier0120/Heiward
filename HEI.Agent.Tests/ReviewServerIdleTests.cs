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

/// <summary>The review page exits once nobody uses it: asking whether it's up isn't using it.</summary>
public sealed class ReviewServerIdleTests {
	[Theory]
	[InlineData("/api/ping")]
	[InlineData("/API/Ping")] // routing ignores case, so this is the ping too
	[InlineData("/api/ping/")]
	public void APing_DoesNotKeepThePageUp(string path) => Assert.False(ReviewServer.KeepsPageUp(path));

	[Theory]
	[InlineData("/")]
	[InlineData("/api/state")] // what the open page polls
	[InlineData("/app.js")]
	[InlineData("/api/scan")]
	[InlineData("/api/pings")]
	public void UsingThePage_KeepsItUp(string path) => Assert.True(ReviewServer.KeepsPageUp(path));
}
