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

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HEI.Agent.Tests;

/// <summary>Opens nothing: says what it was handed, or throws what a test tells it to.</summary>
sealed class UninstallRecorder : IUninstallOpener {
	public readonly List<(string How, string What)> Opened = new();
	public Exception? Throw;

	public void OpenLink(string link) {
		if (Throw != null) throw Throw;
		Opened.Add(("link", link));
	}

	public void Run(string uninstallString) {
		if (Throw != null) throw Throw;
		Opened.Add(("run", uninstallString));
	}
}

/// <summary>A registry that counts what's asked of it, to see when it isn't read at all.</summary>
sealed class CountingRegistry(IGameRegistry inner) : IGameRegistry {
	public int Reads;
	public string? Value(string key, string name) { Reads++; return inner.Value(key, name); }
	public IReadOnlyList<string> SubKeys(string key) { Reads++; return inner.SubKeys(key); }
}

/// <summary>Uninstall strings as each launcher registers them, and the games they belong to, made under a temporary folder.</summary>
static class Uninstalls {
	public const string Hklm64 = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
	public const string Hklm32 = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
	public const string Hkcu = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall";

	public const string SteamCmd = "\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/100"; // as 3DMark's is on the owner's PC
	public const string GogCmd = "\"D:\\GOG Games\\The Witcher 3\\unins000.exe\"";
	public const string UbisoftCmd = "\"C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\upc.exe\" uplay://uninstall/5";
	public const string BattleNetCmd = "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=fenris --displayname=\"Diablo IV\"";
	public const string EaCmd = "\"C:\\Program Files\\Common Files\\EAInstaller\\Apex\\Cleanup.exe\" uninstall_game -autologging";

	public const string EpicNote = "The Epic Games Launcher doesn't let other apps uninstall its games: uninstall it from its library.";

	public static GameInstall Game(string root, string launcher, string appId, string name) {
		string folder = Path.Combine(root, launcher, name);
		Directory.CreateDirectory(folder);
		return new GameInstall(GameLibraries.IdOf(launcher, appId + "|" + folder), launcher, appId, name, folder, null, 5L << 30, null, null);
	}

	public static FakeRegistry Entry(this FakeRegistry r, string view, string key, string? uninstall, string? location = null) {
		if (uninstall != null) r.Set(view + "\\" + key, "UninstallString", uninstall);
		if (location != null) r.Set(view + "\\" + key, "InstallLocation", location);
		r.Set(view + "\\" + key, "DisplayName", key);
		return r;
	}
}

