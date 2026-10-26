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

using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFmpeg.AutoGen;
using HEI.Core.FFTools.FFmpegNative;
using HEI.Core.Utils;

namespace HEI.Core.FFTools {
	internal enum FfmpegDownloadPhase { Downloading, Verifying, Extracting }

	internal readonly record struct FfmpegDownloadProgress(FfmpegDownloadPhase Phase, string DisplayName, long BytesDone, long? BytesTotal);

	/// <param name="Sha256">Pinned hash of the archive: a download that doesn't have it is deleted, never extracted or run.</param>
	internal sealed record FfmpegDownloadPlan(Uri DownloadUrl, string ArchiveFileName, ArchiveKind ArchiveKind, string DisplayName, string Sha256);

	/// <summary>
	/// Downloads and installs the shared FFmpeg/FFprobe build matching the compiled-in
	/// FFmpeg.AutoGen binding version. One implementation for every frontend — the GUI
	/// and the Web UI previously each carried a full copy of this logic, and only the
	/// AI component downloader had the mid-transfer stall guard.
	/// </summary>
	internal static class FfmpegDownloader {
		internal const long MaxDownloadBytes = 500 * 1024 * 1024; // 500 MB safety cap

		internal enum DownloadOS { Windows, Linux, OSX }

		/// <summary>The manor's signed FFmpeg (the staff fetch the same), in its public releases.</summary>
		const string ManorFfmpeg = "https://github.com/Jcollier0120/Manor-releases/releases/download/ffmpeg-v8.1.3-6/";

		/// <summary>
		/// Native Windows builds, keyed by processor and version tag, tried in order. First the manor's lean LGPL build
		/// (ffmpeg-winarm64-lean: dav1d and zlib only, 11 MB; on ARM64 built -O3 tuned for Oryon: HEVC ~15% faster per
		/// core; Media Foundation's encoders, the only way to the Snapdragon's hardware video encoder). Since 1.9.69 its
		/// n8.1.3-6, whose every .exe and .dll is signed: Smart App Control blocks a program no one signed that it doesn't
		/// know, as the earlier lean builds and BtbN's (a new build each day) are. On ARM64 a fallback should that download
		/// fail: tordona's essentials build, as BtbN's winarm64 builds crash with 0xC0000005 while avcodec-62.dll loads on
		/// Snapdragon X machines (the fault is in their statically linked librsvg/cairo/DirectWrite init code), even for
		/// `ffmpeg -version`. The pinned ones run in-process and as ffmpeg.exe, and are pinned by release and SHA-256: only
		/// the tested archives install. Nothing else does: since 1.9.98 neither x64 nor a tag without an entry falls back to
		/// BtbN's "latest", which is a different, unsigned build each day, and since 1.9.100 neither do Linux and macOS, which
		/// Heiward doesn't run on (docs/PLATFORMS.md has the builds a port would pin).
		/// </summary>
		static readonly Dictionary<(Architecture Arch, string Tag), (string Url, string Sha256)[]> WinBuilds = new() {
			[(Architecture.Arm64, "8.1")] = new[] {
				(ManorFfmpeg + "ffmpeg-8.1.3-lean-lgpl-shared-win-arm64.zip",
					"28a4b3aaf6d2fa99815d079d6d1619e57e235f5b98333aae7ff2e9de9b85eebf"),
				("https://github.com/tordona/ffmpeg-win-arm64/releases/download/8.1.3/ffmpeg-8.1.3-essentials-shared-win-arm64.7z",
					"f4d26c8a8387b61c9eacb92432cf5f2cdd075464aae7523731d17d53df1344cd"),
			},
			[(Architecture.X64, "8.1")] = new[] {
				(ManorFfmpeg + "ffmpeg-8.1.3-lean-lgpl-shared-win-x64.zip",
					"e26b6ec56ee89b39acb94792916bcb8346df1094f62432b5c18f56b286b1b12b"),
			},
		};

