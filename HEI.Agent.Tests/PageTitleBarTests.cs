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
/// The page's title bar in one row (the owner, 2026-11-15: "let's combine these 2 rows to make 1. Drop the heiward icon and
/// home label and add a basic home icon next to the up a level icon. Shorten the url bar and add ellipses if it gets too
/// long, and put the accelerator badge and other icons on the same row"), and the home page's foot moved to Settings
/// ("move the text on this screen at the bottom to the settings screen"). The path's folding runs app.js's own
/// crumbsToFold and fitCrumbs in a JavaScript engine, against crumbs of made-up widths.
/// </summary>
public sealed class PageTitleBarTests {
	static string Page => ThePage.Asset("index.html").ReplaceLineEndings("\n");
	static string Script => ThePage.Asset("app.js").ReplaceLineEndings("\n");
	static string Styles => ThePage.Asset("app.css").ReplaceLineEndings("\n");

	/// <summary>index.html's title bar, from its tag to its end.</summary>
	static string TitleBar() {
		string page = Page;
		int start = page.IndexOf("<header class=\"titlebar\">", StringComparison.Ordinal);
		Assert.True(start > 0);
		return page[start..page.IndexOf("</header>", start, StringComparison.Ordinal)];
	}

	[Fact]
	public void OneRow_BackForwardUpHome_ThePath_ThenTheStatusBadgeButtonsAndScanNow() {
		string bar = TitleBar();
		string[] order = ["id=\"nav-back\"", "id=\"nav-fwd\"", "id=\"nav-up\"", "id=\"nav-home\"", "id=\"crumbs\"", "id=\"subtitle\"", "id=\"ai-badge\"",
			"id=\"pause-btn\"", "id=\"about-btn\"", "id=\"settings-btn\"", "id=\"theme-btn\"", "id=\"scan-now\""];
		int at = -1;
		foreach (string part in order) {
			int next = bar.IndexOf(part, StringComparison.Ordinal);
			Assert.True(next > at, part + " isn't next in the title bar");
			at = next;
		}
		// The second row is gone: no command bar, and nothing of the title bar's left outside it.
		Assert.DoesNotContain("commandbar", Page);
		Assert.DoesNotContain("commandbar", Styles);
		Assert.Equal(1, Page.Split("id=\"crumbs\"").Length - 1);
		// The Location landmark stays, round the buttons and the path.
		Assert.Contains("<nav class=\"location\" aria-label=\"Location\">", bar);
	}

	[Fact]
	public void TheShieldAndTheHomeLabel_AreGone() {
		string bar = TitleBar();
		Assert.DoesNotContain("brand", bar);
		Assert.DoesNotContain("<span>Home</span>", bar);
		Assert.DoesNotContain("<svg", bar);
		Assert.DoesNotContain("$('brand')", Script);
		Assert.DoesNotContain(".brand ", Styles);
		Assert.DoesNotContain(".brand:", Styles);
		// The setup's own banner keeps Heiward's mark.
		Assert.Contains("<div class=\"setup-brand\">\n        <svg class=\"brand-mark\"", Page);
	}

	[Fact]
	public void Home_IsANavButtonBesideUp_WithATooltipAndAName_AndGoesToThisPC() {
		Assert.Contains("<button class=\"icon-btn\" id=\"nav-up\" title=\"Up one level\" aria-label=\"Up one level\"></button>\n" +
			"        <button class=\"icon-btn\" id=\"nav-home\" title=\"Home\" aria-label=\"Home\"></button>", TitleBar());
		string script = Script;
		Assert.Contains("$('nav-home').append(icon('home'));", script);
		Assert.Contains("$('nav-home').addEventListener('click', () => { go(null); $('home').scrollTop = 0; });", script);
		// A house in the same 16 px stroke as Back, Forward and Up.
		Assert.Matches(@"\n  home: \[\['path', '[^']+', 'stroke'\]\],\n", script);
	}

