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

namespace HEI.Core.Tests.AI;

/// <summary>
/// The manor's accelerators (Manor's docs/ACCELERATORS.md), as Heiward takes part: the ids every program names them by,
/// a card's lock and line beside the NPU's, the shared failure markers, the checks of a card, and Auto's choice without
/// a working NPU. Every file goes to a scratch folder (NPU_AGENT_NPU_LOCK), never the real locks.
/// </summary>
[Collection("NpuLock")] // the lock location is a process-wide environment variable
public sealed class AcceleratorsTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "hei-accel-" + Guid.NewGuid().ToString("N"));
	readonly string npuLock;

	public AcceleratorsTests() {
		npuLock = Path.Combine(root, "locks", "npu");
		Environment.SetEnvironmentVariable("NPU_AGENT_NPU_LOCK", npuLock);
		GpuChecks.PathOverride = Path.Combine(root, "gpu-checks.json");
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("NPU_AGENT_NPU_LOCK", null);
		GpuChecks.PathOverride = null;
		try { Directory.Delete(root, true); } catch { }
	}

	// ---------------------------------------------------------------- ids

	[Theory]
	[InlineData("NVIDIA GeForce RTX 4090", "gpu-nvidia-geforce-rtx-4090")]
	[InlineData("NVIDIA GeForce RTX 4090 #2", "gpu-nvidia-geforce-rtx-4090-2")]
	[InlineData("Qualcomm(R) Adreno(TM) X2-90 GPU", "gpu-qualcomm-r-adreno-tm-x2-90-gpu")]
	[InlineData("AMD Radeon(TM) 890M Graphics", "gpu-amd-radeon-tm-890m-graphics")]
	[InlineData("  Intel(R) Arc(TM) A770  ", "gpu-intel-r-arc-tm-a770")]
	[InlineData("", "gpu-graphics-card")]
	[InlineData("(™)", "gpu-graphics-card")]
	public void ACardsId_IsItsNameSlugged_AsTheOthersDoIt(string name, string id) {
		Assert.Equal(id, Accelerators.GpuId(name));
		Assert.True(Accelerators.IsId(id));
	}

	[Fact]
	public void ASecondCardOfTheSameName_GetsItsOwnId() {
		IReadOnlyList<GpuAdapter> cards = GpuAdapters.Keyed(new DxgiAdapter[] {
			new(0, "NVIDIA GeForce RTX 4090", 24UL << 30, 0x10DE, false, "0x00000000_0x0000c6b1", "32.0.15.6094"),
			new(1, "NVIDIA GeForce RTX 4090", 24UL << 30, 0x10DE, false, "0x00000000_0x0000d1f1", "32.0.15.6094"),
		});
		Assert.Equal(new[] { "gpu-nvidia-geforce-rtx-4090", "gpu-nvidia-geforce-rtx-4090-2" }, cards.Select(c => c.AcceleratorId));
		Assert.Equal("0x00000000_0x0000d1f1", cards[1].Luid);
		Assert.Equal("32.0.15.6094", cards[1].Driver);
	}

	[Theory]
	[InlineData("npu", true)]
	[InlineData("cpu", true)]
	[InlineData("gpu-nvidia-geforce-rtx-4090-2", true)]
	[InlineData("gpu-", false)]
	[InlineData("gpu-Upper", false)]
	[InlineData("gpu-a--b", false)]
	[InlineData("..\\npu", false)]
	[InlineData("npu.2", false)] // a second slot is a folder of the lock, not an accelerator
	public void OnlyAnAcceleratorId_NamesALockOrAMarker(string id, bool valid) => Assert.Equal(valid, Accelerators.IsId(id));

	[Fact]
	public void ALuidAndADriver_ReadAsWindowsShowsThem() {
		Assert.Equal("0x00000000_0x000137d9", GpuAdapters.LuidText(0, 0x137D9));
		Assert.Equal("0x00000001_0x80000000", GpuAdapters.LuidText(1, 0x80000000));
		Assert.Equal("32.0.101.6127", GpuAdapters.DriverText((32L << 48) | (0L << 32) | (101L << 16) | 6127));
	}

	// ---------------------------------------------------------------- the lock and the line

	[Fact]
	public void ACardsLock_IsAFolderBesideTheNpus_AndItsLineBesideIt() {
		if (!OperatingSystem.IsWindows()) return;
		Assert.Equal(npuLock, NpuLock.LockDirectoryFor("npu"));
		Assert.Equal(Path.Combine(root, "locks", "gpu-x"), NpuLock.LockDirectoryFor("gpu-x"));
		Assert.Throws<ArgumentException>(() => NpuLock.LockDirectoryFor("..\\elsewhere"));
	}

	[Fact]
	public void TwoHolders_TakeTurnsOnACard_AndTheNpuStaysFree() {
		if (!OperatingSystem.IsWindows()) return;
		string card = NpuLock.LockDirectoryFor("gpu-x")!;
		string line = card + ".queue";
		var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
		Exception? error = null;
		Thread second;
		using (NpuLock.Acquire("gpu-x")) {
			order.Enqueue("first");
			// The card's lock is its own folder, holding owner.json as the NPU's does.
			using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(card, "owner.json"))))
				Assert.Equal(Environment.ProcessId, doc.RootElement.GetProperty("pid").GetInt32());
			// The NPU's lock is untouched, and free: a card's holder doesn't hold the NPU.
			Assert.False(Directory.Exists(npuLock));
			using (NpuLock.Acquire(TimeSpan.FromSeconds(5))) Assert.True(Directory.Exists(npuLock));
			Assert.False(Directory.Exists(npuLock));

			second = new Thread(() => {
				try { using (NpuLock.Acquire("gpu-x", TimeSpan.FromSeconds(20))) order.Enqueue("second"); }
				catch (Exception e) { error = e; }
			});
			second.Start();
			// The second waits in the card's own line, with a ticket the others can read.
			Until(() => Directory.Exists(line) && Directory.GetFiles(line, "*.ticket").Length == 1);
			string ticket = Directory.GetFiles(line, "*.ticket")[0];
			Assert.Matches(@"^1-\d{17}-\d+-[0-9a-f]{8}\.ticket$", Path.GetFileName(ticket));
			using (var doc = JsonDocument.Parse(File.ReadAllText(ticket)))
				Assert.Equal("heiward", doc.RootElement.GetProperty("who").GetString());
			Thread.Sleep(300);
			Assert.Single(order); // still waiting
		}
		Assert.True(second.Join(TimeSpan.FromSeconds(20)));
		Assert.Null(error);
		Assert.Equal(["first", "second"], order);
		Assert.False(Directory.Exists(card));
		Assert.Empty(Directory.GetFiles(line, "*.ticket"));
	}

	[Fact]
	public void ACardHeldByAnotherProgram_IsWaitedFor() {
		if (!OperatingSystem.IsWindows()) return;
		string card = NpuLock.LockDirectoryFor("gpu-nvidia-geforce-rtx-4090")!;
		Directory.CreateDirectory(card);
		File.WriteAllText(Path.Combine(card, "owner.json"), $$"""{"pid":{{Environment.ProcessId}},"since":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}""");
		Assert.Throws<TimeoutException>(() => NpuLock.Acquire("gpu-nvidia-geforce-rtx-4090", TimeSpan.FromMilliseconds(300)));
		Assert.Empty(Directory.GetFiles(card + ".queue", "*.ticket")); // it left the line when it gave up
		Assert.False(Directory.Exists(npuLock));
	}

	static void Until(Func<bool> check) {
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (!check()) {
			Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "timed out waiting for the test condition");
			Thread.Sleep(20);
		}
	}

	// ---------------------------------------------------------------- failure markers

	string Shared => Path.Combine(root, "accelerators");

	[Fact]
	public void TheSharedFolder_IsBesideTheLocks_MovedWithTheNpuLock() =>
		Assert.Equal(Shared, Accelerators.Folder);

	[Fact]
	public void AFailure_IsWrittenWhole_SkippedForTenMinutes_AndClearedByASuccess() {
		DateTime now = DateTime.UtcNow;
		Accelerators.MarkFailed("gpu-x", "DirectML could not open the model: 0x887A0005\n   at Microsoft.ML.OnnxRuntime...", now);
		string file = Path.Combine(Shared, "gpu-x.failed.json");
		Assert.True(File.Exists(file));
		Assert.Empty(Directory.GetFiles(Shared, "*.tmp")); // written aside, then renamed
		using (var doc = JsonDocument.Parse(File.ReadAllText(file))) {
			JsonElement m = doc.RootElement;
			Assert.Equal(new[] { "since", "reason", "by" }, m.EnumerateObject().Select(p => p.Name));
			Assert.Equal(now, DateTime.Parse(m.GetProperty("since").GetString()!, null, System.Globalization.DateTimeStyles.AdjustToUniversal), TimeSpan.FromMilliseconds(1));
			Assert.EndsWith("Z", m.GetProperty("since").GetString());
			Assert.Equal("DirectML could not open the model: 0x887A0005", m.GetProperty("reason").GetString()); // one line
			Assert.Equal("heiward", m.GetProperty("by").GetString());
		}

		Assert.NotNull(Accelerators.FailureOf("gpu-x", now.AddMinutes(9.9)));
		Assert.Null(Accelerators.FailureOf("gpu-x", now.AddMinutes(10))); // tried again after 10 minutes
		Assert.Null(Accelerators.FailureOf("gpu-y", now));

		Accelerators.Succeeded("gpu-x");
		Assert.False(File.Exists(file));
		Assert.Null(Accelerators.FailureOf("gpu-x", now));
	}

	[Fact]
	public void AnotherProgramsMarker_Counts() {
		Directory.CreateDirectory(Shared);
		File.WriteAllText(Path.Combine(Shared, "npu.failed.json"),
			$$"""{ "since": "{{DateTime.UtcNow.AddMinutes(-3):yyyy-MM-ddTHH:mm:ss.fffZ}}", "reason": "GenieX did not start within 30 s", "by": "reeve" }""");
		Accelerators.Failure f = Accelerators.FailureOf("npu")!;
		Assert.Equal("reeve", f.By);
		Assert.Equal("GenieX did not start within 30 s", f.Reason);
		Assert.InRange(f.UntilUtc - DateTime.UtcNow, TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(8));
	}

	[Theory]
	[InlineData("{ not json")]
	[InlineData("""{ "since": "yesterday-ish", "reason": "x" }""")]
	[InlineData("""{ "reason": "no since" }""")]
	[InlineData("""{ "since": 12345, "reason": "x" }""")]
	[InlineData("[]")]
	public void AnUnreadableMarker_CountsAsAbsent(string contents) {
		Directory.CreateDirectory(Shared);
		File.WriteAllText(Path.Combine(Shared, "gpu-x.failed.json"), contents);
		Assert.Null(Accelerators.FailureOf("gpu-x"));
	}

	// ---------------------------------------------------------------- checks of a card, and Auto

	static GpuAdapter Card(string name = "NVIDIA GeForce RTX 4090", string driver = "32.0.15.6094") =>
		new(0, name, name, 24UL << 30, 0x10DE, "0x00000000_0x0000c6b1", driver);

	[Fact]
	public void ACardsCheck_IsKeptWithItsDriver_AndAFailedOneIsTriedAgainADayLater() {
		DateTime now = DateTime.UtcNow;
		GpuAdapter card = Card();
		Assert.True(GpuChecks.NeedsCheck(card, now));
		Assert.False(GpuChecks.Passed(card));

		GpuChecks.Record(card, passed: true, nowUtc: now);
		Assert.True(GpuChecks.Passed(card));
		Assert.False(GpuChecks.NeedsCheck(card, now.AddDays(30)));
		// A new driver: checked again.
		Assert.False(GpuChecks.Passed(Card(driver: "32.0.15.7000")));
		Assert.True(GpuChecks.NeedsCheck(Card(driver: "32.0.15.7000"), now));

		GpuChecks.Record(card, passed: false, "DirectML: device removed\nmore", now);
		Assert.False(GpuChecks.Passed(card));
		Assert.Equal("DirectML: device removed", GpuChecks.Find(card)!.Reason);
		Assert.False(GpuChecks.NeedsCheck(card, now.AddHours(23)));
		Assert.True(GpuChecks.NeedsCheck(card, now.AddDays(1)));

		// One entry per card: another card's check stays.
		GpuChecks.Record(Card("Intel(R) UHD Graphics 770"), passed: true, nowUtc: now);
		Assert.True(GpuChecks.Passed(Card("Intel(R) UHD Graphics 770")));
		Assert.NotNull(GpuChecks.Find(card));
	}

	static readonly Accelerators.Failure Failed = new(DateTime.UtcNow.AddMinutes(-2), "GenieX did not start within 30 s", "reeve");

	[Fact]
	public void Auto_WithAWorkingNpu_TakesIt() {
		AcceleratorPlan plan = AcceleratorPlan.ChooseAuto(npu: true, null, gpuPack: true, Card(), cardChecked: true, null);
		Assert.Equal(AiDevice.Npu, plan.Device);
		Assert.Null(plan.FellBackFrom);
	}

	[Fact]
	public void Auto_WithNoNpu_TakesACheckedCard_AsItsChoiceNotAFallback() {
		AcceleratorPlan plan = AcceleratorPlan.ChooseAuto(npu: false, null, gpuPack: true, Card(), cardChecked: true, null);
		Assert.Equal(AiDevice.Gpu, plan.Device);
		Assert.Equal("NVIDIA GeForce RTX 4090", plan.Card!.Key);
		Assert.Null(plan.FellBackFrom);
	}

	[Fact]
	public void Auto_WithNoNpu_LeavesAnUncheckedCard_OrNoPack_ToTheCpu() {
		Assert.Equal(AiDevice.Cpu, AcceleratorPlan.ChooseAuto(false, null, gpuPack: true, Card(), cardChecked: false, null).Device);
		Assert.Equal(AiDevice.Cpu, AcceleratorPlan.ChooseAuto(false, null, gpuPack: false, Card(), cardChecked: true, null).Device);
		Assert.Equal(AiDevice.Cpu, AcceleratorPlan.ChooseAuto(false, null, gpuPack: true, card: null, cardChecked: false, null).Device);
		Assert.Null(AcceleratorPlan.ChooseAuto(false, null, true, Card(), false, null).FellBackFrom); // nothing was meant for elsewhere
	}

	[Fact]
	public void Auto_SkipsACardMarkedFailed_AndSaysWhy() {
		AcceleratorPlan plan = AcceleratorPlan.ChooseAuto(npu: false, null, gpuPack: true, Card(), cardChecked: true,
			new Accelerators.Failure(DateTime.UtcNow, "DirectML could not open the model", "heiward"));
		Assert.Equal(AiDevice.Cpu, plan.Device);
		Assert.Equal("GPU", plan.FellBackFrom);
		Assert.Contains("the NVIDIA GeForce RTX 4090 was marked failed by heiward (DirectML could not open the model)", plan.Why);
	}

	[Fact]
	public void Auto_WithTheNpuMarkedFailed_FallsBackToACheckedCard_ElseTheCpu() {
		AcceleratorPlan gpu = AcceleratorPlan.ChooseAuto(npu: true, Failed, gpuPack: true, Card(), cardChecked: true, null);
		Assert.Equal(AiDevice.Gpu, gpu.Device);
		Assert.Equal("NPU", gpu.FellBackFrom);
		Assert.Contains("the NPU was marked failed by reeve (GenieX did not start within 30 s)", gpu.Why);

		AcceleratorPlan cpu = AcceleratorPlan.ChooseAuto(npu: true, Failed, gpuPack: true, Card(), cardChecked: false, null);
		Assert.Equal(AiDevice.Cpu, cpu.Device);
		Assert.Equal("NPU", cpu.FellBackFrom);
	}

	[Fact]
	public void Auto_ReadsTheMarkersAndChecks_ThroughTheSharedFiles() {
		// The whole path, on files: a card checked and then marked failed by another program is skipped.
		GpuAdapter card = Card();
		GpuChecks.Record(card, passed: true);
		Assert.True(GpuChecks.Passed(card));
		Accelerators.MarkFailed(card.AcceleratorId, "llama-server did not start within 30 s");
		Assert.Equal(AiDevice.Cpu, AcceleratorPlan.ChooseAuto(false, Accelerators.FailureOf("npu"), true, card, GpuChecks.Passed(card), Accelerators.FailureOf(card.AcceleratorId)).Device);
		Accelerators.Succeeded(card.AcceleratorId);
		Assert.Equal(AiDevice.Gpu, AcceleratorPlan.ChooseAuto(false, Accelerators.FailureOf("npu"), true, card, GpuChecks.Passed(card), Accelerators.FailureOf(card.AcceleratorId)).Device);
	}

	[Fact]
	public void TheCpuEmbedder_TakesNoLock_AndNamesTheCpu() {
		using var embedder = new OnnxEmbedder(HEI.TestSupport.TestModels.TinyEmbedderPath);
		embedder.EmbedBatch(new[] { OnnxEmbedderTests.PatternFrame(1) });
		Assert.Equal("cpu", embedder.AcceleratorId);
		Assert.Null(embedder.LockName);
		Assert.Null(embedder.Fallback);
		Assert.Equal(0, embedder.Stats.LockTurns);
		Assert.False(Directory.Exists(Path.Combine(root, "locks")));
	}
}
