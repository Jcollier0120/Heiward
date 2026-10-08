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

/// <summary>Heiward's first scan in the manor's first-round line (the Steward's kit 2.43.0): who waits, the turn, and what the page is told.</summary>
[Collection(AgentHomeCollection.Name)]
public sealed class FirstRoundTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-first-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public FirstRoundTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	sealed class Released : IDisposable {
		public bool Done;
		public void Dispose() => Done = true;
	}

	[Theory]
	[InlineData(true, false, null, true, true)] // a scheduled first scan, the manor's pace gentle by default: waits
	[InlineData(true, false, "gentle", true, true)]
	[InlineData(true, false, "full", true, false)] // Manor says full: as before
	[InlineData(true, true, null, true, false)] // a scan finished before: later scans never wait
	[InlineData(false, false, null, true, false)] // Scan now: never waits
	[InlineData(true, false, null, false, false)] // no manor's line on this PC: Heiward as it always was
	public void WhoWaits(bool scheduled, bool done, string? pace, bool lineHere, bool waits) =>
		Assert.Equal(waits, FirstRound.Waits(scheduled, done, pace, lineHere));

	[Fact]
	public void AFirstScan_SaysItWaits_ThenRuns_AndGivesTheTurnUp() {
		var held = new Released();
		string? whileWaiting = null;
		var said = new List<string>();
		IDisposable? turn = FirstRound.Take(scheduled: true, manor: null, said.Add, acquire: wait => {
			whileWaiting = FirstRound.Now();
			Assert.Equal(FirstRound.Wait, wait);
			return held;
		});
		Assert.NotNull(turn);
		Assert.Equal(FirstRound.Waiting, whileWaiting);
		Assert.Equal(FirstRound.Running, FirstRound.Now());
		Assert.Contains(said, s => s.Contains("full speed"));
		turn!.Dispose();
		Assert.True(held.Done);
		Assert.Null(FirstRound.Now());
	}

	[Fact]
	public void NoTurn_ForScanNow_ALaterScan_OrAFullPace() {
		int asked = 0;
		IDisposable Acquire(TimeSpan _) {
			asked++;
			return new Released();
		}
		Assert.Null(FirstRound.Take(scheduled: false, manor: null, _ => { }, acquire: Acquire));
		Assert.Null(FirstRound.Take(scheduled: true, manor: Manor.FromJson("""{"backgroundPace": "full"}"""), _ => { }, acquire: Acquire));
		File.WriteAllText(AgentPaths.Report, "{}");
		Assert.True(FirstRound.Done());
		Assert.Null(FirstRound.Take(scheduled: true, manor: null, _ => { }, acquire: Acquire));
		Assert.Equal(0, asked);
	}

	[Fact]
	public void NoTurnInTime_ClearsWhatThePageIsTold_AndSaysSo() {
		Assert.Throws<TimeoutException>(() => FirstRound.Take(scheduled: true, manor: null, _ => { }, acquire: _ => throw new TimeoutException("no turn")));
		Assert.Null(FirstRound.Now());
	}

	[Fact]
	public void AStateLeftByAScanThatHasGone_SaysNothing() {
		// A pid that is not running (pids are multiples of 4 on Windows; this one is odd).
		File.WriteAllText(Path.Combine(AgentPaths.Home, "first-round.json"), """{"state":"running","pid":2147483641}""");
		Assert.Null(FirstRound.Now());
	}

	[Theory]
	[InlineData("""{"backgroundPace": "gentle"}""", "gentle")]
	[InlineData("""{"backgroundPace": "full"}""", "full")]
	[InlineData("""{"backgroundPace": "fast"}""", null)]
	[InlineData("""{"backgroundPace": true}""", null)]
	[InlineData("{}", null)]
	public void TheManorsPace_GentleOrFull_ElseNothing(string json, string? pace) => Assert.Equal(pace, Manor.FromJson(json).BackgroundPace);
}
