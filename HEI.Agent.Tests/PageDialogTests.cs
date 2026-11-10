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
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;
using Jint;

namespace HEI.Agent.Tests;

/// <summary>
/// The page asks in its own dialog, never in the browser's confirm(), alert() or prompt() windows (the owner, 2026-11-10:
/// "let's stop using these alert windows and put in a nicer modal instead"). The scripts are read by a JavaScript parser,
/// so a comment that names them doesn't count; and app.js's askDialog and askTextDialog run in a JavaScript engine,
/// against a page with just enough of a DOM for them: what they show, how they're answered, where the focus goes, and
/// questions asked together waiting their turn.
/// </summary>
public sealed class PageDialogTests {
	static readonly string[] BrowserWindows = ["confirm", "alert", "prompt"];

	static string Asset(string name) => ThePage.Asset(name).ReplaceLineEndings("\n"); // a checkout's own line endings

	/// <summary>Every script the page loads, as hei.exe carries them.</summary>
	static IEnumerable<string> Scripts() =>
		typeof(ReviewServer).Assembly.GetManifestResourceNames()
			.Where(n => n.StartsWith("wwwroot/", StringComparison.Ordinal) && n.EndsWith(".js", StringComparison.Ordinal))
			.Select(n => n["wwwroot/".Length..]);

	static IEnumerable<Node> AllNodes(Node node) {
		var todo = new Stack<Node>();
		todo.Push(node);
		while (todo.Count > 0) {
			Node n = todo.Pop();
			yield return n;
			foreach (Node child in n.ChildNodes) todo.Push(child);
		}
	}

	static Script Parse(string script) => new Parser().ParseScript(script);

	/// <summary>Each call in <paramref name="script"/> to a function of that name, or to window's, as "name at line".</summary>
	static List<string> CallsTo(string script, IReadOnlyCollection<string> names) {
		var found = new List<string>();
		foreach (CallExpression call in AllNodes(Parse(script)).OfType<CallExpression>()) {
			string? name = call.Callee switch {
				Identifier id => id.Name,
				MemberExpression { Computed: false, Object: Identifier { Name: "window" or "globalThis" or "self" }, Property: Identifier p } => p.Name,
				_ => null,
			};
			if (name != null && names.Contains(name)) found.Add(name + " at line " + call.Location.Start.Line);
		}
		return found;
	}

	[Fact]
	public void ThePage_NeverOpensTheBrowsersWindows() {
		var scripts = Scripts().ToList();
		Assert.Contains("app.js", scripts);
		Assert.Contains("tour.js", scripts);
		foreach (string name in scripts) {
			List<string> calls = CallsTo(Asset(name), BrowserWindows);
			Assert.True(calls.Count == 0, name + " opens the browser's own window: " + string.Join(", ", calls));
		}
		// The parser does find calls: each question the page asked in one of those it asks in its own.
		Assert.True(CallsTo(Asset("app.js"), ["askDialog"]).Count >= 12);
		Assert.Single(CallsTo(Asset("app.js"), ["askTextDialog"]));
	}

	[Fact]
	public void ThePage_WaitsForEachAnswer_BeforeItActs() {
		string script = Asset("app.js");
		// Every question is awaited where it's asked: what it asks about runs only once it's answered.
		string[] asks = ["askDialog", "askTextDialog"];
		int awaited = AllNodes(Parse(script)).OfType<AwaitExpression>()
			.Count(a => a.Argument is CallExpression { Callee: Identifier id } && asks.Contains(id.Name));
		Assert.Equal(CallsTo(script, asks).Count, awaited);
		Assert.True(awaited >= 13);
		// A switch of automatic cleanup shows on while it asks, and Cancel puts it back.
		string auto = ThePage.Function(script, "renderAutoCard");
		Assert.Equal(3, Regex.Matches(auto, Regex.Escape("{ box.checked = false; return; }")).Count);
		// Going to another page answers Cancel to its questions, as it closes a game's Uninstall dialog.
		Assert.Matches(@"window\.addEventListener\('hashchange', \(\) => \{\n  trail\.sync\(\);\n  closeUninstallDialog\(\);[^\n]*\n  dropQuestions\(\);", script);
	}

