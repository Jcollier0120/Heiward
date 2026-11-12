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
/// The review page asks for the open folders' listings together, and each lists a folder on disk: on a disk that seeks
/// (a spinning disk, a network share, or one Windows can't tell about) they take turns, partitions of one disk together;
/// on an SSD, and across separate disks, they run at once. Windows' answers (the seek penalty, the physical disk) are
/// given by each test, its disks named afresh so no other test's listings share their turns.
/// </summary>
public sealed class DiskListingGateTests {
	readonly string run = Guid.NewGuid().ToString("N");
	readonly List<string> askedAbout = [];

	/// <summary>Windows' answers: <paramref name="seeks"/> drive roots seek, the rest are SSDs; <paramref name="disks"/> drive roots by disk.</summary>
	(Func<string, string?> DiskOf, Func<string, int> Slowness) Windows(string[] seeks, Dictionary<string, string>? disks = null) => (
		root => disks != null && disks.TryGetValue(root, out string? d) ? run + d : null,
		root => {
			lock (askedAbout) askedAbout.Add(root);
			return seeks.Contains(root, StringComparer.OrdinalIgnoreCase) ? 1 : 0;
		});

	/// <summary>
	/// Lists each path at once (each on a thread of its own, as the server's requests come in), and says how many of the
	/// listings were under way at the same time at most. Each listing takes a moment, or waits at <paramref name="meet"/>.
	/// </summary>
	async Task<int> MostAtOnce(string[] paths, (Func<string, string?> DiskOf, Func<string, int> Slowness) windows, Barrier? meet = null) {
		int inside = 0, most = 0;
		await Task.WhenAll(paths.Select(p => Task.Run(() => DiskListingGate.RunAsync(p, () => {
			int now = Interlocked.Increment(ref inside);
			lock (paths) most = Math.Max(most, now);
			if (meet != null) Assert.True(meet.SignalAndWait(TimeSpan.FromSeconds(20)), "the listings met");
			else Thread.Sleep(60);
			Interlocked.Decrement(ref inside);
			return p;
		}, default, windows.DiskOf, windows.Slowness))));
		return most;
	}

	[Fact]
	public async Task OnADiskThatSeeks_TheListingsTakeTurns() {
		var windows = Windows([@"E:\"], new() { [@"E:\"] = "disk0" });
		Assert.Equal(1, await MostAtOnce([@"E:\Photos", @"E:\Photos\2019", @"E:\Videos", @"E:\Old"], windows));
		Assert.All(askedAbout, root => Assert.Equal(@"E:\", root));
	}

	[Fact]
	public async Task OnAnSsd_TheyRunAtOnce() {
		using var meet = new Barrier(3);
		Assert.Equal(3, await MostAtOnce([@"D:\Photos", @"D:\Photos\2019", @"D:\Videos"], Windows([], new() { [@"D:\"] = "disk1" }), meet));
	}

	[Fact]
	public async Task PartitionsOfOneSpinningDisk_TakeTurnsTogether() {
		var windows = Windows([@"E:\", @"H:\"], new() { [@"E:\"] = "disk0", [@"H:\"] = "disk0" });
		Assert.Equal(1, await MostAtOnce([@"E:\Photos", @"H:\Backup", @"E:\Videos", @"H:\Old"], windows));
	}

	[Fact]
	public async Task SeparateSpinningDisks_DontWaitForEachOther() {
		using var meet = new Barrier(2);
		var windows = Windows([@"E:\", @"H:\"], new() { [@"E:\"] = "disk0", [@"H:\"] = "disk3" });
		Assert.Equal(2, await MostAtOnce([@"E:\Photos", @"H:\Backup"], windows, meet));
	}

	[Fact]
	public async Task ADriveWindowsCantPlaceOnADisk_TakesTurnsByItsDriveRoot() {
		// A network share (it seeks, and has no disk Windows names), and a drive whose disk Windows doesn't say.
		var windows = Windows([@"\\nas\media", @"E:\"]);
		Assert.Equal(1, await MostAtOnce([@"\\nas\media\Photos", @"\\nas\media\Videos", @"\\nas\media\Old"], windows));
		Assert.All(askedAbout, root => Assert.Equal(@"\\nas\media", root));
		askedAbout.Clear();
		Assert.Equal(1, await MostAtOnce([@"E:\Photos", @"e:\Videos"], windows));
	}

	[Fact]
	public async Task AListingThatFails_LetsTheNextOneOnItsDiskGo() {
		var windows = Windows([@"E:\"], new() { [@"E:\"] = "disk0" });
		await Assert.ThrowsAsync<IOException>(() => DiskListingGate.RunAsync<string>(@"E:\Gone", () => throw new IOException("gone"),
			default, windows.DiskOf, windows.Slowness));
		Task<string> next = DiskListingGate.RunAsync(@"E:\Photos", () => "listed", default, windows.DiskOf, windows.Slowness);
		Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(20))));
		Assert.Equal("listed", await next);
	}
}
