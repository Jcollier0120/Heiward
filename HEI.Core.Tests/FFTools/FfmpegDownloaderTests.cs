// /*
//     Copyright (C) 2026 0x90d
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
using HEI.Core.FFTools;
using HEI.Core.Utils;

namespace HEI.Core.Tests.FFTools;

public class FfmpegDownloaderTests {

	[Theory]
	[InlineData(62, 62, 62, 8)]
	[InlineData(61, 61, 61, 7)]
	[InlineData(60, 60, 60, 6)]
	[InlineData(59, 59, 59, 5)]
	[InlineData(0, 0, 0, 0)]
	[InlineData(59, 62, 60, 8)] // mixed headers: the newest major wins
	public void MapToFfmpegMajor_MapsLibraryMajorsToFfmpegMajor(int avcodec, int avformat, int avutil, int expected) =>
		Assert.Equal(expected, FfmpegDownloader.MapToFfmpegMajor(avcodec, avformat, avutil));

	[Theory]
	[InlineData(8, "8.1")]
	[InlineData(7, "7.1")]
	[InlineData(6, "6.1")]
	[InlineData(5, "5.1")]
	[InlineData(0, "7.1")] // unknown headers fall back to a tag that exists
	public void VersionTagForMajor_MapsToPublishedBtbNTags(int major, string expected) =>
		Assert.Equal(expected, FfmpegDownloader.VersionTagForMajor(major));

	static FfmpegDownloader.DownloadOS ParseOs(string os) => Enum.Parse<FfmpegDownloader.DownloadOS>(os);

	[Theory]
	[InlineData("Windows", Architecture.X64, "win64", "BtbN", "zip")]
	[InlineData("Windows", Architecture.X86, "win32", "BtbN", "zip")]
	[InlineData("Linux", Architecture.X64, "linux64", "BtbN", "tar.xz")]
	[InlineData("Linux", Architecture.Arm, "linuxarmhf", "BtbN", "tar.xz")]
	[InlineData("OSX", Architecture.X64, "macos64", "yt-dlp", "zip")]
	[InlineData("OSX", Architecture.Arm64, "macosarm64", "yt-dlp", "zip")]
	public void GetDownloadPlans_BuildsTheExpectedReleaseUrl(
		string os, Architecture arch, string rid, string repoOwner, string ext) {
		var plans = FfmpegDownloader.GetDownloadPlans(ParseOs(os), arch, "8.1");

		var plan = Assert.Single(plans);
		Assert.Equal($"ffmpeg-n8.1-latest-{rid}-gpl-shared-8.1.{ext}", plan.ArchiveFileName);
		Assert.Equal(ext == "zip" ? ArchiveKind.Zip : ArchiveKind.TarXz, plan.ArchiveKind);
		Assert.Contains($"github.com/{repoOwner}/FFmpeg-Builds/releases/download/latest/{plan.ArchiveFileName}", plan.DownloadUrl.AbsoluteUri);
		Assert.Contains("8.1", plan.DisplayName);
		Assert.Null(plan.Sha256);
	}

	[Fact]
	public void GetDownloadPlans_WindowsArm64UsesPinnedNativeBuilds_LeanFirstThenTordona() {
		// BtbN's winarm64 builds crash on load on Snapdragon X; 8.1 takes pinned builds instead.
		var plans = FfmpegDownloader.GetDownloadPlans(FfmpegDownloader.DownloadOS.Windows, Architecture.Arm64, "8.1");

		Assert.Equal(2, plans.Count);
		Assert.Equal("ffmpeg-8.1.3-lean-lgpl-shared-win-arm64.zip", plans[0].ArchiveFileName);
		Assert.Equal(ArchiveKind.Zip, plans[0].ArchiveKind);
		Assert.Equal("https://github.com/Jcollier0120/ffmpeg-winarm64-lean/releases/download/n8.1.3-4/" + plans[0].ArchiveFileName, plans[0].DownloadUrl.AbsoluteUri);
		Assert.Equal("ffmpeg-8.1.3-essentials-shared-win-arm64.7z", plans[1].ArchiveFileName);
		Assert.Equal(ArchiveKind.SevenZip, plans[1].ArchiveKind);
		Assert.Equal("https://github.com/tordona/ffmpeg-win-arm64/releases/download/8.1.3/" + plans[1].ArchiveFileName, plans[1].DownloadUrl.AbsoluteUri);
		Assert.All(plans, plan => {
			Assert.Matches("^[0-9a-f]{64}$", plan.Sha256);
			Assert.Contains("8.1", plan.DisplayName);
		});
	}

	[Fact]
	public void GetDownloadPlans_WindowsArm64WithoutAPinnedBuildFallsBackToBtbN() {
		var plan = Assert.Single(FfmpegDownloader.GetDownloadPlans(FfmpegDownloader.DownloadOS.Windows, Architecture.Arm64, "7.1"));

		Assert.Equal("ffmpeg-n7.1-latest-winarm64-gpl-shared-7.1.zip", plan.ArchiveFileName);
		Assert.Null(plan.Sha256);
	}

	[Theory]
	[InlineData("OSX", Architecture.X86)] // no 32-bit mac builds
	[InlineData("Windows", Architecture.Arm)] // no 32-bit ARM Windows builds
	public void GetDownloadPlans_ReturnsEmptyForUnsupportedCombos(string os, Architecture arch) =>
		Assert.Empty(FfmpegDownloader.GetDownloadPlans(ParseOs(os), arch, "8.1"));

	[Fact]
	public void GetDownloadPlans_ForCurrentPlatformIsNonEmptyOnCi() {
		// Every platform CI runs on (win/linux/mac, x64/arm64) has a published build.
		Assert.NotEmpty(FfmpegDownloader.GetDownloadPlans());
	}

	[Fact]
	public void TryParseExpectedChecksum_FindsTheArchiveEntry() {
		const string listing =
			"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip\n" +
			"FEDCBA9876543210fedcba9876543210fedcba9876543210fedcba9876543210  ffmpeg-n8.1-latest-linux64-gpl-shared-8.1.tar.xz\n";

		Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
			FfmpegDownloader.TryParseExpectedChecksum(listing, "ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip"));
		// hash is lower-cased, filename match is case-insensitive
		Assert.Equal("fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
			FfmpegDownloader.TryParseExpectedChecksum(listing, "FFMPEG-N8.1-LATEST-LINUX64-GPL-SHARED-8.1.TAR.XZ"));
	}

	[Fact]
	public void TryParseExpectedChecksum_HandlesCrLfAndMissingEntries() {
		const string listing = "abc123  some-archive.zip\r\nother  another.zip\r\n";
		Assert.Equal("abc123", FfmpegDownloader.TryParseExpectedChecksum(listing, "some-archive.zip"));
		Assert.Null(FfmpegDownloader.TryParseExpectedChecksum(listing, "absent.zip"));
		Assert.Null(FfmpegDownloader.TryParseExpectedChecksum(string.Empty, "some-archive.zip"));
	}
}
