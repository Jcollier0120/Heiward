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
/// What the page asks once a new install is set up, and when. The owner, 2026-11-13, on the setup window's Done:
/// "Scan now / Scan by itself shows up too early before the user goes to the home page": the first-scan banner was
/// across the top of Done, which says the tour asks that, and the tour asks it again. Now it waits for the home page,
/// after the setup and the tour, and asks there only while the first scan still waits: a tour skipped or left with Later
/// leaves it asking, so the first scan is never left with nothing asking. Then: "Heiward onboarding should skip the
/// game/developer mode if it came from the store since the installer now asks for it"; and the page said scheduled scans
/// weren't running just after the setup, the scan task being there all along ("seems like there was an issue setting up
/// the task scheduler").
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME is process-wide, and so is Task Scheduler's kept answer
public sealed class PageFirstScanTests : IDisposable {
	const string Waits = "Heiward's first scan waits for you. Scan now starts it at full speed; or let it scan by itself: the first runs with the next scheduled scan, scans every 6 hours on AC power.";

	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-first-scan-" + Guid.NewGuid().ToString("N"));
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public PageFirstScanTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Scheduler.Forget();
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	// ---- The first-scan banner waits for the home page

	[Fact]
	public void TheSetup_AndItsDone_ShowNoBanner() {
		var js = Page();
		js.Execute("state.setup.needed = true; renderAgent(state);"); // the questions, then the install
		Assert.Equal("", Banner(js));
		// Installed: Done shows until Open Heiward (setupRan). Its own words say the tour asks; the banner asked it here before.
		js.Execute("state.setup.needed = false; setupRan = true; renderAgent(state);");
		Assert.Equal("", Banner(js));
		// Nor does any other: Done isn't the place to say scheduled scans aren't running, say.
		js.Execute("state.agent.firstScanWaits = false; state.agent.scheduleMissing = true; renderAgent(state);");
		Assert.Equal("", Banner(js));
	}

	[Fact]
	public void OpenHeiward_TheTourOpens_AndTheBannerWaitsForItsEnd() {
		var js = AtDone();
		js.Execute("press(card, 'Open Heiward');");
		Assert.Equal("home", js.Evaluate("route.view").AsString());
		Assert.Equal("", Banner(js)); // the tour opens in a moment: no banner before it
		Assert.True(js.Evaluate("tourOnLoad()").AsBoolean());
		Assert.Equal(0, (int)js.Evaluate("tourStep").AsNumber());
		js.Execute("renderAgent(state);"); // a poll while it shows
		Assert.Equal("", Banner(js));
		// Its last step asks the same, so the banner doesn't.
		js.Execute("next(); next(); next(); next();");
		Assert.Equal("Its first scan", TourTitle(js));
		Assert.Equal(new[] { "Back", "Later", "Scan by itself", "Scan now" }, TourButtons(js));
		Assert.Equal("", Banner(js));
	}

	[Fact]
	public void TheTourSkipped_TheBannerAsks_SoTheFirstScanIsNeverStranded() {
		var js = AtDone();
		js.Execute("press(card, 'Open Heiward'); tourOnLoad(); press($('tour'), 'Skip');");
		Assert.True(js.Evaluate("tourStep === null").AsBoolean());
		Assert.Equal("[\"/api/tour/done\"]", js.Evaluate("JSON.stringify(posted)").AsString());
		Assert.Equal(Waits, Banner(js));
		Assert.Equal(new[] { "Scan now", "Scan by itself" }, BannerButtons(js));
		js.Execute("renderAgent(state);"); // and the polls after it
		Assert.Equal(Waits, Banner(js));
	}

	[Fact]
	public void Later_TheBannerAsks() {
		var js = AtDone();
		js.Execute("press(card, 'Open Heiward'); tourOnLoad(); next(); next(); next(); next(); press($('tour'), 'Later');");
		Assert.True(js.Evaluate("tourStep === null").AsBoolean());
		Assert.Equal(Waits, Banner(js));
		Assert.Equal("[]", js.Evaluate("JSON.stringify(asked)").AsString());
	}