/// <summary>
/// A game's Uninstall: which uninstaller each launcher's games get, read from a fake registry (both of HKLM's views and HKCU),
/// what is handed to the opener (exactly what's registered, or Steam's link), and the page's button and dialog. Nothing here
/// reads this PC's registry or starts an uninstaller: the opener is a recorder.
/// </summary>
public sealed class GameUninstallTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "hei-uninstall-" + Guid.NewGuid().ToString("N"));
	readonly FakeRegistry registry = new();
	readonly UninstallRecorder recorder = new();

	public void Dispose() { try { Directory.Delete(root, true); } catch { } }

	GameInstall Game(string launcher, string appId, string name) => Uninstalls.Game(root, launcher, appId, name);

	GameUninstall.Plan? Find(GameInstall game) => GameUninstall.Find(game, registry);

	GameUninstaller Uninstaller(bool packaged = false) => new(registry, recorder, packaged);

	// ---- Finding each launcher's uninstaller

	[Fact]
	public void LooksIn_HklmsBothViews_AndHkcu() =>
		Assert.Equal([Uninstalls.Hklm64, Uninstalls.Hklm32, Uninstalls.Hkcu], GameUninstall.Views);

	[Theory]
	[InlineData(Uninstalls.Hklm64)] // where Steam wrote 3DMark's on the owner's PC
	[InlineData(Uninstalls.Hklm32)]
	[InlineData(Uninstalls.Hkcu)]
	public void Steam_ItsOwnEntry_OpensSteamsQuestion(string view) {
		registry.Entry(view, "Steam App 100", Uninstalls.SteamCmd);
		Assert.Equal(new GameUninstall.Plan(GameUninstall.Steam, "steam://uninstall/100"), Find(Game(GameLaunchers.Steam, "100", "Game A")));
		// Another game's entry is no entry of its own.
		Assert.Null(Find(Game(GameLaunchers.Steam, "200", "Game B")));
	}

	[Theory]
	[InlineData(GameLaunchers.Gog, "1207664663", "1207664663_is1", Uninstalls.GogCmd, Uninstalls.Hklm32)]
	[InlineData(GameLaunchers.Gog, "1207664663", "1207664663_is1", Uninstalls.GogCmd, Uninstalls.Hkcu)]
	[InlineData(GameLaunchers.Ubisoft, "5", "Uplay Install 5", Uninstalls.UbisoftCmd, Uninstalls.Hklm32)]
	[InlineData(GameLaunchers.Ubisoft, "5", "Uplay Install 5", Uninstalls.UbisoftCmd, Uninstalls.Hklm64)]
	[InlineData(GameLaunchers.BattleNet, "Diablo IV", "Diablo IV", Uninstalls.BattleNetCmd, Uninstalls.Hklm32)]
	public void TheLaunchersOwnEntryName_ItsUninstallStringAsRegistered(string launcher, string appId, string key, string command, string view) {
		registry.Entry(view, key, command);
		Assert.Equal(new GameUninstall.Plan(GameUninstall.Entry, command), Find(Game(launcher, appId, "A Game")));
	}

	[Fact]
	public void TheEntryWhoseInstallLocationIsTheGamesFolder_ComparedAsFullPaths() {
		GameInstall apex = Game(GameLaunchers.Ea, "Apex", "Apex Legends");
		// Its parent folder, and a folder whose name only starts the same: neither is the game's.
		registry.Entry(Uninstalls.Hklm64, "{parent}", "\"C:\\x\\parent.exe\"", Path.GetDirectoryName(apex.Folder));
		registry.Entry(Uninstalls.Hklm64, "{longer}", "\"C:\\x\\longer.exe\"", apex.Folder + " II");
		Assert.Null(Find(apex));
		// Quoted, with forward slashes, a trailing slash and other capitals, in the 32-bit view: the same folder.
		registry.Entry(Uninstalls.Hklm32, "{EA-GUID}", Uninstalls.EaCmd, "\"" + apex.Folder.ToUpperInvariant().Replace('\\', '/') + "/\"");
		Assert.Equal(new GameUninstall.Plan(GameUninstall.Entry, Uninstalls.EaCmd), Find(apex));
	}

	[Fact]
	public void TheLaunchersOwnEntryName_ComesBeforeAnInstallLocation() {
		GameInstall witcher = Game(GameLaunchers.Gog, "1207664663", "The Witcher 3");
		registry.Entry(Uninstalls.Hklm64, "{other}", "\"C:\\x\\other.exe\"", witcher.Folder);
		registry.Entry(Uninstalls.Hkcu, "1207664663_is1", Uninstalls.GogCmd);
		Assert.Equal(Uninstalls.GogCmd, Find(witcher)!.Target);
	}

	[Theory]
	[InlineData(GameLaunchers.Gog, "Windows lists no uninstaller for it: uninstall it from GOG GALAXY.")]
	[InlineData(GameLaunchers.Ea, "Windows lists no uninstaller for it: uninstall it from the EA app.")]
	[InlineData(GameLaunchers.Ubisoft, "Windows lists no uninstaller for it: uninstall it from Ubisoft Connect.")]
	[InlineData(GameLaunchers.BattleNet, "Windows lists no uninstaller for it: uninstall it from Battle.net.")]
	[InlineData(GameLaunchers.Steam, "Windows lists no uninstaller for it: uninstall it from Steam.")]
	public void NoEntry_TheButtonIsGreyed_NamingTheLauncher(string launcher, string note) {
		registry.Entry(Uninstalls.Hklm32, "Something Else", "\"C:\\x\\else.exe\"", @"C:\Else");
		GameInstall game = Game(launcher, "1", "No Entry");
		Assert.Null(Find(game));
		Assert.Null(GameUninstall.Mark([game], registry).Single().Uninstall);
		Assert.Equal(note, game.UninstallNote);
		Assert.Null((game with { Uninstall = GameUninstall.Entry }).UninstallNote);
	}

	[Fact]
	public void Epic_AlwaysGreyed_EvenWithAnEntryForItsFolder_AndNoLinkGuessed() {
		GameInstall fortnite = Game(GameLaunchers.Epic, "Fortnite", "Fortnite");
		registry.Entry(Uninstalls.Hklm64, "Fortnite", "\"C:\\x\\fortnite-uninstall.exe\"", fortnite.Folder);
		Assert.Null(Find(fortnite));
		Assert.Equal(Uninstalls.EpicNote, fortnite.UninstallNote);
		// The page reads it from the games list.
		JsonElement json = JsonSerializer.SerializeToElement(fortnite, AgentConfig.Json);
		Assert.Equal(Uninstalls.EpicNote, json.GetProperty("uninstallNote").GetString());
		Assert.Equal(JsonValueKind.Null, json.GetProperty("uninstall").ValueKind);
	}

	[Fact]
	public void Xbox_WindowsInstalledApps_WithoutReadingTheRegistry() {
		var counting = new CountingRegistry(registry);
		Assert.Equal(new GameUninstall.Plan(GameUninstall.Settings, "ms-settings:appsfeatures"), GameUninstall.Find(Game(GameLaunchers.Xbox, "Game B", "Game B"), counting));
		Assert.Equal(0, counting.Reads);
	}

	[Fact]
	public void NeverQuiet_NorAQuietUninstallString() {
		GameInstall witcher = Game(GameLaunchers.Gog, "1", "The Witcher 3");
		// Only a QuietUninstallString: not used.
		registry.Set(Uninstalls.Hklm32 + @"\1_is1", "QuietUninstallString", "\"D:\\GOG Games\\The Witcher 3\\unins000.exe\" /VERYSILENT");
		Assert.Null(Find(witcher));
		// An UninstallString that would skip the uninstaller's questions: not used either.
		registry.Entry(Uninstalls.Hklm32, "1_is1", "\"D:\\GOG Games\\The Witcher 3\\unins000.exe\" /SILENT");
		Assert.Null(Find(witcher));
		registry.Entry(Uninstalls.Hklm32, "1_is1", "MsiExec.exe /X{12345678-1234-1234-1234-123456789012} /qn");
		Assert.Null(Find(witcher));
		registry.Entry(Uninstalls.Hklm32, "1_is1", "MsiExec.exe /X{12345678-1234-1234-1234-123456789012}");
		Assert.Equal("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}", Find(witcher)!.Target); // exactly as registered
	}

	[Theory]
	[InlineData("\"D:\\GOG Games\\W3\\unins000.exe\"", "D:\\GOG Games\\W3\\unins000.exe", "")]
	[InlineData("\"C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\upc.exe\" uplay://uninstall/5", "C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\upc.exe", "uplay://uninstall/5")]
	[InlineData("C:\\Program Files (x86)\\Some Game\\uninstall.exe /lang=en", "C:\\Program Files (x86)\\Some Game\\uninstall.exe", "/lang=en")]
	[InlineData("  \"C:\\x\\Blizzard Uninstaller.exe\"   --uid=fenris --displayname=\"Diablo IV\" ", "C:\\x\\Blizzard Uninstaller.exe", "--uid=fenris --displayname=\"Diablo IV\"")]
	public void AnUninstallString_AsAProgramAndItsArguments(string command, string exe, string args) =>
		Assert.Equal((exe, args), GameUninstall.Split(command));

	[Fact]
	public void MsiExecByName_IsWindowsOwn() =>
		Assert.Equal((Path.Combine(Environment.SystemDirectory, "msiexec.exe"), "/X{A}"), GameUninstall.Split("MsiExec.exe /X{A}"));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("unins000.exe")] // not by its full path
	[InlineData("rundll32.exe C:\\x\\uninstall.dll,Remove")]
	[InlineData("\"C:\\x\\unins000.exe")] // a quote never closed
	[InlineData("\"C:\\x\\unins000.exe\"/S")]
	[InlineData("C:\\x\\uninstall.bat")]
	public void NotAProgramByItsFullPath_IsntRun(string? command) {
		Assert.Null(GameUninstall.Split(command));
		Assert.False(GameUninstall.Usable(command));
	}

	[Theory]
	[InlineData("/S", true)]
	[InlineData("/SILENT /NORESTART", true)]
	[InlineData("/VERYSILENT", true)]
	[InlineData("/X{A} /quiet", true)]
	[InlineData("/X{A} /qn", true)]
	[InlineData("/X{A} /QB-", true)]
	[InlineData("/passive", true)]
	[InlineData("--silent", true)]
	[InlineData("-quiet=1", true)]
	[InlineData("--uid=fenris --displayname=\"Silent Hill\"", false)]
	[InlineData("uninstall_game -autologging", false)]
	[InlineData("uplay://uninstall/5", false)]
	[InlineData("/X{A}", false)]
	[InlineData("", false)]
	public void SwitchesThatSkipTheQuestions(string arguments, bool quiet) => Assert.Equal(quiet, GameUninstall.RunsQuietly(arguments));

	[Fact]
	public void TheCheck_ReadsTheEntriesOnce_OnlyWhenAGameNeedsThem() {
		var counting = new CountingRegistry(registry);
		GameUninstall.Mark([Game(GameLaunchers.Epic, "F", "Fortnite"), Game(GameLaunchers.Xbox, "B", "Game B")], counting);
		Assert.Equal(0, counting.Reads);
		registry.Entry(Uninstalls.Hklm32, "1_is1", Uninstalls.GogCmd).Entry(Uninstalls.Hkcu, "Uplay Install 5", Uninstalls.UbisoftCmd);
		List<GameInstall> marked = GameUninstall.Mark([Game(GameLaunchers.Gog, "1", "W3"), Game(GameLaunchers.Ubisoft, "5", "FC6"), Game(GameLaunchers.Ea, "A", "Apex")], counting);
		Assert.Equal([GameUninstall.Entry, GameUninstall.Entry, null], marked.Select(g => g.Uninstall));
		// Each view listed once, and each of its two entries' two values: not again for each game.
		Assert.Equal(3 + 2 * 2, counting.Reads);
	}

	[Fact]
	public void TheGamesCheck_MarksEachGamesWay() {
		using var pc = new FakeGamePC();
		pc.Registry.Entry(Uninstalls.Hklm64, "Steam App 100", Uninstalls.SteamCmd)
			.Entry(Uninstalls.Hklm32, "1207664663_is1", Uninstalls.GogCmd)
			.Entry(Uninstalls.Hklm64, "{EA-Apex}", Uninstalls.EaCmd, pc.P("EA", "Apex") + "\\");
		pc.Registry.Set(GameLibraries.UninstallKey + @"\Diablo IV", "UninstallString", Uninstalls.BattleNetCmd);
		var ways = GameScanner.Run(pc.Places).Games.ToDictionary(g => g.Name, g => g.Uninstall);
		Assert.Equal(GameUninstall.Steam, ways["Game A"]);
		Assert.Null(ways["Game B"]); // Steam wrote no entry for it
		Assert.Equal(GameUninstall.Entry, ways["The Witcher 3"]);
		Assert.Equal(GameUninstall.Entry, ways["Apex"]);
		Assert.Equal(GameUninstall.Entry, ways["Diablo IV"]);
		Assert.Null(ways["Far Cry 6"]); // no "Uplay Install 5" here
		Assert.Null(ways["Fortnite"]);
		Assert.Equal(GameUninstall.Settings, ways["Game B™"]);
	}

	// ---- Opening it: exactly what's registered, or Steam's link, or Windows' Installed apps; never anything else

	[Fact]
	public void Open_HandsTheOpener_ExactlyTheRegisteredStringOrSteamsLink() {
		registry.Entry(Uninstalls.Hklm64, "Steam App 100", Uninstalls.SteamCmd).Entry(Uninstalls.Hklm32, "1_is1", Uninstalls.GogCmd)
			.Entry(Uninstalls.Hklm32, "Uplay Install 5", Uninstalls.UbisoftCmd).Entry(Uninstalls.Hklm32, "Diablo IV", Uninstalls.BattleNetCmd);
		GameInstall apex = Game(GameLaunchers.Ea, "Apex", "Apex");
		registry.Entry(Uninstalls.Hklm64, "{EA}", Uninstalls.EaCmd, apex.Folder);
		GameUninstaller u = Uninstaller();
		var answers = new[] {
			u.Open(Game(GameLaunchers.Steam, "100", "Game A")), u.Open(Game(GameLaunchers.Gog, "1", "The Witcher 3")), u.Open(Game(GameLaunchers.Ubisoft, "5", "Far Cry 6")),
			u.Open(Game(GameLaunchers.BattleNet, "Diablo IV", "Diablo IV")), u.Open(apex), u.Open(Game(GameLaunchers.Xbox, "Game B", "Game B")),
		};
		Assert.Equal([("link", "steam://uninstall/100"), ("run", Uninstalls.GogCmd), ("run", Uninstalls.UbisoftCmd), ("run", Uninstalls.BattleNetCmd), ("run", Uninstalls.EaCmd),
			("link", "ms-settings:appsfeatures")], recorder.Opened);
		Assert.All(answers, a => {
			Assert.True(a.Opened);
			Assert.EndsWith("The next games check shows whether it's gone.", a.Message); // never that it's gone
		});
		Assert.Equal("Steam is asking whether to uninstall Game A: answer it in Steam's window. The next games check shows whether it's gone.", answers[0].Message);
		Assert.StartsWith("The Witcher 3's uninstaller is open: it asks before it removes anything", answers[1].Message);
		Assert.Equal("Windows' Installed apps is open: find Game B there to uninstall it. The next games check shows whether it's gone.", answers[5].Message);
	}

	[Fact]
	public void Open_NothingToOpen_OpensNothing() {
		GameUninstaller u = Uninstaller();
		var epic = u.Open(Game(GameLaunchers.Epic, "Fortnite", "Fortnite"));
		Assert.Equal((false, Uninstalls.EpicNote), (epic.Opened, epic.Message));
		Assert.Equal("Windows lists no uninstaller for it: uninstall it from GOG GALAXY.", u.Open(Game(GameLaunchers.Gog, "9", "Gone Entry")).Message);
		// Its folder gone since the check (uninstalled already): nothing opens, even with its entry still there.
		registry.Entry(Uninstalls.Hklm32, "1_is1", Uninstalls.GogCmd);
		GameInstall witcher = Game(GameLaunchers.Gog, "1", "The Witcher 3");
		Directory.Delete(witcher.Folder);
		var gone = u.Open(witcher);
		Assert.False(gone.Opened);
		Assert.Equal("The Witcher 3 isn't in its folder any more: check again to see what's installed.", gone.Message);
		Assert.Empty(recorder.Opened);
	}

	[Fact]
	public void Open_WhenWindowsWontStartIt_SaysSoPlainly() {
		registry.Entry(Uninstalls.Hklm32, "1_is1", Uninstalls.GogCmd);
		GameInstall witcher = Game(GameLaunchers.Gog, "1", "The Witcher 3");
		recorder.Throw = new Win32Exception(5, "Access is denied");
		Assert.Equal("The Microsoft Store version of Heiward couldn't open The Witcher 3's uninstaller (Access is denied). Uninstall it from GOG GALAXY, or in Settings > Apps > Installed apps.",
			Uninstaller(packaged: true).Open(witcher).Message);
		Assert.Equal("The Witcher 3's uninstaller didn't open (Access is denied). Uninstall it from GOG GALAXY, or in Settings > Apps > Installed apps.",
			Uninstaller().Open(witcher).Message);
		recorder.Throw = new Win32Exception(1223); // the person said no to Windows' administrator question
		var no = Uninstaller().Open(witcher);
		Assert.False(no.Opened);
		Assert.StartsWith("Windows asked whether to let The Witcher 3's uninstaller make changes, and was told no: nothing was opened.", no.Message);
		recorder.Throw = new Win32Exception(2, "The system cannot find the file specified");
		Assert.Equal("Windows' Installed apps didn't open (The system cannot find the file specified). Uninstall it in Settings > Apps > Installed apps.",
			Uninstaller().Open(Game(GameLaunchers.Xbox, "B", "Game B")).Message);
	}

	[Fact]
	public void TheShell_OpensOnlyItsOwnLinks_AndNoQuietUninstaller() {
		// Each is refused before anything starts (and none of these paths exists, should one get through).
		var shell = new ShellOpener();
		string nowhere = Path.Combine(root, "nowhere", "unins000.exe");
		Assert.Throws<InvalidOperationException>(() => shell.OpenLink("https://example.com/"));
		Assert.Throws<InvalidOperationException>(() => shell.OpenLink("steam://uninstall/100 --other"));
		Assert.Throws<InvalidOperationException>(() => shell.OpenLink("steam://run/100"));
		Assert.Throws<InvalidOperationException>(() => shell.OpenLink(nowhere));
		Assert.Throws<InvalidOperationException>(() => shell.Run("\"" + nowhere + "\" /VERYSILENT"));
		Assert.Throws<InvalidOperationException>(() => shell.Run("hei-no-such-uninstaller.exe"));
		Assert.True(GameUninstall.IsOwnLink("steam://uninstall/223850"));
		Assert.True(GameUninstall.IsOwnLink("ms-settings:appsfeatures"));
	}

	// ---- The page: the button, Heiward's own dialog, and the greyed button's tooltip

	/// <summary>A top-level function of the script, to the "}" that closes it.</summary>
	static string Body(string script, string name) {
		Match start = Regex.Match(script, @"(?m)^(async )?function " + Regex.Escape(name) + @"\(");
		Assert.True(start.Success, name + " isn't in the script");
		Match end = new Regex(@"\n\}\r?\n").Match(script, start.Index);
		return script[start.Index..(end.Index + 2)];
	}

	static readonly string Script = ThePage.Asset("app.js");

	[Fact]
	public void ThePage_AGamesPage_HasUninstall_UnderItsNameBesideItsCover() {
		string page = Body(Script, "gamePage");
		Assert.Contains("actions.append(gameUninstallButton(game))", page);
		Assert.Contains("devHeader(glyph, game.name, intro, chips, actions)", page);
		Assert.Contains("if (actions) text.append(actions);", Body(Script, "devHeader"));
	}

	[Fact]
	public void ThePage_TheButton_OpensTheDialog_ForAnUninstaller_AndPostsNothingItself() {
		string button = Body(Script, "gameUninstallButton");
		Assert.Contains("el('button', 'btn secondary', 'Uninstall')", button);
		// An uninstaller Windows lists: Heiward's dialog first. Steam's question and Windows' Installed apps: at once.
		Assert.Contains("btn.addEventListener('click', () => (game.uninstall === 'entry' ? openUninstallDialog(game) : uninstallGame(game)));", button);
		Assert.DoesNotContain("post(", button);
		Assert.DoesNotContain("confirm(", button);
	}

	[Fact]
	public void ThePage_TheDialog_CancelAndEscClose_OnlyItsUninstallButtonAsks() {
		string dialog = Body(Script, "openUninstallDialog");
		foreach (string line in new[] {
			"box.setAttribute('role', 'dialog');", "box.setAttribute('aria-modal', 'true');", "el('h2', null, 'Uninstall ' + game.name + '?')",
			"launcherSubject(game.launcher) + ' opens its own uninstaller, which asks before it removes anything.'",
			"[['Game', game.name], ['Size', bytes(game.bytes)], ['Launcher', launcherName(game.launcher)], ['Folder', game.folder]]",
			"Heiward itself deletes nothing",
			// Cancel: first, focused, and what Esc and a click beside the dialog do.
			"const cancel = el('button', 'btn secondary', 'Cancel');", "cancel.addEventListener('click', closeUninstallDialog);", "cancel.focus();",
			"if (e.key === 'Escape') { e.preventDefault(); closeUninstallDialog(); }", "if (e.target === box) closeUninstallDialog();",
			"buttons.append(cancel, go);",
			// Only "Uninstall <game>" asks for it.
			"const go = el('button', 'btn', 'Uninstall ' + game.name);", "go.addEventListener('click', () => { closeUninstallDialog(); uninstallGame(game); });",
			// The page under it is held.
			"for (const c of held) c.inert = true;",
		})
			Assert.Contains(line, dialog);
		Assert.Single(Regex.Matches(dialog, @"uninstallGame\("));
		Assert.DoesNotContain("post(", dialog);
		Assert.DoesNotContain("confirm(", dialog);
		string close = Body(Script, "closeUninstallDialog");
		Assert.DoesNotContain("post(", close);
		Assert.DoesNotContain("uninstallGame(", close);
		Assert.Contains("for (const c of held) c.inert = false;", close);
	}

	[Fact]
	public void ThePage_OnlyUninstallGame_Posts_AndOnlyTheGamesId() {
		string uninstall = Body(Script, "uninstallGame");
		Assert.Contains("await post('/api/games/' + encodeURIComponent(game.id) + '/uninstall');", uninstall);
		Assert.Single(Regex.Matches(Script, Regex.Escape("+ '/uninstall'")));
		// Called from the button (Steam, Xbox) and the dialog's own Uninstall: nowhere else.
		var callers = Regex.Matches(Script, @"(?<!function )uninstallGame\(").Select(m => Script.LastIndexOf("\nfunction ", m.Index, StringComparison.Ordinal))
			.Select(i => Regex.Match(Script[(i + 1)..], @"^function (\w+)").Groups[1].Value).ToList();
		Assert.Equal(["gameUninstallButton", "openUninstallDialog"], callers);
		// Going to another page closes the dialog: it belongs to its game's page.
		Assert.Matches(@"window\.addEventListener\('hashchange', \(\) => \{\r?\n  closeUninstallDialog\(\);", Script);
	}

	[Fact]
	public void ThePage_AGreyedButton_StillShowsItsTooltip_OnHoverAndFocus() {
		string button = Body(Script, "gameUninstallButton");
		foreach (string line in new[] {
			"btn.setAttribute('aria-disabled', 'true');", "e.preventDefault()", "el('span', 'tip below start')", "game.uninstallNote",
			"body.setAttribute('role', 'tooltip');", "btn.setAttribute('aria-describedby', body.id);",
		})
			Assert.Contains(line, button);
		// Not the disabled attribute, which would take the button out of hover and the keyboard's reach.
		int greyed = button.IndexOf("if (!game.uninstall) {", StringComparison.Ordinal);
		Assert.DoesNotContain("disabled = true", button[greyed..button.IndexOf("return wrap;", greyed, StringComparison.Ordinal)]);
		string css = ThePage.Asset("app.css");
		Assert.Contains(".tip:hover .tip-body, .tip:focus-within .tip-body", css);
		Assert.Contains(".tip.start .tip-body { right: auto; left: 0; }", css);
		Assert.Contains(".btn[aria-disabled=\"true\"], .btn[aria-disabled=\"true\"]:hover { opacity: 0.5;", css);
		Assert.Contains(".tour, .modal {", css);
	}
}

