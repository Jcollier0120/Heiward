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

/// <summary>Opening or reloading the review page starts a scan only when one is due: the last scan plus the interval.</summary>
public sealed class ScanWhenDueTests {
	static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

	[Fact]
	public void ReloadingBetweenScans_StartsNothing() =>
		Assert.Null(ScanWhenDue.Due(Now.AddMinutes(-20), Now.AddMinutes(-25), everyMinutes: 60, Now));

	[Fact]
	public void OnceTheIntervalHasPassed_AScanIsDue() {
		string? why = ScanWhenDue.Due(Now.AddMinutes(-61), Now.AddMinutes(-66), everyMinutes: 60, Now);
		Assert.NotNull(why);
		Assert.Contains("every 60 minutes", why);
	}

	[Fact]
	public void AScanStoppedHalfway_CountsAsTheLastScan() {
		// The last finished scan is a day old, but one started ten minutes ago (and was stopped): not due again yet.
		Assert.Null(ScanWhenDue.Due(Now.AddDays(-1), Now.AddMinutes(-10), everyMinutes: 60, Now));
		Assert.Equal(Now.AddMinutes(50), ScanWhenDue.NextUtc(Now.AddDays(-1), Now.AddMinutes(-10), 60));
	}

	[Fact]
	public void AReportFromAnotherBuild_StillCountsAsTheLastScan() =>
		// After an update the old report is set aside, but it was still a scan: the next is due by the schedule, not at once.
		Assert.Null(ScanWhenDue.Due(Now.AddMinutes(-5), null, everyMinutes: 60, Now));

	[Fact]
	public void ScansOnlyWhenAsked_NeverStartOnOpen() {
		Assert.Null(ScanWhenDue.Due(Now.AddDays(-30), null, everyMinutes: 0, Now));
		Assert.Null(ScanWhenDue.NextUtc(Now.AddDays(-30), null, 0));
	}

	[Fact]
	public void NoScanYet_TheSetupStartsTheFirst() {
		Assert.Null(ScanWhenDue.Due(null, null, everyMinutes: 60, Now));
		Assert.Null(ScanWhenDue.NextUtc(null, null, 60));
	}

	[Fact]
	public void TheIntervalIsAtLeastFifteenMinutes_AsTheScheduledTaskRunsIt() {
		Assert.Null(ScanWhenDue.Due(Now.AddMinutes(-10), null, everyMinutes: 5, Now));
		Assert.NotNull(ScanWhenDue.Due(Now.AddMinutes(-15), null, everyMinutes: 5, Now));
	}

	[Fact]
	public void TheLaterOfTheTwo_IsTheLastScan() {
		Assert.Equal(Now.AddMinutes(40), ScanWhenDue.NextUtc(Now.AddMinutes(-20), Now.AddDays(-2), 60));
		Assert.Equal(Now.AddMinutes(30), ScanWhenDue.NextUtc(null, Now.AddMinutes(-30), 60));
	}
}
