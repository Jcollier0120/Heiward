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

/// <summary>The setup's steps (app.js, renderSetup): its slides, its questions, and its buttons in the footer.</summary>
public sealed class SetupStepsTests {
	static readonly string Script = ThePage.Asset("app.js");

	/// <summary>The slides' names as the setup shows them, for the given setup state and answer.</summary>
	static string[] Slides(string setup, string ai, string device) {
		var js = new Engine();
		int from = Script.IndexOf("const SETUP_SLIDES", StringComparison.Ordinal), to = Script.IndexOf("const SETUP_STEPS", StringComparison.Ordinal);
		js.Execute(Script[from..to]);
		js.Execute($"var setupAnswers = {{ device: {device} }}; var state = {{ setup: {setup}, ai: {ai} }};");
		js.Execute(ThePage.Function(Script, "setupOnNpu"));
		return js.Evaluate("setupSlides().map(s => s[0]).join(',')").AsString().Split(',');
	}

	[Fact]
	public void OnAPCWithNoNPU_TheSlidesShowTheGraphicsCard() =>
		Assert.Equal(["found", "gpu", "safe", "gentle", "more", "castellan-gpu"], Slides("{ needed: true, npu: false }", "null", "null"));

	[Fact]
	public void WithAnNPU_TheyShowIt() =>
		Assert.Equal(["found", "npu", "safe", "gentle", "more", "castellan"], Slides("{ needed: true, npu: true }", "null", "null"));

	[Theory]
	[InlineData("'gpu'")]
	[InlineData("'cpu'")]
	public void WithAnNPU_ButTheAIPutElsewhere_TheyShowTheGraphicsCard(string device) =>
		Assert.Contains("gpu", Slides("{ needed: true, npu: true }", "null", device));

	[Fact]
	public void Done_ShowsWhereTheAIRuns() =>
		Assert.Contains("npu", Slides("{ needed: false }", "{ device: 'NPU' }", "null"));

	[Theory]
	[InlineData("gpu.svg", ">GPU</text>")]
	[InlineData("castellan-gpu.svg", ">GPU</text>")]
	public void TheGraphicsCardsSlides_AreThere_AndSayGPU(string file, string chip) {
		// MSBuild names the resource with this OS's folder separator (ReviewServer.SetupArt).
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/setup/" + file)
			?? typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/setup\\" + file)!;
		string svg = new StreamReader(s).ReadToEnd();
		Assert.Contains(chip, svg, StringComparison.Ordinal);
		Assert.DoesNotContain(">NPU</text>", svg, StringComparison.Ordinal);
	}

	[Fact]
	public void GameAndDeveloperMode_AreAsked_UnlessManorDecidesThem() {
		string modes = ThePage.Function(Script, "setupModes");
		Assert.Contains("if (!s.games.manor)", modes, StringComparison.Ordinal);
		Assert.Contains("if (!s.dev.manor)", modes, StringComparison.Ordinal);
		string start = ThePage.Function(Script, "startSetup");
		Assert.Contains("!state.games.manor) switches.gameMode", start, StringComparison.Ordinal);
		Assert.Contains("!state.dev.manor) switches.developerMode", start, StringComparison.Ordinal);
		Assert.Contains("setupModes(s)", ThePage.Function(Script, "setupForm"), StringComparison.Ordinal);
	}

	[Fact]
	public void EachStepsButtons_AreInTheFooter_WhichNeverCoversTheStep() {
		string page = ThePage.Asset("index.html");
		int foot = page.IndexOf("<footer class=\"setup-foot\">", StringComparison.Ordinal);
		Assert.True(foot > page.IndexOf("id=\"setup-card\"", StringComparison.Ordinal));
		Assert.True(page.IndexOf("id=\"setup-bar\"", StringComparison.Ordinal) > foot);
		Assert.Contains("$('setup-bar').replaceChildren", ThePage.Function(Script, "setupFootBar"), StringComparison.Ordinal);
		foreach (string view in new[] { "setupWelcome", "setupForm", "setupDone", "renderSetupProgress" })
			Assert.Contains("setup-bar", ThePage.Function(Script, view), StringComparison.Ordinal);
		// The card scrolls on its own when it must, between the banner and the footer, which stay put.
		string css = ThePage.Asset("app.css");
		Assert.Contains(".setup-card {\n  flex: 1 1 auto; min-height: 0; overflow-y: auto;", css.Replace("\r\n", "\n"), StringComparison.Ordinal);
	}
}
