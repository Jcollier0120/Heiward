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
/// The command bar's Back, Forward and Up (the owner, 2026-11-09): in the folder view Back and Up used to do much the
/// same, Back climbing the folders one by one. Now Back goes to the last page you were on, skipping the folder steps
/// (trail.js), Forward does the same the other way, and Up goes one folder up, and from a drive's root to This PC.
/// These run the page's own trail.js, and app.js's routing and Up, in a JavaScript engine, against a tab whose history
/// behaves as a browser's: a hash you go to is a new entry and a hashchange, history.go lands later (Settle), and the
/// history and sessionStorage outlast a reload.
/// </summary>
public sealed class TrailTests {
	static string Asset(string name) {
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/" + name)!;
		return new StreamReader(s).ReadToEnd().ReplaceLineEndings("\n"); // a checkout's own line endings
	}

	/// <summary>app.js from <paramref name="from"/> up to <paramref name="to"/>.</summary>
	static string Slice(string from, string to) {
		string script = Asset("app.js");
		int start = script.IndexOf(from, StringComparison.Ordinal);
		Assert.True(start >= 0, from);
		int end = script.IndexOf(to, start + from.Length, StringComparison.Ordinal);
		Assert.True(end > start, to);
		return script[start..end];
	}

	/// <summary>A browser tab's history, location and sessionStorage, as trail.js and app.js use them.</summary>
	const string FakeTab = """
		var trail;
		var gone = [];
		var win = (function () {
		  var entries = [{ hash: START, state: null }];
		  var index = 0;
		  var store = {};
		  var pending = [];
		  function clone(v) { return v == null ? null : JSON.parse(JSON.stringify(v)); }
		  // A browser fires hashchange when the entry it lands on has another hash; app.js's handler starts with trail.sync().
		  function arrive(before) { if (entries[index].hash !== before) trail.sync(); }
		  var w = {
		    location: {
		      get hash() { return entries[index].hash; },
		      set hash(v) {
		        if (v === entries[index].hash) return;
		        entries.length = index + 1;
		        entries.push({ hash: v, state: null });
		        index++;
		        trail.sync();
		      },
		    },
		    history: {
		      get state() { return clone(entries[index].state); },
		      replaceState: function (state, title, url) {
		        entries[index].state = clone(state);
		        if (url !== undefined) entries[index].hash = url;
		      },
		      go: function (d) { gone.push(d); pending.push(d); },
		    },
		    sessionStorage: {
		      getItem: function (k) { return Object.prototype.hasOwnProperty.call(store, k) ? store[k] : null; },
		      setItem: function (k, v) { store[k] = String(v); },
		    },
		  };
		  // history.go lands after the click's script has run.
		  w.settle = function () {
		    while (pending.length) {
		      var before = entries[index].hash;
		      index = Math.max(0, Math.min(entries.length - 1, index + pending.shift()));
		      arrive(before);
		    }
		  };
		  w.browserBack = function () { var before = entries[index].hash; index--; arrive(before); };
		  w.browserForward = function () { var before = entries[index].hash; index++; arrive(before); };
		  w.reload = function () { trail = makeTrail(w); };
		  w.entries = function () { return entries.length; };
		  return w;
		})();
		var location = win.location;
		trail = makeTrail(win);
		""";

	/// <summary>One tab with Heiward's page in it, on a PC with drive E: (Photos) and C:.</summary>
	sealed class Tab {
		readonly Engine js = new();

		public Tab(string start = "#/") {
			js.SetValue("START", start);
			js.Execute(Asset("trail.js"));
			js.Execute(FakeTab);
			// app.js's own paths, routing and Up, with the drives a scan found.
			js.Execute("var state = { drives: [{ root: 'E:\\\\', name: 'Photos' }, { root: 'C:\\\\', name: 'Windows' }] };\n"
				+ "const SEP = '\\\\';\nfunction renderRoute() { }\nvar route;\n"
				+ Slice("const key = (p)", "\nfunction nameOf(") + "\n"
				+ Slice("function parseRoute()", "\n/** Back and Forward by page") + "\n"
				+ "function up() { route = parseRoute();\n" + Slice("$('nav-up').addEventListener('click', () => {", "\n});")[("$('nav-up').addEventListener('click', () => {").Length..] + "\n}");
		}

		static string Q(string s) => JsonSerializer.Serialize(s);