	[Fact]
	public void ThePage_HasTheDialogsLook_InEveryTheme() {
		string css = Asset("app.css");
		Assert.Contains(".modal.ask { z-index: 90; }", css); // over the tour (60) too
		Assert.Contains(".ask-text { line-height: 1.5; white-space: pre-line; overflow-wrap: anywhere; }", css);
		// The theme's own colours: the danger button in its alert colour, on any theme's surface.
		Assert.Contains(".btn.danger { background: var(--alert); color: var(--surface); }", css);
	}

	// ---- The dialog itself, run

	/// <summary>Just enough of a page for the dialog: elements in a tree, the focus, and the document's keydown listeners.</summary>
	const string FakePage = """
		var focused = null;
		function makeEl(tag) {
		  return {
		    tagName: tag.toUpperCase(), children: [], parent: null, attrs: {}, handlers: {},
		    className: '', textContent: '', id: '', type: '', value: '', inert: false, selectedAll: false,
		    setAttribute: function (k, v) { this.attrs[k] = String(v); },
		    getAttribute: function (k) { return Object.prototype.hasOwnProperty.call(this.attrs, k) ? this.attrs[k] : null; },
		    append: function () {
		      for (var i = 0; i < arguments.length; i++) { var c = arguments[i]; c.parent = this; this.children.push(c); }
		    },
		    remove: function () {
		      if (!this.parent) return;
		      var s = this.parent.children;
		      s.splice(s.indexOf(this), 1);
		      this.parent = null;
		    },
		    addEventListener: function (t, f) { (this.handlers[t] = this.handlers[t] || []).push(f); },
		    focus: function () { focused = this; },
		    select: function () { this.selectedAll = true; },
		    click: function () { var me = this; (this.handlers.click || []).forEach(function (f) { f({ target: me }); }); },
		    get isConnected() { for (var n = this; n; n = n.parent) if (n === document.body) return true; return false; },
		  };
		}
		var document = {
		  body: makeEl('body'),
		  keydown: [],
		  createElement: makeEl,
		  get activeElement() { return focused && focused.isConnected ? focused : document.body; },
		  addEventListener: function (t, f) { if (t === 'keydown') this.keydown.push(f); },
		  removeEventListener: function (t, f) { if (t === 'keydown') this.keydown = this.keydown.filter(function (x) { return x !== f; }); },
		};
		// The page under the dialog: its views, and a button that asked.
		var main = makeEl('main'), asker = makeEl('button');
		main.append(asker);
		document.body.append(main, makeEl('footer'));
		asker.focus();
		function press(key, shift) {
		  var e = { key: key, shiftKey: !!shift, target: document.activeElement, isComposing: false, prevented: false,
		    preventDefault: function () { this.prevented = true; }, stopPropagation: function () { } };
		  document.keydown.slice().forEach(function (f) { f(e); });
		  return e.prevented;
		}
		function find(node, test) {
		  if (test(node)) return node;
		  for (var i = 0; i < node.children.length; i++) { var f = find(node.children[i], test); if (f) return f; }
		  return null;
		}
		function dialog() { return find(document.body, function (n) { return n.className === 'modal ask'; }); }
		function dialogs() { return document.body.children.filter(function (n) { return n.className === 'modal ask'; }).length; }
		function button(words) { return find(dialog(), function (n) { return n.tagName === 'BUTTON' && n.textContent === words; }); }
		function field() { return find(dialog(), function (n) { return n.tagName === 'INPUT'; }); }
		var answers = [];
		""";

	/// <summary>The page with app.js's el() and its questions in it.</summary>
	sealed class Page {
		readonly Engine js = new();

		public Page() {
			string script = Asset("app.js");
			js.Execute(FakePage);
			js.Execute(Slice(script, "function el(tag, cls, text) {", "\n// Writes for what a poll"));
			js.Execute(Slice(script, "// ---------------------------------------------------------------- the page's own questions",
				"\n// ---------------------------------------------------------------- the agent: pause, resume, start"));
		}

