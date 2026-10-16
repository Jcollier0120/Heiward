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

namespace HEI.Agent {
	/// <summary>
	/// Before a scan first uses the NPU with its driver and plugin, the NPU is checked: <c>hei probe --device npu</c> in its
	/// own process, which compiles the model and runs it once (<see cref="NpuChecks"/>). A plugin that ends that process
	/// from native code (Windows ML's Vitis AI on a Ryzen AI 7 350, 2026-10-15) then costs the check, not the scan: the
	/// check is recorded as failed, the compiled graphs it left are deleted, and the NPU's lock it died holding is let go.
	/// </summary>
	static class NpuCheck {
		/// <summary>
		/// Whether to check the NPU now: Auto or the NPU is set, this build drives this PC's NPU and its pack is here, it hasn't
		/// failed in the last 10 minutes (a check now would only fail too), and it has no check with its driver and plugin (or
		/// one that failed a day ago or more).
		/// </summary>
		internal static bool Due(AiDevice setting, bool npuCandidate, Accelerators.Failure? marker, NpuChecks.Check? check, DateTime nowUtc) =>
			setting is AiDevice.Auto or AiDevice.Npu && npuCandidate && marker == null && NpuChecks.NeedsCheck(check, nowUtc);

		/// <summary>Checks the NPU when it's due (<see cref="Due"/>): once per driver and plugin, the model's compile in its own process.</summary>
		public static async Task EnsureAsync(AiDevice setting, Action<string> say, CancellationToken ct) {
			NpuChecks.Check? check;
			try {
				bool candidate = NpuComponents.IsSupportedPlatform && NpuComponents.IsInstalled && AiComponents.IsReady;
				check = candidate ? NpuChecks.Current() : null;
				if (!Due(setting, candidate, Accelerators.FailureOf(Accelerators.Npu), check, DateTime.UtcNow)) return;
			}
			catch (Exception e) when (e is not OperationCanceledException) {
				say("couldn't tell whether the NPU needs checking: " + e.Message);
				return;
			}
			string driver = NpuHardware.Driver is { Length: > 0 } d ? $", driver {d}" : "";
			say($"AI matching: checking the {NpuComponents.NpuName} ({NpuComponents.PackVersion}{driver}) before a scan uses it: it sets the model up once, in its own process");
			Installer.ProbeResult probe;
			try { probe = await Installer.ProbeAsync("npu", ct, say: line => say("  " + line)); }
			catch (Exception e) when (e is not OperationCanceledException) {
				// The probe couldn't even start: nothing learned about the NPU, so no record.
				say($"  the check couldn't run ({e.Message})");
				return;
			}
			if (probe.Crashed)
				say($"  {Crashed(probe)}: AI matching leaves the NPU (checked again after a driver or plugin update, or in a day)");
			else if (probe.WrongEmbeddings)
				say($"  {WrongEmbeddings()}: AI matching leaves the NPU (checked again after a driver or plugin update, or in a day)");
			else if (probe.ExitCode != 0)
				say("  the NPU couldn't run the AI model (see the log): scans leave it for 10 minutes");
			else
				say("  the NPU runs the AI model: scans use it");
		}

		/// <summary>
		/// Whether a copied NPU pack runs the model (setup's reuse): the probe in its own process. A probe the plugin ended, or
		/// whose NPU gave wrong embeddings, is the plugin's failure, not the copy's: it's recorded as the NPU's check (so it
		/// isn't compiled again straight after) and the copy is kept, as a download would only meet the same plugin.
		/// </summary>
		public static async Task<bool> CopiedPackRunsAsync(CancellationToken ct) {
			Installer.ProbeResult probe = await Installer.ProbeAsync("npu", ct);
			if (!probe.Crashed && !probe.WrongEmbeddings) return probe.ExitCode == 0;
			Console.WriteLine($"  {(probe.Crashed ? Crashed(probe) : WrongEmbeddings())}: AI matching leaves the NPU (checked again after a driver or plugin update, or in a day)");
			return true;
		}

		/// <summary>
		/// A probe whose NPU ran the model and gave wrong embeddings: it recorded its failed check itself, with which pictures,
		/// and it's recorded again here under the plugin as this process finds it, which is how the scan looks it up (on a
		/// Ryzen AI 7 350 the probe found the Vitis AI plugin's version and the agent didn't, so the scan used the NPU anyway).
		/// The graphs it compiled are deleted, so the next check compiles them afresh.
		/// </summary>
		static string WrongEmbeddings() {
			NpuChecks.RecordProbesFailure("its embeddings didn't match the processor's");
			NpuComponents.DropCompiledGraphs();
			return $"the {NpuComponents.NpuName} ran the AI model but its embeddings didn't match the processor's";
		}

		/// <summary>A probe the plugin ended: its failed check recorded, the compiled graphs it left deleted, the NPU's lock it died holding let go.</summary>
		static string Crashed(Installer.ProbeResult probe) {
			string reason = probe.TimedOut
				? $"setting the model up hung in the {NpuComponents.NpuName}'s plugin (no answer in {Installer.ProbeTimeout.TotalMinutes:0} minutes)"
				: $"setting the model up ended its process (exit 0x{probe.ExitCode:X8}) from the {NpuComponents.NpuName}'s plugin";
			NpuChecks.Record(passed: false, reason);
			NpuComponents.DropCompiledGraphs();
			NpuLock.ReleaseIfHeldBy(Accelerators.Npu, probe.Pid);
			return reason;
		}
	}
}
