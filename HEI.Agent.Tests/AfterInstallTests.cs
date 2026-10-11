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

/// <summary>
/// What the install starts once it's done: the first scan with scheduled scans; with scans only on demand (the setup's
/// "Only when I press Scan now", or an update keeping it), the page alone, and no scan until the person asks.
/// </summary>
public sealed class AfterInstallTests {
	static bool Scans(List<string[]> starts) => starts.Any(a => a[0] == "scan");

	[Theory]
	[InlineData(true, true)]
	[InlineData(false, true)]
	[InlineData(false, false)]
	public void OnDemandOnly_StartsNoScan(bool openPage, bool pageFree) =>
		Assert.False(Scans(Installer.AfterInstall(openPage, pageFree, onDemandOnly: true)));

	[Fact]
	public void OnDemandOnly_TheStoreSetup_KeepsThePageUp() {
		// The Store version's setup runs "install --yes --no-browser --on-demand" from its own page.
		var starts = Installer.AfterInstall(openPage: false, pageFree: true, onDemandOnly: true);
		Assert.Equal(["serve", "--no-browser"], Assert.Single(starts));
	}

	[Fact]
	public void OnDemandOnly_WithTheBrowser_OpensThePage() =>
		Assert.Equal(["open"], Assert.Single(Installer.AfterInstall(openPage: true, pageFree: true, onDemandOnly: true)));

	[Fact]
	public void OnDemandOnly_AnotherCopysPage_StartsNothing() =>
		Assert.Empty(Installer.AfterInstall(openPage: false, pageFree: false, onDemandOnly: true));

	[Fact]
	public void Scheduled_TheFirstScanStarts_WaitingItsTurn() {
		var starts = Installer.AfterInstall(openPage: true, pageFree: true, onDemandOnly: false);
		Assert.Equal(["scan", "--open", "--wait-turn"], Assert.Single(starts));
		starts = Installer.AfterInstall(openPage: false, pageFree: true, onDemandOnly: false);
		Assert.Equal(2, starts.Count);
		Assert.Equal(["serve", "--no-browser"], starts[0]);
		Assert.Equal(["scan", "--wait-turn"], starts[1]);
	}
}
