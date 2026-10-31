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
/// Games' covers from the pictures the launchers keep on the PC: Steam's, one case per place it keeps them, and an Xbox game's logo.
/// Every folder here is a fake one in a temporary folder: never this PC's Steam.
/// </summary>
public sealed class GameCoversTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "hei-covers-" + Guid.NewGuid().ToString("N"));
	string Steam => Path.Combine(root, "Steam");
	string Cache => Path.Combine(Steam, "appcache", "librarycache");
	string Grid(string account) => Path.Combine(Steam, "userdata", account, "config", "grid");

	public void Dispose() { try { Directory.Delete(root, true); } catch { } }

	string File(string path, DateTime? written = null) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		System.IO.File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF]);
		if (written is { } at) System.IO.File.SetLastWriteTimeUtc(path, at);
		return path;
	}

	GameCovers.Cover? Steam_(string app) => GameCovers.Steam(Steam, app, GameCovers.SteamGridFolders(Steam));

	[Fact]
	public void Steam_YourOwnPortrait_First() {
		File(Path.Combine(Cache, "100", "library_600x900.jpg"));
		File(Path.Combine(Grid("1"), "100p.jpg"), DateTime.UtcNow.AddDays(-2));
		string newer = File(Path.Combine(Grid("2"), "100p.png"), DateTime.UtcNow.AddDays(-1));
		File(Path.Combine(Grid("2"), "100.png")); // Steam's wide custom art: not the portrait
		Assert.Equal(new GameCovers.Cover(newer, GameCovers.Portrait), Steam_("100"));
	}

	[Fact]
	public void Steam_LibraryPortrait_InTheAppsFolder() {
		string direct = File(Path.Combine(Cache, "100", "library_600x900.jpg"));
		File(Path.Combine(Cache, "100", "header.jpg"));
		File(Path.Combine(Cache, "100", "0123abcd", "library_600x900.jpg"), DateTime.UtcNow.AddDays(1));
		Assert.Equal(new GameCovers.Cover(direct, GameCovers.Portrait), Steam_("100"));
	}

	[Fact]
	public void Steam_LibraryPortrait_AFolderDeeper_TheNewest() {
		File(Path.Combine(Cache, "100", "a875c348", "library_600x900.jpg"), DateTime.UtcNow.AddDays(-3));
		string newest = File(Path.Combine(Cache, "100", "8b21381a", "library_600x900.jpg"), DateTime.UtcNow.AddDays(-1));
		File(Path.Combine(Cache, "100", "f86f8903", "library_header.jpg"));
		File(Path.Combine(Cache, "100", "header.jpg"));
		Assert.Equal(new GameCovers.Cover(newest, GameCovers.Portrait), Steam_("100"));
	}

	[Fact]
	public void Steam_LibraryPortrait_TheOldFlatFile() {
		string flat = File(Path.Combine(Cache, "100_library_600x900.jpg"));
		File(Path.Combine(Cache, "100", "header.jpg"));
		Assert.Equal(new GameCovers.Cover(flat, GameCovers.Portrait), Steam_("100"));
	}

	[Fact]
	public void Steam_OnlyTheHeader_IsWide() {
		string header = File(Path.Combine(Cache, "100", "header.jpg"));
		File(Path.Combine(Cache, "100", "library_hero.jpg"));
		Assert.Equal(new GameCovers.Cover(header, GameCovers.Wide), Steam_("100"));
		string oldHeader = File(Path.Combine(Cache, "200_header.jpg"));
		Assert.Equal(new GameCovers.Cover(oldHeader, GameCovers.Wide), Steam_("200"));
	}

	[Fact]
	public void Steam_None() {
		Assert.Null(Steam_("100")); // no librarycache at all
		File(Path.Combine(Cache, "100", "logo.png"));
		File(Path.Combine(Cache, "200", "library_600x900.jpg"));
		Assert.Null(Steam_("100"));
		Assert.Empty(GameCovers.SteamGridFolders(Steam)); // no userdata
	}

	[Theory]
	[InlineData("")]
	[InlineData("..")]
	[InlineData(@"..\200")]
	[InlineData("100a")]
	[InlineData(@"C:\Windows")]
	public void Steam_AnAppIdIsDigitsOnly(string app) {
		File(Path.Combine(Cache, "200", "library_600x900.jpg"));
		File(Path.Combine(Cache, "library_600x900.jpg"));
		Assert.Null(Steam_(app));
	}

	string Xbox(string config) {
		string game = Path.Combine(root, "XboxGames", "Game B");
		Directory.CreateDirectory(Path.Combine(game, "Content"));
		System.IO.File.WriteAllText(Path.Combine(game, "Content", "MicrosoftGame.config"), config);
		return game;
	}

	[Fact]
	public void Xbox_TheSquareLogoItsConfigNames() {
		string game = Xbox("<Game><ShellVisuals DefaultDisplayName=\"Game B\" StoreLogo=\"StoreLogo.png\" Square150x150Logo=\"Assets/Logo150.png\" Square44x44Logo=\"Small.png\" /></Game>");
		Assert.Null(GameCovers.Xbox(game)); // named, but not there
		string logo = File(Path.Combine(game, "Content", "Assets", "Logo150.png"));
		Assert.Equal(new GameCovers.Cover(logo, GameCovers.Square), GameCovers.Xbox(game));
		// The bigger one first, when it's there, wherever the config names it.
		Xbox("<Game><ShellVisuals Square150x150Logo=\"Assets/Logo150.png\" Square480x480Logo=\"Logo480.png\" /></Game>");
		string big = File(Path.Combine(game, "Content", "Logo480.png"));
		Assert.Equal(new GameCovers.Cover(big, GameCovers.Square), GameCovers.Xbox(game));
	}

	[Theory]
	[InlineData(@"..\..\secret.png")]
	[InlineData("../outside.png")]
	[InlineData(@"C:\Windows\Web\logo.png")]
	[InlineData("Logo.exe")]
	public void Xbox_NothingOutsideItsContent_NorAnythingButAPicture(string named) {
		string game = Xbox($"<Game><ShellVisuals Square150x150Logo=\"{named}\" /></Game>");
		File(Path.Combine(root, "secret.png"));
		File(Path.Combine(root, "XboxGames", "outside.png"));
		File(Path.Combine(game, "Content", "Logo.exe"));
		Assert.Null(GameCovers.Xbox(game));
		Assert.Null(GameCovers.Xbox(Path.Combine(root, "XboxGames", "Not There")));
	}

	[Fact]
	public void TheGamesCheck_RecordsEachGamesCover() {
		using var pc = new FakeGamePC();
		// Game A (Steam's app 100) has Steam's portrait; Game B (app 200) only its header; the Xbox Game B its logo.
		string portrait = pc.File(Path.Combine(pc.Steam, "appcache", "librarycache", "100", "library_600x900.jpg"), 10);
		string header = pc.File(Path.Combine(pc.Steam, "appcache", "librarycache", "200", "header.jpg"), 10);
		pc.Text(Path.Combine(pc.Drive, "XboxGames", "Game B", "Content", "MicrosoftGame.config"),
			"<Game configVersion=\"1\"><ShellVisuals DefaultDisplayName=\"Game B™\" Square150x150Logo=\"Logo.png\" /></Game>");
		string logo = pc.File(Path.Combine(pc.Drive, "XboxGames", "Game B", "Content", "Logo.png"), 10);
		var games = GameScanner.Run(pc.Places).Games.ToDictionary(g => g.Name);
		Assert.Equal((portrait, GameCovers.Portrait), (games["Game A"].Cover, games["Game A"].CoverShape));
		Assert.Equal((header, GameCovers.Wide), (games["Game B"].Cover, games["Game B"].CoverShape));
		Assert.Equal((logo, GameCovers.Square), (games["Game B™"].Cover, games["Game B™"].CoverShape));
		// The other launchers keep no picture per game.
		Assert.All(games.Values.Where(g => g.Launcher is not (GameLaunchers.Steam or GameLaunchers.Xbox)), g => Assert.Null(g.Cover));
	}

	[Theory]
	[InlineData(@"C:\x\cover.jpg", "image/jpeg")]
	[InlineData(@"C:\x\cover.JPEG", "image/jpeg")]
	[InlineData(@"C:\x\cover.png", "image/png")]
	[InlineData(@"C:\x\cover.webp", null)]
	[InlineData(@"C:\Windows\win.ini", null)]
	public void OnlyPictures_AreServed(string path, string? type) => Assert.Equal(type, GameCovers.ContentType(path));

	// ---- When a game was last played: whole 30-day months, rounded down, on the page and in the check alike

	[Theory]
	[InlineData(375, 12)] // 12.5 months: still 12
	[InlineData(389, 12)]
	[InlineData(390, 13)]
	[InlineData(91, 3)]
	public void MonthsAgo_RoundsDown(int days, int months) {
		DateTime now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
		Assert.Equal(months, GameScanner.MonthsAgo(now.AddDays(-days), now));
		var game = new GameInstall("g", GameLaunchers.Steam, "100", "Forza", @"D:\Games\Forza", null, 1, now.AddDays(-days), null);
		Assert.Equal($"Last played {months} months ago, in Steam", GameScanner.IdleItems([game], now).Single().Detail);
	}

	// ---- The page

	[Fact]
	public void ThePage_TheOverviewsGrid_OfCovers() {
		string script = ThePage.Asset("app.js"), css = ThePage.Asset("app.css");
		string overview = ThePage.Function(script, "gamesOverview");
		Assert.Contains("el('div', 'game-grid')", overview);
		Assert.Contains("grid.append(gameTile(game))", overview);
		Assert.Contains("'Show all '", overview);
		// A button, so it opens with the keyboard as with a click; the cover's alt text is the game's name.
		string tile = ThePage.Function(script, "gameTile");
		Assert.Contains("el('button', 'game-tile')", tile);
		Assert.Contains("gameArt(game, game.name)", tile);
		Assert.Contains("playedText(game.lastPlayedUtc)", tile);
		string art = ThePage.Function(script, "gameArt");
		foreach (string line in new[] { "img.loading = 'lazy';", "img.decoding = 'async';", "img.alt = alt;", "addEventListener('error', blank", "launcherName(game.launcher)", "'fit'" })
			Assert.Contains(line, art);
		Assert.Contains(".game-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(", css);
		Assert.Contains("aspect-ratio: 2 / 3;", css);
		Assert.Contains(".game-art.fit img { height: auto; max-height: 100%; object-fit: contain; }", css);
	}

	[Fact]
	public void ThePage_TheGamesList_AndAGamesPage_ShowTheCover() {
		string script = ThePage.Asset("app.js");
		Assert.Contains("row(g.name, gameThumb(g),", ThePage.Function(script, "renderGamesNav"));
		Assert.Contains("if (!game.cover) return icon('pad');", ThePage.Function(script, "gameThumb"));
		Assert.Contains("gameArt(game, '', 'hero')", ThePage.Function(script, "gamePage"));
	}

	[Fact]
	public void ThePage_AGamesPage_SaysWhenItWasLastPlayedOnce() {
		string script = ThePage.Asset("app.js");
		string row = ThePage.Function(script, "gameRow");
		Assert.Contains("if (!(i.kind === 'idle' && game && game.lastPlayedUtc))", row);
		Assert.DoesNotContain("playedText", row);
		Assert.Contains("gameSection(c.key, here, undefined, game)", ThePage.Function(script, "gamePage"));
	}

	[Fact]
	public void ThePage_CountsMonthsTheSameWay() {
		string script = ThePage.Asset("app.js");
		string played = ThePage.Function(script, "playedText");
		Assert.Contains("Math.floor(days / 30) + ' months ago'", played);
		Assert.DoesNotContain("Math.round", played);
	}
}

/// <summary>The page's own files, as hei.exe carries them, for tests that read its script and styles.</summary>
static class ThePage {
	public static string Asset(string name) {
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/" + name)!;
		return new StreamReader(s).ReadToEnd();
	}

	/// <summary>A top-level function of <paramref name="script"/>, up to the next one.</summary>
	public static string Function(string script, string name) {
		int start = script.IndexOf("function " + name + "(", StringComparison.Ordinal);
		Assert.True(start >= 0, name + " isn't in the script");
		int end = script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
		return script[start..(end < 0 ? script.Length : end)];
	}
}
