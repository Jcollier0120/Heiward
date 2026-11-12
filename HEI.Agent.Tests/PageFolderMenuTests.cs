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
using Jint;

namespace HEI.Agent.Tests;

/// <summary>
/// A folder's right-click menu on the page (the drive cards' folder tree, and a folder's list of folders): Scan now reads
/// just that folder and the folders in it (the owner, 2026-11-12: "add a scan now button to the right click menu of
/// directories underneath the drive in the side panel"). app.js's openFolderMenu runs in a JavaScript engine, against a
/// page with just enough of a DOM for its menu.
/// </summary>
public sealed class PageFolderMenuTests {
	const string FakePage = """
		function makeEl(tag) {
		  return {
		    tagName: tag.toUpperCase(), attrs: {}, handlers: {}, children: [], style: {}, className: '', textContent: '', type: '', title: '', disabled: false,
		    setAttribute: function (k, v) { this.attrs[k] = String(v); },
		    addEventListener: function (t, f) { (this.handlers[t] = this.handlers[t] || []).push(f); },
		    click: function () { (this.handlers.click || []).forEach(function (f) { f({}); }); },
		    append: function () { for (var i = 0; i < arguments.length; i++) this.children.push(arguments[i]); },
		    replaceChildren: function () { this.children = []; this.append.apply(this, arguments); },
		  };
		}
		var document = { createElement: makeEl, createDocumentFragment: function () { return makeEl('#fragment'); } };
		function find(node, test, found) {
		  found = found || [];
		  if (test(node)) found.push(node);
		  (node.children || []).forEach(function (c) { find(c, test, found); });
		  return found;
		}
		// A folder's page: the listing it shows, and the card it's on.
		var content = makeEl('div'), route = { view: 'folder', path: 'E:\\Development' }, drivesScanned = [];
		function $(id) { return content; }
		var showAll = new Set();
		function key(p) { return trim(p); }
		function listing(path) { return Promise.resolve({ path: path, files: 3, bytes: 0, duplicates: {}, children: [] }); }
		function cardFor() { return drive; }
		function nameOf(p) { return p; } function count(n) { return String(n); } function bytes() { return ''; } function took() { return ''; }
		function driveIcon() { return makeEl('svg'); } function folderIcon() { return makeEl('svg'); } function icon() { return makeEl('svg'); }
		function subfolderTable() { return makeEl('table'); } function duplicatesSection() { return makeEl('section'); }
		function sizeNode(tag) { return makeEl(tag); } function driveUsed() { return ['', '']; } function askSizesSoon() { }
		function buttons() {
		  return find(content, function (n) { return n.tagName === 'BUTTON'; })
		    .map(function (b) { return b.textContent + (b.disabled ? ' (off)' : ''); });
		}
		function press(words) { find(content, function (n) { return n.tagName === 'BUTTON' && n.textContent === words; })[0].click(); }
		function trim(p) { return p.replace(/\\+$/, '').toLowerCase(); }
		function sameFolder(a, b) { return trim(a) === trim(b); }
		var drive = { root: 'E:\\', type: 'fixed', onRequest: false, scans: 'auto' };
		var state = { drives: [drive], scan: { running: false }, schedule: { everyMinutes: 60, text: 'scans every hour' }, setup: { needed: false }, agent: { paused: false } };
		function scanNow() { } function bumpScan() { } function ago() { return 'an hour ago'; } function scanTime() { return 'today 09:00'; }
		// A drive scanned only when asked, as the page has it from the server.
		function onRequest() { drive.onRequest = true; drive.scans = 'manual'; drive.manualWhy = 'drive'; }
		var shown = null, scanned = [];
		function showMenu(e, label, items, hint) { shown = { label: label, items: items, hint: hint }; }
		function closeFolderMenu() { }
		function scanFolder(path) { scanned.push(path); }
		function go() { } function revealPath() { } function copyPath() { } function overrideFolder() { } function setOnRequest() { }
		function scanDrive(root) { drivesScanned.push(root); }
		function menu(node) { openFolderMenu({}, node); return shown.items.map(function (b) { return b.textContent + (b.disabled ? ' (' + b.title + ')' : ''); }); }
		function choose(words) { shown.items.filter(function (b) { return b.textContent === words; })[0].click(); }
		""";

	readonly Engine js = new();

	public PageFolderMenuTests() {
		string script = ThePage.Asset("app.js").ReplaceLineEndings("\n");
		js.Execute(FakePage);
		foreach (string name in new[] { "el", "menuItems", "openFolderMenu", "scanFolderWhy", "scanStrip" })
			js.Execute(ThePage.Function(script, name));
		int render = script.IndexOf("async function renderContent(", StringComparison.Ordinal);
		js.Execute(script[render..script.IndexOf("\nfunction subfolderTable(", render, StringComparison.Ordinal)]);
		// The stubs stand: the slices above bring the page's own of some of them along.
		js.Execute("function scanFolder(path) { scanned.push(path); } function scanDrive(root) { drivesScanned.push(root); } function overrideFolder() { }" +
			" function bumpScan() { } function scanTime() { return 'today 09:00'; }");
	}

	string[] Menu(string node) => JsonSerializer.Deserialize<string[]>(js.Evaluate($"JSON.stringify(menu({node}))").AsString())!;