		/// <summary>A link or button to another of the page's hashes.</summary>
		public Tab Open(string hash) { js.Execute($"location.hash = {Q(hash)};"); return this; }
		/// <summary>A folder, as the page opens one (app.js, go).</summary>
		public Tab Folder(string path) { js.Execute($"go({Q(path)});"); return this; }
		public string Here => js.Evaluate("location.hash").AsString();
		public bool CanBack => js.Evaluate("trail.canBack()").AsBoolean();
		public bool CanForward => js.Evaluate("trail.canForward()").AsBoolean();
		/// <summary>The page's Back, and the browser landing where it went.</summary>
		public Tab Back() { Click("trail.back()"); return Settle(); }
		public Tab Forward() { Click("trail.forward()"); return Settle(); }
		public Tab Up() { js.Execute("up();"); return this; }
		public void Click(string code) => js.Execute(code + ";");
		public Tab Settle() { js.Execute("win.settle();"); return this; }
		public Tab BrowserBack() { js.Execute("win.browserBack();"); return this; }
		public Tab BrowserForward() { js.Execute("win.browserForward();"); return this; }
		public Tab Reload() { js.Execute("win.reload();"); return this; }
		public Tab Replace(string hash) { js.Execute($"trail.replace({Q(hash)});"); return this; }
		public int Entries => (int)js.Evaluate("win.entries()").AsNumber();
		/// <summary>Each history.go the page asked for, by how many steps.</summary>
		public int[] Gone => JsonSerializer.Deserialize<int[]>(js.Evaluate("JSON.stringify(gone)").AsString())!;
	}

	static string F(string path) => "#/f/" + Uri.EscapeDataString(path);

	// ---- Back

	[Fact]
	public void Back_FromAFolderThreeLevelsDown_GoesToThisPC_InOneStep() {
		var tab = new Tab().Folder("E:\\").Folder("E:\\Photos").Folder("E:\\Photos\\2024");
		Assert.Equal(F("E:\\Photos\\2024"), tab.Here);
		Assert.True(tab.CanBack);
		tab.Back();
		Assert.Equal("#/", tab.Here);
		Assert.Equal(new[] { -3 }, tab.Gone); // one jump, not a step per folder
		Assert.False(tab.CanBack);
	}

	[Fact]
	public void Back_GoesToThePageYouCameFrom() {
		var tab = new Tab().Open("#/settings").Folder("E:\\").Folder("E:\\Photos");
		tab.Back();
		Assert.Equal("#/settings", tab.Here);
		tab.Back();
		Assert.Equal("#/", tab.Here);
	}

	[Fact]
	public void Back_FromThePageAfterAFolderView_GoesToTheFolderYouLeft() {
		var tab = new Tab().Folder("E:\\").Folder("E:\\Photos").Open("#/about");
		tab.Back();
		Assert.Equal(F("E:\\Photos"), tab.Here); // the folder view, as you left it
		tab.Back();
		Assert.Equal("#/", tab.Here);
	}

	[Fact]
	public void Back_FromThePagesBetween_StepsOneEach() {
		var tab = new Tab().Open("#/settings").Open("#/about");
		tab.Back();
		Assert.Equal("#/settings", tab.Here);
		tab.Back();
		Assert.Equal("#/", tab.Here);
	}

	[Fact]
	public void Back_SkipsAnAreasParts_AsItDoesFolders() {
		var tab = new Tab().Open("#/dev").Open("#/dev/s/node_modules").Open("#/dev/g/Projects");
		tab.Back();
		Assert.Equal("#/", tab.Here);
		var games = new Tab().Open("#/settings").Open("#/games").Open("#/games/d/E%3A%5C").Open("#/games/g/Some%20game");
		games.Back();
		Assert.Equal("#/settings", games.Here);
	}

	[Fact]
	public void ANewTab_HasNowhereToGoBackOrForward() {
		var tab = new Tab();
		Assert.False(tab.CanBack);
		Assert.False(tab.CanForward);
		tab.Back().Forward();
		Assert.Empty(tab.Gone);
		Assert.Equal("#/", tab.Here);
	}

	[Fact]
	public void AFolderView_OpenedFirst_HasNoBack() {
		var tab = new Tab(F("E:\\Photos")).Folder("E:\\Photos\\2024");
		Assert.False(tab.CanBack);
	}

	[Fact]
	public void ADoubleClick_GoesBackOnce() {
		var tab = new Tab().Open("#/settings").Folder("E:\\").Folder("E:\\Photos");
		tab.Click("trail.back()");
		tab.Click("trail.back()"); // before the browser has got there
		Assert.Equal(new[] { -2 }, tab.Gone);
		tab.Settle();
		Assert.Equal("#/settings", tab.Here);
		tab.Back(); // and once it has, Back works again
		Assert.Equal("#/", tab.Here);
	}

