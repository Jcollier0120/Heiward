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

/// <summary>Heiward's window opens where it was last, as big as it was (WindowPlace, Installer.EdgeArguments, app.js placeWindow).</summary>
[Collection(AgentHomeCollection.Name)]
public sealed class WindowPlaceTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-window-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public WindowPlaceTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	const string Url = "http://heiward.localhost:28474/";

	[Theory]
	[InlineData(0, 0, 1920, 1032, 320, 86, 1280, 860)]   // 1920 x 1080 at 100 %: 1280 x 860, centred
	[InlineData(0, 0, 1536, 816, 128, 12, 1280, 792)]    // at 125 %: as tall as leaves a margin
	[InlineData(0, 0, 1366, 720, 43, 12, 1280, 696)]     // 1366 x 768 at 100 %
	[InlineData(0, 0, 1093, 566, 0, 0, 1093, 566)]       // at 125 %: the whole work area, which the setup fits
	[InlineData(0, 48, 1920, 1032, 320, 134, 1280, 860)] // the taskbar at the top
	public void TheFirstWindow_IsCentred_AtTheSizeTheSetupIsLaidOutFor(int left, int top, int width, int height, int x, int y, int w, int h) =>
		Assert.Equal(new WindowPlace(x, y, w, h), WindowPlace.Default(new(left, top, width, height)));

	[Fact]
	public void TheFirstWindow_OpensCentredOnTheMainScreen() {
		string[] args = Installer.EdgeArguments(Url, saved: null, work: new(0, 0, 1920, 1032), savedOnScreen: false);
		Assert.Equal(["--app=" + Url, "--window-position=320,86", "--window-size=1280,860", "--no-first-run"], args);
	}

	[Fact]
	public void ALaterWindow_OpensWhereTheLastOneWas_AsBig() {
		string[] args = Installer.EdgeArguments(Url, new WindowPlace(-1700, 40, 1500, 900), new(0, 0, 1920, 1032), savedOnScreen: true);
		Assert.Equal(["--app=" + Url, "--window-position=-1700,40", "--window-size=1500,900", "--no-first-run"], args);
	}

	[Fact]
	public void AMaximizedOne_OpensMaximized() =>
		Assert.Contains("--start-maximized", Installer.EdgeArguments(Url, new WindowPlace(100, 100, 1200, 800, Maximized: true), null, savedOnScreen: true));

	[Fact]
	public void OneOnAScreenSinceUnplugged_KeepsItsSize_AndEdgePlacesIt() {
		string[] args = Installer.EdgeArguments(Url, new WindowPlace(4000, 200, 1300, 850), new(0, 0, 1920, 1032), savedOnScreen: false);
		Assert.DoesNotContain(args, a => a.StartsWith("--window-position=", StringComparison.Ordinal));
		Assert.Contains("--window-size=1300,850", args);
	}

	[Fact]
	public void APlace_IsKept_AndReadBack() {
		Assert.Null(WindowPlace.Load());
		Assert.True(WindowPlace.Save(new WindowPlace(200, 120, 1400, 900)));
		Assert.Equal(new WindowPlace(200, 120, 1400, 900), WindowPlace.Load());
	}

	[Fact]
	public void Maximized_KeepsTheSizeItHadBefore_ForWhenItsMadeSmallerAgain() {
		WindowPlace.Save(new WindowPlace(200, 120, 1400, 900));
		WindowPlace.Save(new WindowPlace(-8, -8, 1936, 1048, Maximized: true));
		Assert.Equal(new WindowPlace(200, 120, 1400, 900, Maximized: true), WindowPlace.Load());
		WindowPlace.Save(new WindowPlace(210, 130, 1400, 900));
		Assert.Equal(new WindowPlace(210, 130, 1400, 900), WindowPlace.Load());
	}

	[Theory]
	[InlineData(-32000, -32000, 160, 28)] // minimized
	[InlineData(100, 100, 300, 200)]      // too small to use
	[InlineData(90000, 100, 1200, 800)]   // miles off
	public void APlaceNoWindowCanBePutBackTo_IsntKept(int x, int y, int w, int h) {
		Assert.False(WindowPlace.Save(new WindowPlace(x, y, w, h)));
		Assert.Null(WindowPlace.Load());
	}

	[Fact]
	public void ABrokenFile_MeansTheFirstWindowsPlace() {
		File.WriteAllText(Path.Combine(dir, "window.json"), "{ not json");
		Assert.Null(WindowPlace.Load());
	}

	[Theory]
	[InlineData(0, 0, 1920, 1032)]
	[InlineData(0, 0, 1536, 816)]
	[InlineData(0, 0, 1093, 566)]
	[InlineData(-1920, 0, 1920, 1040)]
	public void ThePage_WorksTheFirstWindowOutAsTheAgentDoes(int left, int top, int width, int height) {
		var js = new Engine();
		js.Execute(ThePage.Function(ThePage.Asset("app.js"), "defaultWindow"));
		var d = js.Evaluate($"defaultWindow({{ x: {left}, y: {top}, w: {width}, h: {height} }})").AsObject();
		WindowPlace agent = WindowPlace.Default(new(left, top, width, height));
		Assert.Equal(agent, new WindowPlace((int)d.Get("x").AsNumber(), (int)d.Get("y").AsNumber(), (int)d.Get("width").AsNumber(), (int)d.Get("height").AsNumber()));
	}

	[Fact]
	public void ThePage_PlacesOnlyItsOwnWindow_AndSaysWhereItIs() {
		string script = ThePage.Asset("app.js");
		string own = ThePage.Function(script, "ownWindow");
		Assert.Contains("display-mode: standalone", own, StringComparison.Ordinal); // an app window, not a tab
		Assert.Contains("document.referrer", own, StringComparison.Ordinal);       // not a page another one went on to
		string place = ThePage.Function(script, "placeWindow");
		Assert.Contains("fetch('/api/window'", place, StringComparison.Ordinal);
		Assert.Contains("pagehide", place, StringComparison.Ordinal);
		Assert.Contains("'X-Agent-Token': token", ThePage.Function(script, "sendWindow"), StringComparison.Ordinal);
		Assert.Contains("\nplaceWindow();", script, StringComparison.Ordinal);
	}
}
