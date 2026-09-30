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

namespace HEI.Agent.Tests;

/// <summary>Pausing scheduled scans and stopping the running one, from the review page or the command line.</summary>
[Collection(AgentHomeCollection.Name)]
public sealed class AgentControlTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-agent-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public AgentControlTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

	[Fact]
	public void APause_LastsItsTime_ThenIsGone() {
		AgentPause.Start(60, Now);
		Assert.Equal(Now.AddHours(1), AgentPause.Load(Now.AddMinutes(59))!.UntilUtc);
		Assert.Null(AgentPause.Load(Now.AddMinutes(61)));
		Assert.False(File.Exists(AgentPaths.Paused));
	}

	[Fact]
	public void APauseWithoutAnEnd_LastsUntilResumed() {
		AgentPause.Start(null, Now);
		Assert.NotNull(AgentPause.Load(Now.AddDays(30)));
		Assert.Equal("until you resume", AgentPause.Load(Now)!.Describe(Now));
		AgentPause.Resume();
		Assert.Null(AgentPause.Load(Now));
	}

	[Fact]
	public void APause_IsAtMostAWeek() =>
		Assert.Equal(Now.AddMinutes(AgentPause.MaxMinutes), AgentPause.Start(1_000_000, Now).UntilUtc);

	[Fact]
	public void AStopRequest_CountsOnlyForTheScanItWasMadeFor() {
		ScanStop.Request();
		Assert.True(ScanStop.Requested(DateTime.UtcNow.AddMinutes(-1)));
		// A scan that started after the request is a new one.
		Assert.False(ScanStop.Requested(DateTime.UtcNow.AddMinutes(1)));
		ScanStop.Clear();
		Assert.False(ScanStop.Requested(DateTime.MinValue));
	}

	[Fact]
	public async Task AStopRequest_CancelsTheScan() {
		DateTime started = DateTime.UtcNow.AddSeconds(-1);
		using var stop = new CancellationTokenSource();
		using var end = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		Task watching = ScanStop.WatchAsync(started, stop, end.Token);
		ScanStop.Request();
		await watching;
		Assert.True(stop.IsCancellationRequested);
	}
}