		static string Slice(string script, string from, string to) {
			int start = script.IndexOf(from, StringComparison.Ordinal);
			Assert.True(start >= 0, from);
			int end = script.IndexOf(to, start + from.Length, StringComparison.Ordinal);
			Assert.True(end > start, to);
			return script[start..end];
		}

		static string Q(string s) => JsonSerializer.Serialize(s);

		/// <summary>Asks, and notes the answer once it comes.</summary>
		public Page Ask(string text, string options = "{}") { Run($"askDialog({Q(text)}, {options}).then(function (a) {{ answers.push(a); }});"); return this; }
		public Page AskText(string text, string value, string options = "{}") {
			Run($"askTextDialog({Q(text)}, {Q(value)}, {options}).then(function (a) {{ answers.push(a); }});");
			return this;
		}
		public Page Click(string words) { Run($"button({Q(words)}).click();"); return this; }
		public bool Press(string key, bool shift = false) => js.Evaluate($"press({Q(key)}, {(shift ? "true" : "false")})").AsBoolean();
		public Page Type(string text) { Run($"field().value = {Q(text)};"); return this; }
		public void Run(string code) {
			js.Execute(code);
			js.Advanced.ProcessTasks(); // the answer's then(), as the browser runs it once the click's script is done
		}
		public string Eval(string code) => js.Evaluate(code).ToString();
		public bool True(string code) => js.Evaluate(code).AsBoolean();
		/// <summary>The answers so far, oldest first, as JSON.</summary>
		public string Answers => js.Evaluate("JSON.stringify(answers)").AsString();
		public int Showing => (int)js.Evaluate("dialogs()").AsNumber();
		/// <summary>The words of the focused element.</summary>
		public string Focus => js.Evaluate("document.activeElement === asker ? 'the asker' : document.activeElement.textContent || document.activeElement.tagName").AsString();
	}

	[Fact]
	public void AQuestion_SaysWhatItAsks_AndHoldsThePage() {
		var page = new Page().Ask("It fetches first.\n\nThe commits stay.", "{ title: 'Delete 2 local branches?', ok: 'Delete branches', danger: true }");
		Assert.Equal(1, page.Showing);
		Assert.Equal("alertdialog", page.Eval("dialog().getAttribute('role')"));
		Assert.Equal("true", page.Eval("dialog().getAttribute('aria-modal')"));
		// Its title names it, and its text, paragraphs kept, says what it means.
		Assert.Equal("Delete 2 local branches?", page.Eval("find(dialog(), function (n) { return n.id === dialog().getAttribute('aria-labelledby'); }).textContent"));
		Assert.Equal("H2", page.Eval("find(dialog(), function (n) { return n.id === dialog().getAttribute('aria-labelledby'); }).tagName"));
		var text = "find(dialog(), function (n) { return n.id === dialog().getAttribute('aria-describedby'); })";
		Assert.Equal("It fetches first.\n\nThe commits stay.", page.Eval(text + ".textContent"));
		Assert.Equal("ask-text", page.Eval(text + ".className"));
		// Cancel, then its own OK, in the alert colour.
		Assert.Equal("Cancel,Delete branches", page.Eval("find(dialog(), function (n) { return n.className === 'tour-buttons'; }).children.map(function (b) { return b.textContent; }).join()"));
		Assert.Equal("btn danger", page.Eval("button('Delete branches').className"));
		Assert.Equal("btn secondary", page.Eval("button('Cancel').className"));
		// The page under it is held until it's answered.
		Assert.True(page.True("main.inert && document.body.children[1].inert && !dialog().inert"));
		page.Click("Cancel");
		Assert.Equal(0, page.Showing);
		Assert.True(page.True("!main.inert && !document.body.children[1].inert"));
	}

	[Fact]
	public void OK_IsYes_Cancel_AndEsc_AreNo() {
		var page = new Page().Ask("Clear the history?", "{ ok: 'Clear history' }").Click("Clear history");
		Assert.Equal("[true]", page.Answers);
		page.Ask("Clear the history?").Click("Cancel");
		Assert.Equal("[true,false]", page.Answers);
		page.Ask("Clear the history?");
		Assert.True(page.Press("Escape"));
		Assert.Equal("[true,false,false]", page.Answers);
		Assert.Equal(0, page.Showing);
		// Without a title or labels: the text alone, OK and Cancel.
		page.Ask("Continue?");
		Assert.Equal("Question", page.Eval("dialog().getAttribute('aria-label')"));
		Assert.True(page.True("button('OK') !== null && button('Cancel') !== null"));
	}