	[Theory]
	[InlineData("Scan now", "scan now")]
	[InlineData("Scan by itself", "by itself")]
	public void AnsweredAtTheTourEnd_NoBannerFlashesUp(string button, string asked) {
		var js = AtDone();
		js.Execute($"press(card, 'Open Heiward'); tourOnLoad(); next(); next(); next(); next(); press($('tour'), {JsonSerializer.Serialize(button)});");
		Assert.Equal(JsonSerializer.Serialize(new[] { asked }), js.Evaluate("JSON.stringify(asked)").AsString());
		Assert.Equal("", Banner(js));
		// Had the answer not gone through, the next poll says the first scan still waits: the banner asks again.
		js.Execute("state.agent.firstScanWaits = true; renderAgent(state);");
		Assert.Equal(Waits, Banner(js));
	}

	[Fact]
	public void Reopened_AfterTheTour_WhileTheFirstScanWaits_TheBannerAsksAtOnce() {
		var js = Page();
		js.Execute("state.tourDue = false; renderAgent(state);"); // the first poll, before tourOnLoad has run
		Assert.Equal(Waits, Banner(js));
		Assert.True(js.Evaluate("tourOnLoad()").AsBoolean());
		Assert.True(js.Evaluate("tourStep === null").AsBoolean());
		Assert.Equal(Waits, Banner(js));
	}

	[Fact]
	public void Reopened_BeforeTheTourWasDone_ItWaitsForTheTourAgain() {
		var js = Page(); // its window was closed while the tour showed: it's still due
		js.Execute("renderAgent(state);");
		Assert.Equal("", Banner(js));
		js.Execute("tourOnLoad(); escape();");
		Assert.Equal(Waits, Banner(js));
	}

	[Fact]
	public void ATourDueOnlyLater_NeverHoldsTheBanner() {
		// Once the page has opened what it opens by itself, only a tour showing holds the banner.
		var js = Page();
		js.Execute("state.tourDue = false; tourOnLoad(); state.tourDue = true; renderAgent(state);");
		Assert.True(js.Evaluate("tourStep === null").AsBoolean());
		Assert.Equal(Waits, Banner(js));
	}

	[Fact]
	public void TakeTheTour_FromAboutOrSettings_HidesItWhileItShows_NotForGood() {
		var js = Page();
		js.Execute("state.tourDue = false; tourOnLoad(); renderAgent(state);");
		Assert.Equal(Waits, Banner(js));
		js.Execute("location.hash = '#/tour'; hashChange();"); // About's or Settings' Take the tour
		Assert.Equal(0, (int)js.Evaluate("tourStep").AsNumber());
		Assert.Equal("", Banner(js));
		js.Execute("renderAgent(state);");
		Assert.Equal("", Banner(js));
		js.Execute("escape();");
		Assert.Equal(Waits, Banner(js));
		js.Execute("renderAgent(state);");
		Assert.Equal(Waits, Banner(js));
		Assert.Equal("[]", js.Evaluate("JSON.stringify(posted)").AsString()); // it wasn't due
	}

	[Fact]
	public void ManorsHireFlow_TheTourFirst_ThenBackToManor() {
		var js = Page();
		js.Execute("state.tourDue = false; location.hash = '#/tour?from=http://localhost:19000/'; renderAgent(state);");
		Assert.Equal("", Banner(js)); // the tour opens in a moment
		js.Execute("tourOnLoad(); renderAgent(state);");
		Assert.Equal("", Banner(js));
		js.Execute("next(); next(); next(); next(); press($('tour'), 'Later');");
		Assert.Equal("http://localhost:19000/", js.Evaluate("location.href").AsString());
		Assert.Equal(Waits, Banner(js)); // should its window come back to Heiward
	}

	[Fact]
	public void TheUninstall_ShowsNoBanner() {
		var js = Page();
		js.Execute("state.tourDue = false; tourOnLoad(); route = { view: 'uninstall' }; renderAgent(state);");
		Assert.Equal("", Banner(js));
	}

	// ---- The tour's "Your choices" skips what the setup asked