	// ---- Forward

	[Fact]
	public void Forward_ReturnsToTheLastFolderYouHadOpen() {
		var tab = new Tab().Folder("E:\\").Folder("E:\\Photos").Folder("E:\\Photos\\2024").Back();
		Assert.True(tab.CanForward);
		tab.Forward();
		Assert.Equal(F("E:\\Photos\\2024"), tab.Here);
		Assert.Equal(new[] { -3, 3 }, tab.Gone);
		Assert.False(tab.CanForward);
	}

	[Fact]
	public void Forward_GoesPageByPage() {
		var tab = new Tab().Open("#/settings").Folder("E:\\").Folder("E:\\Photos").Open("#/about").Back().Back().Back();
		Assert.Equal("#/", tab.Here);
		tab.Forward();
		Assert.Equal("#/settings", tab.Here);
		tab.Forward();
		Assert.Equal(F("E:\\Photos"), tab.Here);
		tab.Forward();
		Assert.Equal("#/about", tab.Here);
		Assert.False(tab.CanForward);
	}

	[Fact]
	public void InTheMiddleOfAFolderView_BackAndForward_LeaveIt() {
		// The browser's own Back steps one folder at a time, as it always did; the page's buttons go by page from there.
		var tab = new Tab().Folder("E:\\").Folder("E:\\Photos").Folder("E:\\Photos\\2024").Open("#/settings").BrowserBack().BrowserBack();
		Assert.Equal(F("E:\\Photos"), tab.Here);
		Assert.True(tab.CanForward);
		tab.Forward();
		Assert.Equal("#/settings", tab.Here); // past the folder view's other steps
		tab.BrowserBack().BrowserBack();
		Assert.Equal(F("E:\\Photos"), tab.Here);
		tab.Back();
		Assert.Equal("#/", tab.Here);
	}

	[Fact]
	public void ANewStepAfterBack_DropsTheOnesAhead() {
		var tab = new Tab().Open("#/settings").Open("#/about").Back();
		Assert.True(tab.CanForward);
		tab.Folder("E:\\");
		Assert.False(tab.CanForward);
		tab.Back();
		Assert.Equal("#/settings", tab.Here);
		tab.Forward();
		Assert.Equal(F("E:\\"), tab.Here);
	}

	[Fact]
	public void TheBrowsersOwnBackAndForward_AreFollowed() {
		var tab = new Tab().Open("#/settings").Open("#/about").BrowserBack();
		Assert.Equal("#/settings", tab.Here);
		tab.Back();
		Assert.Equal("#/", tab.Here);
		tab.BrowserForward().BrowserForward();
		Assert.Equal("#/about", tab.Here);
		Assert.False(tab.CanForward);
		tab.Back();
		Assert.Equal("#/settings", tab.Here);
	}

	// ---- A reload, and a hash the page puts in place of another

	[Fact]
	public void AReload_KeepsTheTrail() {
		var tab = new Tab().Open("#/settings").Folder("E:\\").Folder("E:\\Photos").Reload();
		Assert.True(tab.CanBack);
		tab.Back();
		Assert.Equal("#/settings", tab.Here);
		tab.Reload();
		Assert.True(tab.CanForward);
		tab.Forward();
		Assert.Equal(F("E:\\Photos"), tab.Here);
	}

	[Fact]
	public void APageThatReplacesItsHash_KeepsItsPlace() {
		// The Developer area turned off since: the page shows This PC in its place (history.replaceState, through the trail).
		var tab = new Tab().Open("#/settings").Open("#/dev");
		int entries = tab.Entries;
		tab.Replace("#/");
		Assert.Equal("#/", tab.Here);
		Assert.Equal(entries, tab.Entries);
		tab.Back();
		Assert.Equal("#/settings", tab.Here);
		tab.Forward();
		Assert.Equal("#/", tab.Here);
	}

	[Fact]
	public void TheFirstHash_MayBeEmpty() {
		var tab = new Tab("").Open("#/").Folder("E:\\");
		tab.Back();
		Assert.Equal("#/", tab.Here); // "" and "#/" are both This PC: one page
		Assert.False(tab.CanBack);
	}

	// ---- Up

