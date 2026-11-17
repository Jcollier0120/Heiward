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
	/// <summary>What a GPU check found: the card runs the model, it can't be used on this PC, or it failed as a working card can.</summary>
	enum GpuProbe { Runs, Unusable, Failed }

	/// <summary>
	/// Auto, with no working NPU, runs AI matching on the graphics card in use once that card has passed a check
	/// (<see cref="GpuChecks"/>). A card the GPU pack hasn't been checked on yet, with its current driver, is checked
	/// before a scan or by setup: <c>hei probe --device gpu</c> in its own process (a process loads one ONNX Runtime),
	/// which records what it found, and marks the card failed (Heiward's own marker) when it can't.
	/// <para>
	/// A card the check finds can't be used on this PC (<see cref="GpuChecks.Unusable"/>) is no card: its check's process
	/// ended under it (a Windows Sandbox lists the host's card, and opening it ends the process, 0xC0000005, 2026-11-17),
	/// or DirectML found no device on it. AI matching runs on what works, quietly: no failure marker, no failure in the
	/// log, one plain line on the page; and the card is checked again only after a driver or pack change, or a restart.
	/// </para>
	/// </summary>
	static class GpuCheck {
		/// <summary>
		/// The card to check now, or null: Auto would leave the NPU (none this build drives, its pack missing, or it
		/// failed in the last 10 minutes or failed its check), the GPU pack is here, and the card in use has no check with
		/// its driver (or failed one over a day ago, or was found unusable before the PC last started) and hasn't failed lately.
		/// </summary>
		internal static GpuAdapter? Due(AgentConfig cfg, DateTime nowUtc) {
			if (!GpuComponents.IsInstalled) return null;
			bool npu = NpuComponents.IsSupportedPlatform && NpuComponents.IsInstalled && AiComponents.IsReady && Accelerators.FailureOf(Accelerators.Npu, nowUtc) == null
				&& NpuChecks.FailureOf(NpuChecks.Current(), nowUtc) == null;
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
			GpuProbe found;
			try { found = await ProbeAsync(card.Key, card, ct, line => say("  " + line)); }
			catch (Exception e) when (e is not OperationCanceledException) {
				// The probe couldn't even start: nothing learned about the card, so no record; the CPU it is.
				say($"  the check couldn't run ({e.Message}): AI matching stays on the CPU");
				return;
			}
			say("  " + Said(found, card.Key));
		}

		/// <summary>What the scan's log says of a check, in one line.</summary>
		internal static string Said(GpuProbe found, string cardKey) => found switch {
			GpuProbe.Runs => $"the {cardKey} runs the AI model: scans use it while there's no working NPU",
			GpuProbe.Unusable => $"the {cardKey} can't be used on this PC: AI matching runs on the processor (the card is checked again after a driver update or a restart)",
			_ => $"the {cardKey} couldn't run the AI model: AI matching stays on the CPU (checked again after a driver update, or in a day)",
		};

		/// <summary>
		/// Runs the GPU probe in its own process (<see cref="Installer.ProbeAsync"/>) on the card <paramref name="gpu"/> names
		/// (null or empty: Windows' default), and says what it found (<see cref="Found"/>).
		/// </summary>
		/// <param name="card">That card as this process finds it; null: looked up, when the GPU pack is here (the probe checks no card without it).</param>
		internal static async Task<GpuProbe> ProbeAsync(string? gpu, GpuAdapter? card, CancellationToken ct, Action<string>? say = null) {
			Installer.ProbeResult probe = await Installer.ProbeAsync("gpu", ct, gpu, say);
			card ??= GpuComponents.IsInstalled ? GpuAdapters.InUse(gpu, GpuAdapters.List()) : null;
			return Found(probe, card);
		}

		/// <summary>
		/// What a GPU probe found. One whose process ended under it (past any catch, so it recorded nothing) or hung found
		/// the card can't be used on this PC, which is recorded here; one that said so itself recorded it already. Any other
		/// failure was recorded and marked by the probe, as a working card's failure is.
		/// </summary>
		internal static GpuProbe Found(Installer.ProbeResult probe, GpuAdapter? card, DateTime? nowUtc = null) {
			if (probe.Unusable) return GpuProbe.Unusable;
			if (probe.Crashed) {
				if (card != null)
					GpuChecks.RecordUnusable(card, probe.TimedOut
						? $"its check gave no answer in {Installer.ProbeTimeout.TotalMinutes:0} minutes"
						: $"opening it ended its check's process (exit 0x{probe.ExitCode:X8})", nowUtc);
				return GpuProbe.Unusable;
			}
			return probe.ExitCode == 0 ? GpuProbe.Runs : GpuProbe.Failed;
		}
	}
}
