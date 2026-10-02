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

/// <summary>
/// A scan the page starts counts as running from the click, not from when its process takes scan.lock,
/// so the page shows it at once instead of on its next slow poll.
/// </summary>
public sealed class LaunchTests {
	static readonly DateTime T0 = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

	[Fact]
	public void NothingStarted_IsNotStarting() => Assert.False(new Launch().Starting(false, T0));

	[Fact]
	public void StartingFromTheClick_UntilTheProcessTakesItsLock() {
		var launch = new Launch();
		launch.Started(() => true, T0);
		Assert.True(launch.Starting(false, T0), "before the process has started up");
		Assert.True(launch.Starting(false, T0.AddSeconds(3)), "a cold start takes a few seconds");
		Assert.False(launch.Starting(true, T0.AddSeconds(4)), "it holds scan.lock: running, no longer starting");
		Assert.False(launch.Starting(false, T0.AddSeconds(5)), "forgotten once seen with the lock, so the end of the scan isn't 'starting'");
	}

	[Fact]
	public void AProcessThatEndsWithoutTheLock_IsNotStarting() {
		// e.g. another scan already held the lock, or the folders are gone: `hei scan` says so and exits.
		bool alive = true;
		var launch = new Launch();
		launch.Started(() => alive, T0);
		Assert.True(launch.Starting(false, T0.AddSeconds(1)));
		alive = false;
		Assert.False(launch.Starting(false, T0.AddSeconds(2)));
		alive = true;
		Assert.False(launch.Starting(false, T0.AddSeconds(3)), "forgotten, not revived");
	}

	[Fact]
	public void AProcessStuckBeforeItsLock_StopsCountingAfterTheLimit() {
		var launch = new Launch();
		launch.Started(() => true, T0);
		Assert.True(launch.Starting(false, T0 + Launch.Limit));
		Assert.False(launch.Starting(false, T0 + Launch.Limit + TimeSpan.FromSeconds(1)));
	}

	[Fact]
	public void ADriveScan_SaysWhichDriveItWillRead() {
		var launch = new Launch();
		launch.Started(() => true, T0, ["E:\\"]);
		Assert.True(launch.Starting(false, T0));
		Assert.Equal(["E:\\"], launch.Roots);
		Assert.False(launch.Starting(true, T0.AddSeconds(2)));
		Assert.Null(launch.Roots);
	}

	[Fact]
	public void ANewLaunch_ReplacesTheOldOne() {
		var launch = new Launch();
		launch.Started(() => false, T0, ["E:\\"]);
		launch.Started(() => true, T0.AddSeconds(30));
		Assert.True(launch.Starting(false, T0.AddSeconds(31)));
		Assert.Null(launch.Roots);
	}

	[Fact]
	public void NoProcess_NothingStarting() {
		var launch = new Launch();
		launch.Started((System.Diagnostics.Process?)null, T0);
		Assert.False(launch.Starting(false, T0));
	}
}
