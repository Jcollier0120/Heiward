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

/// <summary>The registry as a test writes it: key → value name → value.</summary>
sealed class FakeRegistry : IGameRegistry {
	readonly Dictionary<string, Dictionary<string, string>> keys = new(StringComparer.OrdinalIgnoreCase);

	public FakeRegistry Set(string key, string name, string value) {
		if (!keys.TryGetValue(key, out var values)) keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
		values[name] = value;
		return this;
	}

	public string? Value(string key, string name) => keys.TryGetValue(key, out var v) && v.TryGetValue(name, out string? s) ? s : null;

	public IReadOnlyList<string> SubKeys(string key) =>
		keys.Keys.Where(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
			.Select(k => k[(key.Length + 1)..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// A PC with games, made under a temporary folder: Steam (two libraries), Epic, GOG GALAXY, the EA app, Ubisoft Connect,
/// Battle.net and an Xbox game, each written the way its launcher writes it, and what games leave behind beside them.
/// Nothing here reads this PC's own launchers, registry or processes.
/// </summary>
sealed class FakeGamePC : IDisposable {
	public readonly string Root = Path.Combine(Path.GetTempPath(), "hei-games-" + Guid.NewGuid().ToString("N"));
	public readonly FakeRegistry Registry = new();
	public readonly List<RunningProgram> Running = new();
	public readonly DateTime Now = DateTime.UtcNow;

	public string P(params string[] parts) => Path.Combine([Root, .. parts]);
	public string Steam => P("PF86", "Steam");
	public string Lib2 => P("Games", "SteamLibrary");
	public string Drive => P("DriveX");

	public GamePlaces Places => new() {
		LocalAppData = P("Local"), LocalLow = P("LocalLow"), ProgramData = P("ProgramData"), ProgramFiles = P("PF"), ProgramFilesX86 = P("PF86"),
		Temp = P("Temp"), Profile = P("Profile"), Documents = P("Profile", "Documents"), Drives = [Drive],
		Registry = Registry, Running = () => Running,
	};

	/// <summary>A file of <paramref name="bytes"/> (2 MB unless said: over the 1 MB a row needs).</summary>
	public string File(string path, long bytes = 2 << 20) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		using (var f = new FileStream(path, FileMode.Create)) f.SetLength(bytes);
		return path;
	}

	public void Text(string path, string text) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		System.IO.File.WriteAllText(path, text);
	}

	public static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

	public void SteamManifest(string lib, string app, string name, string dir, DateTime? played = null, long size = 3 << 20) =>
		Text(Path.Combine(lib, "steamapps", $"appmanifest_{app}.acf"),
			$"\"AppState\"\n{{\n\t\"appid\"\t\t\"{app}\"\n\t\"name\"\t\t\"{name}\"\n\t\"installdir\"\t\t\"{dir}\"\n\t\"SizeOnDisk\"\t\t\"{size}\"\n" +
			$"\t\"LastUpdated\"\t\t\"{Unix(Now.AddDays(-400))}\"\n\t\"LastPlayed\"\t\t\"{(played is { } p ? Unix(p) : 0)}\"\n}}\n");

	public FakeGamePC() {
		// Steam: its own folder and a second library, listed the new way (and the old way in the old file).
		string vdfLib2 = Lib2.Replace("\\", "\\\\");
		Text(Path.Combine(Steam, "steamapps", "libraryfolders.vdf"),
			"\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + Steam.Replace("\\", "\\\\") + "\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"100\"\t\t\"3145728\"\n\t\t}\n\t}\n" +
			"\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + vdfLib2 + "\"\n\t}\n}\n");
		Registry.Set(@"HKCU\Software\Valve\Steam", "SteamPath", Steam.Replace('\\', '/'));
		SteamManifest(Steam, "100", "Game A", "Game A", played: Now.AddDays(-200));
		File(P("PF86", "Steam", "steamapps", "common", "Game A", "Binaries", "Win64", "GameA.exe"), 1000);
		SteamManifest(Lib2, "200", "Game B", "GameB", played: Now.AddDays(-2));
		File(Path.Combine(Lib2, "steamapps", "common", "GameB", "GameB.exe"));
		// A game being downloaded for the first time: its manifest claims a folder that isn't there yet.
		SteamManifest(Lib2, "500", "Game E", "GameE");
		// Leftovers: a folder no manifest claims; another with a saved game in it.
		File(Path.Combine(Lib2, "steamapps", "common", "Old Game", "data.pak"));
		File(Path.Combine(Lib2, "steamapps", "common", "Saved Orphan", "data.pak"));
		File(Path.Combine(Lib2, "steamapps", "common", "Saved Orphan", "SaveGames", "slot1.sav"), 100);
		// Workshop items: of an uninstalled game, and of an installed one.
		File(Path.Combine(Lib2, "steamapps", "workshop", "content", "300", "1", "mod.vpk"));
		Text(Path.Combine(Lib2, "steamapps", "workshop", "appworkshop_300.acf"), "\"AppWorkshop\" { }");
		File(Path.Combine(Lib2, "steamapps", "workshop", "content", "100", "1", "mod.vpk"));
		// Shader caches: of an uninstalled game, of an installed one, and the graphics driver's.
		File(Path.Combine(Steam, "steamapps", "shadercache", "300", "fozpipelinesv6", "a.foz"));
		File(Path.Combine(Steam, "steamapps", "shadercache", "100", "fozpipelinesv6", "a.foz"));
		File(P("Local", "NVIDIA", "DXCache", "a.bin"));
		// Steam's caches, and its downloads: of a game it lists (paused) and of one it doesn't.
		File(Path.Combine(Steam, "depotcache", "1.manifest"));
		File(Path.Combine(Lib2, "steamapps", "downloading", "200", "chunk"));
		File(Path.Combine(Lib2, "steamapps", "downloading", "400", "chunk"));

		// Epic: a game, its add-on in the same folder, a leftover with Epic's record in it, and the launcher's own folder.
		string epic = P("PF", "Epic Games");
		Text(P("ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests", "A1.item"),
			$"{{ \"DisplayName\": \"Fortnite\", \"AppName\": \"Fortnite\", \"MainGameAppName\": \"Fortnite\", \"InstallLocation\": \"{Path.Combine(epic, "Fortnite").Replace("\\", "\\\\")}\", \"InstallSize\": 5242880 }}");
		Text(P("ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests", "A2.item"),
			$"{{ \"DisplayName\": \"Fortnite Add-on\", \"AppName\": \"FortniteDLC\", \"MainGameAppName\": \"Fortnite\", \"InstallLocation\": \"{Path.Combine(epic, "Fortnite").Replace("\\", "\\\\")}\" }}");
		File(Path.Combine(epic, "Fortnite", "FortniteClient.exe"), 1000);
		File(Path.Combine(epic, "OldEpic", "game.pak"));
		Directory.CreateDirectory(Path.Combine(epic, "OldEpic", ".egstore"));
		File(Path.Combine(epic, "Launcher", "launcher.dll"));

		// GOG GALAXY, the EA app, Ubisoft Connect, Battle.net: what they write to the registry.
		Registry.Set(GameLibraries.GogKey + @"\1207664663", "path", P("GOG", "The Witcher 3")).Set(GameLibraries.GogKey + @"\1207664663", "gameName", "The Witcher 3");
		File(P("GOG", "The Witcher 3", "witcher3.exe"), 4096);
		Registry.Set(GameLibraries.EaKey + @"\Apex", "Install Dir", P("EA", "Apex"));
		Directory.CreateDirectory(P("EA", "Apex", "__Installer"));
		File(P("EA", "Apex", "r5apex.exe"), 4096);
		Registry.Set(GameLibraries.EaKey + @"\Not A Game", "Install Dir", P("EA", "Tool")); // no __Installer: not the EA app's
		Directory.CreateDirectory(P("EA", "Tool"));
		Registry.Set(GameLibraries.UbisoftKey + @"\5", "InstallDir", P("Ubi", "Far Cry 6").Replace('\\', '/') + "/");
		File(P("Ubi", "Far Cry 6", "farcry6.exe"), 4096);
		string d4 = GameLibraries.UninstallKey + @"\Diablo IV";
		Registry.Set(d4, "Publisher", "Blizzard Entertainment").Set(d4, "DisplayName", "Diablo IV").Set(d4, "InstallLocation", P("Blizzard", "Diablo IV"));
		File(P("Blizzard", "Diablo IV", "Diablo IV.exe"), 4096);
		string bnet = GameLibraries.UninstallKey + @"\Battle.net";
		Registry.Set(bnet, "Publisher", "Blizzard Entertainment").Set(bnet, "DisplayName", "Battle.net").Set(bnet, "InstallLocation", P("Blizzard", "Battle.net"));
		Directory.CreateDirectory(P("Blizzard", "Battle.net"));

		// Xbox: Game B again, named with its ™.
		Text(Path.Combine(Drive, "XboxGames", "Game B", "Content", "MicrosoftGame.config"),
			"<Game configVersion=\"1\"><ShellVisuals DefaultDisplayName=\"Game B™\" PublisherDisplayName=\"Someone\" /></Game>");
		File(Path.Combine(Drive, "XboxGames", "Game B", "Content", "gameb.exe"), 1 << 20);

		// Crash dumps: Windows' of Game A, Windows Error Reporting's of Game A, an Unreal game's crash folder, Steam's own.
		File(P("Local", "CrashDumps", "GameA.exe.1234.dmp"), 4096);
		File(P("Local", "CrashDumps", "notepad.exe.99.dmp"), 4096);
		File(P("Local", "Microsoft", "Windows", "WER", "ReportArchive", "AppCrash_GameA.exe_abc123_def456_1", "Report.wer"), 4096);
		File(P("Local", "Microsoft", "Windows", "WER", "ReportArchive", "AppCrash_notepad.exe_abc_def_2", "Report.wer"), 4096);
		File(P("Local", "SomeShooter", "Saved", "Crashes", "UECC-1", "dump.dmp"), 4096);
		File(P("Local", "SomeShooter", "Saved", "SaveGames", "1.sav"), 100);
		File(Path.Combine(Steam, "dumps", "steam.dmp"), 4096);
	}

	public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

/// <summary>The game finders, each against a fake PC: launchers and libraries, leftovers, caches, shader caches, crash dumps, twice, idle.</summary>
public sealed class GameFindersTests : IDisposable {
	readonly FakeGamePC pc = new();

	public void Dispose() => pc.Dispose();

	GameReport Check() => GameScanner.Run(pc.Places);

	static List<GameItem> In(GameReport r, string category) => r.Categories.FirstOrDefault(c => c.Key == category)?.Items ?? [];

	// ---- Valve's KeyValues

	[Fact]
	public void KeyValues_ReadsNestingEscapesAndComments() {
		KeyValues kv = KeyValues.Parse("// a comment\n\"root\"\n{\n\t\"path\"\t\"D:\\\\Steam \\\"Lib\\\"\"\n\t\"Child\" { \"a\" \"1\" }\n\t\"path\" \"second\"\n}");
		KeyValues root = kv.Child("ROOT")!;
		Assert.Equal("D:\\Steam \"Lib\"", root.Text("path")); // the first of a repeated key
		Assert.Equal("1", root.Child("child")!.Text("A"));
		Assert.Null(KeyValues.Parse("\"cut\" { \"short").Child("cut")!.Text("short"));
	}

	[Fact]
	public void SteamLibraries_BothFormats() {
		Assert.Equal([pc.Steam, pc.Lib2], GameLibraries.SteamLibraryFolders(pc.Steam));
		// The old format: "1" "D:\\SteamLibrary".
		pc.Text(Path.Combine(pc.Steam, "steamapps", "libraryfolders.vdf"), "\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\" \"1\"\n\t\"1\"\t\"" + pc.Lib2.Replace("\\", "\\\\") + "\"\n}");
		Assert.Equal([pc.Steam, pc.Lib2], GameLibraries.SteamLibraryFolders(pc.Steam));
	}

	// ---- Launchers and libraries

	[Fact]
	public void FindsEveryLaunchersGames() {
		GameLibraries.Found found = GameLibraries.Find(pc.Places);
		var byName = found.Games.ToDictionary(g => g.Name);
		Assert.Equal(["Apex", "Diablo IV", "Far Cry 6", "Fortnite", "Game A", "Game B", "Game B™", "The Witcher 3"], byName.Keys.Order(StringComparer.Ordinal));
		Assert.Equal(GameLaunchers.Steam, byName["Game A"].Launcher);
		Assert.Equal(GameLaunchers.Epic, byName["Fortnite"].Launcher);
		Assert.Equal(GameLaunchers.Gog, byName["The Witcher 3"].Launcher);
		Assert.Equal(GameLaunchers.Ea, byName["Apex"].Launcher);
		Assert.Equal(GameLaunchers.Ubisoft, byName["Far Cry 6"].Launcher);
		Assert.Equal(GameLaunchers.BattleNet, byName["Diablo IV"].Launcher);
		Assert.Equal(GameLaunchers.Xbox, byName["Game B™"].Launcher);
		// Sizes and last played, where the launcher records them.
		Assert.Equal(3 << 20, byName["Game A"].Bytes);
		Assert.Equal(5 << 20, byName["Fortnite"].Bytes);
		Assert.Equal(FakeGamePC.Unix(pc.Now.AddDays(-200)), FakeGamePC.Unix(byName["Game A"].LastPlayedUtc!.Value));
		Assert.Null(byName["The Witcher 3"].LastPlayedUtc);
		Assert.Equal(pc.P("Ubi", "Far Cry 6"), byName["Far Cry 6"].Folder); // forward slashes and the trailing one, made Windows'
		// A game still downloading claims its folder, though it isn't a game yet.
		Assert.Contains(Path.Combine(pc.Lib2, "steamapps", "common", "GameE"), found.Claimed);
		Assert.Contains("500", found.SteamApps);
		Assert.Equal(["steam", "epic", "gog", "ea", "ubisoft", "battlenet", "xbox"], found.Launchers);
	}

	[Fact]
	public void NoLaunchers_NoGames() {
		GameReport r = GameScanner.Run(new GamePlaces {
			LocalAppData = pc.P("e", "L"), LocalLow = pc.P("e", "LL"), ProgramData = pc.P("e", "PD"), ProgramFiles = pc.P("e", "PF"), ProgramFilesX86 = pc.P("e", "PF86"),
			Temp = pc.P("e", "T"), Profile = pc.P("e", "U"), Documents = pc.P("e", "U", "D"), Drives = [], Registry = new FakeRegistry(), Running = () => [],
		});
		Assert.Empty(r.Games);
		Assert.Empty(r.Categories);
		Assert.Empty(r.Launchers);
	}

	// ---- Leftovers of uninstalled games

	[Fact]
	public void Leftovers_UnclaimedLibraryFolders_AndWorkshopOfUninstalledGames() {
		var items = In(Check(), GameScanner.Leftovers).ToDictionary(i => i.Name);
		GameItem old = items["Old Game"];
		Assert.Equal("orphan", old.Kind);
		Assert.True(old.Suggested);
		Assert.Null(old.Blocked);
		// A folder with a saved game in it waits for you.
		GameItem saved = items["Saved Orphan"];
		Assert.False(saved.Suggested);
		Assert.Equal("It may hold saved games: look inside it yourself", saved.Blocked);
		// Workshop items of a game no longer installed, with Steam's record of them; none of an installed game.
		GameItem workshop = items["Workshop items of Steam game 300"];
		Assert.True(workshop.Suggested);
		Assert.Contains(Path.Combine(pc.Lib2, "steamapps", "workshop", "appworkshop_300.acf"), workshop.Paths);
		Assert.DoesNotContain(items.Values, i => i.AppId == "100");
		// Epic: only a folder with Epic's own record in it.
		Assert.Equal(GameLaunchers.Epic, items["OldEpic"].Launcher);
		Assert.DoesNotContain("Launcher", items.Keys);
		// Installed games and a download in progress are never leftovers.
		Assert.DoesNotContain(items.Values, i => i.Name is "Game A" or "GameB" or "GameE" or "Fortnite");
	}

	[Fact]
	public void Leftovers_InALibrarySteamListsNoGameIn_AreNeverTicked() {
		foreach (string m in Directory.GetFiles(Path.Combine(pc.Lib2, "steamapps"), "appmanifest_*.acf")) File.Delete(m);
		GameItem gameB = In(Check(), GameScanner.Leftovers).Single(i => i.Name == "GameB");
		Assert.False(gameB.Suggested);
		Assert.Contains("lost track", gameB.Detail);
	}

	// ---- Download caches

	[Fact]
	public void Caches_TheLaunchersOwn_LeftAloneWhileTheLauncherRuns() {
		var items = In(Check(), GameScanner.Caches).ToDictionary(i => i.Name);
		GameItem depot = items["Steam's download cache"];
		Assert.True(depot.Suggested);
		Assert.Null(depot.Blocked);
		// Downloads: of a game no longer listed, a leftover; of one Steam lists, a paused download, never ticked.
		GameItem leftover = items["Unfinished download of Steam game 400"];
		Assert.Equal("download", leftover.Kind);
		Assert.True(leftover.Suggested);
		GameItem paused = items["Unfinished download of Game B"];
		Assert.Equal("paused", paused.Kind);
		Assert.False(paused.Suggested);

		pc.Running.Add(new RunningProgram("steam", @"C:\Program Files (x86)\Steam\steam.exe"));
		items = In(Check(), GameScanner.Caches).ToDictionary(i => i.Name);
		Assert.Equal("Steam is running: close it first", items["Steam's download cache"].Blocked);
		Assert.False(items["Steam's download cache"].Suggested);
		Assert.NotNull(items["Unfinished download of Steam game 400"].Blocked);
	}

	// ---- Shader caches

	[Fact]
	public void Shaders_OfUninstalledGamesTicked_OfInstalledGamesNever() {
		var items = In(Check(), GameScanner.Shaders).ToDictionary(i => i.Name);
		GameItem gone = items["Shaders of Steam game 300"];
		Assert.True(gone.Suggested);
		Assert.Null(gone.Game);
		GameItem installed = items["Shaders of Game A"];
		Assert.False(installed.Suggested);
		Assert.NotNull(installed.Game);
		Assert.Contains("stutter", installed.Removing);
		GameItem driver = items["NVIDIA shader cache"];
		Assert.Equal("gpu-shader", driver.Kind);
		Assert.False(driver.Suggested);
		Assert.Contains("stutter", driver.Removing);
	}

	[Fact]
	public void Shaders_WaitWhileAGameRuns() {
		pc.Running.Add(new RunningProgram("GameA", pc.P("PF86", "Steam", "steamapps", "common", "Game A", "Binaries", "Win64", "GameA.exe")));
		var items = In(Check(), GameScanner.Shaders).ToDictionary(i => i.Name);
		Assert.Equal("Game A is running: close it first", items["Shaders of Game A"].Blocked);
		Assert.Equal("Game A is running: close it first", items["NVIDIA shader cache"].Blocked);
		Assert.Null(items["Shaders of Steam game 300"].Blocked);
	}

	// ---- Crash dumps

	[Fact]
	public void Dumps_OfTheGamesOnly() {
		var items = In(Check(), GameScanner.Dumps);
		GameItem dumps = items.Single(i => i.Kind == "dump" && i.Name == "Crash dumps of Game A");
		Assert.Equal([pc.P("Local", "CrashDumps", "GameA.exe.1234.dmp")], dumps.Paths); // not notepad's
		Assert.True(dumps.Suggested);
		GameItem wer = items.Single(i => i.Kind == "wer");
		Assert.Equal("Windows' crash reports of Game A", wer.Name);
		Assert.Single(wer.Paths);
		GameItem crash = items.Single(i => i.Kind == "crash");
		Assert.Equal("Crash reports of SomeShooter", crash.Name);
		Assert.Equal([pc.P("Local", "SomeShooter", "Saved", "Crashes")], crash.Paths); // never the saves beside it
		Assert.Contains(items, i => i.Name == "Steam's crash dumps");
	}

	[Theory]
	[InlineData("AppCrash_GameA.exe_abc_def_1", "GameA.exe")]
	[InlineData("AppHang_FortniteClient-Wi_abc_def_1", "FortniteClient-Wi")]
	[InlineData("Critical_x_1", null)]
	[InlineData("AppCrash", null)]
	public void WerFolders_NameTheirProgram(string folder, string? exe) => Assert.Equal(exe, GameScanner.WerExe(folder));

	// ---- Twice, and not played in months

	[Fact]
	public void Twice_TheSameGameInTwoLaunchers() {
		GameItem twice = Assert.Single(In(Check(), GameScanner.Twice));
		Assert.True(twice.Info);
		Assert.False(twice.Suggested);
		Assert.Equal(2, twice.Paths.Count);
		Assert.Contains("Steam", twice.Detail);
		Assert.Contains("the Xbox app", twice.Detail);
		Assert.Equal("game b", GameScanner.SameName("Game B™"));
		Assert.Equal("the witcher 3 wild hunt", GameScanner.SameName("The Witcher® 3: Wild Hunt"));
	}

	[Fact]
	public void Idle_NotPlayedInMonths_WithTheLaunchersOwnMove() {
		GameItem idle = Assert.Single(In(Check(), GameScanner.Idle));
		Assert.Equal("Game A", idle.Name);
		Assert.True(idle.Info);
		Assert.Contains("6 months ago", idle.Detail);
		Assert.Contains("Move install folder", idle.Removing);
		Assert.Contains("Manage › Files › Move", GameLaunchers.MoveAdvice(GameLaunchers.Xbox));
	}

	[Fact]
	public void TheReport_NamesNothingTechnical() {
		GameReport r = Check();
		foreach (GameCategory c in r.Categories) {
			foreach (string text in new[] { c.Title, c.Explain }.Concat(c.Items.SelectMany(i => new[] { i.Name, i.Detail, i.Removing, i.Blocked ?? "" })))
				foreach (string jargon in new[] { ".vdf", ".acf", "manifest", "registry", "json", "ReportArchive", "hei games", "hei setup", "settings" })
					Assert.DoesNotContain(jargon, text, StringComparison.OrdinalIgnoreCase);
		}
	}
}

/// <summary>What's never touched, and the checks made again just before anything is removed.</summary>
public sealed class GameSafetyTests : IDisposable {
	readonly FakeGamePC pc = new();

	public void Dispose() => pc.Dispose();

	[Fact]
	public void NeverAnInstalledGame_NorWhatHoldsOne() {
		var found = GameLibraries.Find(pc.Places);
		string gameA = pc.P("PF86", "Steam", "steamapps", "common", "Game A");
		Assert.Equal("It's part of Game A, which is installed", GameSafety.Refuse(gameA, found.Games, pc.Places, found.SteamRoot));
		Assert.Equal("It's part of Game A, which is installed", GameSafety.Refuse(Path.Combine(gameA, "Binaries"), found.Games, pc.Places, found.SteamRoot));
		Assert.Equal("It holds Game A, which is installed", GameSafety.Refuse(pc.P("PF86", "Steam", "steamapps", "common"), found.Games, pc.Places, found.SteamRoot));
		Assert.Equal("It's a drive", GameSafety.Refuse(Path.GetPathRoot(pc.Root)!, found.Games, pc.Places, found.SteamRoot));
		Assert.Null(GameSafety.Refuse(Path.Combine(pc.Steam, "depotcache"), found.Games, pc.Places, found.SteamRoot));
	}

	[Fact]
	public void NeverSavedGames() {
		var found = GameLibraries.Find(pc.Places);
		foreach (string path in new[] {
			pc.P("Profile", "Saved Games", "Game"), pc.P("Profile", "Documents", "My Games", "Game"), Path.Combine(pc.Steam, "userdata", "1", "100"),
			pc.P("Local", "SomeShooter", "Saved", "SaveGames"), pc.P("Profile"),
		})
			Assert.Equal("Saved games are never touched", GameSafety.Refuse(path, found.Games, pc.Places, found.SteamRoot));
		Assert.Equal("It may hold saved games: look inside it yourself",
			GameSafety.Refuse(Path.Combine(pc.Lib2, "steamapps", "common", "Saved Orphan"), found.Games, pc.Places, found.SteamRoot));
		Assert.True(GameSafety.LooksLikeSave("slot.sav", folder: false));
		Assert.True(GameSafety.LooksLikeSave("SaveGames", folder: true));
		Assert.False(GameSafety.LooksLikeSave("Saved", folder: true)); // Unreal's folder of logs and crashes
	}

	GameItem Item(string kind, string name) => GameScanner.Run(pc.Places).Items.First(i => i.Kind == kind && i.Name == name);

	[Fact]
	public void Recheck_AFolderALauncherClaimsAgain_Stays() {
		GameItem old = Item("orphan", "Old Game");
		Assert.Null(GameScanner.Recheck(old, pc.Places));
		pc.SteamManifest(pc.Lib2, "600", "Old Game", "Old Game");
		Assert.Equal("A launcher lists it as installed again", GameScanner.Recheck(old, pc.Places));
		Assert.Equal("A launcher lists it as installed again", GameCleaner.Remove(old, pc.Places).Error);
		Assert.True(File.Exists(Path.Combine(pc.Lib2, "steamapps", "common", "Old Game", "data.pak")));
	}

	[Fact]
	public void Recheck_WhatAGameLeft_StaysOnceItsInstalledAgain() {
		GameItem workshop = Item("workshop", "Workshop items of Steam game 300");
		GameItem shaders = Item("shader", "Shaders of Steam game 300");
		pc.SteamManifest(pc.Steam, "300", "Game C", "Game C");
		Assert.Equal("The game is installed again", GameScanner.Recheck(workshop, pc.Places));
		Assert.Equal("The game is installed again", GameScanner.Recheck(shaders, pc.Places));
	}

	[Fact]
	public void Recheck_NothingOfALauncherOrGameThatRuns() {
		GameItem depot = Item("cache", "Steam's download cache");
		GameItem dumps = Item("dump", "Crash dumps of Game A");
		GameItem driver = Item("gpu-shader", "NVIDIA shader cache");
		pc.Running.Add(new RunningProgram("steamwebhelper", null));
		Assert.Equal("Steam is running: close it first", GameScanner.Recheck(depot, pc.Places));
		Assert.Null(GameScanner.Recheck(dumps, pc.Places));
		pc.Running.Add(new RunningProgram("GameA", pc.P("PF86", "Steam", "steamapps", "common", "Game A", "GameA.exe")));
		Assert.Equal("Game A is running: close it first", GameScanner.Recheck(dumps, pc.Places));
		Assert.Equal("Game A is running: close it first", GameScanner.Recheck(driver, pc.Places));
		Assert.Equal("Steam is running: close it first", GameCleaner.Remove(depot, pc.Places).Error);
		Assert.True(File.Exists(Path.Combine(pc.Steam, "depotcache", "1.manifest")));
	}

	[Fact]
	public void Remove_NeverMovesAGame_NorAFolderThatGotSaves() {
		GameItem idle = GameScanner.Run(pc.Places).Items.First(i => i.Kind == "idle");
		Assert.StartsWith("Heiward doesn't move or uninstall games.", GameCleaner.Remove(idle, pc.Places).Error);
		// Saved since the check: refused now, whatever the list said.
		GameItem old = Item("orphan", "Old Game");
		pc.File(Path.Combine(pc.Lib2, "steamapps", "common", "Old Game", "profile.save"), 10);
		Assert.Equal("It may hold saved games: look inside it yourself", GameCleaner.Remove(old, pc.Places).Error);
		// An item the list says is a leftover, that is an installed game's folder: refused.
		var forged = old with { Kind = "crash", Paths = [pc.P("PF86", "Steam", "steamapps", "common", "Game A")] };
		Assert.Equal("It's part of Game A, which is installed", GameCleaner.Remove(forged, pc.Places).Error);
		Assert.True(File.Exists(pc.P("PF86", "Steam", "steamapps", "common", "Game A", "Binaries", "Win64", "GameA.exe")));
	}

	[Fact]
	public void TheRecycleBin_TakesNothingFromANetworkPath_AndNothingGoneIsAnError() {
		Assert.Equal("on a network location, which has no Recycle Bin", Recycler.BinRefusal(@"\\server\share\Games\Old", 10));
		RecycleResult r = Recycler.RecyclePaths([pc.P("not", "there")]);
		Assert.Empty(r.Recycled);
		Assert.Empty(r.Failed);
	}
}
