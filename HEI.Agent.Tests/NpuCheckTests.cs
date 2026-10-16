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

using System.Diagnostics;
using HEI.Core.AI;

namespace HEI.Agent.Tests;

/// <summary>
/// The NPU is checked in its own process before a scan first uses it with its driver and plugin: once while the check
/// holds, again a day after one failed, never while the NPU failed in the last 10 minutes, nor for a setting that
/// doesn't use it.
/// </summary>
public sealed class NpuCheckTests {
	static readonly DateTime Now = DateTime.UtcNow;

	static NpuChecks.Check Check(bool passed, DateTime at) =>
		new("NPU Compute Accelerator Device", "32.0.20102.3930", "VitisAIExecutionProvider 1.8.75.0", passed, at, passed ? null : "ended its process");

	[Fact]
	public void AnUncheckedNpu_IsChecked_ForAutoOrTheNpu() {
		Assert.True(NpuCheck.Due(AiDevice.Auto, npuCandidate: true, marker: null, check: null, Now));
		Assert.True(NpuCheck.Due(AiDevice.Npu, npuCandidate: true, marker: null, check: null, Now));
		Assert.False(NpuCheck.Due(AiDevice.Cpu, npuCandidate: true, marker: null, check: null, Now));
		Assert.False(NpuCheck.Due(AiDevice.Gpu, npuCandidate: true, marker: null, check: null, Now));
	}

	[Fact]
	public void NoNpuThisBuildDrives_NothingToCheck() =>
		Assert.False(NpuCheck.Due(AiDevice.Auto, npuCandidate: false, marker: null, check: null, Now));

	[Fact]
	public void ACheckedNpu_IsNotCheckedAgain_UntilAFailedCheckIsADayOld() {
		Assert.False(NpuCheck.Due(AiDevice.Auto, true, null, Check(passed: true, Now.AddDays(-100)), Now));
		Assert.False(NpuCheck.Due(AiDevice.Auto, true, null, Check(passed: false, Now.AddHours(-23)), Now));
		Assert.True(NpuCheck.Due(AiDevice.Auto, true, null, Check(passed: false, Now.AddDays(-1)), Now));
	}

	[Fact]
	public void AProbesAnswer_KeepsItsLastLines_WhenAPluginWroteHundreds() {
		Assert.Equal("the NPU ran the model", Installer.Tail("the NPU ran the model"));
		string compiler = string.Join('\n', Enumerable.Range(1, 800).Select(i => $"INFO: [aiecompiler] step {i}")) + "\nCompilation Failed";
		string said = Installer.Tail(compiler);
		Assert.StartsWith("(781 lines of output before these)\n", said);
		Assert.EndsWith("step 800\nCompilation Failed", said);
		Assert.Equal(21, said.Split('\n').Length);
	}

	[Fact]
	public async Task AProbeThatHangs_IsEndedAfterItsTime_AndOneThatAnswersIsNot() {
		using var hung = Process.Start(new ProcessStartInfo("ping", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
		var waited = Stopwatch.StartNew();
		Assert.True(await Installer.EndedAfterAsync(hung, TimeSpan.FromMilliseconds(500), CancellationToken.None));
		Assert.True(hung.HasExited);
		Assert.True(waited.Elapsed < TimeSpan.FromSeconds(15));
		Assert.True(new Installer.ProbeResult(hung.ExitCode, hung.Id, TimedOut: true).Crashed);

		using var quick = Process.Start(new ProcessStartInfo("cmd", "/c exit 1") { UseShellExecute = false, CreateNoWindow = true })!;
		Assert.False(await Installer.EndedAfterAsync(quick, TimeSpan.FromMinutes(1), CancellationToken.None));
		Assert.False(new Installer.ProbeResult(quick.ExitCode, quick.Id).Crashed); // 1: the model didn't run, a plain failure
	}

	[Fact]
	public void AProbeWhoseNpuGaveWrongEmbeddings_SaysSo_AndDidntCrash() {
		var wrong = new Installer.ProbeResult(Installer.ProbeWrongEmbeddings, Pid: 4242);
		Assert.True(wrong.WrongEmbeddings);
		Assert.False(wrong.Crashed); // it recorded its own failed check: nothing to clean up after a dead process
		Assert.False(new Installer.ProbeResult(0, 4242).WrongEmbeddings);
		Assert.False(new Installer.ProbeResult(1, 4242).WrongEmbeddings);
		// Ended after 10 minutes, whatever code Windows gave it: a hang, not an answer.
		var hung = new Installer.ProbeResult(Installer.ProbeWrongEmbeddings, 4242, TimedOut: true);
		Assert.True(hung.Crashed);
		Assert.False(hung.WrongEmbeddings);
	}

	[Fact]
	public void AnNpuThatFailedLately_IsLeftAlone() {
		var marker = new Accelerators.Failure(Now.AddMinutes(-2), "the AMD Ryzen AI NPU could not run the model", "heiward");
		Assert.False(NpuCheck.Due(AiDevice.Auto, true, marker, check: null, Now));
	}
}
