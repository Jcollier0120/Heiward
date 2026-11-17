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

using System.Text.RegularExpressions;
using HEI.Core.AI;
using HEI.Core.Utils;
using Jint;

namespace HEI.Agent.Tests;

/// <summary>
/// Auto without a working NPU takes the graphics card once it has passed a check: a card not checked yet (with its
/// driver) is checked before the scan, but not one that failed in the last 10 minutes, nor again soon after a failed check.
/// A card whose check crashes can't be used on this PC (a Windows Sandbox lists the host's card, and opening it ends the
/// check's process, 2026-11-17): no card, quietly, until its driver changes or the PC restarts.
/// </summary>
[Collection(AgentHomeCollection.Name)]
public sealed class GpuCheckTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-gpucheck-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public GpuCheckTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
		Accelerators.FolderOverride = Path.Combine(dir, "accelerators");
		GpuChecks.PathOverride = Path.Combine(dir, "gpu-checks.json");
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		Accelerators.FolderOverride = null;
		GpuChecks.PathOverride = null;
		GpuChecks.StartedOverride = null;
		Installer.ProbeOverride = null;
		try { Directory.Delete(dir, true); } catch { }
	}

	static readonly GpuAdapter Card = new(0, "Qualcomm(R) Adreno(TM) X2-90 GPU", "Qualcomm(R) Adreno(TM) X2-90 GPU", 0, 0x4D4F4351, "0x00000000_0x000137d9", "31.0.112.0");

	static readonly GpuAdapter Sandboxed = new(0, "NVIDIA GeForce RTX 4080 SUPER", "NVIDIA GeForce RTX 4080 SUPER", 16UL << 30, 0x10DE, "0x00000000_0x0000d2f1", "32.0.15.8157");

	/// <summary>What the Surveyor reads as a fault in a staff member's log (its src/logs.ts ERROR_RE, the words of it a probe could say).</summary>
	static readonly Regex Alarm = new(@"\b(error|errors?:|failed|failure|fatal|exception|unhandled|panic)\b", RegexOptions.IgnoreCase);

	/// <summary>A stand-in for the probe that ends as DirectML ended it in a Windows Sandbox: .NET's last words on stderr, and 0xC0000005.</summary>
	static (string, string[]) Crashing => (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/s", "/c", "echo Fatal error.>&2& exit -1073741819& rem"]);

	static string Log => File.Exists(AgentPaths.Log) ? File.ReadAllText(AgentPaths.Log) : "";

	[Fact]
	public void AnUncheckedCard_IsChecked_OnceUntilItsDriverChanges() {
		DateTime now = DateTime.UtcNow;
		Assert.Same(Card, GpuCheck.Due(Card, now));
		GpuChecks.Record(Card, passed: true, nowUtc: now);
		Assert.Null(GpuCheck.Due(Card, now.AddDays(100)));
		Assert.NotNull(GpuCheck.Due(Card with { Driver = "31.0.200.0" }, now));
	}

	[Fact]
	public void ACardThatFailed_IsNotCheckedAgainSoon() {
		DateTime now = DateTime.UtcNow;
		Accelerators.MarkFailed(Card.AcceleratorId, "DirectML could not open the model", now);
		Assert.Null(GpuCheck.Due(Card, now.AddMinutes(5))); // marked failed: left alone for 10 minutes
		Assert.Same(Card, GpuCheck.Due(Card, now.AddMinutes(11)));

		GpuChecks.Record(Card, passed: false, "device removed", now);
		Assert.Null(GpuCheck.Due(Card, now.AddMinutes(11)));
		Assert.Same(Card, GpuCheck.Due(Card, now.AddDays(1)));
	}

	[Fact]
	public void NoCard_NothingToCheck() => Assert.Null(GpuCheck.Due((GpuAdapter?)null, DateTime.UtcNow));

	[Fact]
	public async Task AProbeThatCrashes_LeavesTheCardUnusable_Quietly_UntilARestartOrADriverUpdate() {
		DateTime before = DateTime.UtcNow;
		GpuChecks.StartedOverride = before.AddHours(-1);
		Assert.Same(Sandboxed, GpuCheck.Due(Sandboxed, before));

		Installer.ProbeOverride = Crashing;
		var said = new List<string>();
		GpuProbe found = await GpuCheck.ProbeAsync(Sandboxed.Key, Sandboxed, CancellationToken.None, said.Add);

		Assert.Equal(GpuProbe.Unusable, found);
		GpuChecks.Check check = GpuChecks.Unusable(Sandboxed)!;
		Assert.NotNull(check);
		Assert.Equal("opening it ended its check's process (exit 0xC0000005)", check.Reason);
		// No failure: no marker for the page or Auto, and nothing in heiward.log, or in what the scan's log says of it, that
		// reads as a fault (the probe's "Fatal error." stays out).
		Assert.Null(Accelerators.FailureOf(Sandboxed.AcceleratorId));
		Assert.DoesNotMatch(Alarm, Log);
		Assert.DoesNotContain("Fatal", Log);
		Assert.All(said, line => Assert.DoesNotMatch(Alarm, line));
		Assert.DoesNotMatch(Alarm, GpuCheck.Said(found, Sandboxed.Key));
		Assert.Contains("can't be used on this PC", GpuCheck.Said(found, Sandboxed.Key));

		// Not checked again every round, nor a day on: nothing has changed.
		Assert.Null(GpuCheck.Due(Sandboxed, DateTime.UtcNow.AddMinutes(1)));
		Assert.Null(GpuCheck.Due(Sandboxed, DateTime.UtcNow.AddDays(3)));
		// A driver update, or a restart, and it's asked again.
		Assert.NotNull(GpuCheck.Due(Sandboxed with { Driver = "32.0.15.9000" }, DateTime.UtcNow));
		GpuChecks.StartedOverride = DateTime.UtcNow.AddMinutes(30);
		Assert.Same(Sandboxed, GpuCheck.Due(Sandboxed, DateTime.UtcNow.AddMinutes(31)));
	}

	[Fact]
	public void AProbeThatHangs_OrSaysTheCardHasNoDevice_IsAnUnusableCardToo() {
		DateTime now = DateTime.UtcNow;
		GpuChecks.StartedOverride = now.AddHours(-1);
		Assert.Equal(GpuProbe.Unusable, GpuCheck.Found(new Installer.ProbeResult(-1, 0, TimedOut: true), Sandboxed, now));
		Assert.Equal("its check gave no answer in 10 minutes", GpuChecks.Unusable(Sandboxed)!.Reason);

		// The probe found no device on it, and recorded that itself: nothing more to record.
		Assert.Equal(GpuProbe.Unusable, GpuCheck.Found(new Installer.ProbeResult(Installer.ProbeUnusable, 0), Card, now));
		Assert.Null(GpuChecks.Find(Card));
		// A crash with no card to name (no GPU pack): nothing recorded, and still no card.
		Assert.Equal(GpuProbe.Unusable, GpuCheck.Found(new Installer.ProbeResult(unchecked((int)0xC0000005), 0), null, now));
	}

	[Fact]
	public async Task ARealFailure_OnACardThatWorks_StaysVisible() {
		// A probe that ended the ordinary way, the model not running on the card: a failure, logged as one, as before.
		Installer.ProbeOverride = (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/s", "/c", "echo DirectML device removed>&2& exit 1& rem"]);
		GpuProbe found = await GpuCheck.ProbeAsync(Card.Key, Card, CancellationToken.None, _ => { });
		Assert.Equal(GpuProbe.Failed, found);
		Assert.Null(GpuChecks.Unusable(Card));
		Assert.Contains("failed (exit 0x00000001): DirectML device removed", Log);
		Assert.Contains("couldn't run the AI model", GpuCheck.Said(found, Card.Key));
	}

	[Fact]
	public void ThePage_SaysSoInOnePlainLine() {
		string notes = AcceleratorNotes("{ inUse: 'cpu', card: null, fallback: null, failed: [], " +
			"unusable: [{ id: 'gpu-nvidia-geforce-rtx-4080-super', name: 'NVIDIA GeForce RTX 4080 SUPER', sinceUtc: '2026-11-17T10:00:00Z', reason: 'opening it ended its check\\'s process (exit 0xC0000005)' }] }", dev: false);
		Assert.Equal("Last ran on the processor.|The NVIDIA GeForce RTX 4080 SUPER can't be used for AI matching on this PC, so it runs on the processor; " +
			"Heiward checks the card again after a driver update or a restart.", notes);
		Assert.DoesNotMatch(Alarm, notes);
		// With developer mode on, which card and why.
		Assert.Contains("(gpu-nvidia-geforce-rtx-4080-super: opening it ended its check's process (exit 0xC0000005)).",
			AcceleratorNotes("{ inUse: 'npu', unusable: [{ id: 'gpu-nvidia-geforce-rtx-4080-super', name: 'NVIDIA GeForce RTX 4080 SUPER', reason: 'opening it ended its check\\'s process (exit 0xC0000005)' }] }", dev: true));
		Assert.Contains("so it runs on the NPU;", AcceleratorNotes("{ inUse: 'npu', unusable: [{ id: 'gpu-x', name: 'X', reason: 'r' }] }", dev: false));
	}

	/// <summary>app.js's acceleratorNotes for <paramref name="accelerators"/> (JSON), each note's text, joined by "|".</summary>
	static string AcceleratorNotes(string accelerators, bool dev) {
		var js = new Engine();
		js.Execute("""
			function makeEl(tag) {
			  return {
			    tagName: tag.toUpperCase(), children: [], className: '', textContent: '',
			    append: function () { for (var i = 0; i < arguments.length; i++) this.children.push(arguments[i]); },
			  };
			}
			var document = { createElement: makeEl };
			function text(n) { return typeof n === 'string' ? n : n.textContent + n.children.map(text).join(''); }
			""");
		string script = ThePage.Asset("app.js").ReplaceLineEndings("\n");
		js.Execute(ThePage.Function(script, "el"));
		js.Execute(ThePage.Function(script, "acceleratorNotes"));
		return js.Evaluate($"acceleratorNotes({accelerators}, {(dev ? "true" : "false")}).map(text).join('|')").AsString();
	}
}
