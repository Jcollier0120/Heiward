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

using Jint;

namespace HEI.Agent.Tests;

/// <summary>
/// Removing sets from a folder's page (Move to Recycle Bin, Skip all, a set's own Move and Keep) spins from the press until
/// the page is drawn again (the owner, 2026-11-12: "When heiward deletes a lot of pairs, it's slow to reload the ui and
/// needs to display a spinner from start to finish"). app.js's bars, set cards, holdForRemoval and its polls run in a
/// JavaScript engine, against a page with just enough of a DOM, whose posts and drawing answer only when a test says so.
/// </summary>
public sealed class PageRemovalTests {
	const string FakePage = """
		function makeEl(tag) {
		  return {
		    tagName: tag.toUpperCase(), attrs: {}, handlers: {}, capture: {}, children: [], parent: null, style: {},
		    className: '', textContent: '', type: '', title: '', disabled: false, checked: false,
		    setAttribute: function (k, v) { this.attrs[k] = String(v); },
		    getAttribute: function (k) { return Object.prototype.hasOwnProperty.call(this.attrs, k) ? this.attrs[k] : null; },
		    removeAttribute: function (k) { delete this.attrs[k]; },
		    addEventListener: function (t, f, capture) { var to = capture ? this.capture : this.handlers; (to[t] = to[t] || []).push(f); },
		    removeEventListener: function (t, f, capture) { var to = capture ? this.capture : this.handlers; to[t] = (to[t] || []).filter(function (x) { return x !== f; }); },
		    append: function () { for (var i = 0; i < arguments.length; i++) { arguments[i].parent = this; this.children.push(arguments[i]); } },
		    prepend: function (c) { c.parent = this; this.children.unshift(c); },
		    remove: function () { if (this.parent) { var s = this.parent.children; s.splice(s.indexOf(this), 1); this.parent = null; } },
		    replaceChildren: function () { this.children = []; this.append.apply(this, arguments); },
		    // As the browser: no click on a greyed button; the capturing listeners above it first, which may stop it.
		    click: function () {
		      if (this.disabled) return;
		      var e = { target: this, stopped: false, prevented: false,
		        preventDefault: function () { this.prevented = true; }, stopPropagation: function () { this.stopped = true; } };
		      var up = [];
		      for (var n = this.parent; n; n = n.parent) up.unshift(n);
		      for (var i = 0; i < up.length && !e.stopped; i++) (up[i].capture.click || []).slice().forEach(function (f) { f(e); });
		      if (!e.stopped) (this.handlers.click || []).slice().forEach(function (f) { f(e); });
		    },
		  };
		}
		var document = { visibilityState: 'visible', createElement: makeEl, createDocumentFragment: function () { return makeEl('#fragment'); } };
		function find(node, test, found) {
		  found = found || [];
		  if (test(node)) found.push(node);
		  node.children.forEach(function (c) { find(c, test, found); });
		  return found;
		}
		// The folder's page, the content in it, and a checkbox on it that isn't the removal's.
		var folderPage = makeEl('main'), content = makeEl('section'), ticks = 0, tick = makeEl('input');
		folderPage.append(content);
		tick.addEventListener('click', function () { ticks++; });
		function $(id) { return id === 'folder' ? folderPage : content; }
		function show(node) { content.replaceChildren(node, tick); return node; }
		function button(words) { return find(content, function (n) { return n.tagName === 'BUTTON' && n.textContent === words; })[0]; }
		function spinning(b) { return b.children.length > 0 && b.children[0].className === 'ui-spinner' && b.getAttribute('aria-busy') === 'true'; }
		function held() { return folderPage.getAttribute('aria-busy') === 'true'; }
		function resultLine() { return find(content, function (n) { return n.className.indexOf('result small') === 0; })[0]; }

		// Timers, as the browser keeps them: what each was set for.
		var timers = [], timeouts = [], timerIds = 0;
		function setTimeout(f, ms) { timers.push({ id: ++timerIds, f: f, ms: ms }); timeouts.push(ms); return timerIds; }
		function clearTimeout(id) { timers = timers.filter(function (t) { return t.id !== id; }); }
		function nextTimer() { return timers[timers.length - 1]; }

		// The server: each post answers when the test says, and /api/state at once unless the test holds it.
		var posts = [];
		function post(url, body) { return new Promise(function (ok, fail) { posts.push({ url: url, body: body, ok: ok, fail: fail }); }); }
		function answer(i, value) { posts[i].ok(value); }
		function refuse(i, words) { posts[i].fail(new Error(words)); }
		var fetched = [], holdFetch = false, heldFetch = null;
		function theState() {
		  return { dev: { roles: null, running: false }, games: { running: false }, scan: { running: false }, setup: { needed: false, running: false },
		    config: { keepHistory: true }, auto: { groups: {} }, pending: [], done: [], totals: {} };
		}
		var state = theState();
		function fetch(url) {
		  fetched.push(url);
		  var res = { ok: true, json: function () { return Promise.resolve(theState()); } };
		  if (!holdFetch) return Promise.resolve(res);
		  return new Promise(function (ok) { heldFetch = function () { ok(res); }; });
		}
		// Drawing the page again: it ends when the test says.
		var draws = 0, drawing = null;
		function renderRoute() { draws++; return new Promise(function (ok) { drawing = ok; }); }
		function drawn() { var f = drawing; drawing = null; f(); }

		var notice = '', error = '';
		function showNotice(m) { notice = m; } function showError(m) { error = m; }
		function askDialog() { return Promise.resolve(true); }
		var uninstall = { gone: false }, listings = new Map(), location = { reload: function () { } };
		function setupShowing() { return false; } function renderHeader() { } function showAgentBanner() { }
		var checks = new Map(), groupsShown = 0, PAGE = 24, SEP = '\\';
		function checked(g) {
		  if (!checks.has(g.key)) checks.set(g.key, new Set(g.items.filter(function (i) { return i.suggested; }).map(function (i) { return i.path; })));
		  return checks.get(g.key);
		}
		function isUnder(p, folder) { return p.toLowerCase().indexOf(folder.toLowerCase()) === 0; }
		function count(n, one, many) { return n + ' ' + (n === 1 ? one : many); }
		function bytes(n) { return n + ' B'; } function nameOf(p) { return p; } function trimSep(p) { return p; } function batchId() { return 'b1'; }
		var KIND = { same: ['same', 'Copies'], similar: ['similar', 'Look-alike'] };
		function itemTile() { return makeEl('div'); } function autoLine() { return makeEl('div'); } function dueText() { return ''; }
		function set(key, kind, names) {
		  return { key: key, kind: kind, media: 'image', keepReason: 'oldest', items: names.map(function (n, i) {
		    return { name: n, path: 'E:\\Photos\\' + n, size: 100, keep: i === 0, suggested: kind !== 'similar' && i > 0 };
		  }) };
		}
		""";

