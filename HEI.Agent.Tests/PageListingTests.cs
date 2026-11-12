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
/// A folder's page drawn again (after sets are removed, refresh(true) clears the listings) asks for every listing it draws
/// together: the folder shown (once, though the tree and the page both draw it), the folders above it, and the other
/// folders open in the tree. It used to ask them one after another, each once the tree had drawn the folder above (the
/// owner, 2026-11-12: "When heiward deletes a lot of pairs, it's slow to reload the ui"). Three are on their way at most
/// (LISTINGS_AT_ONCE), the next going out as each answers: Heiward lists one at a time on a spinning disk anyway, and the
/// page's other requests need the browser's connections too. app.js's listing, folder tree, renderRoute and renderContent
/// run in a JavaScript engine, against a page with just enough of a DOM, whose /api/tree answers only when a test says so.
/// </summary>
public sealed class PageListingTests {
	const string FakePage = """
		function makeEl(tag) {
		  return {
		    tagName: tag.toUpperCase(), attrs: {}, handlers: {}, children: [], style: {}, dataset: {}, className: '', textContent: '', title: '', scrollTop: 0,
		    classList: { add: function () { }, remove: function () { }, toggle: function () { }, contains: function () { return false; } },
		    setAttribute: function (k, v) { this.attrs[k] = String(v); },
		    addEventListener: function (t, f) { (this.handlers[t] = this.handlers[t] || []).push(f); },
		    append: function () { for (var i = 0; i < arguments.length; i++) this.children.push(arguments[i]); },
		    replaceChildren: function () { this.children = []; this.append.apply(this, arguments); },
		    querySelector: function () { return null; },
		  };
		}
		var document = { createElement: makeEl, createDocumentFragment: function () { return makeEl('#fragment'); } };
		var ids = {};
		function $(id) { return ids[id] = ids[id] || makeEl('div'); }
		function find(node, test, found) {
		  found = found || [];
		  if (test(node)) found.push(node);
		  (node.children || []).forEach(function (c) { find(c, test, found); });
		  return found;
		}
		// The tree's rows, top to bottom: each one's name, and which is selected.
		function rows() {
		  return find($('tree'), function (n) { return n.tagName === 'BUTTON' && n.className.indexOf('tree-row') === 0; }).map(function (r) {
		    var label = find(r, function (n) { return n.className === 'label'; })[0];
		    return label.textContent + (r.attrs['aria-selected'] === 'true' ? ' (selected)' : '');
		  });
		}
		function heading() { var h = find($('content'), function (n) { return n.tagName === 'H1'; })[0]; return h ? h.textContent : null; }

		// The server: each /api/tree answers when the test says.
		var asked = [];
		function fetch(url) {
		  var m = /path=([^&]*)(&all=true)?/.exec(url);
		  return new Promise(function (resolve, reject) {
		    asked.push({ path: decodeURIComponent(m[1]), all: !!m[2], done: false,
		      ok: function (v) { this.done = true; resolve(v); }, no: function (e) { this.done = true; reject(e); } });
		  });
		}
		function askedPaths() { return asked.map(function (a) { return a.path; }); }
		function onTheirWay() { return asked.filter(function (a) { return !a.done; }).length; }
		// Folders on E:, and which of them have folders in them.
		var folders = {
		  'E:\\': ['Old', 'Photos', 'Videos'],
		  'E:\\Old': ['Deep'], 'E:\\Old\\Deep': [],
		  'E:\\Photos': ['2019', '2020'], 'E:\\Photos\\2019': ['Trip'], 'E:\\Photos\\2019\\Trip': [], 'E:\\Photos\\2020': [],
		  'E:\\Videos': ['Clips'], 'E:\\Videos\\Clips': ['Cut'], 'E:\\Videos\\Clips\\Cut': [],
		};
		function under(path, name) { return (path.slice(-1) === '\\' ? path : path + '\\') + name; }
		function answer(a) {
		  var children = folders[a.path].map(function (n) {
		    var p = under(a.path, n);
		    return { name: n, path: p, exempt: null, files: 1, bytes: 10, duplicates: { copies: 0, lookalikes: 0, reclaim: 0 }, expandable: folders[p].length > 0 };
		  });
		  a.ok({ ok: true, json: function () { return Promise.resolve({ path: a.path, exempt: null, files: 1, bytes: 10,
		    duplicates: { copies: 0, lookalikes: 0, reclaim: 0 }, children: children, hiddenEmpty: 0 }); } });
		}
		function answerAll(order) { var all = asked.filter(function (a) { return !a.done; }); if (order === 'last first') all.reverse(); all.forEach(answer); }

		var SEP = '\\', FOLD_EXEMPT = 5;
		var listings = new Map(), expanded = new Map(), showAll = new Set(), exemptOpen = new Set();
		var drive = { root: 'E:\\', name: 'Data (E:)', type: 'fixed', scanned: true, scans: 'auto', duplicates: { copies: 0 } };
		var state = { drives: [drive], dev: { enabled: false }, games: { enabled: false } };
		var route = { view: 'folder', path: 'E:\\Photos\\2019' };
		function open(path) { expanded.set(path.toLowerCase(), path); }
		function uninstallShowing() { return false; } function setupShowing() { return false; } function renderCrumbs() { }
		function count(n) { return String(n); } function bytes() { return ''; } function took() { return ''; } function scanModeTitle() { return ''; }
		function driveIcon() { return makeEl('svg'); } function folderIcon() { return makeEl('svg'); } function icon() { return makeEl('svg'); } function pcIcon() { return makeEl('svg'); }
		function sizeNode(tag) { return makeEl(tag); } function driveUsed() { return ['', '']; } function askSizesSoon() { }
		function scanStrip() { return makeEl('div'); } function subfolderTable() { return makeEl('table'); } function duplicatesSection() { return makeEl('section'); }
		function go() { } function openFolderMenu() { }
		var drawn = 0;
		function draw() { renderRoute().then(function () { drawn++; }); }
		""";