	[Fact]
	public void TheStoreVersion_TheTourDoesntAskGameOrDeveloperModeAgain() {
		var js = AtDone();
		js.Execute("press(card, 'Open Heiward'); tourOnLoad(); next();");
		Assert.Equal("Your choices", TourTitle(js));
		Assert.Equal(new[] { "What it scans", "Automatic cleanup" }, Choices(js));
		Assert.Equal("[]", js.Evaluate("JSON.stringify(switches())").AsString());
	}

	[Fact]
	public void TheStoreVersion_ReopenedOrTakenAgain_DoesntAskThemEither() {
		var js = Page(); // a new page: this one didn't run the setup
		js.Execute("tourOnLoad(); next();");
		Assert.Equal(new[] { "What it scans", "Automatic cleanup" }, Choices(js));
	}

	[Fact]
	public void TheCopyFromGitHub_ItsSetupWindowAskedThem_TheTourDoesnt() {
		var js = AtDone("state.about.store = false;");
		js.Execute("press(card, 'Open Heiward'); tourOnLoad(); next();");
		Assert.Equal(new[] { "What it scans", "Automatic cleanup" }, Choices(js));
	}

	[Fact]
	public void TheCopyFromGitHub_InstalledWithoutTheSetupWindow_TheTourAsksThem() {
		var js = Page("state.about.store = false;"); // hei install in a console: nothing asked them
		js.Execute("tourOnLoad(); next();");
		Assert.Equal(new[] { "Game mode", "Developer mode", "What it scans", "Automatic cleanup" }, Choices(js));
		Assert.Equal("[\"Game mode\",\"Developer mode\"]", js.Evaluate("JSON.stringify(switches())").AsString());
	}

	[Fact]
	public void WhereManorDecides_TheTourStillSaysSo_TheSetupHavingAskedNothing() {
		var js = Page("state.games.manor = { note: 'Hill Manor turns this on' }; state.games.enabled = true; state.dev.manor = { note: 'Hill Manor turns this off' };");
		js.Execute("tourOnLoad(); next();");
		// Game mode as Manor has it, without a switch; developer mode, turned off by Manor's Developer options, not at all.
		Assert.Equal(new[] { "Game mode", "What it scans", "Automatic cleanup" }, Choices(js));
		Assert.Equal("[]", js.Evaluate("JSON.stringify(switches())").AsString());
	}

	// ---- Task Scheduler's answer, after the setup

	[Fact]
	public void TheSetupsEnd_ForgetsWhatTaskSchedulerSaidWhileItRan() {
		// The page's polls while the setup showed asked before the install registered the scan task: "missing", kept for
		// half an hour. The install registers it in its own process, so its own Forget never reached the page's.
		bool setUp = false;
		Scheduler.Keep(null, DateTime.UtcNow);
		StoreSetup.Ended(0, () => setUp = true);
		Assert.False(Scheduler.Keeps);
		Assert.True(setUp);
		// Failed part way: it may have registered them all the same.
		Scheduler.Keep(null, DateTime.UtcNow);
		setUp = false;
		StoreSetup.Ended(1, () => setUp = true);
		Assert.False(Scheduler.Keeps);
		Assert.False(setUp);
	}

	// ---- The fakes below are the page's own

	[Fact]
	public void TheFakesBelow_AreThePagesOwn() {
		string app = ThePage.Asset("app.js").ReplaceLineEndings("\n");
		string tour = ThePage.Asset("tour.js").ReplaceLineEndings("\n");
		Assert.Contains("const setupShowing = (s) => s.setup.needed || setupRan;", app);
		Assert.Contains("const scansByItself = (s) => !!(s.schedule && s.schedule.everyMinutes > 0);", app);
		Assert.Contains("const uninstallShowing = () => route.view === 'uninstall' || uninstall.phase !== 'ask';", ThePage.Function(app, "setupDone"));
		Assert.Contains("let tourStep = null;", tour);
		Assert.Contains("let tourLoaded = false;", tour);
		Assert.Contains("window.addEventListener('hashchange', () => {\n  if (isTourHash(location.hash)) startTour(tourFromHash(location.hash));\n});", tour);
		// The setup's Done is what this page's setup ends on; it asks game mode and developer mode in setupModes.
		Assert.Contains("if (setupAnswers.gameMode == null) setupAnswers.gameMode = !!s.games.enabled;", ThePage.Function(app, "setupModes"));
		Assert.Contains("if (setupAnswers.developerMode == null) setupAnswers.developerMode = !!s.dev.enabled;", ThePage.Function(app, "setupModes"));
	}