	[Fact]
	public void ThePathBar_TakesTheRoomLeft_UpTo560Px_InOneLine() {
		string css = Styles;
		Assert.Contains("flex: 1 1 0; min-width: 0; max-width: 560px; height: 32px; overflow: hidden;", css);
		Assert.Contains(".crumbs li.folded { display: none; }", css);
		Assert.Contains(".crumb span { overflow: hidden; text-overflow: ellipsis; min-width: 0; }", css);
	}

	[Fact]
	public void TheScansStatus_IsBesideTheBadge_AndScanNowsTooltip() {
		string render = ThePage.Function(Script, "renderHeader");
		Assert.Contains("setText($('subtitle'), status);", render);
		Assert.Contains("setTitle(scanBtn, status);", render);
		// Beside the badge, in the controls the tour points at.
		string bar = TitleBar();
		Assert.True(bar.IndexOf("data-tour=\"controls\"", StringComparison.Ordinal) < bar.IndexOf("id=\"subtitle\"", StringComparison.Ordinal));
		Assert.Contains(".titlebar.fold-status .subtitle { display: none; }", Styles);
	}

	[Fact]
	public void ANarrowWindow_FoldsTheRest_InOrder_ThePathTakingARowOfItsOwnLast() {
		string script = Script;
		Assert.Contains("const TITLE_FOLDS = ['fold-manor', 'fold-badge', 'fold-status', 'fold-wrap'];", script);
		string css = Styles;
		foreach (string fold in new[] { ".titlebar.fold-manor .manor-back span { display: none; }", ".titlebar.fold-badge .ai-badge { font-size: 0; }",
			".titlebar.fold-status .subtitle { display: none; }", ".titlebar.fold-wrap .location { order: 2; flex-basis: 100%; }" })
			Assert.Contains(fold, css);
		// The badge's short word, from renderAiBadge; hidden from a screen reader, which has its whole words.
		Assert.Contains("content: attr(data-short) / '';", css);
		Assert.Contains("badge.dataset.short = short;", ThePage.Function(script, "renderAiBadge"));
		// Again whenever the window's width changes, and whenever what the row says does.
		Assert.Contains("new ResizeObserver(() => {", script);
		Assert.Contains("if (fits !== titleFits) { titleFits = fits; fitTitlebar(); }", ThePage.Function(script, "renderHeader"));
	}

	// ---- The path's folding

	readonly Engine js = new();

	const string FakeCrumbs = """
		// Crumbs of given widths in a bar of a given width: just enough of a DOM for app.js's fitCrumbs.
		function classes(e) {
		  return {
		    has: {},
		    add: function () { for (var i = 0; i < arguments.length; i++) this.has[arguments[i]] = true; },
		    remove: function () { for (var i = 0; i < arguments.length; i++) delete this.has[arguments[i]]; },
		    toggle: function (c, on) { if (on === undefined) on = !this.has[c]; if (on) this.has[c] = true; else delete this.has[c]; return on; },
		    contains: function (c) { return !!this.has[c]; },
		  };
		}
		function crumbLi(text, width) {
		  var b = { title: '', textContent: text };
		  var li = { textContent: text, button: b, width: width, classList: classes() };
		  li.getBoundingClientRect = function () { return { width: li.classList.contains('folded') ? 0 : li.width }; };
		  li.querySelector = function () { return b; };
		  return li;
		}
		var crumbs = { children: [], clientWidth: 0, classList: classes() };
		crumbs.querySelector = function () { return crumbs.children.filter(function (li) { return li.classList.contains('crumb-fold'); })[0] || null; };
		function getComputedStyle() { return { columnGap: '2px', paddingLeft: '6px', paddingRight: '6px' }; }
		function $(id) { return crumbs; }
		function setTitle(node, title) { node.title = title; }
		// The crumbs (each [name, width]) after This PC, in a bar this wide; "…" is 40 px.
		function lay(barWidth, names) {
		  var lis = [crumbLi('This PC', 80)];
		  if (names.length > 1) { var fold = crumbLi('…', 40); fold.classList.add('crumb-fold', 'folded'); lis.push(fold); }
		  names.forEach(function (n) { lis.push(crumbLi(n[0], n[1])); });
		  crumbs.children = lis;
		  crumbs.clientWidth = barWidth;
		  fitCrumbs();
		  return {
		    shown: lis.filter(function (li) { return !li.classList.contains('folded'); })
		      .map(function (li) { return li.textContent + (li.classList.contains('bare') ? ' (icon)' : ''); }),
		    more: names.length > 1 ? lis[1].button.title : null,
		  };
		}
		""";

