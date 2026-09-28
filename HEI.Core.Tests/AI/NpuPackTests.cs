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
			Assert.Equal(cache, new OpenVinoPack(acceptCpu: false).ProviderOptions(cache)["cache_dir"]);
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
	public void The_OpenVINO_test_mode_also_takes_the_CPU_device() {
		Assert.True(new OpenVinoPack(acceptCpu: false).Accepts(OrtHardwareDeviceType.NPU));
		Assert.False(new OpenVinoPack(acceptCpu: false).Accepts(OrtHardwareDeviceType.CPU));
		Assert.True(new OpenVinoPack(acceptCpu: true).Accepts(OrtHardwareDeviceType.CPU));
		Assert.False(new QnnPack().Accepts(OrtHardwareDeviceType.GPU));
	}
}