	[Fact]
	public void Up_ClimbsTheFolders_AndFromADrivesRoot_GoesToThisPC() {
		var tab = new Tab().Folder("E:\\").Folder("E:\\Photos").Folder("E:\\Photos\\2024");
		tab.Up();
		Assert.Equal(F("E:\\Photos"), tab.Here);
		tab.Up();
		Assert.Equal(F("E:\\"), tab.Here);
		tab.Up();
		Assert.Equal("#/", tab.Here);
		Assert.Empty(tab.Gone); // Up opens the folder above: a step of its own, not a jump back
	}

	[Fact]
	public void UpAndBack_NoLongerDoTheSame() {
		var up = new Tab().Open("#/settings").Folder("E:\\").Folder("E:\\Photos").Folder("E:\\Photos\\2024").Up();
		Assert.Equal(F("E:\\Photos"), up.Here);
		var back = new Tab().Open("#/settings").Folder("E:\\").Folder("E:\\Photos").Folder("E:\\Photos\\2024").Back();
		Assert.Equal("#/settings", back.Here);
	}

	[Fact]
	public void Up_FromSettingsOrAbout_GoesToThisPC_AndFromAnAreasPart_ToTheAreasFirstPage() {
		Assert.Equal("#/", new Tab().Open("#/settings").Up().Here);
		Assert.Equal("#/", new Tab().Open("#/about").Up().Here);
		var dev = new Tab().Open("#/dev").Open("#/dev/s/node_modules").Up();
		Assert.Equal("#/dev", dev.Here);
		Assert.Equal("#/", dev.Up().Here);
		var games = new Tab().Open("#/games/g/Some%20game").Up();
		Assert.Equal("#/games", games.Here);
	}

	[Fact]
	public void AfterUpToThisPC_Back_GoesToTheFolderViewAsYouLeftIt() {
		var tab = new Tab().Open("#/settings").Folder("E:\\").Folder("E:\\Photos").Up().Up();
		Assert.Equal("#/", tab.Here);
		tab.Back();
		Assert.Equal(F("E:\\"), tab.Here);
		tab.Back();
		Assert.Equal("#/settings", tab.Here);
	}

	// ---- trailPage

	[Theory]
	[InlineData("", "#/")]
	[InlineData("#", "#/")]
	[InlineData("#/", "#/")]
	[InlineData("#/settings", "#/settings")]
	[InlineData("#/f/E%3A%5C", "folder")]
	[InlineData("#/f/C%3A%5CUsers", "folder")]
	[InlineData("#/dev", "#/dev")]
	[InlineData("#/dev/s/node_modules", "#/dev")]
	[InlineData("#/games/k/shaders", "#/games")]
	[InlineData("#/developer", "#/developer")] // not the area: only its own hashes
	public void EachStep_IsOnAPage(string hash, string page) {
		var js = new Engine();
		js.Execute(Asset("trail.js"));
		Assert.Equal(page, js.Invoke("trailPage", hash, 0).AsString());
	}

	// ---- The page's wiring

	[Fact]
	public void ThePage_LoadsTheTrail_BeforeAppJs() {
		string page = Asset("index.html");
		int trail = page.IndexOf("<script src=\"/trail.js\" defer></script>", StringComparison.Ordinal);
		Assert.True(trail > 0);
		Assert.True(trail < page.IndexOf("<script src=\"/app.js\" defer></script>", StringComparison.Ordinal));
		// Served as its own file, as app.js is (ReviewServer).
		Assert.Contains("app.MapGet(\"/trail.js\"", File.ReadAllText(Path.Combine(RepoRoot(), "HEI.Agent", "ReviewServer.cs")));
	}

	[Fact]
	public void TheButtons_AreTheTrails() {
		string script = Asset("app.js");
		Assert.Contains("$('nav-back').addEventListener('click', () => trail.back());", script);
		Assert.Contains("$('nav-fwd').addEventListener('click', () => trail.forward());", script);
		Assert.Contains("$('nav-back').disabled = !trail.canBack();", script);
		Assert.Contains("$('nav-fwd').disabled = !trail.canForward();", script);
		Assert.Contains("window.addEventListener('hashchange', () => {\n  trail.sync();", script);
		// Every hash put in place of another goes through the trail, so the step keeps its place.
		foreach (string name in new[] { "app.js", "tour.js" }) {
			string code = Asset(name);
			Assert.DoesNotContain("history.replaceState(", code);
			Assert.DoesNotContain("history.pushState(", code);
			Assert.DoesNotContain("history.back(", code);
			Assert.DoesNotContain("history.forward(", code);
		}
	}

	static string RepoRoot() {
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "HEI.Agent", "ReviewServer.cs"))) dir = Path.GetDirectoryName(dir);
		Assert.NotNull(dir);
		return dir!;
	}
}
