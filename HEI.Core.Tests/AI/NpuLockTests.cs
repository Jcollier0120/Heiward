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
