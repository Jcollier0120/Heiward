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

using HEI.Core.AI;
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <summary>
	/// Auto, with no working NPU, runs AI matching on the graphics card in use once that card has passed a check
	/// (<see cref="GpuChecks"/>). A card the GPU pack hasn't been checked on yet, with its current driver, is checked
	/// before a scan or by setup: <c>hei probe --device gpu</c> in its own process (a process loads one ONNX Runtime),
	/// which records what it found, and marks the card failed (Heiward's own marker) when it can't.
	/// </summary>
	static class GpuCheck {
		/// <summary>
		/// The card to check now, or null: Auto would leave the NPU (none this build drives, its pack missing, or it
		/// failed in the last 10 minutes), the GPU pack is here, and the card in use has no check with its driver (or
		/// failed one over a day ago) and hasn't failed lately.
		/// </summary>
		internal static GpuAdapter? Due(AgentConfig cfg, DateTime nowUtc) {
			if (!GpuComponents.IsInstalled) return null;
			bool npu = NpuComponents.IsSupportedPlatform && NpuComponents.IsInstalled && AiComponents.IsReady && Accelerators.FailureOf(Accelerators.Npu, nowUtc) == null;
			if (npu) return null;
			return Due(GpuAdapters.InUse(cfg.Gpu, GpuAdapters.List()), nowUtc);
		}

		/// <summary>The card, when it needs a check and hasn't failed in the last 10 minutes.</summary>
		internal static GpuAdapter? Due(GpuAdapter? card, DateTime nowUtc) =>
			card != null && GpuChecks.NeedsCheck(card, nowUtc) && Accelerators.FailureOf(card.AcceleratorId, nowUtc) == null ? card : null;

		/// <summary>Checks the card in use when it's due (<see cref="Due(AgentConfig, DateTime)"/>); a few seconds, once per card and driver.</summary>
		public static async Task EnsureAsync(AgentConfig cfg, Action<string> say, CancellationToken ct) {
			GpuAdapter? card;
			try { card = Due(cfg, DateTime.UtcNow); }
			catch (Exception e) when (e is not OperationCanceledException) {
				say("couldn't tell whether the graphics card needs checking: " + e.Message);
				return;
			}
			if (card == null) return;
			say($"AI matching: no working NPU, so checking the {card.Key} ({card.AcceleratorId}{(card.Driver.Length > 0 ? ", driver " + card.Driver : "")}) before using it");
			bool passed;
			try { passed = await Installer.ProbeDeviceAsync("gpu", ct, card.Key, line => say("  " + line)); }
			catch (Exception e) when (e is not OperationCanceledException) {
				// The probe couldn't even start: nothing learned about the card, so no record; the CPU it is.
				say($"  the check couldn't run ({e.Message}): AI matching stays on the CPU");
				return;
			}
			say(passed ? $"  the {card.Key} runs the AI model: scans use it while there's no working NPU"
				: $"  the {card.Key} couldn't run the AI model: AI matching stays on the CPU (checked again after a driver update, or in a day)");
		}
	}
}