		/// <summary>The FFmpeg major version the compiled FFmpeg.AutoGen binding expects, or 0 when unknown.</summary>
		internal static int MapToFfmpegMajor(int avcodecMajor, int avformatMajor, int avutilMajor) {
			int[] majors = { avcodecMajor, avformatMajor, avutilMajor };
			int want = 0;
			foreach (var m in majors) {
				int v = m switch {
					62 => 8,
					61 => 7,
					60 => 6,
					59 => 5,
					_ => 0
				};
				if (v > want) want = v;
			}
			return want;
		}

		internal static string VersionTagForMajor(int ffMajor) => ffMajor switch {
			// The tag WinBuilds is keyed by: one minor per major, the one pinned there.
			8 => "8.1",
			7 => "7.1",
			6 => "6.1",
			5 => "5.1",
			_ => "7.1"
		};

		internal static string GetVersionTag() => VersionTagForMajor(
			MapToFfmpegMajor(ffmpeg.LIBAVCODEC_VERSION_MAJOR, ffmpeg.LIBAVFORMAT_VERSION_MAJOR, ffmpeg.LIBAVUTIL_VERSION_MAJOR));

		internal static List<FfmpegDownloadPlan> GetDownloadPlans() {
			DownloadOS? os =
				RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? DownloadOS.Windows :
				RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? DownloadOS.Linux :
				RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? DownloadOS.OSX :
				null;
			if (os == null)
				return new List<FfmpegDownloadPlan>();
			return GetDownloadPlans(os.Value, RuntimeInformation.ProcessArchitecture, GetVersionTag());
		}

		/// <summary>
		/// The pinned builds for this OS, processor and tag, in the order to try them. Only Windows has any: none is no download,
		/// and the caller says how to install FFmpeg by hand instead.
		/// </summary>
		internal static List<FfmpegDownloadPlan> GetDownloadPlans(DownloadOS os, Architecture arch, string versionTag) {
			var plans = new List<FfmpegDownloadPlan>();
			if (os != DownloadOS.Windows || !WinBuilds.TryGetValue((arch, versionTag), out var pinnedBuilds))
				return plans;
			plans.AddRange(pinnedBuilds.Select(pinned => {
				var url = new Uri(pinned.Url);
				string archive = Path.GetFileName(url.AbsolutePath);
				ArchiveKind archiveKind = archive.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.SevenZip : ArchiveKind.Zip;
				return new FfmpegDownloadPlan(url, archive, archiveKind, $"Windows {(arch == Architecture.Arm64 ? "ARM64" : "x64")} ({versionTag})", pinned.Sha256);
			}));
			return plans;
		}

		/// <summary>
		/// Downloads, verifies, extracts and installs FFmpeg/FFprobe into the app folder.
		/// Returns the bin target folder on success. Throws PlatformNotSupportedException
		/// when no build exists for this platform/architecture, the last
		/// HttpRequestException when every plan's download failed, and lets extraction/
		/// installation errors (IOException, UnauthorizedAccessException, checksum
		/// mismatch) propagate — callers map those onto their own user messaging.
		/// </summary>
		internal static async Task<string> DownloadAndInstallAsync(IProgress<FfmpegDownloadProgress>? progress, CancellationToken token) {
			List<FfmpegDownloadPlan> plans = GetDownloadPlans();
			if (plans.Count == 0)
				throw new PlatformNotSupportedException("No FFmpeg download is available for this platform/architecture.");

			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
			return await DownloadAndInstallAsync(http, plans, InstallFromExtracted, progress, token);
		}