/// <summary>
/// POST /api/games/&lt;id&gt;/uninstall on the review page, with a fake registry and a recorder: only a game of the last check, only
/// what its entry says, behind the page's token and Origin checks like every action, and never anything the request names.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME and MANOR_HOME are process-wide
public sealed class GameUninstallPageTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-uninstallpage-" + Guid.NewGuid().ToString("N"));
	readonly string? manorHome = Environment.GetEnvironmentVariable("MANOR_HOME");
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	readonly FakeRegistry registry = new();
	readonly UninstallRecorder recorder = new();

	public GameUninstallPageTests() {
		Directory.CreateDirectory(Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("MANOR_HOME", Path.Combine(dir, "manor")); // no Manor
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("MANOR_HOME", manorHome);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	async Task<(int Port, Task<int> Server, HttpClient Http, string Token)> StartAsync() {
		var cfg = new AgentConfig { GameMode = "on", ScanEveryMinutes = 0 };
		using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
			probe.Start();
			cfg.Port = ((IPEndPoint)probe.LocalEndpoint).Port;
		}
		cfg.Save();
		Task<int> server = ReviewServer.RunAsync(cfg, openBrowser: false, CancellationToken.None, new GameUninstaller(registry, recorder, Packaged: false));
		var up = Stopwatch.StartNew();
		while (!await ReviewServer.IsUpAsync(cfg.Port)) {
			Assert.True(up.Elapsed < TimeSpan.FromSeconds(20) && !server.IsCompleted, "The review page didn't come up");
			await Task.Delay(100);
		}
		var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{cfg.Port}/"), Timeout = TimeSpan.FromSeconds(30) };
		return (cfg.Port, server, http, ReviewServer.TokenIn(await http.GetStringAsync("/"))!);
	}

	static async Task<(HttpStatusCode Status, string Body)> Post(HttpClient http, string path, string? token, object? body = null, string? origin = null) {
		using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
		if (token != null) request.Headers.Add("X-Agent-Token", token);
		if (origin != null) request.Headers.Add("Origin", origin);
		using HttpResponseMessage answer = await http.SendAsync(request);
		return (answer.StatusCode, await answer.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task OnlyAGameOfTheLastCheck_OnlyWhatItsEntrySays_NeverWhatTheRequestNames() {
		string games = Path.Combine(dir, "games");
		GameInstall steam = Uninstalls.Game(games, GameLaunchers.Steam, "100", "Game A"), gog = Uninstalls.Game(games, GameLaunchers.Gog, "1", "The Witcher 3"),
			epic = Uninstalls.Game(games, GameLaunchers.Epic, "Fortnite", "Fortnite"), xbox = Uninstalls.Game(games, GameLaunchers.Xbox, "Game B", "Game B");
		registry.Entry(Uninstalls.Hklm64, "Steam App 100", Uninstalls.SteamCmd).Entry(Uninstalls.Hklm32, "1_is1", Uninstalls.GogCmd);
		new GameReport { ScannedAtUtc = DateTime.UtcNow, Build = AppBuild.Current, Games = GameUninstall.Mark([steam, gog, epic, xbox], registry) }.Save();
		var (port, server, http, token) = await StartAsync();
		try {
			// The games list carries each game's way, and why a greyed one is.
			using (JsonDocument list = JsonDocument.Parse(await http.GetStringAsync("/api/games"))) {
				var byName = list.RootElement.GetProperty("report").GetProperty("games").EnumerateArray().ToDictionary(g => g.GetProperty("name").GetString()!);
				Assert.Equal("entry", byName["The Witcher 3"].GetProperty("uninstall").GetString());
				Assert.Equal(Uninstalls.EpicNote, byName["Fortnite"].GetProperty("uninstallNote").GetString());
			}

			// Not a game of the last check, or not an id at all: nothing opens.
			foreach (string id in new[] { "ffffffffffffffff", "..%2F..%2Fx", Uri.EscapeDataString(Uninstalls.GogCmd), "C:%5CWindows%5Cnotepad.exe" })
				Assert.NotEqual(HttpStatusCode.OK, (await Post(http, $"/api/games/{id}/uninstall", token)).Status);
			Assert.Equal(HttpStatusCode.NotFound, (await Post(http, "/api/games/ffffffffffffffff/uninstall", token)).Status);
			Assert.Empty(recorder.Opened);

			// Without the page's token, or from another site: refused before anything is looked up.
			Assert.Equal(HttpStatusCode.Forbidden, (await Post(http, $"/api/games/{gog.Id}/uninstall", token: null)).Status);
			Assert.Equal(HttpStatusCode.Forbidden, (await Post(http, $"/api/games/{gog.Id}/uninstall", token, origin: "http://example.com")).Status);
			Assert.Empty(recorder.Opened);

			// Whatever the request carries, exactly what's registered (or Steam's link, or Windows' Installed apps) is opened.
			var forged = new { uninstallString = @"C:\Windows\System32\calc.exe", command = "calc", target = "https://example.com/", way = "settings", folder = @"C:\", launcher = "steam" };
			var (status, body) = await Post(http, $"/api/games/{gog.Id}/uninstall", token, forged);
			Assert.Equal(HttpStatusCode.OK, status);
			Assert.Contains("The next games check shows whether it", body);
			Assert.Equal(HttpStatusCode.OK, (await Post(http, $"/api/games/{steam.Id}/uninstall", token, forged)).Status);
			Assert.Equal(HttpStatusCode.OK, (await Post(http, $"/api/games/{xbox.Id}/uninstall", token, forged)).Status);
			Assert.Equal([("run", Uninstalls.GogCmd), ("link", "steam://uninstall/100"), ("link", "ms-settings:appsfeatures")], recorder.Opened);

			// Epic: refused, saying where to uninstall it instead.
			var (epicStatus, epicBody) = await Post(http, $"/api/games/{epic.Id}/uninstall", token, forged);
			Assert.Equal(HttpStatusCode.Conflict, epicStatus);
			Assert.Equal(Uninstalls.EpicNote, JsonDocument.Parse(epicBody).RootElement.GetProperty("error").GetString());
			Assert.Equal(3, recorder.Opened.Count);

			// Game mode off: the Games area's requests are refused, this one too.
			Assert.Equal(HttpStatusCode.OK, (await Post(http, "/api/settings", token, new { gameMode = false })).Status);
			Assert.Equal(HttpStatusCode.Conflict, (await Post(http, $"/api/games/{gog.Id}/uninstall", token)).Status);
			Assert.Equal(3, recorder.Opened.Count);
		}
		finally {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
			http.Dispose();
		}
	}
}