	/// <summary>The page of <paramref name="path"/>, drawn: the buttons on it.</summary>
	string[] Page(string path) {
		js.Execute($"route.path = {JsonSerializer.Serialize(path)}; renderContent();");
		js.Advanced.ProcessTasks();
		return JsonSerializer.Deserialize<string[]>(js.Evaluate("JSON.stringify(buttons())").AsString())!;
	}

	string Json(string expression) => js.Evaluate($"JSON.stringify({expression})").AsString();

	[Fact]
	public void AFoldersPage_OnADriveScannedOnlyWhenAsked_ScansJustThatFolder_OrTheWholeDrive() {
		js.Execute("onRequest();");
		Assert.Equal(new[] { "Scan this folder now", "Scan the whole drive", "Scan automatically again" }, Page(@"E:\Development"));
		js.Execute("press('Scan this folder now');");
		Assert.Equal(@"[""E:\\Development""]", Json("scanned"));
		Assert.Equal("[]", Json("drivesScanned"));
		js.Execute("press('Scan the whole drive');");
		Assert.Equal(@"[""E:\\""]", Json("drivesScanned"));
	}

	[Fact]
	public void TheDrivesOwnPage_ScansTheDrive_AndWhileAScanRunsNothingStarts() {
		js.Execute("onRequest();");
		Assert.Equal(new[] { "Scan this drive now", "Scan automatically again" }, Page(@"E:\"));
		js.Execute("state.scan.running = true;");
		Assert.Equal(new[] { "Scanning… (off)", "Scan the whole drive (off)", "Scan automatically again" }, Page(@"E:\Development"));
	}

	[Fact]
	public void ADriveScannedAutomatically_ItsOwnPageScansTheDrive_AndAFoldersPageHasNoStrip() {
		string[] drivePage = Page(@"E:\");
		Assert.Contains("Scan this drive now", drivePage);
		Assert.DoesNotContain("Scan this folder now", drivePage);
		Assert.Empty(Page(@"E:\Development")); // its right-click menu has Scan now
	}

	/// <summary>The tooltip of the button saying <paramref name="words"/> on the page drawn last.</summary>
	string Title(string words) =>
		js.Evaluate($"find(content, function (n) {{ return n.tagName === 'BUTTON' && n.textContent === {JsonSerializer.Serialize(words)}; }})[0].title").AsString();

	[Fact]
	public void ScanThisDriveNow_SaysItReadsOnlyThatDrive_ButNotOnADriveScannedOnlyWhenAsked() {
		Page(@"E:\");
		Assert.Equal("Reads only this drive. The other drives stay as their last scans found them, and the scheduled scans keep their times.", Title("Scan this drive now"));
		// No scheduled scans at all (the setup's "only when I press Scan now"): just that drive too, with no schedule to keep.
		js.Execute("drive.scans = 'manual'; drive.manualWhy = 'onDemand';");
		Page(@"E:\");
		Assert.Equal("Reads only this drive. The other drives stay as their last scans found them.", Title("Scan this drive now"));
		// Scanned only when asked: it's read with the drives scheduled scans read, as before.
		js.Execute("onRequest();");
		Page(@"E:\");
		Assert.Equal("", Title("Scan this drive now"));
	}

	[Fact]
	public void AFolderOnADrive_CanBeScannedByItself() {
		string[] items = Menu(@"{ name: 'Development', path: 'E:\\Development', drive: drive }");
		Assert.Contains("Scan now", items);
		// Before Leave out of scans (greyed out on a fixed drive, which is scanned whole).
		int leave = Array.FindIndex(items, i => i.StartsWith("Leave out of scans", StringComparison.Ordinal));
		Assert.InRange(Array.IndexOf(items, "Scan now"), 0, leave - 1);
		js.Execute("choose('Scan now');");
		Assert.Equal(@"[""E:\\Development""]", js.Evaluate("JSON.stringify(scanned)").AsString());
	}

	[Fact]
	public void AFolderOnADriveScannedOnlyWhenAsked_OrInAScannedFolder_CanBeScannedByItself() {
		js.Execute("onRequest();");
		Assert.Contains("Scan now", Menu(@"{ name: 'Development', path: 'E:\\Development', drive: drive }"));
		js.Execute(@"state.drives.push({ root: 'C:\\Users\\me\\Pictures', type: 'folder' });");
		Assert.Contains("Scan now", Menu(@"{ name: '2019', path: 'C:\\Users\\me\\Pictures\\2019', drive: state.drives[1] }"));
	}

	[Fact]
	public void WhileAScanRuns_ScanNowWaits() {
		js.Execute("state.scan.running = true;");
		Assert.Contains("Scan now (A scan is running now.)", Menu(@"{ name: 'Development', path: 'E:\\Development', drive: drive }"));
	}

	[Fact]
	public void ADrive_AFolderLeftOut_OrOneOutsideTheScans_HasNoScanNow() {
		Assert.DoesNotContain("Scan now", Menu(@"{ name: 'E:', path: 'E:\\', drive: drive }"));
		Assert.DoesNotContain("Scan now", Menu(@"{ name: 'node_modules', path: 'E:\\Development\\node_modules', drive: drive, exempt: 'Program files' }"));
		Assert.DoesNotContain("Scan now", Menu(@"{ name: 'Elsewhere', path: 'F:\\Elsewhere' }"));
	}
}
