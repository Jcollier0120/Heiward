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
using Microsoft.ML.OnnxRuntime;
using HEI.Core.AI;

namespace HEI.Core.Tests.AI;

/// <summary>Which NPU a PC has, and what each vendor's pack is made of.</summary>
public class NpuPackTests {
	[Theory]
	// As Windows lists them (Device Manager, "Neural processors").
	[InlineData("Qualcomm Technologies, Inc.", "Snapdragon(R) X2 Elite Extreme - X2E94100 - Qualcomm(R) Hexagon(TM) NPU", NpuVendor.Qualcomm)]
	[InlineData("Intel Corporation", "Intel(R) AI Boost", NpuVendor.Intel)]
	[InlineData("Advanced Micro Devices, Inc.", "NPU Compute Accelerator Device", NpuVendor.Amd)]
	[InlineData("", "AMD IPU Device", NpuVendor.Amd)]
	[InlineData("Contoso", "Some accelerator", NpuVendor.None)]
	[InlineData("Samdisk Corp", "Lambda accelerator", NpuVendor.None)]
	[InlineData(null, null, NpuVendor.None)]
	public void An_NPU_is_recognised_by_its_maker(string? manufacturer, string? name, NpuVendor expected) =>
		Assert.Equal(expected, NpuHardware.Classify(manufacturer, name));

