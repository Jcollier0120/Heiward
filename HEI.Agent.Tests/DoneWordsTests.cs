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
/// What the install says once it's done, which Manor shows in its Updates box: a new install is "Installed" and its first
/// scan; an update says it updated, from which version where it's known, and that its scan only looks at new files.
/// </summary>
public sealed class DoneWordsTests {
	[Fact]
	public void ANewInstall_SaysInstalled_AndTheFirstScan() {
		Assert.Equal(["Installed. The first scan starts now.", "Later scans only look at new files. Nothing is ever deleted unless you choose it on the page."],
			Installer.DoneWords(onDemandOnly: false, firstScanWaits: false, openPage: false, replaces: null, "1.9.105"));
		Assert.Equal("Installed. The first scan starts now; the review page opens in your browser and shows its progress.",
			Installer.DoneWords(onDemandOnly: false, firstScanWaits: false, openPage: true, replaces: null, "1.9.105")[0]);
	}

	[Fact]
	public void ANewInstall_KeepsItsWords_ForAWaitingFirstScan_AndScansOnDemand() {
		Assert.Equal("Installed. The review page opens in your browser: the first scan starts when you press Scan now there, or let it scan by itself.",
			Installer.DoneWords(onDemandOnly: false, firstScanWaits: true, openPage: true, replaces: null, "1.9.105")[0]);
		Assert.Equal("Installed. The first scan starts when you press Scan now on the review page, or let it scan by itself there.",
			Installer.DoneWords(onDemandOnly: false, firstScanWaits: true, openPage: false, replaces: null, "1.9.105")[0]);
		Assert.Equal("Installed. Scans run when you press Scan now on the review page, which opens in your browser.",
			Installer.DoneWords(onDemandOnly: true, firstScanWaits: false, openPage: true, replaces: null, "1.9.105")[0]);
		Assert.Equal("Installed. Scans run when you press Scan now on the review page.",
			Installer.DoneWords(onDemandOnly: true, firstScanWaits: false, openPage: false, replaces: null, "1.9.105")[0]);
	}

	[Fact]
	public void AnUpdate_FromManor_SaysFromWhichVersion() {
		// Manor runs "install --yes --no-browser": no browser, a scan with scheduled scans.
		Assert.Equal(["Updated from 1.9.101 to 1.9.104. A scan starts now; it only looks at new files.", "Nothing is ever deleted unless you choose it on the page."],
			Installer.DoneWords(onDemandOnly: false, firstScanWaits: false, openPage: false, replaces: "1.9.101", "1.9.104"));
		Assert.Equal("Updated from 1.9.101 to 1.9.104. A scan starts now; it only looks at new files. The review page opens in your browser and shows its progress.",
			Installer.DoneWords(onDemandOnly: false, firstScanWaits: false, openPage: true, replaces: "1.9.101", "1.9.104")[0]);
	}

	[Fact]
	public void AnUpdate_WithScansOnDemand_SaysUpdated() {
		Assert.Equal("Updated from 1.9.101 to 1.9.104. Scans run when you press Scan now on the review page.",
			Installer.DoneWords(onDemandOnly: true, firstScanWaits: false, openPage: false, replaces: "1.9.101", "1.9.104")[0]);
		Assert.Equal("Updated from 1.9.101 to 1.9.104. Scans run when you press Scan now on the review page, which opens in your browser.",
			Installer.DoneWords(onDemandOnly: true, firstScanWaits: false, openPage: true, replaces: "1.9.101", "1.9.104")[0]);
	}

	[Fact]
	public void AnUpdate_BeforeAnyScan_StillWaitsForTheFirst() =>
		Assert.Equal("Updated from 1.9.101 to 1.9.104. The first scan starts when you press Scan now on the review page, or let it scan by itself there.",
			Installer.DoneWords(onDemandOnly: false, firstScanWaits: true, openPage: false, replaces: "1.9.101", "1.9.104")[0]);

	[Fact]
	public void AnUpdate_FromAnUnknownVersion_SaysUpdatedTo() =>
		Assert.Equal("Updated to 1.9.104. A scan starts now; it only looks at new files.",
			Installer.DoneWords(onDemandOnly: false, firstScanWaits: false, openPage: false, replaces: "", "1.9.104")[0]);

	[Fact]
	public void TheSameVersion_IsReinstalled_AndAnOlderOne_WentBack() {
		Assert.StartsWith("Reinstalled 1.9.104. ", Installer.DoneWords(onDemandOnly: false, firstScanWaits: false, openPage: false, replaces: "1.9.104", "1.9.104")[0]);
		Assert.StartsWith("Went back from 1.9.105 to 1.9.104. ", Installer.DoneWords(onDemandOnly: false, firstScanWaits: false, openPage: false, replaces: "1.9.105", "1.9.104")[0]);
		// By number, not by text: 1.9.99 is older than 1.9.104.
		Assert.StartsWith("Updated from 1.9.99 to 1.9.104. ", Installer.DoneWords(onDemandOnly: false, firstScanWaits: false, openPage: false, replaces: "1.9.99", "1.9.104")[0]);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void AnUpdate_NeverSaysInstalled_NorAFirstScanThatsDone(bool onDemandOnly, bool openPage) {
		foreach (string replaces in new[] { "1.9.101", "" })
			foreach (string line in Installer.DoneWords(onDemandOnly, firstScanWaits: false, openPage, replaces, "1.9.104")) {
				Assert.DoesNotContain("Installed", line);
				Assert.DoesNotContain("first scan", line);
			}
	}
}
