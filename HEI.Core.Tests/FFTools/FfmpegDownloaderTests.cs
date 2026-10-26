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

using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
	[InlineData(0, "7.1")] // unknown headers fall back to a known tag
	public void VersionTagForMajor_MapsToOneTagPerMajor(int major, string expected) =>
		Assert.Equal(expected, FfmpegDownloader.VersionTagForMajor(major));

	static FfmpegDownloader.DownloadOS ParseOs(string os) => Enum.Parse<FfmpegDownloader.DownloadOS>(os);

	/// <summary>Every plan for every OS, processor and tag there is.</summary>
	static IEnumerable<FfmpegDownloadPlan> EveryPlan() =>
		from os in Enum.GetValues<FfmpegDownloader.DownloadOS>()
		from arch in Enum.GetValues<Architecture>()
		from tag in new[] { "8.1", "7.1", "6.1", "5.1" }
		from plan in FfmpegDownloader.GetDownloadPlans(os, arch, tag)
		select plan;

	[Fact]
	public void EveryPlan_IsPinnedByHash_AtATaggedRelease_NeverLatest() {
		Assert.NotEmpty(EveryPlan());
		Assert.All(EveryPlan(), plan => {
			Assert.Matches("^[0-9a-f]{64}$", plan.Sha256);
			Assert.Equal(Uri.UriSchemeHttps, plan.DownloadUrl.Scheme);
			// A GitHub release's download address names its tag: never the "latest" that floats to a new build each day.
			Assert.Matches("^/[^/]+/[^/]+/releases/download/[^/]+/[^/]+$", plan.DownloadUrl.AbsolutePath);
			Assert.DoesNotContain("latest", plan.DownloadUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(Path.GetFileName(plan.DownloadUrl.AbsolutePath), plan.ArchiveFileName);
		});
	}

	[Fact]
	public void GetDownloadPlans_WindowsArm64UsesPinnedNativeBuilds_SignedLeanFirstThenTordona() {
		// BtbN's winarm64 builds crash on load on Snapdragon X; 8.1 takes pinned builds instead, the manor's signed one first.
		var plans = FfmpegDownloader.GetDownloadPlans(FfmpegDownloader.DownloadOS.Windows, Architecture.Arm64, "8.1");

		Assert.Equal(2, plans.Count);
		Assert.Equal("ffmpeg-8.1.3-lean-lgpl-shared-win-arm64.zip", plans[0].ArchiveFileName);
		Assert.Equal(ArchiveKind.Zip, plans[0].ArchiveKind);
		Assert.Equal("https://github.com/Jcollier0120/Manor-releases/releases/download/ffmpeg-v8.1.3-6/" + plans[0].ArchiveFileName, plans[0].DownloadUrl.AbsoluteUri);
		Assert.Equal("28a4b3aaf6d2fa99815d079d6d1619e57e235f5b98333aae7ff2e9de9b85eebf", plans[0].Sha256);
		Assert.Equal("ffmpeg-8.1.3-essentials-shared-win-arm64.7z", plans[1].ArchiveFileName);
		Assert.Equal(ArchiveKind.SevenZip, plans[1].ArchiveKind);
		Assert.Equal("https://github.com/tordona/ffmpeg-win-arm64/releases/download/8.1.3/" + plans[1].ArchiveFileName, plans[1].DownloadUrl.AbsoluteUri);
		Assert.All(plans, plan => Assert.Contains("8.1", plan.DisplayName));
	}

	[Fact]
	public void GetDownloadPlans_WindowsX64UsesOnlyTheSignedLeanBuild() {
		// Smart App Control blocks BtbN's unsigned builds (a new one each day): x64 takes the manor's signed one, and nothing else.
		var plan = Assert.Single(FfmpegDownloader.GetDownloadPlans(FfmpegDownloader.DownloadOS.Windows, Architecture.X64, "8.1"));

		Assert.Equal("https://github.com/Jcollier0120/Manor-releases/releases/download/ffmpeg-v8.1.3-6/ffmpeg-8.1.3-lean-lgpl-shared-win-x64.zip", plan.DownloadUrl.AbsoluteUri);
		Assert.Equal("e26b6ec56ee89b39acb94792916bcb8346df1094f62432b5c18f56b286b1b12b", plan.Sha256);
		Assert.Equal(ArchiveKind.Zip, plan.ArchiveKind);
		Assert.Equal("Windows x64 (8.1)", plan.DisplayName);
	}

	[Theory]
	[InlineData(Architecture.X64)]
	[InlineData(Architecture.Arm64)]
	public void GetDownloadPlans_WindowsWithoutAPinnedBuildTakesNothing(Architecture arch) =>
		// A binding bumped without a pin downloads nothing, rather than BtbN's "latest".
		Assert.Empty(FfmpegDownloader.GetDownloadPlans(FfmpegDownloader.DownloadOS.Windows, arch, "7.1"));

	[Theory]
	[InlineData("Linux", Architecture.X64)] // Heiward is Windows-only: no download, rather than BtbN's or yt-dlp's "latest"
	[InlineData("Linux", Architecture.Arm64)]
	[InlineData("Linux", Architecture.Arm)]
	[InlineData("OSX", Architecture.X64)]
	[InlineData("OSX", Architecture.Arm64)]
	[InlineData("Windows", Architecture.Arm)] // no 32-bit ARM Windows builds
	[InlineData("Windows", Architecture.X86)] // no pinned 32-bit Windows build
	public void GetDownloadPlans_ReturnsEmptyForUnsupportedCombos(string os, Architecture arch) =>
		Assert.Empty(FfmpegDownloader.GetDownloadPlans(ParseOs(os), arch, "8.1"));

	[Fact]
	public void GetDownloadPlans_ForCurrentPlatformIsNonEmptyOnCi() {
		// CI and every PC Heiward runs on are Windows, x64 or Arm64: each has a pinned build.
		Assert.NotEmpty(FfmpegDownloader.GetDownloadPlans());
	}

	/// <summary>Answers every request with <paramref name="body"/> (or 404 for an address in <paramref name="missing"/>), counting them.</summary>
	sealed class StubHandler(byte[] body, params string[] missing) : HttpMessageHandler {
		public int Requests;
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			Interlocked.Increment(ref Requests);
			return Task.FromResult(missing.Contains(request.RequestUri!.AbsoluteUri)
				? new HttpResponseMessage(HttpStatusCode.NotFound)
				: new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
		}
	}

	/// <summary>A zip laid out as an FFmpeg build is: one folder, its bin\ with ffmpeg.exe and ffprobe.exe.</summary>
	static byte[] FfmpegLikeZip() {
		using var stream = new MemoryStream();
		using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
			foreach (string name in new[] { "ffmpeg.exe", "ffprobe.exe" })
				using (var writer = new StreamWriter(zip.CreateEntry($"ffmpeg-test/bin/{name}").Open()))
					writer.Write("not a program");
		return stream.ToArray();
	}

	static FfmpegDownloadPlan Plan(string url, string sha256) =>
		new(new Uri(url), Path.GetFileName(new Uri(url).AbsolutePath), ArchiveKind.Zip, "Test", sha256);

	[Fact]
	public async Task ADownloadWithoutItsPinnedHash_IsRefused_NeverExtractedOrInstalled_AndNoOtherBuildTried() {
		using var handler = new StubHandler(FfmpegLikeZip());
		using var http = new HttpClient(handler);
		bool installed = false;
		var plans = new[] {
			Plan("https://example.test/releases/download/v1/a.zip", new string('0', 64)),
			Plan("https://example.test/releases/download/v1/b.zip", Convert.ToHexStringLower(SHA256.HashData(FfmpegLikeZip()))),
		};

		var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			FfmpegDownloader.DownloadAndInstallAsync(http, plans, _ => { installed = true; return "bin"; }, null, CancellationToken.None));

		Assert.Contains("Checksum mismatch for 'a.zip'", e.Message);
		Assert.False(installed);
		Assert.Equal(1, handler.Requests);
	}

	[Fact]
	public async Task ADownloadWithItsPinnedHash_IsExtractedAndInstalled() {
		byte[] zip = FfmpegLikeZip();
		using var http = new HttpClient(new StubHandler(zip));
		string? sawFfmpeg = null;
		var plans = new[] { Plan("https://example.test/releases/download/v1/a.zip", Convert.ToHexStringLower(SHA256.HashData(zip))) };

		string folder = await FfmpegDownloader.DownloadAndInstallAsync(http, plans, extracted => {
			sawFfmpeg = Directory.EnumerateFiles(extracted, "ffmpeg.exe", SearchOption.AllDirectories).SingleOrDefault();
			return "bin";
		}, null, CancellationToken.None);

		Assert.Equal("bin", folder);
		Assert.NotNull(sawFfmpeg);
	}

	[Fact]
	public async Task ABuildThatFailsToDownload_GivesWayToTheNext() {
		byte[] zip = FfmpegLikeZip();
		string sha = Convert.ToHexStringLower(SHA256.HashData(zip));
		const string gone = "https://example.test/releases/download/v1/a.zip";
		using var handler = new StubHandler(zip, gone);
		using var http = new HttpClient(handler);

		string folder = await FfmpegDownloader.DownloadAndInstallAsync(http, new[] { Plan(gone, sha), Plan("https://example.test/releases/download/v1/b.zip", sha) },
			_ => "bin", null, CancellationToken.None);

		Assert.Equal("bin", folder);
		Assert.Equal(2, handler.Requests);
	}
}
