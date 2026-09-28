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

using System.Text.Json;
using HEI.Core.AI;

namespace HEI.Agent {
	/// <summary>
	/// Where AI matching runs, written down by whoever found out, so the review page never loads an AI
	/// runtime to guess: the install and "hei setup" (the NPU plugin lists the NPU, or a probe runs the
	/// model on the GPU), then every scan, with the device its embeddings actually ran on after any
	/// fallback. The page's badge reads this one small file, so it is right from the first visit.
	/// </summary>
	/// <param name="Device">"NPU", "GPU", "CPU", or "off" (AI components missing).</param>
	/// <param name="Source">"install", "setup" or "scan".</param>
	/// <param name="Setting">settings.json's aiDevice: auto, npu, gpu or cpu.</param>
	/// <param name="NpuVendor">Who made the NPU Windows lists: None, Qualcomm, Intel or Amd.</param>
	/// <param name="NpuName">That NPU's name as Windows shows it ("" without one).</param>
	/// <param name="NpuDisplayName">What Heiward calls it when this build can drive it ("Intel AI Boost NPU"), else "".</param>
	/// <param name="NpuSupported">This build has a pack for it.</param>
	/// <param name="NpuInstalled">That pack is downloaded.</param>
	sealed record AiStatus(string Device, string Source, DateTime CheckedAtUtc, string Setting,
		string NpuVendor, string NpuName, string NpuDisplayName, bool NpuSupported, bool NpuInstalled) {

		static string FilePath => Path.Combine(AgentPaths.Home, "ai-status.json");

		/// <summary>
		/// Records <paramref name="device"/> together with what the hardware and the packs say now: the
		/// device list and a few file checks, cheap next to the AI work that just found the device.
		/// </summary>
		public static AiStatus Record(AgentConfig cfg, string device, string source) {
			bool supported = NpuComponents.IsSupportedPlatform;
			var status = new AiStatus(device, source, DateTime.UtcNow, cfg.AiDevice,
				NpuHardware.Vendor.ToString(), NpuHardware.Name, supported ? NpuComponents.NpuName : "", supported, supported && NpuComponents.IsInstalled);
			try { AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(status, AgentConfig.Json)); }
			catch (Exception e) { AgentPaths.AppendLog($"ai-status.json not written: {e.Message}"); }
			return status;
		}

		public static AiStatus? Load() {
			try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AiStatus>(File.ReadAllText(FilePath), AgentConfig.Json) : null; }
			catch { return null; }
		}

		/// <summary>One line for "hei status" and the install: where it runs, and why not on the NPU.</summary>
		public string Describe() => Device switch {
			"NPU" => $"AI matching runs on the {(NpuDisplayName.Length > 0 ? NpuDisplayName : "NPU")}.",
			"off" => "AI matching is off: the AI components are not installed ('hei setup').",
			_ => $"AI matching runs on the {Device}: " + (
				NpuVendor == nameof(HEI.Core.AI.NpuVendor.None) ? "no NPU on this PC." :
				!NpuSupported ? $"this build does not support the NPU ({NpuName}) yet." :
				!NpuInstalled ? "the NPU pack is not installed ('hei setup')." :
				Setting is "gpu" or "cpu" ? $"settings.json asks for the {Setting.ToUpperInvariant()}." :
				"the NPU could not run the model (see heiward.log)."),
		};
	}
}