	[Fact]
	public void TheFocus_GoesIn_StaysIn_AndComesBack() {
		// A plain question: OK has the focus. A danger one: Cancel, so Enter doesn't delete.
		var page = new Page().Ask("Skip all 3 look-alike sets?", "{ ok: 'Skip them all' }");
		Assert.Equal("Skip them all", page.Focus);
		// Tab goes round its buttons, both ways.
		Assert.True(page.Press("Tab"));
		Assert.Equal("Cancel", page.Focus);
		Assert.True(page.Press("Tab", shift: true));
		Assert.Equal("Skip them all", page.Focus);
		Assert.False(page.Press("Tab", shift: true)); // from OK back to Cancel, the browser's own step
		page.Click("Skip them all");
		Assert.Equal("the asker", page.Focus);
		page.Ask("Delete permanently?", "{ ok: 'Delete permanently', danger: true }");
		Assert.Equal("Cancel", page.Focus);
		Assert.True(page.Press("Escape"));
		Assert.Equal("the asker", page.Focus);
	}

	[Fact]
	public void QuestionsAskedTogether_WaitTheirTurn_OldestFirst() {
		var page = new Page().Ask("First?", "{ ok: 'One' }").Ask("Second?", "{ ok: 'Two' }");
		Assert.Equal(1, page.Showing);
		Assert.Equal("First?", page.Eval("find(dialog(), function (n) { return n.className === 'ask-text'; }).textContent"));
		page.Click("One");
		Assert.Equal("[true]", page.Answers);
		Assert.Equal(1, page.Showing);
		Assert.Equal("Second?", page.Eval("find(dialog(), function (n) { return n.className === 'ask-text'; }).textContent"));
		Assert.True(page.True("main.inert")); // still held
		page.Click("Cancel");
		Assert.Equal("[true,false]", page.Answers);
		Assert.Equal(0, page.Showing);
		Assert.True(page.True("!main.inert"));
		Assert.Equal("the asker", page.Focus);
		// Going to another page answers Cancel to them all.
		page.Ask("Third?").Ask("Fourth?").Run("dropQuestions();");
		Assert.Equal("[true,false,false,false]", page.Answers);
		Assert.Equal(0, page.Showing);
		Assert.True(page.True("!main.inert && document.keydown.length === 0"));
	}

	[Fact]
	public void AQuestionToTypeIn_AnswersWhatsTyped_OrNull() {
		var page = new Page().AskText("Name for the project (web, api):", "web", "{ title: 'Group as one project', ok: 'Group them' }");
		// Its line has the suggestion, all of it selected, and the focus; the text labels it.
		Assert.Equal("web", page.Eval("field().value"));
		Assert.True(page.True("field().selectedAll && document.activeElement === field()"));
		Assert.Equal(page.Eval("dialog().getAttribute('aria-describedby')"), page.Eval("field().getAttribute('aria-labelledby')"));
		// Tab goes round the line and the buttons.
		Assert.True(page.Press("Tab", shift: true));
		Assert.Equal("Group them", page.Focus);
		Assert.True(page.Press("Tab"));
		Assert.Equal("INPUT", page.Focus);
		// Enter in the line is OK.
		page.Type("Shop");
		Assert.True(page.Press("Enter"));
		Assert.Equal("[\"Shop\"]", page.Answers);
		page.AskText("Name for the project:", "web").Click("OK");
		Assert.Equal("[\"Shop\",\"web\"]", page.Answers);
		page.AskText("Name for the project:", "web").Type("Shop").Click("Cancel");
		page.AskText("Name for the project:", "web");
		Assert.True(page.Press("Escape"));
		Assert.Equal("[\"Shop\",\"web\",null,null]", page.Answers);
	}
}