		/// <summary>
		/// Tries <paramref name="plans"/> in order, each into a temp folder of its own. A download whose SHA-256 isn't the
		/// plan's throws InvalidOperationException at once, and is deleted unextracted: <paramref name="install"/> never sees it,
		/// and no later plan is tried, as a changed file is no network hiccup.
		/// </summary>
		internal static async Task<string> DownloadAndInstallAsync(HttpClient http, IReadOnlyList<FfmpegDownloadPlan> plans, Func<string, string> install,
			IProgress<FfmpegDownloadProgress>? progress, CancellationToken token) {
			HttpRequestException? lastHttpError = null;
			foreach (FfmpegDownloadPlan plan in plans) {
				// Per-attempt temp dir: a fixed shared path let two VDF processes clobber
				// each other's in-progress download (same hardening as the AI downloader).
				string tempRoot = Path.Combine(Path.GetTempPath(), $"HEI.FFmpegDownload.{Guid.NewGuid():N}");
				try {
					string downloadPath = Path.Combine(tempRoot, plan.ArchiveFileName);
					string extractDir = Path.Combine(tempRoot, "extracted");
					Directory.CreateDirectory(extractDir);

					try {
						await DownloadUtils.DownloadFileAsync(http, plan.DownloadUrl, downloadPath, plan.DisplayName,
							(done, total) => progress?.Report(new FfmpegDownloadProgress(FfmpegDownloadPhase.Downloading, plan.DisplayName, done, total)),
							token, MaxDownloadBytes);
						progress?.Report(new FfmpegDownloadProgress(FfmpegDownloadPhase.Verifying, plan.DisplayName, 0, null));
						await VerifyPinnedChecksumAsync(downloadPath, plan.ArchiveFileName, plan.Sha256, token);
						progress?.Report(new FfmpegDownloadProgress(FfmpegDownloadPhase.Extracting, plan.DisplayName, 0, null));
						ArchiveUtils.Extract(downloadPath, extractDir, plan.ArchiveKind);
						return install(extractDir);
					}
					catch (HttpRequestException ex) {
						lastHttpError = ex; // try the next plan (if any)
					}
				}
				finally {
					try { Directory.Delete(tempRoot, true); } catch { }
				}
			}
			throw (Exception?)lastHttpError ?? new PlatformNotSupportedException("No FFmpeg download is available for this platform/architecture.");
		}

		static string InstallFromExtracted(string extractedFolder) {
			string targetFolder = Path.Combine(CoreUtils.CurrentFolder, "bin");
			Directory.CreateDirectory(targetFolder);
			string targetLibFolder = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
				? Path.Combine(CoreUtils.CurrentFolder, "lib")
				: targetFolder;
			Directory.CreateDirectory(targetLibFolder);
			CopyFfmpegFiles(extractedFolder, targetFolder, targetLibFolder);
			return targetFolder;
		}

		static void CopyFfmpegFiles(string sourceRoot, string targetFolder, string targetLibFolder) {
			string ffmpegName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
			string ffprobeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";

			string? ffmpegPath = Directory.EnumerateFiles(sourceRoot, ffmpegName, SearchOption.AllDirectories).FirstOrDefault();
			string? ffprobePath = Directory.EnumerateFiles(sourceRoot, ffprobeName, SearchOption.AllDirectories).FirstOrDefault();

			if (ffmpegPath == null || ffprobePath == null)
				throw new FileNotFoundException("ffmpeg/ffprobe not found in the extracted archive.");

			string? binFolder = Path.GetDirectoryName(ffmpegPath);
			if (string.IsNullOrEmpty(binFolder))
				throw new DirectoryNotFoundException("Failed to locate ffmpeg folder in the archive.");

			foreach (var file in Directory.EnumerateFiles(binFolder)) {
				string fileName = Path.GetFileName(file);
				CopyFile(file, Path.Combine(targetFolder, fileName));
			}

			var libraryFiles = FFmpegHelper.GenerateLibraryFileNames();
			foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)) {
				var fileName = Path.GetFileName(file);
				if (libraryFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)) {
					CopyFile(file, Path.Combine(targetLibFolder, fileName));
				}
			}
		}

		static void CopyFile(string sourcePath, string destinationPath) {
			string targetPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
				? FFToolsUtils.LongPathFix(destinationPath)
				: destinationPath;
			File.Copy(sourcePath, targetPath, true);
		}

		/// <summary>Throws InvalidOperationException unless the file's SHA-256 is <paramref name="expectedHash"/>.</summary>
		static async Task VerifyPinnedChecksumAsync(string filePath, string archiveFileName, string expectedHash, CancellationToken token) {
			await using var fs = File.OpenRead(filePath);
			var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, token));

			if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException(
					$"Checksum mismatch for '{archiveFileName}': expected {expectedHash}, got {actualHash}. The download may be corrupted or tampered with.");
		}
	}
}
