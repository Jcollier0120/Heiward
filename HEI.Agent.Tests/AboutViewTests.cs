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

namespace HEI.Agent.Tests;

/// <summary>
/// The About view, behind the button beside Settings as on Manor's and every kit agent's page: this build, the licence's
/// notice and where its work runs, read once. Settings keeps the switches, updating by itself and the "In a manor" card among them.
/// </summary>
public sealed class AboutViewTests {
	static string Asset(string name) {
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/" + name)!;
		return new StreamReader(s).ReadToEnd();
	}

	static string Function(string script, string name) {
		int start = script.IndexOf("function " + name + "(", StringComparison.Ordinal);
		Assert.True(start > 0, name);
		return script[start..script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal)];
	}

	[Fact]
	public void TheAboutButton_SitsBesideSettings() {
		string page = Asset("index.html");
		int about = page.IndexOf("id=\"about-btn\"", StringComparison.Ordinal);
		Assert.True(about > 0);
		Assert.True(about < page.IndexOf("id=\"settings-btn\"", StringComparison.Ordinal));
	}

	[Fact]
	public void AboutHolds_TheBuild_AndWhereItsWorkRuns_SettingsNeither() {
		string page = Asset("index.html");
		int settings = page.IndexOf("<main id=\"settingsview\"", StringComparison.Ordinal);
		int about = page.IndexOf("<main id=\"aboutview\"", StringComparison.Ordinal);
		Assert.True(settings > 0 && about > settings);
		string settingsView = page[settings..about];
		string aboutView = page[about..page.IndexOf("</main>", about, StringComparison.Ordinal)];
		foreach (string part in new[] { "id=\"about-card\"", "id=\"where-card\"" }) {
			Assert.Contains(part, aboutView);
			Assert.DoesNotContain(part, settingsView);
		}
		Assert.Contains("id=\"updates-card\"", settingsView);
	}

	[Fact]
	public void TheSwitches_StayInSettings_TheLicenceGoesToAbout() {
		string script = Asset("app.js");
		string aboutCard = Function(script, "renderAboutCard");
		string updatesCard = Function(script, "renderUpdatesCard");
		Assert.Contains("GNU AGPL v3", aboutCard);
		Assert.DoesNotContain("toggleSwitch(", aboutCard);
		Assert.Contains("saveSettings({ autoUpdate: on })", updatesCard);
		Assert.Contains("saveSettings({ manorCard: on })", updatesCard);
		Assert.Contains("renderUpdatesCard(s);", Function(script, "renderSettings"));
		string renderAbout = Function(script, "renderAbout");
		Assert.Contains("renderAboutCard(s);", renderAbout);
		Assert.Contains("renderWhereCard(s);", renderAbout);
		Assert.Contains("if (h === '#/about') return { view: 'about' };", script);
	}
}
