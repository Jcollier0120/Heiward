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

/// <summary>What "hei status" and the install say about where AI matching runs, and why not on the NPU.</summary>
public sealed class AiStatusTests {
	static AiStatus Status(string device, string vendor = "Intel", bool supported = true, bool installed = true, string setting = "auto") =>
		new(device, "scan", DateTime.UtcNow, setting, vendor, vendor == "None" ? "" : "Intel(R) AI Boost",
			supported ? "Intel AI Boost NPU" : "", supported, installed);

	[Fact]
	public void On_the_NPU_it_names_it() =>
		Assert.Equal("AI matching runs on the Intel AI Boost NPU.", Status("NPU").Describe());

	[Theory]
	[InlineData("CPU", "None", true, true, "auto", "no NPU on this PC")]
	[InlineData("GPU", "Intel", false, false, "gpu", "does not support the NPU (Intel(R) AI Boost) yet")]
	[InlineData("CPU", "Intel", true, false, "auto", "the NPU pack is not installed")]
	[InlineData("GPU", "Intel", true, true, "gpu", "settings.json asks for the GPU")]
	[InlineData("CPU", "Intel", true, true, "auto", "the NPU could not run the model")]
	public void Off_the_NPU_it_says_why(string device, string vendor, bool supported, bool installed, string setting, string reason) {
		string text = Status(device, vendor, supported, installed, setting).Describe();
		Assert.StartsWith($"AI matching runs on the {device}: ", text);
		Assert.Contains(reason, text);
	}

	[Fact]
	public void Without_the_AI_components_it_is_off() =>
		Assert.Contains("hei setup", Status("off").Describe());

	[Fact]
	public void On_a_card_it_names_it() =>
		Assert.Equal("AI matching runs on the GPU (NVIDIA GeForce RTX 4090): no NPU on this PC.",
			(Status("GPU", "None", setting: "auto") with { Accelerator = "gpu-nvidia-geforce-rtx-4090", Card = "NVIDIA GeForce RTX 4090" }).Describe());

	[Fact]
	public void After_a_fallback_it_says_why() {
		string text = (Status("GPU", "Qualcomm") with {
			Card = "Qualcomm(R) Adreno(TM) X2-90 GPU",
			Fallback = "NPU to GPU: the NPU was marked failed by reeve (GenieX did not start within 30 s) until 14:12",
		}).Describe();
		Assert.StartsWith("AI matching runs on the GPU (Qualcomm(R) Adreno(TM) X2-90 GPU): it fell back, NPU to GPU: the NPU was marked failed by reeve", text);
	}

	[Fact]
	public void A_scan_that_stopped_on_the_NPU_says_so() =>
		Assert.Equal("AI matching runs on the Intel AI Boost NPU (NPU failed: device lost during a scan; AI matching stopped for the files left).",
			(Status("NPU") with { Fallback = "NPU failed: device lost during a scan; AI matching stopped for the files left" }).Describe());
}
