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

namespace HEI.Agent.Tests;

/// <summary>
/// Auto without a working NPU takes the graphics card once it has passed a check: a card not checked yet (with its
/// driver) is checked before the scan, but not one that failed in the last 10 minutes, nor again soon after a failed check.
/// </summary>
[Collection(AgentHomeCollection.Name)]
public sealed class GpuCheckTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-gpucheck-" + Guid.NewGuid().ToString("N"));

	public GpuCheckTests() {
		Directory.CreateDirectory(dir);
		Accelerators.FolderOverride = Path.Combine(dir, "accelerators");
		GpuChecks.PathOverride = Path.Combine(dir, "gpu-checks.json");
	}

	public void Dispose() {
		Accelerators.FolderOverride = null;
		GpuChecks.PathOverride = null;
		try { Directory.Delete(dir, true); } catch { }
	}

	static readonly GpuAdapter Card = new(0, "Qualcomm(R) Adreno(TM) X2-90 GPU", "Qualcomm(R) Adreno(TM) X2-90 GPU", 0, 0x4D4F4351, "0x00000000_0x000137d9", "31.0.112.0");

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
}