	public PageTitleBarTests() {
		js.Execute(FakeCrumbs);
		foreach (string name in new[] { "crumbsToFold", "fitCrumbs" })
			js.Execute(ThePage.Function(Script, name));
	}

	(string[] Shown, string? More) Lay(int bar, params (string Name, int Width)[] crumbs) {
		string names = JsonSerializer.Serialize(crumbs.Select(c => new object[] { c.Name, c.Width }));
		using JsonDocument laid = JsonDocument.Parse(js.Evaluate($"JSON.stringify(lay({bar}, {names}))").AsString());
		JsonElement more = laid.RootElement.GetProperty("more");
		return (laid.RootElement.GetProperty("shown").EnumerateArray().Select(e => e.GetString()!).ToArray(),
			more.ValueKind == JsonValueKind.Null ? null : more.GetString());
	}

	static readonly (string, int)[] Deep = [("Backup (D:)", 110), ("Block 0", 70), ("E Drive", 70), ("Pictures", 80), ("Day 3", 60)];

	[Fact]
	public void APathThatFits_ShowsEveryCrumb_AndNoEllipsis() {
		var (shown, more) = Lay(600, Deep);
		Assert.Equal(["This PC", "Backup (D:)", "Block 0", "E Drive", "Pictures", "Day 3"], shown);
		Assert.Equal("", more);
	}

	[Fact]
	public void APathTooLong_FoldsItsMiddle_NearestThisPCFirst_KeepingWhereYouAre() {
		// 80 + 110 + 70 + 70 + 80 + 60 and a 2 px gap after each: 482 in a bar of 400 (388 inside its padding).
		var (shown, more) = Lay(400, Deep);
		Assert.Equal(["This PC", "…", "E Drive", "Pictures", "Day 3"], shown);
		Assert.Equal("Backup (D:) › Block 0", more);
		(shown, more) = Lay(300, Deep);
		Assert.Equal(["This PC", "…", "Pictures", "Day 3"], shown);
		Assert.Equal("Backup (D:) › Block 0 › E Drive", more);
	}

	[Fact]
	public void ANarrowBar_KeepsThisPC_AndTheLast_ThisPCAsItsIconWhenEvenThatIsTooWide() {
		var (shown, more) = Lay(200, Deep);
		Assert.Equal(["This PC", "…", "Day 3"], shown);
		Assert.Equal("Backup (D:) › Block 0 › E Drive › Pictures", more);
		(shown, _) = Lay(200, ("Backup (D:)", 110), ("A very long folder name indeed", 260));
		Assert.Equal(["This PC (icon)", "…", "A very long folder name indeed"], shown);
	}

	[Fact]
	public void OneCrumbAfterThisPC_NeverFolds_ItsNameEndsInAnEllipsisInstead() {
		var (shown, more) = Lay(150, ("A very long drive name (D:)", 260));
		Assert.Equal(["This PC", "A very long drive name (D:)"], shown);
		Assert.Null(more);
		Assert.Contains("if (items.length > 2) items.splice(1, 0, crumbFold());", ThePage.Function(Script, "renderCrumbs"));
	}