	readonly Engine js = new();

	public PageRemovalTests() {
		string script = ThePage.Asset("app.js").ReplaceLineEndings("\n");
		js.Execute(FakePage);
		js.Execute(ThePage.Function(script, "el"));
		// holdForRemoval, waitForRemoval, the Skip all bar and the folder's Move to Recycle Bin bar; a set's card; the polls.
		js.Execute(Slice(script, "// Moving checked files to the Recycle Bin", "\nfunction itemTile("));
		js.Execute(Slice(script, "function groupCard(", "\n// ---------------------------------------------------------------- render loop"));
		js.Execute(Slice(script, "let timer = null;", "\n// ---------------------------------------------------------------- theme menu"));
	}

	static string Slice(string script, string from, string to) {
		int start = script.IndexOf(from, StringComparison.Ordinal);
		Assert.True(start >= 0, from);
		int end = script.IndexOf(to, start + from.Length, StringComparison.Ordinal);
		Assert.True(end > start, to);
		return script[start..end];
	}

	/// <summary>Runs <paramref name="code"/>, then what it awaited that has answered, as the browser does once the script is done.</summary>
	void Run(string code) {
		js.Execute(code);
		js.Advanced.ProcessTasks();
	}

	bool True(string code) => js.Evaluate(code).AsBoolean();
	string Text(string code) => js.Evaluate(code).ToString();

