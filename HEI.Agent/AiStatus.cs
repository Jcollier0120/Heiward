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
using HEI.Core.Utils;

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
	/// <param name="Accelerator">Where it ran as the manor names it (the Steward's kit: kit\spec\ACCELERATORS.md): "npu", "cpu" or "gpu-…"; null when off.</param>
	/// <param name="Card">On a graphics card, its name as Windows lists it (settings.json's gpu); otherwise null.</param>
	/// <param name="Fallback">
	/// When the work was meant for another device, from where to where and why, in one line ("NPU to GPU: the Qualcomm
	/// Hexagon NPU pack failed to load (…)"), or that it stopped ("GPU failed: …"); null when it ran where it was meant to.
	/// </param>
	sealed record AiStatus(string Device, string Source, DateTime CheckedAtUtc, string Setting,
		string NpuVendor, string NpuName, string NpuDisplayName, bool NpuSupported, bool NpuInstalled,
		string? Accelerator = null, string? Card = null, string? Fallback = null) {

		static string FilePath => Path.Combine(AgentPaths.Home, "ai-status.json");

		/// <summary>
		/// Records <paramref name="device"/> together with what the hardware and the packs say now: the
		/// device list and a few file checks, cheap next to the AI work that just found the device.
		/// </summary>
		/// <param name="accelerator">The accelerator it ran on; null: the one <paramref name="device"/> means here (the card in use for the GPU).</param>
		/// <param name="card">The card it ran on; null: the card in use, for the GPU.</param>
		/// <param name="fallback">Why it didn't run where it was meant to (<see cref="OnnxEmbedder.Fallback"/>), written to heiward.log too.</param>
		public static AiStatus Record(AgentConfig cfg, string device, string source, string? accelerator = null, string? card = null, string? fallback = null) {
			bool supported = NpuComponents.IsSupportedPlatform;
			if (device == "GPU" && (accelerator == null || card == null) && GpuAdapters.InUse(cfg.Gpu, GpuAdapters.List()) is { } inUse) {
				accelerator ??= inUse.AcceleratorId;
				card ??= inUse.Key;
			}
			accelerator ??= device switch { "NPU" => Accelerators.Npu, "CPU" => Accelerators.Cpu, "GPU" => Accelerators.GpuId(card ?? ""), _ => null };
			var status = new AiStatus(device, source, DateTime.UtcNow, cfg.AiDevice,
				NpuHardware.Vendor.ToString(), NpuHardware.Name, supported ? NpuComponents.NpuName : "", supported, supported && NpuComponents.IsInstalled,
				accelerator, device == "GPU" ? card : null, fallback);
			if (fallback != null) AgentPaths.AppendLog($"AI fell back ({source}): {fallback}");
			try { AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(status, AgentConfig.Json)); }
			catch (Exception e) { AgentPaths.AppendLog($"ai-status.json not written: {e.Message}"); }
			return status;
		}

		/// <summary>"npu", "gpu" or "cpu"; null when AI matching is off.</summary>
		public string? Kind => Device switch { "NPU" => "npu", "GPU" => "gpu", "CPU" => "cpu", _ => null };

		/// <summary>One accelerator's failure marker that still counts, for "hei status" and the page.</summary>
		internal sealed record MarkedFailed(string Id, string Name, DateTime SinceUtc, DateTime UntilUtc, string Reason, string By);

		/// <summary>
		/// This PC's accelerators Heiward has marked failed (its own markers, never the manor's shared ones), which Auto
		/// skips until the marker's 10 minutes are up: the NPU when there is one, each graphics card, the CPU.
		/// </summary>
		internal static List<MarkedFailed> Failures(IReadOnlyList<GpuAdapter> cards, DateTime? nowUtc = null) {
			var ids = new List<(string Id, string Name)>();
			if (NpuHardware.Vendor != HEI.Core.AI.NpuVendor.None) ids.Add((Accelerators.Npu, NpuHardware.Name.Length > 0 ? NpuHardware.Name : "the NPU"));
			ids.AddRange(cards.Select(c => (c.AcceleratorId, c.Key)));
			ids.Add((Accelerators.Cpu, "the processor"));
			var failed = new List<MarkedFailed>();
			foreach (var (id, name) in ids)
				if (Accelerators.FailureOf(id, nowUtc) is { } f)
					failed.Add(new MarkedFailed(id, name, f.SinceUtc, f.UntilUtc, f.Reason, f.By));
			return failed;
		}

		public static AiStatus? Load() {
			try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AiStatus>(File.ReadAllText(FilePath), AgentConfig.Json) : null; }
			catch { return null; }
		}

		/// <summary>One line for "hei status" and the install: where it runs, and why not on the NPU.</summary>
		public string Describe() => Device switch {
			"NPU" when Fallback != null => $"AI matching runs on the {(NpuDisplayName.Length > 0 ? NpuDisplayName : "NPU")} ({Fallback}).",
			"NPU" => $"AI matching runs on the {(NpuDisplayName.Length > 0 ? NpuDisplayName : "NPU")}.",
			"off" => "AI matching is off: the AI components are not installed ('hei setup').",
			_ when Fallback != null => $"AI matching runs on the {Device}{(Card != null ? $" ({Card})" : "")}: it fell back, {Fallback}.",
			_ => $"AI matching runs on the {Device}{(Card != null ? $" ({Card})" : "")}: " + (
				NpuVendor == nameof(HEI.Core.AI.NpuVendor.None) ? "no NPU on this PC." :
				!NpuSupported ? $"this build does not support the NPU ({NpuName}) yet." :
				!NpuInstalled ? "the NPU pack is not installed ('hei setup')." :
				Setting is "gpu" or "cpu" ? $"settings.json asks for the {Setting.ToUpperInvariant()}." :
				"the NPU could not run the model (see heiward.log)."),
		};
	}
}
