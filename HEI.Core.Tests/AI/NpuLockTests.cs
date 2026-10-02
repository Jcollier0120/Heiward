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

namespace HEI.Core.Tests.AI;

/// <summary>
/// The NPU lock must stay wire-compatible with the other NPU tools that use the same lock: same
/// directory protocol, same owner.json fields, same stale rules. Windows only (NPU tools are).
/// </summary>
[Collection("NpuLock")] // the lock location is a process-wide environment variable
public sealed class NpuLockTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-npulock-" + Guid.NewGuid().ToString("N"), "locks", "npu");

	public NpuLockTests() => Environment.SetEnvironmentVariable("NPU_AGENT_NPU_LOCK", dir);

	public void Dispose() {
		Environment.SetEnvironmentVariable("NPU_AGENT_NPU_LOCK", null);
		try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, true); } catch { }
	}

	[Fact]
	public void WritesTheOwnerFileNpuAgentReads_AndRemovesItOnRelease() {
		if (!OperatingSystem.IsWindows()) return;
		using (NpuLock.Acquire()) {
			using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "owner.json")));
			Assert.Equal(Environment.ProcessId, doc.RootElement.GetProperty("pid").GetInt32());
			long since = doc.RootElement.GetProperty("since").GetInt64(); // milliseconds since the epoch, like Date.now()
			Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - since, 0, 60_000);
		}
		Assert.False(Directory.Exists(dir));
	}

	[Fact]
	public void WaitsForALiveHolder_ThenTimesOut() {
		if (!OperatingSystem.IsWindows()) return;
		using (NpuLock.Acquire())
			Assert.Throws<TimeoutException>(() => NpuLock.Acquire(TimeSpan.FromMilliseconds(400)));
	}

	[Fact]
	public void EvictsAHolderThatDied() {
		if (!OperatingSystem.IsWindows()) return;
		Directory.CreateDirectory(dir);
		// A pid that is not running (pids are multiples of 4 on Windows; this one is odd).
		File.WriteAllText(Path.Combine(dir, "owner.json"), $$"""{"pid":{{int.MaxValue - 2}},"since":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}""");
		using (NpuLock.Acquire(TimeSpan.FromSeconds(5))) {
			using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "owner.json")));
			Assert.Equal(Environment.ProcessId, doc.RootElement.GetProperty("pid").GetInt32());
		}
	}

	// ---------------------------------------------------------------- the NPU queue

	/// <summary>The cases every implementation of the queue runs unchanged (a copy of the shared npu-queue-vectors.json).</summary>
	static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "npu-queue-vectors.json"))).RootElement;

	[Fact]
	public void OrdersTheLineLikeEveryOtherImplementation() {
		long nowUs = Vectors.GetProperty("nowUs").GetInt64();
		foreach (JsonElement c in Vectors.GetProperty("order").EnumerateArray()) {
			var tickets = c.GetProperty("tickets").EnumerateArray().Select(t => NpuLock.ParseTicket(t.GetString()!)).OfType<NpuLock.Ticket>().ToList();
			tickets.Sort((a, b) => NpuLock.CompareTickets(a, b, nowUs));
			Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()), tickets.Select(t => t.Name));
		}
	}

	[Fact]
	public void JudgesHeartbeatsLikeEveryOtherImplementation() {
		foreach (JsonElement d in Vectors.GetProperty("dead").EnumerateArray()) {
			bool alive = d.GetProperty("pidAlive").GetBoolean();
			Assert.True(d.GetProperty("dead").GetBoolean() == NpuLock.IsDeadTicket(TimeSpan.FromMilliseconds(d.GetProperty("ageMs").GetInt32()), () => alive), d.GetProperty("case").GetString());
		}
	}

	string QueueDir => NpuLock.QueueDirectoryFor(dir);
	string[] Tickets() => Directory.Exists(QueueDir) ? Directory.GetFiles(QueueDir, "*.ticket") : [];

	void HoldAsAnotherTool() {
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "owner.json"), $$"""{"pid":{{Environment.ProcessId}},"since":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}""");
	}

	/// <summary>Removes a folder the way a well-behaved tool does: retrying while someone has a file in it open for a moment.</summary>
	static void DeleteRetrying(string path) {
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (true) {
			try {
				Directory.Delete(path, recursive: true);
				return;
			}
			catch (IOException) when (sw.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(20); }
		}
	}

	static void Until(Func<bool> check) {
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (!check()) {
			Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "timed out waiting for the test condition");
			Thread.Sleep(20);
		}
	}

	/// <summary>
	/// Waiters on their own threads. An exception is recorded rather than thrown (a thread's exception
	/// would end the whole test run), and <see cref="Dispose"/> joins them before the test's folder is
	/// deleted, so a failed test never pulls the folder out from under a waiter.
	/// </summary>
	sealed class Waiters : IDisposable {
		readonly List<Thread> threads = [];
		public readonly System.Collections.Concurrent.ConcurrentQueue<string> Order = new();
		public readonly System.Collections.Concurrent.ConcurrentQueue<Exception> Errors = new();

		public int Count => threads.Count;

		public void Start(string label, bool interactive) {
			var t = new Thread(() => {
				try { using (NpuLock.Acquire(TimeSpan.FromSeconds(20), interactive)) Order.Enqueue(label); }
				catch (Exception e) { Errors.Enqueue(e); }
			});
			t.Start();
			threads.Add(t);
		}

		public bool JoinAll() => threads.All(t => t.Join(TimeSpan.FromSeconds(25)));

		public void Dispose() => JoinAll();
	}

	[Fact]
	public void ServesWaitersInLineOrder_APersonFirst() {
		if (!OperatingSystem.IsWindows()) return;
		HoldAsAnotherTool();
		using var waiters = new Waiters();
		foreach (var (label, interactive) in new[] { ("a", false), ("b", false), ("c", true) }) {
			waiters.Start(label, interactive);
			int expected = waiters.Count;
			Until(() => Tickets().Length == expected);
		}
		DeleteRetrying(dir); // the other tool lets go
		Assert.True(waiters.JoinAll());
		Assert.Empty(waiters.Errors);
		Assert.Equal(["c", "a", "b"], waiters.Order);
		Assert.Empty(Tickets());
		Assert.False(Directory.Exists(dir));
	}

	[Fact]
	public void AReaderNeverBlocksTheRelease() {
		if (!OperatingSystem.IsWindows()) return;
		HoldAsAnotherTool();
		// A waiter mid-read (the head of the line reads owner.json 20 times a second) while the holder lets go.
		using (NpuLock.OpenShared(Path.Combine(dir, "owner.json")))
			Directory.Delete(dir, recursive: true);
		Assert.False(Directory.Exists(dir));
	}

	[Fact]
	public void ReleaseWaitsOutAReaderThatDoesNotShareDelete() {
		if (!OperatingSystem.IsWindows()) return;
		IDisposable held = NpuLock.Acquire();
		// Another tool reads owner.json the plain way (Python's open() shares read and write, not delete).
		var reader = new FileStream(Path.Combine(dir, "owner.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		var closes = new Thread(() => {
			Thread.Sleep(300);
			reader.Dispose();
		});
		closes.Start();
		held.Dispose();
		closes.Join();
		Assert.False(Directory.Exists(dir), "the release gave up and left a live process holding the NPU");
	}

	[Fact]
	public void KeepsItsPlaceWhenTheQueueFolderIsRemoved() {
		if (!OperatingSystem.IsWindows()) return;
		HoldAsAnotherTool();
		using var waiters = new Waiters();
		waiters.Start("a", false);
		Until(() => Tickets().Length == 1);
		DeleteRetrying(QueueDir);
		Until(() => Tickets().Length == 1); // back in line, not crashed
		DeleteRetrying(dir);
		Assert.True(waiters.JoinAll());
		Assert.Empty(waiters.Errors);
		Assert.Equal(["a"], waiters.Order);
	}

	[Fact]
	public void ClearsADeadWaiterFromTheLine() {
		if (!OperatingSystem.IsWindows()) return;
		Directory.CreateDirectory(QueueDir);
		long aMinuteAgoUs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds() * 1000;
		string ghost = Path.Combine(QueueDir, $"1-{aMinuteAgoUs:D17}-{int.MaxValue - 2}-deadbeef.ticket");
		File.WriteAllText(ghost, "{}");
		File.SetLastWriteTimeUtc(ghost, DateTime.UtcNow.AddSeconds(-6));
		using (NpuLock.Acquire(TimeSpan.FromSeconds(5))) { }
		Assert.False(File.Exists(ghost));
	}

	[Fact]
	public void LeavesTheLineWhenItGivesUp() {
		if (!OperatingSystem.IsWindows()) return;
		HoldAsAnotherTool();
		Assert.Throws<TimeoutException>(() => NpuLock.Acquire(TimeSpan.FromMilliseconds(300)));
		Assert.Empty(Tickets());
	}

	[Fact]
	public void EvictsAHolderThatOverstayedTenMinutes() {
		if (!OperatingSystem.IsWindows()) return;
		Directory.CreateDirectory(dir);
		long elevenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-11).ToUnixTimeMilliseconds();
		File.WriteAllText(Path.Combine(dir, "owner.json"), $$"""{"pid":{{Environment.ProcessId}},"since":{{elevenMinutesAgo}}}""");
		using (NpuLock.Acquire(TimeSpan.FromSeconds(5))) { }
		Assert.False(Directory.Exists(dir));
	}
}