	[Fact]
	public void SkipAll_SpinsFromThePress_UntilThePageIsDrawnAgain() {
		Run("show(skipBar('E:\\\\Photos', [set('a', 'similar', ['1.jpg', '2.jpg']), set('b', 'similar', ['3.jpg', '4.jpg'])]));");
		Run("button('Skip all 2').click();");
		// Asked and answered: the button spins and is greyed, and the folder's page waits.
		Assert.True(True("posts.length === 1 && posts[0].url === '/api/groups/skip'"));
		Assert.True(True("spinning(button('Skip all 2')) && button('Skip all 2').disabled && held()"));
		// A second press, or a click anywhere else on the folder's page, starts nothing.
		Run("button('Skip all 2').click(); tick.click();");
		Assert.True(True("posts.length === 1 && ticks === 0"));
		// A poll meanwhile asks nothing and draws nothing: it tries again in a second.
		Run("refresh();");
		Assert.True(True("fetched.length === 0 && draws === 0 && nextTimer().ms === 1000"));
		// The post answers: the page is drawn again, the button still spinning until that's done.
		Run("answer(0, { skipped: 2 });");
		Assert.True(True("fetched.length === 1 && draws === 1"));
		Assert.True(True("spinning(button('Skip all 2')) && button('Skip all 2').disabled && held()"));
		Assert.Equal("", Text("notice"));
		Run("drawn();");
		Assert.True(True("!spinning(button('Skip all 2')) && !held() && !folderPage.capture.click.length"));
		Assert.Equal("Skipped 2 look-alike sets.", Text("notice"));
		// Polls go on as before.
		Assert.True(True("nextTimer().ms === 15000"));
		Run("tick.click();");
		Assert.True(True("ticks === 1"));
	}

	[Fact]
	public void MovingAFoldersCopies_SaysHowFarItIs_AwaitsTheRedraw_AndKeepsWhatItSaid() {
		Run("show(cleanupBar('E:\\\\Photos', [set('a', 'same', ['1.jpg', '2.jpg']), set('b', 'same', ['3.jpg', '4.jpg'])]));");
		Run("button('Move 2 files to Recycle Bin').click();");
		var move = "button('Move 2 files to Recycle Bin')";
		Assert.True(True($"spinning({move}) && {move}.disabled && held()"));
		Assert.Equal("Moving… set 1 of 2", Text("resultLine().textContent"));
		Run("answer(0, { recycled: ['E:\\\\Photos\\\\2.jpg'], recycledBytes: 100, failed: [] });");
		Assert.Equal("Moving… set 2 of 2", Text("resultLine().textContent"));
		Assert.True(True($"spinning({move}) && held() && draws === 0"));
		Run("answer(1, { recycled: [], recycledBytes: 0, failed: [{ path: 'E:\\\\Photos\\\\4.jpg', reason: 'in use' }] });");
		// What it did shows while the page is drawn again, the button still spinning; no wait of its own first.
		const string said = "Moved 1 file, 100 B, to the Recycle Bin. Not moved: 4.jpg (in use)";
		Assert.Equal(said, Text("resultLine().textContent"));
		Assert.True(True($"draws === 1 && spinning({move}) && held()"));
		Run("drawn();");
		// The bar goes with the sets it moved: what it said stays, in the alert colour since a file stayed.
		Assert.True(True($"!spinning({move}) && !held()"));
		Assert.Equal(said, Text("error"));
		Assert.True(True("timeouts.length === 1 && timeouts[0] === 15000")); // the next poll's, and nothing before the drawing
		Assert.True(True("resultLine().className === 'result small bad'"));
	}

