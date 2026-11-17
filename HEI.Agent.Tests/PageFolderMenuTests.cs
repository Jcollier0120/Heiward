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
		var showAll = new Set(), SEP = '\\';
		function key(p) { return trim(p); }
		var listed = { files: 3 };
		function listing(path) { return Promise.resolve({ path: path, files: listed.files, unscanned: !!listed.unscanned, bytes: 0, duplicates: {}, children: [] }); }
		function nameOf(p) { return p; } function count(n) { return String(n); } function bytes() { return ''; } function took() { return ''; }
		function driveIcon() { return makeEl('svg'); } function folderIcon() { return makeEl('svg'); } function icon() { return makeEl('svg'); }
		function subfolderTable() { return makeEl('table'); } function duplicatesSection() { return makeEl('section'); }
		function sizeNode(tag) { return makeEl(tag); } function driveUsed() { return ['', '']; } function askSizesSoon() { }
		function buttons() {
		  return find(content, function (n) { return n.tagName === 'BUTTON'; })
		    .map(function (b) { return b.textContent + (b.disabled ? ' (off)' : ''); });
		}
		function press(words) { find(content, function (n) { return n.tagName === 'BUTTON' && n.textContent === words; })[0].click(); }
		function chips() {
		  return find(content, function (n) { return n.tagName === 'SPAN' && /^chip\b/.test(n.className); }).map(function (c) { return c.textContent; });
		}
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
		// The page's own cardFor: a folder's rows come from its listing, without the drive they're on.
		foreach (string name in new[] { "el", "menuItems", "openFolderMenu", "scanFolderWhy", "scanStrip", "otherDrivesStay", "trimSep", "isUnder", "cardFor",
			"overlaps", "scanReads" })
			js.Execute(ThePage.Function(script, name));
		js.Execute(Line(script, "const UNSCANNED_NOTE = "));
		js.Execute(Line(script, "const normRoot = "));
		int render = script.IndexOf("async function renderContent(", StringComparison.Ordinal);
		js.Execute(script[render..script.IndexOf("\nfunction subfolderTable(", render, StringComparison.Ordinal)]);
		// The stubs stand: the slices above bring the page's own of some of them along.
		js.Execute("function scanFolder(path) { scanned.push(path); } function scanDrive(root) { drivesScanned.push(root); } function overrideFolder() { }" +
			" function bumpScan() { } function scanTime() { return 'today 09:00'; }");
	}

	/// <summary>The line of <paramref name="script"/> that starts with <paramref name="start"/> (a constant).</summary>
	static string Line(string script, string start) {
		int at = script.IndexOf("\n" + start, StringComparison.Ordinal);
		Assert.True(at >= 0, start);
		return script[(at + 1)..script.IndexOf('\n', at + 1)];
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
	public void ScanThisDriveNow_SaysItReadsOnlyThatDrive_OnEveryDrive() {
		const string Alone = "Reads only this drive. The other drives stay as their last scans found them.";
		Page(@"E:\");
		Assert.Equal("Reads only this drive. The other drives stay as their last scans found them, and the scheduled scans keep their times.", Title("Scan this drive now"));
		// No scheduled scans at all (the setup's "only when I press Scan now"): just that drive too, with no schedule to keep.
		js.Execute("drive.scans = 'manual'; drive.manualWhy = 'onDemand';");
		Page(@"E:\");
		Assert.Equal(Alone, Title("Scan this drive now"));
		// Scanned only when asked: just that drive as well, not with the drives scheduled scans read, which don't read this one.
		js.Execute("onRequest();");
		Page(@"E:\");
		Assert.Equal(Alone, Title("Scan this drive now"));
		Page(@"E:\Development");
		Assert.Equal(Alone, Title("Scan the whole drive"));
	}

	[Fact]
	public void AFolderOnADrive_CanBeScannedByItself() {
		string[] items = Menu(@"{ name: 'Development', path: 'E:\\Development', drive: drive }");
		Assert.Contains("Scan now", items);
		// Before Leave out of scans, which a folder on a fixed drive can be.
		Assert.InRange(Array.IndexOf(items, "Scan now"), 0, Array.IndexOf(items, "Leave out of scans") - 1);
		js.Execute("choose('Scan now');");
		Assert.Equal(@"[""E:\\Development""]", js.Evaluate("JSON.stringify(scanned)").AsString());
	}

	/// <summary>
	/// The owner, 2026-11-15, right-clicking D:\Block 0\E Drive in the tree: "I also don't see the scan it now option on
	/// directories". A folder's rows come from its listing (/api/tree), which doesn't say which drive they're on, and the
	/// menu only looked for a drive whose root the folder is: no folder below a drive got Scan now.
	/// </summary>
	[Fact]
	public void AFolderAsItsListingHasIt_WithoutItsDrive_CanBeScannedByItself() {
		const string Row = @"{ name: 'E Drive', path: 'E:\\Block 0\\E Drive', exempt: null, files: 0, bytes: 0, duplicates: { copies: 0, lookalikes: 0, reclaim: 0 }, expandable: true }";
		string[] items = Menu(Row);
		Assert.Equal(new[] { "Open", "Open in File Explorer", "Show in File Explorer", "Scan now", "Leave out of scans", "Copy path" }, items);
		js.Execute("choose('Scan now');");
		Assert.Equal(@"[""E:\\Block 0\\E Drive""]", Json("scanned"));
		// On a drive scanned only when asked, and in a folder scanned as a root of its own, too.
		js.Execute("onRequest();");
		Assert.Contains("Scan now", Menu(Row));
		js.Execute(@"state.drives.push({ root: 'C:\\Users\\me\\Pictures', type: 'folder', scanned: true });");
		Assert.Contains("Scan now", Menu(@"{ name: 'Trip', path: 'C:\\Users\\me\\Pictures\\2019\\Trip', exempt: null }"));
		// Not on a drive Heiward doesn't scan.
		js.Execute(@"state.drives.push({ root: 'F:\\', type: 'removable', scanned: false });");
		Assert.DoesNotContain("Scan now", Menu(@"{ name: 'Elsewhere', path: 'F:\\Elsewhere', exempt: null }"));
	}

	/// <summary>The strip's line under a drive's title: when it's scanned next, or why not.</summary>
	string Next() => js.Evaluate("find(content, function (n) { return n.className === 'scan-next'; })[0].textContent").AsString();

	/// <summary>
	/// The owner's E:\ on a new install, 2026-11-17: "Last scanned: never. Next scheduled scan: today 08:13 PM", and Scan now
	/// instead of at 08:13 PM. But no scheduled scan runs before the first scan: it waits for the person.
	/// </summary>
	[Fact]
	public void ADrivesPage_WhileTheFirstScanWaits_NamesNoScheduledScan_AndScansNow() {
		js.Execute("state.schedule.nextUtc = '2026-11-18T01:13:00Z'; state.agent.firstScanWaits = true; var everyDrive = 0; function scanNow() { everyDrive++; }");
		Assert.Equal(new[] { "Scan this drive now", "Scan every drive now" }, Page(@"E:\"));
		Assert.Equal("Heiward's first scan waits for you: no drive is scanned on a schedule until it has run.", Next());
		js.Execute("press('Scan this drive now');");
		Assert.Equal(@"[""E:\\""]", Json("drivesScanned"));
		js.Execute("press('Scan every drive now');");
		Assert.Equal(1, js.Evaluate("everyDrive").AsNumber());
	}

	[Fact]
	public void ADrivesPage_WithAScheduledScanDue_ScanThisDriveNowScansIt_NotTheScheduledScan() {
		js.Execute("state.schedule.nextUtc = '2026-11-18T01:13:00Z'; var bumped = []; function bumpScan(slot) { bumped.push(slot); }");
		string[] buttons = Page(@"E:\");
		Assert.Equal("Scan this drive now", buttons[0]);
		Assert.StartsWith("Scan now instead of at ", buttons[1]);
		Assert.Equal("Next scheduled scan: today 09:00.", Next());
		js.Execute("press('Scan this drive now');");
		Assert.Equal(@"[""E:\\""]", Json("drivesScanned"));
		Assert.Equal("[]", Json("bumped"));
	}

	[Fact]
	public void ADrivesPage_WhileAnotherDrivesScanRuns_SaysItWaitsForThatScan() {
		js.Execute("state.scan.running = true; state.scan.status = { roots: ['C:\\\\'] };");
		Assert.Equal(new[] { "Scanning… (off)" }, Page(@"E:\"));
		Assert.EndsWith(" Another scan is running now: once it has finished, this drive can be scanned.", Next());
		// Its own scan running: nothing to wait for.
		js.Execute("state.scan.status = { roots: ['E:\\\\'] };");
		Page(@"E:\");
		Assert.DoesNotContain("Another scan", Next());
	}

	/// <summary>
	/// "Last scan took under a second" beside "Not scanned yet", of the owner's E:\: a folder's scan, which read nothing on it
	/// whole, had timed the drive. Only a scan that read it whole says how long it took.
	/// </summary>
	[Fact]
	public void ADriveNeverScannedWhole_SaysNotScannedYet_NotHowLongItsLastScanTook() {
		js.Execute("function took() { return 'under a second'; } drive.scan = { listingSec: 0.4, analysisSec: 0 }; listed = { files: 0, unscanned: true };");
		Page(@"E:\");
		Assert.Equal(new[] { "Not scanned yet" }, Js<string[]>("chips()"));
		js.Execute("drive.lastScanUtc = '2026-11-17T22:20:00Z'; listed = { files: 3 };");
		Page(@"E:\");
		Assert.Equal(new[] { "3", "Last scan took under a second" }, Js<string[]>("chips()"));
	}

	[Fact]
	public void AFoldersPage_NotScannedYet_SaysSo_NotZeroPhotosAndVideos() {
		js.Execute("listed = { files: 0, unscanned: true };");
		Page(@"E:\emudeck backup");
		Assert.Equal(new[] { "Not scanned yet" }, Js<string[]>("chips()"));
		// The count, as before (this page's count() gives the number alone).
		js.Execute("listed = { files: 0 };");
		Page(@"E:\Empty");
		Assert.Equal(new[] { "0" }, Js<string[]>("chips()"));
		js.Execute("listed = { files: 3 };");
		Page(@"E:\Development");
		Assert.Equal(new[] { "3" }, Js<string[]>("chips()"));
	}

	T Js<T>(string expression) => JsonSerializer.Deserialize<T>(Json(expression))!;

	/// <summary>The Folders table: a folder not scanned yet says so, and isn't greyed out as one with nothing in it.</summary>
	[Fact]
	public void TheFoldersTable_SaysNotScannedYet_ForAFolderWithPhotosNoScanCounted() {
		js.Execute(ThePage.Function(ThePage.Asset("app.js").ReplaceLineEndings("\n"), "subfolderTable"));
		js.Execute("""
			var sortBy = { key: 'files', desc: true }, folderSizes = new Map(), FOLD_EXEMPT = 5, exemptOpen = new Set(), expanded = new Map(), SIZE_NOTE = '';
			function renderRoute() { }
			function folder(name, files, unscanned) {
			  return { name: name, path: 'E:\\Backup\\' + name, exempt: null, files: files, unscanned: unscanned, duplicates: { copies: 0, lookalikes: 0, reclaim: 0 } };
			}
			var table = subfolderTable('E:\\Backup', { children: [folder('Empty', 0, false), folder('Saves', 0, true), folder('Photos', 4, false)], hiddenEmpty: 2 });
			function text(n) { return n.textContent + (n.children || []).map(text).join(''); }
			var shownRows = find(table, function (n) { return n.tagName === 'TR' && /^row\b/.test(n.className); }).map(function (r) {
			  return [text(r.children[0]), r.className, text(r.children[2]), r.children[2].title];
			});
			var showEmpty = find(table, function (n) { return n.tagName === 'BUTTON' && /show-empty/.test(n.className); })[0].textContent;
			""");
		string[][] rows = Js<string[][]>("shownRows");
		// Counted first, then not scanned yet, then none.
		Assert.Equal(new[] { "Photos", "Saves", "Empty" }, rows.Select(r => r[0]));
		Assert.Equal(new[] { "row", "row", "row none" }, rows.Select(r => r[1]));
		Assert.Equal(new[] { "4", "Not scanned yet", "–" }, rows.Select(r => r[2]));
		Assert.StartsWith("No scan has counted", rows[1][3]);
		Assert.Equal("Show 2 without photos or videos", Js<string>("showEmpty"));
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
		Assert.Contains("Scan now (A scan is running now: once it has finished, this folder can be scanned.)", Menu(@"{ name: 'Development', path: 'E:\\Development', drive: drive }"));
	}

	[Fact]
	public void ADrive_AFolderLeftOut_OrOneOutsideTheScans_HasNoScanNow() {
		Assert.DoesNotContain("Scan now", Menu(@"{ name: 'E:', path: 'E:\\', drive: drive }"));
		Assert.DoesNotContain("Scan now", Menu(@"{ name: 'node_modules', path: 'E:\\Development\\node_modules', drive: drive, exempt: 'Program files' }"));
		Assert.DoesNotContain("Scan now", Menu(@"{ name: 'Elsewhere', path: 'F:\\Elsewhere' }"));
	}
}