	[Theory]
	[InlineData("[80, 100, 100]", 300, 40, 0)]
	[InlineData("[80, 100, 100, 100]", 300, 40, 2)]
	[InlineData("[80, 100, 100, 100]", 330, 40, 1)]
	[InlineData("[80, 300]", 100, 40, 0)] // nothing between to fold
	[InlineData("[80, 100, 300]", 100, 40, 1)] // never the last
	public void CrumbsToFold_CountsFromTheSecond(string widths, int room, int more, int folded) =>
		Assert.Equal(folded, (int)js.Evaluate($"crumbsToFold({widths}, {room}, {more})").AsNumber());

	[Fact]
	public void TheWholePath_IsTheBarsTooltip_AndTheEllipsisOpensTheFoldedOnes() {
		string render = ThePage.Function(Script, "renderCrumbs");
		Assert.Contains("const full = route.view === 'folder' && cardFor(route.path) ? route.path : items.map((li) => li.textContent).join(' › ');", render);
		Assert.Contains("setTitle(crumbs, full);", render);
		string fold = ThePage.Function(Script, "crumbFold");
		Assert.Contains("b.setAttribute('aria-label', 'More of the path');", fold);
		Assert.Contains("showMenu(e, 'Path', items);", fold);
	}

	// ---- The home page's foot, in Settings

	[Fact]
	public void TheHomePage_HasNoFoot() {
		Assert.DoesNotContain("id=\"footer\"", Page);
		Assert.DoesNotContain("renderFooter", Script);
	}

	/// <summary>Settings' Everything else card for <paramref name="config"/> (JSON), as its text.</summary>
	static string MoreCard(string config, bool dev) {
		var js = new Engine();
		js.Execute("""
			function makeEl(tag) {
			  return {
			    tagName: tag.toUpperCase(), children: [], className: '', textContent: '',
			    addEventListener: function () { },
			    append: function () { for (var i = 0; i < arguments.length; i++) this.children.push(arguments[i]); },
			    replaceChildren: function () { this.children = []; this.append.apply(this, arguments); },
			  };
			}
			var document = { createElement: makeEl };
			var card = makeEl('div');
			function $(id) { return card; }
			function text(n) { return typeof n === 'string' ? n : n.textContent + n.children.map(text).join(''); }
			var SEP = '\\';
			function trimSep(p) { return p.replace(/\\+$/, ''); }
			function acceleratorNotes() { return []; }
			function takeTheTour() { }
			""");
		js.Execute(ThePage.Function(Script, "el"));
		js.Execute(ThePage.Function(Script, "renderMoreCard"));
		return js.Evaluate($"renderMoreCard({{ config: {config}, dev: {{ enabled: {(dev ? "true" : "false")} }}, accelerators: null, gpu: null }}); text(card)").AsString();
	}

	[Fact]
	public void Settings_SaysWhatTheFootSaid() {
		// What's scanned, the exempt folders greyed out, the file types left out, and the settings file with developer mode on.
		string all = MoreCard("{ allDrives: true, folders: ['C:\\\\', 'D:\\\\'], excludeExtensions: ['.tmp', '.part'], aiDevice: 'auto', " +
			"path: 'C:\\\\Users\\\\Example\\\\AppData\\\\Local\\\\Heiward\\\\settings.json' }", dev: true);
		Assert.Contains("Every fixed drive, minus Windows, programs, games, app data and code.", all);
		Assert.Contains("The folders left out show greyed out as exempt.", all);
		Assert.Contains("Skipped file types.tmp .part", all);
		Assert.Contains(@"Settings file: C:\Users\Example\AppData\Local\Heiward\settings.json", all);
		// Only some folders scanned: those.
		string some = MoreCard("{ allDrives: false, folders: ['D:\\\\Photos'], excludeExtensions: [], aiDevice: 'auto', path: null }", dev: false);
		Assert.Contains(@"What's scannedD:\Photos", some);
		Assert.DoesNotContain("greyed out", some);
		Assert.DoesNotContain("Settings file", some);
	}
}
