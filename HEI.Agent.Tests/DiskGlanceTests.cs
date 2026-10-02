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

/// <summary>The home page's "At a glance": this PC's drives added up, the used space by kind, the photos and videos by type.</summary>
public sealed class DiskGlanceTests {
	const long GB = 1L << 30;
	static readonly DupStats NoDups = new(0, 0, 0);

	static DriveCard Drive(string root, string type, long total, long free, bool onRequest = false) =>
		new(root, $"Disk ({root.TrimEnd('\\')})", type, total, free, true, null, NoDups, onRequest);

	static ScanIndex Index(params (string Path, long Size)[] files) =>
		ScanIndex.Build(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), new[] { @"C:\", @"D:\" }, files,
			new Dictionary<string, TimeSpan>(), new Dictionary<string, TimeSpan>());

	[Fact]
	public void TheIndex_SortsEachDrivesPhotosAndVideosByType() {
		var index = Index((@"C:\Photos\a.JPG", 10), (@"C:\Photos\b.jpg", 20), (@"C:\Video\c.mp4", 300), (@"D:\d.heic", 5), (@"D:\noext", 1));
		Assert.Equal(new FolderFiles(2, 30), index.Types[@"C:\"]["jpg"]);
		Assert.Equal(new FolderFiles(1, 300), index.Types[@"C:\"]["mp4"]);
		Assert.Equal(new FolderFiles(1, 5), index.Types[@"D:\"]["heic"]);
		Assert.Equal(new FolderFiles(1, 1), index.Types[@"D:\"][""]);
	}

	[Fact]
	public void ThisPcsDrivesAddUp_NetworkDrivesAndUnreadyOnesDont() {
		var drives = new[] {
			Drive(@"C:\", "fixed", 1000 * GB, 100 * GB),
			Drive(@"E:\", "removable", 500 * GB, 400 * GB),
			Drive(@"\\nas\share\", "network", 8000 * GB, 2000 * GB),
			Drive(@"F:\", "fixed", 0, 0), // not ready: no size
		};
		var g = DiskGlance.Build(drives, null, 0, 0, 0, 0);
		Assert.Equal(2, g.Drives);
		Assert.Equal(1, g.NetworkDrives);
		Assert.Equal(1500 * GB, g.TotalBytes);
		Assert.Equal(500 * GB, g.FreeBytes);
		Assert.Equal(1000 * GB, g.UsedBytes);
		Assert.False(g.Typed, "no scan yet");
		Assert.Equal(["other"], g.Kinds.Select(k => k.Key));
		Assert.Equal(1000 * GB, g.Kinds[0].Bytes);
	}

	[Fact]
	public void TheUsedSpace_ByKind_BiggestFirst_ThenEverythingElse() {
		var drives = new[] { Drive(@"C:\", "fixed", 1000 * GB, 400 * GB), Drive(@"D:\", "fixed", 1000 * GB, 1000 * GB) };
		var index = Index((@"C:\v\a.mp4", 200 * GB), (@"C:\v\b.mov", 50 * GB), (@"C:\p\c.jpg", 30 * GB), (@"D:\p\d.heic", 10 * GB));
		var g = DiskGlance.Build(drives, index, developerBytes: 80 * GB, developerTicked: 20 * GB, duplicatesBytes: 5 * GB, recycleBinBytes: 3 * GB);
		Assert.True(g.Typed);
		Assert.Equal(600 * GB, g.UsedBytes);
		Assert.Equal(["videos", "developer", "photos", "bin", "other"], g.Kinds.Select(k => k.Key));
		Assert.Equal(250 * GB, g.Kinds.Single(k => k.Key == "videos").Bytes);
		Assert.Equal(40 * GB, g.Kinds.Single(k => k.Key == "photos").Bytes);
		Assert.Equal(2, g.Kinds.Single(k => k.Key == "photos").Files);
		Assert.Equal(600 * GB - 250 * GB - 80 * GB - 40 * GB - 3 * GB, g.Kinds.Single(k => k.Key == "other").Bytes);
		Assert.Equal(g.UsedBytes, g.Kinds.Sum(k => k.Bytes));
		Assert.Equal(new GlanceReclaim(5 * GB, 20 * GB, 3 * GB), g.Reclaim);
		Assert.Equal(28 * GB, g.Reclaim.Total);
	}

	[Fact]
	public void FileTypes_TheSixBiggest_ThenTheRestTogether() {
		var drives = new[] { Drive(@"C:\", "fixed", 1000 * GB, 500 * GB) };
		var files = new[] { "mp4", "jpg", "mov", "heic", "png", "mkv", "gif", "avi" }
			.Select((t, i) => ($@"C:\m\f{i}.{t}", (long)(100 - 10 * i) * GB)).ToArray();
		var g = DiskGlance.Build(drives, Index(files), 0, 0, 0, 0);
		Assert.Equal(["mp4", "jpg", "mov", "heic", "png", "mkv"], g.Types.Select(t => t.Type));
		Assert.Equal(["video", "photo", "video", "photo", "photo", "video"], g.Types.Select(t => t.Kind));
		Assert.NotNull(g.OtherTypes);
		Assert.Equal("2 more", g.OtherTypes!.Type);
		Assert.Equal((40 + 30) * GB, g.OtherTypes.Bytes);
	}

	[Fact]
	public void FilesOnANetworkShare_AreNotCountedAgainstThisPc() {
		var drives = new[] { Drive(@"C:\", "fixed", 100 * GB, 50 * GB), Drive(@"\\nas\share\", "network", 1000 * GB, 10 * GB) };
		var index = ScanIndex.Build(DateTime.UtcNow, new[] { @"C:\", @"\\nas\share\" }, new (string, long)[] { (@"C:\a.jpg", 1 * GB), (@"\\nas\share\b.mp4", 900 * GB) },
			new Dictionary<string, TimeSpan>(), new Dictionary<string, TimeSpan>());
		var g = DiskGlance.Build(drives, index, 0, 0, 0, 0);
		Assert.Equal(["jpg"], g.Types.Select(t => t.Type));
		Assert.DoesNotContain(g.Kinds, k => k.Key == "videos");
	}

	[Fact]
	public void SortedSpaceNeverExceedsTheUsedSpace() {
		// The drive was emptied since the scan: what's sorted is scaled to fit, and nothing is left for everything else.
		var drives = new[] { Drive(@"C:\", "fixed", 100 * GB, 90 * GB) };
		var g = DiskGlance.Build(drives, Index((@"C:\a.mp4", 30 * GB)), developerBytes: 10 * GB, 0, 0, 0);
		Assert.True(g.Kinds.Sum(k => k.Bytes) <= g.UsedBytes);
		Assert.Equal(0, g.Kinds.Single(k => k.Key == "other").Bytes);
	}

	[Fact]
	public void DrivesUnderATenthFree_AreNearlyFull_FullestFirst() {
		var drives = new[] {
			Drive(@"C:\", "fixed", 1000 * GB, 50 * GB),
			Drive(@"D:\", "fixed", 1000 * GB, 99 * GB),
			Drive(@"E:\", "removable", 100 * GB, 1 * GB),
			Drive(@"F:\", "fixed", 1000 * GB, 100 * GB), // exactly a tenth: fine
		};
		var g = DiskGlance.Build(drives, null, 0, 0, 0, 0);
		Assert.Equal([@"E:\", @"C:\", @"D:\"], g.Low.Select(d => d.Root));
		Assert.Equal(0.99, g.Low[0].UsedShare);
	}

	[Theory]
	[InlineData("jpg", "photo")]
	[InlineData("HEIC", "photo")]
	[InlineData("mp4", "video")]
	[InlineData("mkv", "video")]
	[InlineData("txt", "other")]
	[InlineData("", "other")]
	public void KindOf_UsesHeiwardsOwnLists(string type, string kind) => Assert.Equal(kind, DiskGlance.KindOf(type));
}
