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
/// The manor's accelerators (the Steward's kit: kit\spec\ACCELERATORS.md), as Heiward takes part: the ids every program names them by,
/// a card's lock and line beside the NPU's, Heiward's own failure markers, the checks of a card, and Auto's choice without
/// a working NPU. Every file goes to a scratch folder (NPU_AGENT_NPU_LOCK, and the markers' folder), never the real locks.
/// </summary>
[Collection("NpuLock")] // the lock location is a process-wide environment variable
public sealed class AcceleratorsTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "hei-accel-" + Guid.NewGuid().ToString("N"));
	readonly string npuLock;

	public AcceleratorsTests() {
		npuLock = Path.Combine(root, "locks", "npu");
		Environment.SetEnvironmentVariable("NPU_AGENT_NPU_LOCK", npuLock);
		GpuChecks.PathOverride = Path.Combine(root, "gpu-checks.json");
		NpuChecks.PathOverride = Path.Combine(root, "npu-checks.json");
		Accelerators.FolderOverride = Own;
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("NPU_AGENT_NPU_LOCK", null);
		GpuChecks.PathOverride = null;
		NpuChecks.PathOverride = null;
		Accelerators.FolderOverride = null;
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

	/// <summary>
	/// The shared cases for each accelerator's lock folders and line (the kit's spec\npu-queue-vectors.json, "slots"). Heiward
	/// knows nothing of slots and takes the first only: its folder and line must be the ones every other program uses, and
	/// the other slots' folders are the lock's, never an accelerator Heiward would name.
	/// </summary>
	[Fact]
	public void AnAcceleratorsFirstSlotAndLine_AreTheSharedOnes() {
		if (!OperatingSystem.IsWindows()) return;
		string locks = Path.Combine(root, "locks");
		JsonElement[] cases = KitSpec.QueueVectors.GetProperty("slots").EnumerateArray().ToArray();
		Assert.NotEmpty(cases);
		foreach (JsonElement c in cases) {
			string what = c.GetProperty("case").GetString()!, id = c.GetProperty("id").GetString()!;
			string[] folders = c.GetProperty("folders").EnumerateArray().Select(f => f.GetString()!).ToArray();
			Assert.True(Accelerators.IsId(id), what);
			string first = NpuLock.LockDirectoryFor(id)!;
			Assert.Equal(Path.Combine(locks, folders[0]), first);
			Assert.Equal(Path.Combine(locks, c.GetProperty("queue").GetString()!), NpuLock.QueueDirectoryFor(first));
			foreach (string other in folders.Skip(1)) Assert.False(Accelerators.IsId(other), $"{what}: {other}");
		}
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
			// The second waits in the card's own line, with a ticket the others can read. The line shows a ticket the
			// moment it is created, before its writer has closed it: wait until it reads whole.
			string ticket = "";
			string? who = null;
			Until(() => Directory.Exists(line) && Directory.GetFiles(line, "*.ticket") is [var t] && (who = WhoOf(ticket = t)) != null);
			Assert.Matches(@"^1-\d{17}-\d+-[0-9a-f]{8}\.ticket$", Path.GetFileName(ticket));
			Assert.Equal("heiward", who);
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

	/// <summary>
	/// A ticket's "who", read as the other programs in line read it (sharing write and delete with its writer); null while
	/// it is still being written. File.ReadAllText shares only reading, so it failed on a ticket its writer still had open.
	/// </summary>
	static string? WhoOf(string ticket) {
		try {
			using var file = new FileStream(ticket, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var doc = JsonDocument.Parse(file);
			return doc.RootElement.GetProperty("who").GetString();
		}
		catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { return null; }
	}

	static void Until(Func<bool> check) {
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (!check()) {
			Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "timed out waiting for the test condition");
			Thread.Sleep(20);
		}
	}

	// ---------------------------------------------------------------- failure markers

	/// <summary>Where the manor's shared markers would be with this NPU_AGENT_NPU_LOCK: beside the locks.</summary>
	string Shared => Path.Combine(root, "accelerators");
	/// <summary>Heiward's own markers here (in use, its {ai}\accelerators).</summary>
	string Own => Path.Combine(root, "ai", "accelerators");

	[Fact]
	public void TheMarkers_AreHeiwardsOwn_BesideItsAiComponents_NotBesideTheLocks() {
		Accelerators.FolderOverride = null;
		try {
			// Wherever NPU_AGENT_NPU_LOCK puts the shared locks, Heiward's markers stay in its own {ai}\accelerators.
			Assert.Equal(Path.Combine(AiComponents.AiFolder, "accelerators"), Accelerators.Folder);
			Assert.NotEqual(Shared, Accelerators.Folder);
		}
		finally { Accelerators.FolderOverride = Own; }
	}

	[Fact]
	public void AnotherProgramsSharedMarker_IsNeitherRead_NorWritten_NorDeleted() {
		// Reeve's marker in the shared folder beside the locks: about its model server (GenieX), not Heiward's runtime.
		Directory.CreateDirectory(Shared);
		string reeves = Path.Combine(Shared, "npu.failed.json");
		string text = $$"""{ "since": "{{DateTime.UtcNow.AddMinutes(-3):yyyy-MM-ddTHH:mm:ss.fffZ}}", "reason": "GenieX did not start within 30 s", "by": "reeve" }""";
		File.WriteAllText(reeves, text);
		Assert.Null(Accelerators.FailureOf("npu"));
		Assert.Equal(AiDevice.Npu, AcceleratorPlan.ChooseAuto(true, Accelerators.FailureOf("npu"), true, Card(), true, null).Device);

		Accelerators.MarkFailed("npu", "the Qualcomm Hexagon NPU pack failed to load (LoadLibrary failed)");
		Accelerators.Succeeded("npu");
		Assert.Equal(text, File.ReadAllText(reeves)); // untouched either way
		Assert.Equal(new[] { "npu.failed.json" }, Directory.GetFiles(Shared).Select(f => Path.GetFileName(f)));
	}

	[Fact]
	public void AFailure_IsWrittenWhole_SkippedForTenMinutes_AndClearedByASuccess() {
		DateTime now = DateTime.UtcNow;
		Accelerators.MarkFailed("gpu-x", "DirectML could not open the model: 0x887A0005\n   at Microsoft.ML.OnnxRuntime...", now);
		string file = Path.Combine(Own, "gpu-x.failed.json");
		Assert.True(File.Exists(file));
		Assert.Empty(Directory.GetFiles(Own, "*.tmp")); // written aside, then renamed
		Assert.False(Directory.Exists(Shared)); // nothing in the manor's shared folder
		using (var doc = JsonDocument.Parse(File.ReadAllText(file))) {
			JsonElement m = doc.RootElement;
			Assert.Equal(new[] { "since", "reason", "by" }, m.EnumerateObject().Select(p => p.Name));
			Assert.Equal(now, DateTime.Parse(m.GetProperty("since").GetString()!, null, System.Globalization.DateTimeStyles.AdjustToUniversal), TimeSpan.FromMilliseconds(1));
			Assert.EndsWith("Z", m.GetProperty("since").GetString());
			Assert.Equal("DirectML could not open the model: 0x887A0005", m.GetProperty("reason").GetString()); // one line
			Assert.Equal("heiward", m.GetProperty("by").GetString());
		}

		Assert.NotNull(Accelerators.FailureOf("gpu-x", now.AddMinutes(9.9)));
		Assert.Null(Accelerators.FailureOf("gpu-x", now.AddMinutes(10))); // exactly 10 minutes old: expired, tried again
		Assert.Null(Accelerators.FailureOf("gpu-y", now));

		Accelerators.Succeeded("gpu-x");
		Assert.False(File.Exists(file));
		Assert.Null(Accelerators.FailureOf("gpu-x", now));
	}

	[Fact]
	public void AMarkerInTheSharedFormat_ReadsBack() {
		Directory.CreateDirectory(Own);
		File.WriteAllText(Path.Combine(Own, "npu.failed.json"),
			$$"""{ "since": "{{DateTime.UtcNow.AddMinutes(-3):yyyy-MM-ddTHH:mm:ss.fffZ}}", "reason": "the Qualcomm Hexagon NPU pack failed to load (LoadLibrary failed)", "by": "heiward" }""");
		Accelerators.Failure f = Accelerators.FailureOf("npu")!;
		Assert.Equal("heiward", f.By);
		Assert.Equal("the Qualcomm Hexagon NPU pack failed to load (LoadLibrary failed)", f.Reason);
		Assert.InRange(f.UntilUtc - DateTime.UtcNow, TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(8));
	}

	[Theory]
	[InlineData("{ not json")]
	[InlineData("""{ "since": "yesterday-ish", "reason": "x" }""")]
	[InlineData("""{ "reason": "no since" }""")]
	[InlineData("""{ "since": 12345, "reason": "x" }""")]
	[InlineData("[]")]
	public void AnUnreadableMarker_CountsAsAbsent(string contents) {
		Directory.CreateDirectory(Own);
		File.WriteAllText(Path.Combine(Own, "gpu-x.failed.json"), contents);
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

	static readonly Accelerators.Failure Failed = new(DateTime.UtcNow.AddMinutes(-2), "the Qualcomm Hexagon NPU pack failed to load (LoadLibrary failed)", "heiward");

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
	public void Auto_SkipsACardThatFailedForHeiward_AndSaysWhy() {
		AcceleratorPlan plan = AcceleratorPlan.ChooseAuto(npu: false, null, gpuPack: true, Card(), cardChecked: true,
			new Accelerators.Failure(DateTime.UtcNow, "DirectML could not open the model on the NVIDIA GeForce RTX 4090: 0x887A0005", "heiward"));
		Assert.Equal(AiDevice.Cpu, plan.Device);
		Assert.Equal("GPU", plan.FellBackFrom);
		Assert.StartsWith("DirectML could not open the model on the NVIDIA GeForce RTX 4090: 0x887A0005, at ", plan.Why);
		Assert.Contains("(tried again from ", plan.Why);
	}

	[Fact]
	public void Auto_WithTheNpuFailedForHeiward_FallsBackToACheckedCard_ElseTheCpu() {
		AcceleratorPlan gpu = AcceleratorPlan.ChooseAuto(npu: true, Failed, gpuPack: true, Card(), cardChecked: true, null);
		Assert.Equal(AiDevice.Gpu, gpu.Device);
		Assert.Equal("NPU", gpu.FellBackFrom);
		Assert.StartsWith("the Qualcomm Hexagon NPU pack failed to load (LoadLibrary failed), at ", gpu.Why); // Heiward's own failure, named

		AcceleratorPlan cpu = AcceleratorPlan.ChooseAuto(npu: true, Failed, gpuPack: true, Card(), cardChecked: false, null);
		Assert.Equal(AiDevice.Cpu, cpu.Device);
		Assert.Equal("NPU", cpu.FellBackFrom);
	}

	[Fact]
	public void Auto_ReadsHeiwardsMarkersAndChecks_ThroughItsFiles() {
		// The whole path, on files: a card checked, then failing for Heiward, is skipped until a success clears it.
		GpuAdapter card = Card();
		GpuChecks.Record(card, passed: true);
		Assert.True(GpuChecks.Passed(card));
		Accelerators.MarkFailed(card.AcceleratorId, "the NVIDIA GeForce RTX 4090 failed during a scan: 0x887A0006");
		Assert.Equal(AiDevice.Cpu, AcceleratorPlan.ChooseAuto(false, Accelerators.FailureOf("npu"), true, card, GpuChecks.Passed(card), Accelerators.FailureOf(card.AcceleratorId)).Device);
		Accelerators.Succeeded(card.AcceleratorId);
		Assert.Equal(AiDevice.Gpu, AcceleratorPlan.ChooseAuto(false, Accelerators.FailureOf("npu"), true, card, GpuChecks.Passed(card), Accelerators.FailureOf(card.AcceleratorId)).Device);
	}

	// ---------------------------------------------------------------- the NPU's check

	const string Krackan = "NPU Compute Accelerator Device", KrackanDriver = "32.0.20102.3930", VitisAi = "VitisAIExecutionProvider 1.8.75.0";

	[Fact]
	public void TheNpusCheck_HoldsForItsDriverAndPluginOnly() {
		DateTime now = DateTime.UtcNow;
		Assert.Null(NpuChecks.Find(Krackan, KrackanDriver, VitisAi));
		Assert.True(NpuChecks.NeedsCheck(null, now));

		NpuChecks.Record(Krackan, KrackanDriver, VitisAi, passed: true, nowUtc: now);
		NpuChecks.Check passed = NpuChecks.Find(Krackan, KrackanDriver, VitisAi)!;
		Assert.True(passed.Passed);
		Assert.Null(passed.Reason);
		Assert.False(NpuChecks.NeedsCheck(passed, now.AddDays(30)));
		Assert.Null(NpuChecks.FailureOf(passed, now));
		// A new driver, or Windows updating the plugin: no check for it yet.
		Assert.Null(NpuChecks.Find(Krackan, "32.0.20103.1000", VitisAi));
		Assert.Null(NpuChecks.Find(Krackan, KrackanDriver, "VitisAIExecutionProvider 1.8.80.0"));
	}

	[Fact]
	public void ACheckThatCrashed_LeavesTheNpuForADay_AndSaysWhy() {
		DateTime now = DateTime.UtcNow;
		NpuChecks.Record(Krackan, KrackanDriver, VitisAi, passed: false,
			"setting the model up ended its process (exit 0xC0000409) from the AMD Ryzen AI NPU's plugin\nmore", now);
		NpuChecks.Check crashed = NpuChecks.Find(Krackan, KrackanDriver, VitisAi)!;
		Assert.False(crashed.Passed);
		Assert.Equal("setting the model up ended its process (exit 0xC0000409) from the AMD Ryzen AI NPU's plugin", crashed.Reason);

		Assert.False(NpuChecks.NeedsCheck(crashed, now.AddHours(23)));
		Accelerators.Failure failure = NpuChecks.FailureOf(crashed, now.AddHours(23))!;
		Assert.StartsWith("it failed its check: setting the model up ended its process", failure.Reason);
		Assert.Equal(crashed.AtUtc + NpuChecks.RetryFailedAfter, failure.UntilUtc); // a day, not the markers' 10 minutes
		AcceleratorPlan plan = AcceleratorPlan.ChooseAuto(npu: true, failure, gpuPack: false, card: null, cardChecked: false, null);
		Assert.EndsWith($"(tried again from {failure.UntilUtc.ToLocalTime():ddd HH:mm})", plan.Why); // tomorrow's time says its day

		Assert.True(NpuChecks.NeedsCheck(crashed, now.AddDays(1).AddMinutes(1)));
		Assert.Null(NpuChecks.FailureOf(crashed, now.AddDays(1).AddMinutes(1)));
	}

	[Fact]
	public void Auto_WithTheNpusCheckFailed_FallsBackToACheckedCard_ElseTheCpu() {
		DateTime now = DateTime.UtcNow;
		NpuChecks.Record(Krackan, KrackanDriver, VitisAi, passed: false, "setting the model up ended its process (exit 0xC0000409)", now);
		Accelerators.Failure failed = NpuChecks.FailureOf(NpuChecks.Find(Krackan, KrackanDriver, VitisAi), now)!;

		AcceleratorPlan gpu = AcceleratorPlan.ChooseAuto(npu: true, failed, gpuPack: true, Card(), cardChecked: true, null);
		Assert.Equal(AiDevice.Gpu, gpu.Device);
		Assert.Equal("NPU", gpu.FellBackFrom);
		Assert.StartsWith("it failed its check: setting the model up ended its process (exit 0xC0000409), at ", gpu.Why);

		AcceleratorPlan cpu = AcceleratorPlan.ChooseAuto(npu: true, failed, gpuPack: false, null, cardChecked: false, null);
		Assert.Equal(AiDevice.Cpu, cpu.Device);
		Assert.Equal("NPU", cpu.FellBackFrom);
	}

	[Fact]
	public void TheNpusLock_LeftByAProcessThatEnded_IsLetGo_ButNotALiveOnesNorAnothers() {
		if (!OperatingSystem.IsWindows()) return;
		// A pid that is not running (pids are multiples of 4 on Windows; this one is odd).
		int dead = int.MaxValue - 2;
		void HeldBy(int pid) {
			Directory.CreateDirectory(npuLock);
			File.WriteAllText(Path.Combine(npuLock, "owner.json"), $$"""{"pid":{{pid}},"since":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}""");
		}

		HeldBy(dead);
		Assert.False(NpuLock.ReleaseIfHeldBy(Accelerators.Npu, dead - 2)); // someone else's
		Assert.True(Directory.Exists(npuLock));
		Assert.True(NpuLock.ReleaseIfHeldBy(Accelerators.Npu, dead));
		Assert.False(Directory.Exists(npuLock));
		Assert.False(NpuLock.ReleaseIfHeldBy(Accelerators.Npu, dead)); // nothing left to let go

		HeldBy(Environment.ProcessId); // a live holder keeps it
		Assert.False(NpuLock.ReleaseIfHeldBy(Accelerators.Npu, Environment.ProcessId));
		Assert.True(Directory.Exists(npuLock));
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