	[Fact]
	public void This_PC_s_NPU_is_found_without_loading_an_AI_runtime() {
		if (!OperatingSystem.IsWindows()) return;
		// Whatever the test PC has, detection answers and names it consistently.
		NpuVendor vendor = NpuHardware.Vendor;
		Assert.Equal(vendor == NpuVendor.None, NpuHardware.Name.Length == 0);
		if (vendor == NpuVendor.Qualcomm && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
			Assert.Equal("Qualcomm Hexagon NPU", NpuComponents.NpuName);
	}

	[Fact]
	public void Each_pack_keeps_its_own_embeddings_and_caches() {
		NpuPack[] packs = { new QnnPack(), new OpenVinoPack(acceptCpu: false), WindowsMlPack.VitisAi() };
		Assert.Equal(packs.Length, packs.Select(p => p.ModelKey).Distinct().Count());
		Assert.Equal(packs.Length, packs.Select(p => p.CacheFolderName).Distinct().Count());
		Assert.Equal("dinov2s-fp16", new QnnPack().ModelKey); // unchanged: existing NPU sidecars stay valid
		foreach (NpuPack p in packs) {
			Assert.Contains(p.CacheFolderName, p.Folders);
			Assert.StartsWith(p.Folders[0], p.KeyFile);
		}
	}

	[Fact]
	public void Only_Qualcomm_keeps_an_EP_context_model_the_others_a_cache_folder() {
		Assert.True(new QnnPack().UsesEpContextModel);
		string cache = Path.Combine(Path.GetTempPath(), "heiward-npu-" + Guid.NewGuid().ToString("N"));
		try {
			// The OpenVINO plugin ignores cache_dir: the folder goes in load_config, as the NPU's CACHE_DIR.
			Dictionary<string, string> openVino = new OpenVinoPack(acceptCpu: false).ProviderOptions(cache);
			Assert.False(openVino.ContainsKey("cache_dir"));
			using (var config = System.Text.Json.JsonDocument.Parse(openVino["load_config"]))
				Assert.Equal(cache, config.RootElement.GetProperty("NPU").GetProperty("CACHE_DIR").GetString());
			Dictionary<string, string> vitis = WindowsMlPack.VitisAi().ProviderOptions(cache);
			Assert.Equal(cache, vitis["cache_dir"]);
			Assert.Equal($"dinov2s_b{NpuComponents.NpuBatch}", vitis["cache_key"]);
			Assert.True(Directory.Exists(cache));
		}
		finally {
			try { Directory.Delete(cache, true); } catch { }
		}
	}

	[Fact]
	public void Vitis_AI_runs_one_image_per_run_through_its_junction_and_its_checks_say_so() {
		// At batch 8 its graph gave only the first image of each run the right embedding (Ryzen AI 7 350, 2026-10-15).
		Assert.Equal(1, WindowsMlPack.VitisAi().Batch);
		// Part of what its check holds for: a check that failed with the plugin in its own folder doesn't hold.
		Assert.Equal("batch 1, through vitisai-plugin", WindowsMlPack.VitisAi().CheckedSetup);
		// Not one of the pack's folders: copying the pack would copy the plugin through it.
		Assert.DoesNotContain("vitisai-plugin", WindowsMlPack.VitisAi().Folders);
		// The other packs load their plugin from where it is, and their checks hold as before.
		Assert.Equal("", new QnnPack().CheckedSetup);
		Assert.Equal("", new OpenVinoPack(acceptCpu: false).CheckedSetup);
		Assert.Equal("", WindowsMlPack.QnnForTests().CheckedSetup);
	}

	/// <summary>Where Windows ML had the Vitis AI plugin on a Ryzen AI 7 350 (2026-10-15), whose catalog said 1.8.75.0 once it was in the process.</summary>
	internal const string VitisAiLibrary = @"C:\Program Files\WindowsApps\Microsoft.WinML.AMD.NPU.EP.Framework.2_1.8.75.0_x64__8wekyb3d8bbwe\ExecutionProvider\onnxruntime_vitisai_ep.dll";

	[Theory]
	[InlineData(VitisAiLibrary, "1.8.75.0")]
	// In the package's root, a package for Arm, one with a resource ID, and forward slashes.
	[InlineData(@"C:\Program Files\WindowsApps\Microsoft.WinML.AMD.NPU.EP.Framework.2_1.8.75.0_x64__8wekyb3d8bbwe\onnxruntime_vitisai_ep.dll", "1.8.75.0")]
	[InlineData(@"C:\Program Files\WindowsApps\Contoso.WinML.NPU.EP_2.1.0.0_arm64__8wekyb3d8bbwe\ExecutionProvider\plugin.dll", "2.1.0.0")]
	[InlineData(@"D:\WindowsApps\Contoso.Plugin_2.0.10.0_neutral_split.scale-100_8wekyb3d8bbwe\bin\x64\plugin.dll", "2.0.10.0")]
	[InlineData("C:/Program Files/WindowsApps/Contoso.Plugin_3.0.0.1_x64__8wekyb3d8bbwe/plugin.dll", "3.0.0.1")]
	// The nearest package folder, when one is inside another.
	[InlineData(@"C:\Contoso.Outer_1.0.0.0_x64__8wekyb3d8bbwe\Contoso.Inner_2.0.0.0_x64__8wekyb3d8bbwe\plugin.dll", "2.0.0.0")]
	// Not in a package's folder: through Heiward's junction, a folder that only has underscores, a version of three parts,
	// a publisher ID that's too short, and the file's own name.
	[InlineData(@"C:\Users\me\AppData\Local\Programs\Heiward\ai\vitisai-plugin\onnxruntime_vitisai_ep.dll", "")]
	[InlineData(@"C:\tools\my_plugin_1.2.3.4_x64\plugin.dll", "")]
	[InlineData(@"C:\Program Files\WindowsApps\Microsoft.WinML.AMD.NPU.EP.Framework.2_1.8.75_x64__8wekyb3d8bbwe\plugin.dll", "")]
	[InlineData(@"C:\Program Files\WindowsApps\Microsoft.WinML.AMD.NPU.EP.Framework.2_1.8.75.0_x64__8wekyb3d8\plugin.dll", "")]
	[InlineData(@"C:\plugins\Contoso.Plugin_3.0.0.1_x64__8wekyb3d8bbwe", "")]
	[InlineData("", "")]
	[InlineData(null, "")]
	public void A_Windows_ML_plugin_s_version_is_its_package_s_from_its_folder(string? libraryPath, string expected) =>
		Assert.Equal(expected, WindowsMlPack.PackageVersionIn(libraryPath));

	[Fact]
	public void The_OpenVINO_test_mode_also_takes_the_CPU_device() {
		Assert.True(new OpenVinoPack(acceptCpu: false).Accepts(OrtHardwareDeviceType.NPU));
		Assert.False(new OpenVinoPack(acceptCpu: false).Accepts(OrtHardwareDeviceType.CPU));
		Assert.True(new OpenVinoPack(acceptCpu: true).Accepts(OrtHardwareDeviceType.CPU));
		Assert.False(new QnnPack().Accepts(OrtHardwareDeviceType.GPU));
	}
}