	readonly Engine js = new();

	public PageListingTests() {
		string script = ThePage.Asset("app.js").ReplaceLineEndings("\n");
		js.Execute(FakePage);
		js.Execute(ThePage.Function(script, "el"));
		// key, trimSep, isUnder, sameFolder, cardFor, parentOf.
		js.Execute(Slice(script, "const key = (p) =>", "\nfunction nameOf("));
		js.Execute(Slice(script, "function nameOf(", "\nasync function post("));
		js.Execute(Slice(script, "// ---------------------------------------------------------------- folder tree", "\n// ---------------------------------------------------------------- folder menu"));
		js.Execute(Slice(script, "async function renderContent(", "\nfunction subfolderTable("));
		js.Execute(Slice(script, "async function renderRoute(", "\n// How the page keeps up."));
		// The drive, the folders down to the one shown and that one open (as the page opens them), and two more open.
		js.Execute(@"open('E:\\'); open('E:\\Photos'); open('E:\\Photos\\2019'); open('E:\\Videos');");
		// Open, but inside a folder that's closed: the tree doesn't show it.
		js.Execute(@"open('E:\\Old\\Deep');");
	}

	static string Slice(string script, string from, string to) {
		int start = script.IndexOf(from, StringComparison.Ordinal);
		Assert.True(start >= 0, from);
		int end = script.IndexOf(to, start + from.Length, StringComparison.Ordinal);
		Assert.True(end > start, to);
		return script[start..end];
	}

	T Js<T>(string expression) => JsonSerializer.Deserialize<T>(js.Evaluate($"JSON.stringify({expression})").AsString())!;

	void Run(string code) {
		js.Execute(code);
		js.Advanced.ProcessTasks();
	}

	/// <summary>Answers every listing on its way, and those that go out meanwhile, until none is left.</summary>
	void AnswerEverything(string order = "") {
		for (int i = 0; i < 20 && Js<int>("onTheirWay()") > 0; i++) Run($"answerAll('{order}');");
		Assert.Equal(0, Js<int>("onTheirWay()"));
	}

	static readonly string[] Drawn = ["This PC", "Data (E:)", "Old", "Photos", "2019 (selected)", "Trip", "2020", "Videos", "Clips"];

