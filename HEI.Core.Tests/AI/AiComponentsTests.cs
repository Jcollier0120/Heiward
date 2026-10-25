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

using System.Net.Http;
using System.Runtime.InteropServices;
using HEI.Core.AI;

namespace HEI.Core.Tests.AI;

public class AiComponentsTests {

	[Fact]
	public void RuntimeDownloadPlan_TargetsPinnedVersionForCurrentPlatform() {
		(Uri url, string archive, string sha256) = AiComponents.GetRuntimeDownloadPlan();

		Assert.Contains(AiComponents.RuntimeVersion, archive);
		Assert.Contains("github.com/microsoft/onnxruntime/releases/download", url.AbsoluteUri);
		Assert.Contains($"v{AiComponents.RuntimeVersion}", url.AbsoluteUri);
		Assert.Matches("^[0-9a-f]{64}$", sha256);
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
			Assert.StartsWith("onnxruntime-win-", archive);
			Assert.EndsWith(".zip", archive);
		}
		else {
			Assert.EndsWith(".tgz", archive);
		}
	}

	[Theory]
	[InlineData("win", Architecture.X64, "onnxruntime-win-x64-1.27.0.zip")]
	[InlineData("win", Architecture.Arm64, "onnxruntime-win-arm64-1.27.0.zip")]
	[InlineData("linux", Architecture.X64, "onnxruntime-linux-x64-1.23.2.tgz")]
	[InlineData("linux", Architecture.Arm64, "onnxruntime-linux-aarch64-1.23.2.tgz")]
	[InlineData("osx", Architecture.X64, "onnxruntime-osx-x86_64-1.23.2.tgz")]
	[InlineData("osx", Architecture.Arm64, "onnxruntime-osx-arm64-1.23.2.tgz")]
	public void RuntimeDownloadPlan_PinsEveryArchiveByItsSha256(string os, Architecture arch, string archive) {
		// Each platform a release targets downloads one archive, and its hash is pinned: a version raised without them fails here.
		(Uri url, string file, string sha256) = AiComponents.GetRuntimeDownloadPlan(os, arch);

		Assert.Equal(archive, file);
		Assert.EndsWith("/" + archive, url.AbsoluteUri);
		Assert.Equal(AiComponents.RuntimeSha256[archive], sha256);
		Assert.Matches("^[0-9a-f]{64}$", sha256);
	}

	sealed class StubHandler(byte[] body) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
	}

	/// <summary>A zip laid out as ONNX Runtime's: onnxruntime-win-x64-1.27.0/lib/&lt;files&gt;.</summary>
	static byte[] RuntimeZip(params string[] libFiles) {
		using var bytes = new MemoryStream();
		using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true)) {
			foreach (string name in libFiles) {
				using var w = new StreamWriter(zip.CreateEntry("onnxruntime-win-x64-1.27.0/lib/" + name).Open());
				w.Write("new " + name);
			}
		}
		return bytes.ToArray();
	}

	static (Uri, string, string) Plan(byte[] archive) =>
		(new Uri("http://localhost/onnxruntime-win-x64-1.27.0.zip"), "onnxruntime-win-x64-1.27.0.zip",
			Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(archive)));

	/// <summary>A folder holding a working runtime, as an earlier download left it.</summary>
	static string InstalledRuntime() {
		string folder = Path.Combine(Path.GetTempPath(), "hei-ai-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, "onnxruntime.dll"), "old runtime");
		File.WriteAllText(Path.Combine(folder, "runtime.version"), "1.23.2");
		return folder;
	}

	static string[] DownloadFolders() => Directory.GetDirectories(Path.GetTempPath(), "HEI.AiDownload.*");

	[Fact]
	public async Task InstallRuntime_RefusesADownloadThatIsNotThePinnedOne_AndKeepsTheRuntimeHere() {
		string folder = InstalledRuntime();
		string[] before = DownloadFolders();
		try {
			byte[] pinned = RuntimeZip("onnxruntime.dll"), served = RuntimeZip("onnxruntime.dll", "evil.dll");
			using var http = new HttpClient(new StubHandler(served));

			var e = await Assert.ThrowsAsync<IOException>(() => AiComponents.InstallRuntimeAsync(http, Plan(pinned), folder, null, CancellationToken.None));

			Assert.Contains("integrity check", e.Message);
			Assert.Equal("old runtime", File.ReadAllText(Path.Combine(folder, "onnxruntime.dll")));
			Assert.Equal("1.23.2", File.ReadAllText(Path.Combine(folder, "runtime.version")));
			Assert.False(File.Exists(Path.Combine(folder, "evil.dll")));
			// Nothing half downloaded stays behind.
			Assert.Empty(DownloadFolders().Except(before));
		}
		finally {
			Directory.Delete(folder, true);
		}
	}

	[Fact]
	public async Task InstallRuntime_KeepsTheRuntimeHere_WhenThePinnedArchiveHasNoRuntime() {
		string folder = InstalledRuntime();
		try {
			byte[] archive = RuntimeZip("readme.txt");
			using var http = new HttpClient(new StubHandler(archive));

			await Assert.ThrowsAsync<IOException>(() => AiComponents.InstallRuntimeAsync(http, Plan(archive), folder, null, CancellationToken.None));

			Assert.Equal("old runtime", File.ReadAllText(Path.Combine(folder, "onnxruntime.dll")));
		}
		finally {
			Directory.Delete(folder, true);
		}
	}

	[Fact]
	public async Task InstallRuntime_ReplacesTheRuntime_WithThePinnedArchive() {
		string folder = InstalledRuntime();
		string[] before = DownloadFolders();
		try {
			byte[] archive = RuntimeZip("onnxruntime.dll", "onnxruntime_providers_shared.dll", "onnxruntime.pdb");
			using var http = new HttpClient(new StubHandler(archive));

			await AiComponents.InstallRuntimeAsync(http, Plan(archive), folder, null, CancellationToken.None);

			Assert.Equal("new onnxruntime.dll", File.ReadAllText(Path.Combine(folder, "onnxruntime.dll")));
			Assert.True(File.Exists(Path.Combine(folder, "onnxruntime_providers_shared.dll")));
			Assert.False(File.Exists(Path.Combine(folder, "onnxruntime.pdb")));
			Assert.Equal(AiComponents.RuntimeVersion, File.ReadAllText(Path.Combine(folder, "runtime.version")));
			Assert.Empty(DownloadFolders().Except(before));
		}
		finally {
			Directory.Delete(folder, true);
		}
	}

	[Fact]
	public void ModelSha256_IsPinned() {
		// The download verifies against this constant — a typo would brick the feature.
		Assert.Equal(64, AiComponents.ModelSha256.Length);
		Assert.All(AiComponents.ModelSha256, c => Assert.True(Uri.IsHexDigit(c)));
	}

	[Theory]
	[InlineData(false, false, false)]
	[InlineData(true, false, true)]
	[InlineData(false, true, true)]
	[InlineData(true, true, true)]
	public void NeedsAiComponents_TracksEveryAiFeature(bool union, bool partial, bool expected) {
		var settings = new Settings { UseAiMatching = union, EnableAiPartialDetection = partial };
		Assert.Equal(expected, settings.NeedsAiComponents);
	}

	[Fact]
	public void NeedsAiComponents_IsNotSerializedIntoSettingsJson() {
		// Computed gate only — persisting it would add a dead key that a hand-edited
		// settings file could set to a value the getter then silently contradicts.
		string json = System.Text.Json.JsonSerializer.Serialize(
			new Settings { UseAiMatching = true }, HEI.Core.Utils.CoreJsonContext.Default.Settings);
		Assert.DoesNotContain("NeedsAiComponents", json);
	}

	[Fact]
	public async Task RunDownloadsAsync_RunsDownloadsConcurrently() {
		// Each download only completes once the other has started — sequential
		// execution would deadlock here and trip the timeout.
		var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var downloads = new List<Func<CancellationToken, Task>> {
			async ct => { aStarted.SetResult(); await bStarted.Task.WaitAsync(ct); },
			async ct => { bStarted.SetResult(); await aStarted.Task.WaitAsync(ct); },
		};

		await AiComponents.RunDownloadsAsync(downloads, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
	}

	[Fact]
	public async Task RunDownloadsAsync_FailureCancelsSiblingAndSurfacesTheRealError() {
		var siblingCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var downloads = new List<Func<CancellationToken, Task>> {
			// The sibling: runs "forever" until the failure cancels it. It is FIRST in
			// the list, so a naive Task.WhenAll await would rethrow its cancellation
			// instead of the real error below.
			async ct => {
				try { await Task.Delay(Timeout.Infinite, ct); }
				finally { siblingCanceled.TrySetResult(); }
			},
			ct => Task.FromException(new IOException("model corrupt")),
		};

		var ex = await Assert.ThrowsAsync<IOException>(() =>
			AiComponents.RunDownloadsAsync(downloads, CancellationToken.None));
		Assert.Equal("model corrupt", ex.Message);
		await siblingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task RunDownloadsAsync_UserCancellationSurfacesAsCancellation() {
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
		var downloads = new List<Func<CancellationToken, Task>> {
			ct => Task.Delay(Timeout.Infinite, ct),
			ct => Task.Delay(Timeout.Infinite, ct),
		};

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			AiComponents.RunDownloadsAsync(downloads, cts.Token));
	}

	[Fact]
	public void EnsureReady_HonorsTestOverride() {
		string? prev = AiComponents.TestOverrideModelPath;
		try {
			AiComponents.TestOverrideModelPath = @"D:\some\model.onnx";
			AiComponents.EnsureReady(); // must not throw
			Assert.Equal(@"D:\some\model.onnx", AiComponents.ModelPath);
		}
		finally {
			AiComponents.TestOverrideModelPath = prev;
		}
	}
}
