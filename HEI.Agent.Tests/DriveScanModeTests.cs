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
/// Each drive's Auto or Manual tag in the folder tree, and the setup's "only when I press Scan now": with it, every drive is
/// Manual, the page says so, and no scheduled scan reads any drive.
/// </summary>
public sealed class DriveScanModeTests : IDisposable {
	readonly string root = RealTemp.Folder("hei-scan-mode-");

	public DriveScanModeTests() => Directory.CreateDirectory(root);
	public void Dispose() { try { Directory.Delete(root, true); } catch { } }

	static string Asset(string name) {
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/" + name)!;
		return new StreamReader(s).ReadToEnd();
	}

	static string Function(string script, string name) {
		int start = script.IndexOf("function " + name + "(", StringComparison.Ordinal);
		Assert.True(start > 0, name);
		return script[start..script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal)];
	}

	[Fact]
	public void ADrive_IsAuto_WhenScheduledScansReadIt() {
		var cfg = new AgentConfig { ScanEveryMinutes = 360 };
		Assert.Equal(("auto", (string?)null), ExplorerView.ScanMode(cfg, onRequest: false, devBuild: false));
	}

	[Fact]
	public void ADrive_IsManual_ByItsOwnSetting_WithNoScheduledScans_OrInADevelopmentBuild() {
		var scheduled = new AgentConfig { ScanEveryMinutes = 360 };
		var onDemand = new AgentConfig { ScanEveryMinutes = 0 };
		Assert.Equal(("manual", ExplorerView.ByDrive), ExplorerView.ScanMode(scheduled, onRequest: true, devBuild: false));
		Assert.Equal(("manual", ExplorerView.OnDemand), ExplorerView.ScanMode(onDemand, onRequest: false, devBuild: false));
		Assert.Equal(("manual", ExplorerView.DevBuildWhy), ExplorerView.ScanMode(scheduled, onRequest: false, devBuild: true));
		// Its own setting says the most: Scan now leaves it alone too.
		Assert.Equal(("manual", ExplorerView.ByDrive), ExplorerView.ScanMode(onDemand, onRequest: true, devBuild: false));
	}

	[Fact]
	public void WithScansOnDemand_EveryDriveIsManual_WithoutCopyingItIntoEachDrivesSetting() {
		var cfg = new AgentConfig { ScanEveryMinutes = 0, ScanAllDrives = false, Folders = [root] };

		var cards = ExplorerView.Drives(cfg, null, new(), devBuild: false);

		Assert.NotEmpty(cards);
		Assert.All(cards, c => Assert.Equal(("manual", ExplorerView.OnDemand), (c.Scans, c.ManualWhy)));
		Assert.All(cards, c => Assert.False(c.OnRequest));
		Assert.Empty(cfg.OnRequestDrives);
		// Scheduled again: Auto again, nothing to undo.
		cfg.ScanEveryMinutes = 360;
		Assert.All(ExplorerView.Drives(cfg, null, new(), devBuild: false), c => Assert.Equal("auto", c.Scans));
	}

	[Fact]
	public void AScheduledScan_StepsAside_WhenScansRunOnlyOnDemand() {
		// The scan task is removed then; one left behind mustn't read any drive.
		Assert.NotNull(ScanWhenDue.ScheduledSkip(0));
		Assert.Contains("Scan now", ScanWhenDue.ScheduledSkip(0));
		Assert.Null(ScanWhenDue.ScheduledSkip(360));
		Assert.Null(ScanWhenDue.Due(DateTime.UtcNow.AddDays(-3), null, 0, DateTime.UtcNow)); // nor does the page start one
	}

	[Fact]
	public void LeavingADriveOutOfScanNow_IsWordedForScansOnDemand() {
		Assert.Contains("Scheduled scans leave it alone", ReviewServer.OnRequestMessage(true, 360));
		Assert.Contains("automatically again", ReviewServer.OnRequestMessage(false, 360));
		Assert.Contains("left out of Scan now", ReviewServer.OnRequestMessage(true, 0));
		Assert.DoesNotContain("automatically", ReviewServer.OnRequestMessage(false, 0));
	}

	[Fact]
	public void TheTree_TagsEachDrive_AndDimsNone() {
		string script = Asset("app.js");
		string tree = Function(script, "treeItem");
		Assert.Contains("!node.drive && !node.exempt && !node.files ? ' empty'", tree);
		Assert.Contains("'count scan-mode'", tree);
		Assert.Contains("auto ? 'Auto' : 'Manual'", tree);
		Assert.Contains("scanModeTitle(node.drive)", tree);

		// Its tooltip: the scan window, and how to change it.
		string title = Function(script, "scanModeTitle");
		Assert.Contains("s.text", title);
		Assert.Contains("Next scheduled scan", title);
		Assert.Contains("Right-click the", title);
		Assert.Contains("only when you press Scan now", title);
		// Drawn like the Exempt tag.
		Assert.Contains(".tree-row .count.scan-mode {", Asset("app.css"));
	}

	[Fact]
	public void ADrivesRightClick_SaysScansRunOnlyOnDemand() {
		string menu = Function(Asset("app.js"), "openFolderMenu");
		Assert.Contains("card.scans !== 'auto'", menu);
		Assert.Contains("'Leave out of Scan now'", menu);
		Assert.Contains("'Include in Scan now'", menu);
		Assert.Contains("no drive is scanned on a schedule", menu);
	}

	[Fact]
	public void ThePage_AsksForSizes_AndShowsThemEverywhereItListsFolders() {
		string script = Asset("app.js");
		Assert.Contains("fetch('/api/sizes?'", Function(script, "askSizes"));
		Assert.Contains("SIZES_AGAIN", Function(script, "askSizes")); // again while some are measured
		Assert.Contains("'…'", Function(script, "sizeText")); // the placeholder
		Assert.Contains("Size on disk", script);
		Assert.Contains("OneDrive", script);
		// The tree: each folder, a drive's used space, the folded row of exempt folders.
		string tree = Function(script, "treeItem");
		Assert.Contains("driveUsed(node.drive)", tree);
		Assert.Contains("sizeNode('span', 'size', [node.path])", tree);
		Assert.Contains("sizeNode('span', 'size', exempt.map((c) => c.path))", tree);
		// The Folders table: a Size column, on every row (exempt ones too), and the folded row's.
		string table = Function(script, "subfolderTable");
		Assert.Contains("['size', 'Size', 'num col-size']", table);
		Assert.Contains("row.append(sizeNode('td', 'num col-size', [c.path]));", table);
		Assert.Contains("sizeNode('td', 'num col-size', exemptRows.map((c) => c.path))", table);
		// The folder's own page.
		Assert.Contains("'Size on disk: '", Function(script, "renderContent"));
	}
}
