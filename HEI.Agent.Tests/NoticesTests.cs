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

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HEI.Core.AI;
using HEI.Core.FFTools;

namespace HEI.Agent.Tests;

/// <summary>
/// The texts every copy carries (LicenseTexts): the GNU AGPL's, and third-party notices that name everything hei.exe
/// carries or downloads. A package added or updated, or a download's pin raised, fails here until tools\notices.ps1
/// writes the notices again.
/// </summary>
public sealed class NoticesTests {
	static string Repo() {
		for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
			if (File.Exists(Path.Combine(dir, "Heiward.sln"))) return dir;
		throw new InvalidOperationException("No Heiward.sln above " + AppContext.BaseDirectory);
	}

	static string Notices() => Encoding.UTF8.GetString(LicenseTexts.Read(LicenseTexts.Notices)).ReplaceLineEndings("\n");

	const string Fix = " is missing from THIRD-PARTY-NOTICES.txt: run tools\\notices.ps1 (and keep its $downloads in step with the pins).";

	static void Names(string notices, string what) => Assert.True(notices.Contains(what, StringComparison.Ordinal), what + Fix);

	[Fact]
	public void HeiExe_CarriesTheAgplsFullText_AsTheRepositoryHasIt() {
		string carried = Encoding.UTF8.GetString(LicenseTexts.Read(LicenseTexts.License)).ReplaceLineEndings("\n");
		Assert.Contains("GNU AFFERO GENERAL PUBLIC LICENSE", carried);
		Assert.Contains("Version 3, 19 November 2007", carried);
		Assert.Equal(File.ReadAllText(Path.Combine(Repo(), "LICENSE")).ReplaceLineEndings("\n"), carried);
	}

	[Fact]
	public void HeiExe_CarriesTheNotices_AsTheRepositoryHasThem() =>
		Assert.Equal(File.ReadAllText(Path.Combine(Repo(), LicenseTexts.Notices)).ReplaceLineEndings("\n"), Notices());

	[Fact]
	public void TheBuildOutput_HasBothBesideHeiExe() {
		// HEI.Agent.csproj copies them to its output (and a publish's), which this project's output takes from it.
		foreach (string name in LicenseTexts.All)
			Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, name)), name);
	}

	[Fact]
	public void TheNotices_NameEveryPackageHeiExeCarries() {
		// HEI.Agent's restore: the packages with files that ship (runtime or native), as hei.exe is published with them.
		using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "HEI.Agent", "obj", "project.assets.json")));
		JsonElement target = assets.RootElement.GetProperty("targets").EnumerateObject().First(t => !t.Name.Contains('/')).Value;
		string notices = Notices();
		var shipped = new List<string>();
		foreach (JsonProperty lib in target.EnumerateObject()) {
			if (lib.Value.GetProperty("type").GetString() != "package") continue;
			bool ships = new[] { "runtime", "native", "runtimeTargets" }.Any(kind =>
				lib.Value.TryGetProperty(kind, out JsonElement files) && files.EnumerateObject().Any(f => !f.Name.EndsWith("/_._", StringComparison.Ordinal)));
			if (!ships) continue;
			shipped.Add(lib.Name);
			Names(notices, "\n" + lib.Name.Replace('/', ' ') + "\n");
		}
		Assert.Contains(shipped, p => p.StartsWith("FFmpeg.AutoGen/", StringComparison.Ordinal));
	}

	[Fact]
	public void TheNotices_NameTheDotNetRuntimesHeiExeCarries() {
		string notices = Notices(), version = $"{Environment.Version.Major}.{Environment.Version.Minor}";
		Names(notices, $"\n.NET runtime {version} (Microsoft.NETCore.App)\n");
		Names(notices, $"\nASP.NET Core runtime {version} (Microsoft.AspNetCore.App)\n");
	}

	[Fact]
	public void TheNotices_NameEveryDownloadAtItsPinnedVersion() {
		string notices = Notices();
		foreach (Architecture arch in new[] { Architecture.X64, Architecture.Arm64 }) {
			List<FfmpegDownloadPlan> plans = FfmpegDownloader.GetDownloadPlans(FfmpegDownloader.DownloadOS.Windows, arch, FfmpegDownloader.GetVersionTag());
			Assert.NotEmpty(plans);
			foreach (FfmpegDownloadPlan plan in plans) Names(notices, plan.ArchiveFileName);
		}
		Names(notices, $"ONNX Runtime {AiComponents.WindowsRuntimeVersion}");
		foreach (string archive in AiComponents.RuntimeSha256.Keys.Where(k => k.StartsWith("onnxruntime-win-", StringComparison.Ordinal)))
			Names(notices, archive);
		Names(notices, AiComponents.ModelFileName);
		Names(notices, NpuComponents.ModelFileName);
		Names(notices, $"Qualcomm.ML.OnnxRuntime.QNN {QnnPack.PackageVersion}");
		Names(notices, $"Intel.ML.OnnxRuntime.EP.OpenVINO {OpenVinoPack.PackageVersion}");
		Names(notices, $"Microsoft.Windows.AI.MachineLearning {WindowsMlPack.PackageVersion}");
		Names(notices, $"Microsoft.ML.OnnxRuntime.DirectML {GpuComponents.PackVersion}");
		Names(notices, $"Microsoft.AI.DirectML {GpuComponents.DirectMLVersion}");
	}

	[Fact]
	public void WriteBeside_WritesBoth_AndLeavesThemWhenTheyMatch() {
		string folder = Path.Combine(Path.GetTempPath(), "hei-license-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try {
			File.WriteAllText(Path.Combine(folder, LicenseTexts.Notices), "an older version's notices");
			LicenseTexts.WriteBeside(folder);
			foreach (string name in LicenseTexts.All)
				Assert.Equal(LicenseTexts.Read(name), File.ReadAllBytes(Path.Combine(folder, name)));

			DateTime written = File.GetLastWriteTimeUtc(Path.Combine(folder, LicenseTexts.License));
			File.SetLastWriteTimeUtc(Path.Combine(folder, LicenseTexts.License), written.AddDays(-1));
			LicenseTexts.WriteBeside(folder);
			Assert.Equal(written.AddDays(-1), File.GetLastWriteTimeUtc(Path.Combine(folder, LicenseTexts.License)));
			Assert.Empty(Directory.GetFiles(folder, "*.new"));
		}
		finally {
			Directory.Delete(folder, true);
		}
	}
}