	/// <summary>The setup window at Done, as this page's setup left it: the Store version's, game and developer mode asked.</summary>
	static Engine AtDone(string? also = null) {
		var js = Page(also);
		js.Execute("setupAnswers.gameMode = false; setupAnswers.developerMode = true; setupRan = true; var card = makeEl('section'); card.replaceChildren.apply(card, setupDone(state)); renderAgent(state);");
		Assert.Equal("", Banner(js));
		return js;
	}

	/// <summary>
	/// The page's banners (app.js: showAgentBanner, renderAgent), the setup's Done (setupDone), and the tour (tour.js: its
	/// start, steps and end, drawn by its own drawTour), against just enough of a page: a DOM of plain objects, the trail,
	/// and a state where a new install's first scan waits and its tour is due.
	/// </summary>
	static Engine Page(string? also = null) {
		string app = ThePage.Asset("app.js").ReplaceLineEndings("\n");
		string tour = ThePage.Asset("tour.js").ReplaceLineEndings("\n");
		var js = new Engine();
		js.Execute("""
			var all = {};
			function makeEl(tag) {
			  var e = {
			    tagName: String(tag).toUpperCase(), handlers: {}, children: [], className: '', textContent: '', dataset: {}, attrs: {}, style: {},
			    classList: {
			      set: {},
			      add: function (c) { this.set[c] = true; },
			      remove: function (c) { delete this.set[c]; },
			      toggle: function (c, on) { if (on === undefined) on = !this.set[c]; if (on) this.set[c] = true; else delete this.set[c]; return on; },
			      contains: function (c) { return !!this.set[c]; },
			    },
			    setAttribute: function (n, v) { this.attrs[n] = String(v); },
			    addEventListener: function (t, f) { (this.handlers[t] = this.handlers[t] || []).push(f); },
			    click: function () { (this.handlers.click || []).forEach(function (f) { f({}); }); },
			    append: function () { for (var i = 0; i < arguments.length; i++) this.children.push(arguments[i]); },
			    replaceChildren: function () { this.children = []; this.append.apply(this, arguments); },
			    remove: function () { var i = document.body.children.indexOf(this); if (i >= 0) document.body.children.splice(i, 1); },
			    focus: function () { },
			    scrollIntoView: function () { },
			  };
			  return e;
			}
			var keydown = [], hashchange = [];
			var document = {
			  createElement: makeEl, body: makeEl('body'), documentElement: makeEl('html'), title: '',
			  addEventListener: function (t, f) { if (t === 'keydown') keydown.push(f); },
			  querySelector: function () { return null; },
			};
			var window = { addEventListener: function (t, f) { if (t === 'hashchange') hashchange.push(f); } };
			function $(id) {
			  if (id === 'tour') return document.body.children.filter(function (c) { return c.id === 'tour'; })[0] || null;
			  return all[id] = all[id] || makeEl('div');
			}
			function find(node, test, found) {
			  found = found || [];
			  if (test(node)) found.push(node);
			  (node.children || []).forEach(function (c) { if (c && typeof c === 'object') find(c, test, found); });
			  return found;
			}
			function press(node, words) {
			  var b = find(node, function (n) { return n.tagName === 'BUTTON' && n.textContent === words; })[0];
			  if (!b) throw new Error('no ' + words + ' button');
			  b.click();
			}
			function next() { press($('tour'), 'Next'); }
			function escape() { keydown.forEach(function (f) { f({ key: 'Escape' }); }); }
			function hashChange() { hashchange.forEach(function (f) { f(); }); }
			function texts(node, cls) { return find(node, function (n) { return n.className === cls; }).map(function (n) { return n.textContent; }); }
			function switches() { return find($('tour'), function (n) { return n.tagName === 'INPUT'; }).map(function (n) { return n.attrs['aria-label']; }); }

			// The page: a new install's, set up, its first scan waiting and its tour due; the Store version.
			var state = {
			  tourDue: true,
			  setup: { needed: false },
			  agent: { paused: false, firstScanWaits: true, scheduleMissing: false, dev: false, firstRound: null },
			  scan: { running: false },
			  schedule: { everyMinutes: 360, text: 'scans every 6 hours on AC power' },
			  config: { scanSpeed: 'background' },
			  games: { enabled: false, manor: null },
			  dev: { enabled: false, manor: null },
			  about: { store: true },
			};
			var posted = [], asked = [];
			var location = { hash: '', href: 'http://heiward.localhost:18484/' };
			var route = { view: 'home' }, homeTab = 'copies';
			var trail = { replace: function (h) { location.hash = h; } };
			function parseRoute() { return { view: location.hash === '#/about' ? 'about' : location.hash === '#/settings' ? 'settings' : 'home' }; }
			function renderRoute() { }
			function post(url) { posted.push(url); return { catch: function () { } }; }
			function scanNow() { asked.push('scan now'); }
			function firstScanByItself() { asked.push('by itself'); }
			function resumeScans() { }
			function startSchedule() { }
			function icon(name) { return makeEl('svg'); }
			function setTitle() { }
			function setAttr() { }
			function setDisabled() { }
			function renderSettling() { }
			function showTab(name) { homeTab = name; }
			function showTabOf() { return true; }
			function saveSettings() { }
			function castellanLink(text, cls) { var a = makeEl('a'); a.textContent = text; a.className = cls || ''; return a; }
			function setupOnNpu() { return false; }
			function setupOnDemand() { return false; }
			function setInterval() { return 1; }
			function clearInterval() { }
			// app.js's own (TheFakesBelow_AreThePagesOwn).
			var setupRan = false, setupShown = null, slideTimer = null, settingsBusy = false;
			var setupAnswers = { gameMode: null, developerMode: null };
			function setupShowing(s) { return s.setup.needed || setupRan; }
			function scansByItself(s) { return !!(s.schedule && s.schedule.everyMinutes > 0); }
			// tour.js's own: two of the page's parts showing.
			var tourStep = null, tourParts = [], tourFrom = null, tourFocus = null, tourTab = null, tourLoaded = false;
			function partsShowing() { return [['controls', 'Where the AI runs, and Scan now', '…'], ['tiles', 'At the top', '…']]; }
			function tourFromHash(hash) { var q = hash.indexOf('?from='); return q < 0 ? null : hash.slice(q + 6); } // no URL in Jint
			""");
		foreach (string name in new[] { "el", "toggleSwitch", "showAgentBanner", "renderAgent", "setupDone" })
			js.Execute(ThePage.Function(app, name));
		foreach (string name in new[] { "tourDue", "tourHolds", "markToured", "isTourHash", "tourScanWords", "startTour", "holdPage",
			"endTour", "answerFirstScan", "setupAskedMode", "clearTourFocus", "drawTour", "tourOnLoad" })
			js.Execute(ThePage.Function(tour, name));
		if (also != null) js.Execute(also);
		return js;
	}

	/// <summary>The banner's words, or "" while it's hidden.</summary>
	static string Banner(Engine js) =>
		js.Evaluate("(function (b) { return b.classList.contains('hidden') ? '' : b.children[0].textContent; })($('agent-banner'))").AsString();

	static string[] BannerButtons(Engine js) => Strings(js, "$('agent-banner').children.slice(1).map(function (b) { return b.textContent; })");

	static string TourTitle(Engine js) => js.Evaluate("find($('tour'), function (n) { return n.id === 'tour-title'; })[0].textContent").AsString();

	static string[] TourButtons(Engine js) => Strings(js, "find($('tour'), function (n) { return n.tagName === 'BUTTON'; }).map(function (b) { return b.textContent; })");

	static string[] Choices(Engine js) => Strings(js, "texts($('tour'), 'auto-title')");

	static string[] Strings(Engine js, string expression) =>
		JsonSerializer.Deserialize<string[]>(js.Evaluate("JSON.stringify(" + expression + ")").AsString())!;
}
