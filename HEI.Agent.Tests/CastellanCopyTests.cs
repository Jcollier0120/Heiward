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

using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace HEI.Agent.Tests;

/// <summary>
/// The Store version standing aside for Castellan's Heiward (CastellanCopy): when the copy from GitHub is the one Manor
/// employs, by Manor's agents.json, and what the Store version's page and install say then. Nothing here installs or
/// removes anything: Manor's folder and both copies' hei.exe are files in a temp folder.
/// </summary>
public sealed class CastellanCopyTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-castellan-" + Guid.NewGuid().ToString("N"));
	readonly string gitHubExe, storeAlias;

	public CastellanCopyTests() {
		Directory.CreateDirectory(dir);
		gitHubExe = Touch(Path.Combine(dir, "Programs", "Heiward", "hei.exe"));
		storeAlias = Path.Combine(dir, "WindowsApps", "hei.exe");
	}

	public void Dispose() {
		try { Directory.Delete(dir, true); } catch { }
	}

	static string Touch(string file) {
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, "");
		return file;
	}

	/// <summary>Manor's folder: installed (settings.json and app\) unless said, with this agents.json.</summary>
	string Manor(string? agents, bool installed = true) {
		string folder = Path.Combine(dir, "manor-" + Guid.NewGuid().ToString("N")[..6]);
		Directory.CreateDirectory(folder);
		if (installed) {
			File.WriteAllText(Path.Combine(folder, "settings.json"), "{}");
			Directory.CreateDirectory(Path.Combine(folder, "app"));
		}
		if (agents != null) File.WriteAllText(Path.Combine(folder, "agents.json"), agents);
		return folder;
	}

	/// <summary>agents.json with Reeve and Heiward, Heiward's paths.hei as given (Manor's own order: GitHub's, then the Store's alias).</summary>
	static string Agents(params string[] hei) =>
		JsonSerializer.Serialize(new {
			agents = new object[] {
				new { id = "reeve", name = "Reeve" },
				new { id = "heiward", name = "Heiward", home = "http://heiward.localhost:18484/", paths = new { hei } },
			},
		});

	[Fact]
	public void TheCopyFromGitHub_IsCastellans_WhenManorEmploysHeiwardThere() =>
		Assert.True(CastellanCopy.Keeps(Manor(Agents(gitHubExe, storeAlias)), gitHubExe));

	[Fact]
	public void ManorsPaths_HaveTheirVariablesFilledIn_AndAnyCase() {
		string name = "HEI_TEST_PROGRAMS_" + Guid.NewGuid().ToString("N")[..8];
		Environment.SetEnvironmentVariable(name, Path.Combine(dir, "Programs"));
		try {
			Assert.True(CastellanCopy.Keeps(Manor(Agents($"%{name}%\\Heiward\\hei.exe", storeAlias)), gitHubExe));
			Assert.True(CastellanCopy.Keeps(Manor(Agents(gitHubExe.ToUpperInvariant())), gitHubExe));
		}
		finally { Environment.SetEnvironmentVariable(name, null); }
	}

	[Fact]
	public void WithoutManor_NoCopyIsCastellans() {
		Assert.False(CastellanCopy.Keeps(null, gitHubExe));
		// A folder an uninstalled Manor left: no app\, so no Manor.
		Assert.False(CastellanCopy.Keeps(Manor(Agents(gitHubExe), installed: false), gitHubExe));
		Assert.False(CastellanCopy.Keeps(Manor(null), gitHubExe));
	}

	[Fact]
	public void ACopyManorDoesntEmploy_IsReplacedAsBefore() =>
		Assert.False(CastellanCopy.Keeps(Manor("""{"agents":[{"id":"reeve","paths":{"hei":"C:\\x\\hei.exe"}}]}"""), gitHubExe));

	[Fact]
	public void TheStoresCopy_ManorAdoptedAsItIs_LeavesTheCopyFromGitHubUnemployed() {
		Touch(storeAlias);
		// Manor runs the first of its candidates that is there: the Store's alias, listed first here.
		Assert.False(CastellanCopy.Keeps(Manor(Agents(storeAlias, gitHubExe)), gitHubExe));
		// Or a hei.exe somewhere else altogether.
		Assert.False(CastellanCopy.Keeps(Manor(Agents(Touch(Path.Combine(dir, "elsewhere", "hei.exe")))), gitHubExe));
	}

	[Fact]
	public void NoCopyFromGitHub_NothingToStandAsideFor() {
		string folder = Manor(Agents(gitHubExe, storeAlias));
		File.Delete(gitHubExe);
		Assert.False(CastellanCopy.Keeps(folder, gitHubExe));
	}

	[Fact]
	public void AnEntryThatNamesNoPath_StillMeansTheCopyFromGitHub() {
		Assert.True(CastellanCopy.Keeps(Manor("""{"agents":[{"id":"heiward","name":"Heiward"}]}"""), gitHubExe));
		// One path rather than a list, as Manor's registry also takes it.
		Assert.True(CastellanCopy.Keeps(Manor(JsonSerializer.Serialize(new { agents = new[] { new { id = "heiward", paths = new { hei = gitHubExe } } } })), gitHubExe));
	}

	[Fact]
	public void AnAgentsJsonThatIsntManors_CountsAsNo() {
		Assert.False(CastellanCopy.Keeps(Manor("not json"), gitHubExe));
		Assert.False(CastellanCopy.Keeps(Manor("""{"agents":{"id":"heiward"}}"""), gitHubExe));
		Assert.False(CastellanCopy.Keeps(Manor("""["heiward"]"""), gitHubExe));
		// A byte-order mark is fine.
		Assert.True(CastellanCopy.Keeps(Manor("\uFEFF" + Agents(gitHubExe)), gitHubExe));
	}

	[Fact]
	public void TheEntryLookup_IsSelfUpdates_Too() {
		using var doc = JsonDocument.Parse(Agents(gitHubExe));
		Assert.Equal("Heiward", CastellanCopy.Entry(doc.RootElement)!.Value.GetProperty("name").GetString());
		using var none = JsonDocument.Parse("""{"agents":[{"id":"reeve"}]}""");
		Assert.Null(CastellanCopy.Entry(none.RootElement));
	}

	[Fact]
	public void TheInstall_SaysWhyItSetsNothingUp() {
		string words = CastellanCopy.Refusal;
		Assert.Contains("Castellan already keeps Heiward on this PC and up to date", words);
		Assert.Contains(Installer.InstallDir, words);
		Assert.Contains("isn't set up", words);
		Assert.Contains("Settings > Apps", words);
	}

	[Fact]
	public void ThePageStandingAside_TakesAPortNoOneListensOn() {
		int port = CastellanCopy.FreePort();
		Assert.NotEqual(AgentPaths.InstalledPort, port);
		var listener = new TcpListener(IPAddress.Loopback, port);
		listener.Start();
		listener.Stop();
	}

	[Fact]
	public void ThePage_SaysCastellanKeepsHeiward_AndOffersNoRemoval() {
		string script;
		using (Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/app.js")!)
			script = new StreamReader(s).ReadToEnd();
		int start = script.IndexOf("function setupKeptByCastellan(", StringComparison.Ordinal);
		string kept = script[start..script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal)];
		Assert.Contains("Castellan already keeps Heiward on this PC", kept);
		Assert.Contains("keeps it up to date", kept);
		Assert.Contains("setup.castellan.url", kept);
		Assert.Contains("Settings > Apps > Installed apps", kept);
		Assert.DoesNotContain("checkbox", kept);
		Assert.DoesNotContain("/api/setup", kept);
		// It comes before the questions, the removal's box among them.
		int render = script.IndexOf("function renderSetup(", StringComparison.Ordinal);
		string renderSetup = script[render..script.IndexOf("\nfunction ", render + 1, StringComparison.Ordinal)];
		Assert.True(renderSetup.IndexOf("setup.castellan", StringComparison.Ordinal) < renderSetup.IndexOf("setupForm(", StringComparison.Ordinal));
	}
}