	[Fact]
	public void AFailure_StillClearsTheSpinner_AndTheButtonsComeBack() {
		Run("show(skipBar('E:\\\\Photos', [set('a', 'similar', ['1.jpg', '2.jpg'])]));");
		Run("button('Skip all 1').click();");
		Run("refuse(0, 'Heiward is busy.');");
		Assert.True(True("!spinning(button('Skip all 1')) && !button('Skip all 1').disabled && !held() && draws === 0"));
		Assert.Equal("Heiward is busy.", Text("error"));

		// A set's own Move: its Keep is greyed with it, and both come back.
		Run("show(groupCard(set('c', 'same', ['5.jpg', '6.jpg']), 'E:\\\\Photos'));");
		Run("button('Move 1 to Recycle Bin').click();");
		Assert.True(True("spinning(button('Move 1 to Recycle Bin')) && button('Not duplicates, keep all').disabled && held()"));
		Run("refuse(1, 'That group is no longer in the report; scan again.');");
		Assert.True(True("!spinning(button('Move 1 to Recycle Bin')) && !button('Move 1 to Recycle Bin').disabled && !button('Not duplicates, keep all').disabled && !held()"));
		Assert.Equal("That group is no longer in the report; scan again.", Text("resultLine().textContent"));
	}

	[Fact]
	public void ASetsOwnButtons_SpinUntilThePageIsDrawnAgain() {
		Run("show(groupCard(set('d', 'similar', ['7.jpg', '8.jpg']), 'E:\\\\Photos'));");
		Run("button('Keep them all').click();");
		Assert.True(True("posts[0].url === '/api/groups/d/keep' && spinning(button('Keep them all')) && button('Check the files to remove').disabled && held()"));
		Run("answer(0, {});");
		Assert.True(True("draws === 1 && spinning(button('Keep them all')) && held()"));
		Run("drawn();");
		Assert.True(True("!spinning(button('Keep them all')) && !held()"));

		Run("show(groupCard(set('e', 'same', ['1.jpg', '2.jpg', '3.jpg']), 'E:\\\\Photos'));");
		Run("button('Move 2 to Recycle Bin').click();");
		Run("answer(1, { recycled: ['E:\\\\Photos\\\\2.jpg', 'E:\\\\Photos\\\\3.jpg'], recycledBytes: 200, failed: [] });");
		Assert.True(True("draws === 2 && spinning(button('Move 2 to Recycle Bin')) && button('Not duplicates, keep all').disabled"));
		Assert.Equal("", Text("notice"));
		Run("drawn();");
		Assert.Equal("Moved 2 file(s), 200 B, to the Recycle Bin.", Text("notice"));
		Assert.True(True("!held() && timeouts.every(function (ms) { return ms === 1000 || ms === 15000; })"));
	}

	[Fact]
	public void APollAskedBeforeTheRemoval_DrawsNothingHalfWay_AndAScansProgressGoesOn() {
		// A poll is on its way when Skip all is pressed: its answer isn't drawn, the removal's own drawing is.
		Run("holdFetch = true; refresh();");
		Run("show(skipBar('E:\\\\Photos', [set('a', 'similar', ['1.jpg', '2.jpg'])])); button('Skip all 1').click();");
		Run("heldFetch();");
		Assert.True(True("draws === 0 && nextTimer().ms === 1000 && held()"));
		// While a scan runs, its progress still draws in place meanwhile, every second and a half.
		Run("state.scan.running = true; refresh();");
		Assert.True(True("nextTimer().f === pollProgress && nextTimer().ms === 1500"));
		Run("holdFetch = false; state.scan.running = false; answer(0, { skipped: 1 });");
		Assert.True(True("draws === 1"));
		Run("drawn();");
		Assert.True(True("!held() && notice === 'Skipped 1 look-alike set.'"));
	}

	[Fact]
	public void TheSpinner_IsTheKits_TurningAsTheSetupsStagesDo() {
		string css = ThePage.Asset("app.css");
		Assert.Contains(".ui-spinner { display: inline-block; flex: none; box-sizing: border-box; width: 14px; height: 14px; border: 2px solid currentColor; border-right-color: transparent; border-radius: 50%; animation: setup-spin .8s linear infinite;", css);
		Assert.Contains("@keyframes setup-spin { to { transform: rotate(360deg); } }", css);
		Assert.Contains("#folder[aria-busy=\"true\"], #folder[aria-busy=\"true\"] * { cursor: progress; }", css);
		// No removal waits a fixed time before the page is drawn again.
		string script = ThePage.Asset("app.js");
		Assert.DoesNotContain("setTimeout(() => refresh(true)", script);
	}
}