	[Fact]
	public void AFoldersPage_AsksForEveryListingItDraws_Together_AndForTheFolderShownOnce() {
		Run("draw();");
		// Before any answer: the folder shown (the tree and the page both draw it), then the drive and the folder down to it.
		Assert.Equal([@"E:\Photos\2019", @"E:\", @"E:\Photos"], Js<string[]>("askedPaths()"));
		// The other open folder the tree shows goes out as soon as any answers: before the tree has drawn its parent.
		Run(@"answer(asked[0]);");
		Assert.Equal([@"E:\Photos\2019", @"E:\", @"E:\Photos", @"E:\Videos"], Js<string[]>("askedPaths()"));
		Assert.Equal(0, Js<int>("drawn"));

		AnswerEverything("last first");
		Assert.Equal(1, Js<int>("drawn"));
		// Not the open folder inside a closed one.
		Assert.Equal(4, Js<int>("asked.length"));
		Assert.Equal(Drawn, Js<string[]>("rows()"));
		Assert.Equal("2019", Js<string>("heading()"));
	}

	[Fact]
	public void DrawnAgainAfterARemoval_TheListingsAreAllAskedAgain_Together() {
		Run("draw();");
		AnswerEverything();
		Assert.Equal(1, Js<int>("drawn"));
		// refresh(true), after sets are removed: the counts in the tree follow the report.
		Run("asked = []; listings.clear(); draw();");
		Assert.Equal([@"E:\Photos\2019", @"E:\", @"E:\Photos"], Js<string[]>("askedPaths()"));
		AnswerEverything("last first");
		Assert.Equal(2, Js<int>("drawn"));
		Assert.Equal([@"E:\", @"E:\Photos", @"E:\Photos\2019", @"E:\Videos"], Js<string[]>("askedPaths()").Order());
		Assert.Equal(Drawn, Js<string[]>("rows()"));
	}

	[Fact]
	public void AnOpenFolderDeeperDown_IsAskedWithTheRest_NotOnceTheFolderAboveIsDrawn() {
		Run(@"open('E:\\Videos\\Clips'); draw();");
		// Three answers in, the two still waiting go out: Videos and the folder open inside it, before Videos answers.
		Run("answerAll();");
		Assert.Equal([@"E:\Photos\2019", @"E:\", @"E:\Photos", @"E:\Videos", @"E:\Videos\Clips"], Js<string[]>("askedPaths()"));
		Assert.Equal(2, Js<int>("onTheirWay()"));
		AnswerEverything("last first");
		Assert.Equal(1, Js<int>("drawn"));
		Assert.Equal(5, Js<int>("asked.length"));
		Assert.Equal([.. Drawn, "Cut"], Js<string[]>("rows()"));
	}

	[Fact]
	public void AtMostThreeListings_AreOnTheirWay_TheNextGoingOut_InTheOrderAsked_AsEachAnswersOrFails() {
		Run(@"open('E:\\Videos\\Clips'); open('E:\\Old'); draw();");
		Assert.Equal(3, Js<int>("asked.length"));
		Assert.Equal(3, Js<int>("onTheirWay()"));
		Run(@"answer(asked[1]);");
		Assert.Equal(4, Js<int>("asked.length"));
		Assert.Equal(3, Js<int>("onTheirWay()"));
		// One that gets no answer frees its place too.
		Run(@"asked[3].no(new TypeError('Failed to fetch'));");
		Assert.Equal(5, Js<int>("asked.length"));
		Assert.Equal(3, Js<int>("onTheirWay()"));
		Run(@"answer(asked[0]); answer(asked[2]);");
		// The rest in their turn, in the order they were asked: the open folders in the order they were opened.
		Assert.Equal([@"E:\Photos\2019", @"E:\", @"E:\Photos", @"E:\Videos", @"E:\Old\Deep", @"E:\Videos\Clips", @"E:\Old"], Js<string[]>("askedPaths()"));
		Assert.Equal(3, Js<int>("onTheirWay()"));
	}

	[Fact]
	public void AListingAskedAgainWhileOnItsWay_IsOneRequest() {
		Run(@"var first = listing('E:\\Photos', false), second = listing('e:\\photos\\', false);");
		Assert.Equal(1, Js<int>("asked.length"));
		Assert.True(Js<bool>("first === second"));
		Run("var got = null; second.then(function (d) { got = d.path; }); answerAll();");
		Assert.Equal(@"E:\Photos", Js<string>("got"));
		// Answered: kept until the next redraw clears it.
		Run(@"listing('E:\\Photos', false);");
		Assert.Equal(1, Js<int>("asked.length"));
		// With its empty folders too: another listing.
		Run(@"listing('E:\\Photos', true);");
		Assert.Equal(2, Js<int>("asked.length"));
		Assert.True(Js<bool>("asked[1].all"));
	}

	[Fact]
	public void AListingThatGotNoAnswer_IsAskedAgain_ButNotOneAskedAnewMeanwhile() {
		Run(@"var failed = null; listing('E:\\Photos', false).catch(function (e) { failed = e.message; }); asked[0].no(new TypeError('Failed to fetch'));");
		Assert.Equal("Failed to fetch", Js<string>("failed"));
		Run(@"listing('E:\\Photos', false);");
		Assert.Equal(2, Js<int>("asked.length"));

		// Cleared (a redraw) and asked anew while the old request was on its way: the old one failing leaves the new one.
		Run(@"listings.clear(); var anew = listing('E:\\Photos', false); asked[1].no(new TypeError('Failed to fetch'));");
		Assert.Equal(3, Js<int>("asked.length"));
		Assert.True(Js<bool>(@"listing('E:\\Photos', false) === anew"));
		Assert.Equal(3, Js<int>("asked.length"));
	}

	[Fact]
	public void TheTree_ComesOutInItsOrder_WhicheverListingAnswersFirst() {
		Run("draw();");
		// The shown folder's first, then the drive's, then the rest.
		Run(@"answer(asked.filter(function (a) { return a.path === 'E:\\Photos\\2019'; })[0]);");
		Run(@"answer(asked.filter(function (a) { return a.path === 'E:\\'; })[0]);");
		Assert.Equal(0, Js<int>("drawn"));
		Run(@"asked.filter(function (a) { return a.path === 'E:\\Videos' || a.path === 'E:\\Photos'; }).forEach(answer);");
		Assert.Equal(1, Js<int>("drawn"));
		Assert.Equal(Drawn, Js<string[]>("rows()"));
	}
}
