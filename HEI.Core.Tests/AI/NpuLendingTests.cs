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

using System.Collections.Concurrent;
using System.Text.Json;
using HEI.Core.AI;

namespace HEI.Core.Tests.AI;

/// <summary>
/// The PC-wide lending record (the kit's LENDING.md): its shared cases, run unchanged (lending-vectors.json), and a
/// lending of this PC's NPU to another PC giving way to work that matters more, and going on after.
/// </summary>
[Collection("NpuLock")] // the lock's location, and so the record's, is a process-wide environment variable
public sealed class NpuLendingTests : IDisposable {
	static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(KitSpec.PathOf("lending-vectors.json"))).RootElement;

	readonly string home = Path.Combine(Path.GetTempPath(), "hei-lending-" + Guid.NewGuid().ToString("N"));
	string Record => Path.Combine(home, "lending");

	public NpuLendingTests() => Environment.SetEnvironmentVariable("NPU_AGENT_NPU_LOCK", Path.Combine(home, "locks", "npu"));

	public void Dispose() {
		Environment.SetEnvironmentVariable("NPU_AGENT_NPU_LOCK", null);
		try { Directory.Delete(home, true); } catch { }
	}

	static IEnumerable<JsonElement> Cases(string section) => Vectors.GetProperty(section).EnumerateArray();

	static string Case(JsonElement c) => c.GetProperty("case").GetString()!;

	[Fact]
	public void The_ranks_and_the_heartbeat_are_the_kits() {
		JsonElement ranks = Vectors.GetProperty("ranks");
		Assert.Equal(NpuLending.Live, ranks.GetProperty("live").GetInt32());
		Assert.Equal(NpuLending.Waited, ranks.GetProperty("waited").GetInt32());
		Assert.Equal(NpuLending.Background, ranks.GetProperty("background").GetInt32());
		Assert.Equal(Vectors.GetProperty("heartbeatMs").GetInt32(), (int)NpuLock.Heartbeat.TotalMilliseconds);
	}

	[Fact]
	public void Folders() {
		if (!OperatingSystem.IsWindows()) return; // the cases' paths are Windows'
		foreach (JsonElement c in Cases("folders"))
			Assert.True(c.GetProperty("lending").GetString() == NpuLending.FolderFor(c.GetProperty("lock").GetString()!), Case(c));
	}

	[Fact]
	public void Names() {
		foreach (JsonElement c in Cases("names")) {
			string name = c.GetProperty("name").GetString()!;
			NpuLending.Entry? got = NpuLending.Read(name);
			JsonElement want = c.GetProperty("read");
			if (want.ValueKind == JsonValueKind.Null) {
				Assert.True(got == null, name);
				continue;
			}
			Assert.Equal(new NpuLending.Entry(want.GetProperty("rank").GetInt32(), want.GetProperty("time").GetInt64(),
				want.GetProperty("pid").GetInt32(), want.GetProperty("nonce").GetString()!), got);
		}
	}

	[Fact]
	public void Dead() {
		foreach (JsonElement c in Cases("dead"))
			Assert.True(c.GetProperty("dead").GetBoolean() ==
				NpuLending.IsDead(TimeSpan.FromMilliseconds(c.GetProperty("ageMs").GetInt32()), c.GetProperty("alive").GetBoolean()), Case(c));
	}

	[Fact]
	public void Give_way() {
		foreach (JsonElement c in Cases("giveWay"))
			Assert.True(c.GetProperty("givesWay").GetBoolean() ==
				NpuLending.GivesWay(c.GetProperty("mine").GetInt32(), c.GetProperty("others").EnumerateArray().Select(o => o.GetInt32())), Case(c));
	}

	string Entry(int rank, int pid, string nonce) {
		Directory.CreateDirectory(Record);
		string path = Path.Combine(Record, $"{rank}-{(DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10:D17}-{pid}-{nonce}.lend");
		File.WriteAllText(path, "{}");
		return path;
	}

	// A scan for another PC lends as background work: while a call's captions are lent on this PC, it takes no new NPU
	// turn, and it goes on once they're done. Its own entry is on the record meanwhile, and gone after.
	[Fact]
	public async Task A_background_lending_gives_way_to_live_work_and_goes_on_after() {
		if (!OperatingSystem.IsWindows()) return;
		string live = Entry(NpuLending.Live, Environment.ProcessId, "cafe");
		var heard = new ConcurrentQueue<bool>();
		using (NpuLending.Join(NpuLending.Background, "heiward", "GAMING-PC", "scanning GAMING-PC's photos and videos for it", CancellationToken.None, heard.Enqueue)) {
			string mine = Assert.Single(Directory.GetFiles(Record, "2-*.lend"));
			using (JsonDocument body = JsonDocument.Parse(File.ReadAllText(mine))) {
				Assert.Equal("background", body.RootElement.GetProperty("rank").GetString());
				Assert.Equal("heiward", body.RootElement.GetProperty("who").GetString());
				Assert.Equal("GAMING-PC", body.RootElement.GetProperty("to").GetString());
				Assert.Equal(Environment.ProcessId, body.RootElement.GetProperty("pid").GetInt32());
			}

			Task turn = Task.Run(() => { using (NpuLock.Acquire(TimeSpan.FromSeconds(10))) { } });
			Assert.NotSame(turn, await Task.WhenAny(turn, Task.Delay(800))); // giving way
			Assert.Equal([true], heard);

			File.Delete(live);
			Assert.Same(turn, await Task.WhenAny(turn, Task.Delay(5000)));
			await turn;
			Assert.Equal([true, false], heard);
		}
		Assert.Empty(Directory.GetFiles(Record, "*.lend"));
	}

	// Live work lent while the scan already waits in the NPU's line: at the head it looks again, leaves the line so
	// nobody waits behind it, and takes its turn once the live work is done.
	[Fact]
	public async Task Live_work_lent_while_it_waits_in_line_still_goes_first() {
		if (!OperatingSystem.IsWindows()) return;
		using (NpuLending.Join(NpuLending.Background, "heiward", "GAMING-PC", null, CancellationToken.None)) {
			IDisposable other = NpuLock.Acquire(TimeSpan.FromSeconds(5)); // another program's turn
			Task turn = Task.Run(() => { using (NpuLock.Acquire(TimeSpan.FromSeconds(20))) { } });
			string queue = Path.Combine(home, "locks", "npu.queue");
			for (int i = 0; i < 40 && Directory.GetFiles(queue, "*.ticket").Length == 0; i++) await Task.Delay(50);
			Assert.Single(Directory.GetFiles(queue, "*.ticket")); // in line

			string live = Entry(NpuLending.Live, Environment.ProcessId, "call");
			await Task.Delay(300); // past the moment a look that said go holds
			other.Dispose();
			Assert.NotSame(turn, await Task.WhenAny(turn, Task.Delay(800))); // giving way
			Assert.Empty(Directory.GetFiles(queue, "*.ticket")); // out of the line meanwhile

			File.Delete(live);
			Assert.Same(turn, await Task.WhenAny(turn, Task.Delay(5000)));
			await turn;
		}
	}

	// A program that ended without leaving the record is no reason to wait: its entry is dead, and cleared on the way.
	[Fact]
	public void A_dead_entry_is_cleared_and_not_given_way_to() {
		if (!OperatingSystem.IsWindows()) return;
		string gone = Entry(NpuLending.Live, Environment.ProcessId, "dead");
		File.SetLastWriteTimeUtc(gone, DateTime.UtcNow.AddSeconds(-20));
		using (NpuLending.Join(NpuLending.Background, "heiward", null, null, CancellationToken.None)) {
			using (NpuLock.Acquire(TimeSpan.FromSeconds(5))) { }
		}
		Assert.False(File.Exists(gone));
	}

	// Stop while it gives way: the turn isn't waited for any longer.
	[Fact]
	public async Task A_stop_ends_the_wait() {
		if (!OperatingSystem.IsWindows()) return;
		Entry(NpuLending.Waited, Environment.ProcessId, "note");
		using var stop = new CancellationTokenSource();
		using (NpuLending.Join(NpuLending.Background, "heiward", null, null, stop.Token)) {
			Task turn = Task.Run(() => { using (NpuLock.Acquire(TimeSpan.FromSeconds(10))) { } });
			Assert.NotSame(turn, await Task.WhenAny(turn, Task.Delay(500)));
			stop.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
		}
	}

	// Nothing on this PC lends: every turn is taken at once, as before the record.
	[Fact]
	public async Task Without_a_lending_nothing_waits() {
		if (!OperatingSystem.IsWindows()) return;
		Entry(NpuLending.Live, Environment.ProcessId, "call");
		var done = Task.Run(() => { using (NpuLock.Acquire(TimeSpan.FromSeconds(5))) { } });
		Assert.Same(done, await Task.WhenAny(done, Task.Delay(3000)));
	}
}
