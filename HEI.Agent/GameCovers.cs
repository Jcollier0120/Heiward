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

using System.Text.RegularExpressions;

namespace HEI.Agent {
	/// <summary>
	/// Each game's cover: the picture its launcher already keeps on this PC. Nothing is downloaded, copied or resized.
	/// <list type="bullet">
	/// <item>Steam: the person's own portrait (userdata\&lt;account&gt;\config\grid\&lt;app id&gt;p.png or .jpg), else the library
	/// portrait Steam keeps in appcache\librarycache (in the app's folder, a folder deeper, or the old flat file), else the
	/// app's header.jpg, a wide 460×215 banner.</item>
	/// <item>Xbox: the square logo the game's MicrosoftGame.config names, when it's a file in the game's Content folder.</item>
	/// <item>Epic, GOG GALAXY, the EA app, Ubisoft Connect and Battle.net keep no plain picture file per game: their games have none.</item>
	/// </list>
	/// Found with each games check and recorded on the game (<see cref="GameInstall.Cover"/>); the page asks for it by the game's id
	/// (<see cref="CoverFiles"/>), never by a path.
	/// </summary>
	static class GameCovers {
		public const string Portrait = "portrait", Wide = "wide", Square = "square";

		/// <param name="Path">The picture's full path.</param>
		/// <param name="Shape"><see cref="Portrait"/>, <see cref="Wide"/> or <see cref="Square"/>.</param>
		internal sealed record Cover(string Path, string Shape);

		/// <summary>The pictures the page shows, by extension; any other file is never served.</summary>
		public static string? ContentType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch {
			".jpg" or ".jpeg" => "image/jpeg",
			".png" => "image/png",
			_ => null,
		};

		/// <summary><paramref name="games"/>, each with its cover when its launcher keeps one.</summary>
		public static List<GameInstall> Add(List<GameInstall> games, string? steamRoot) {
			IReadOnlyList<string> grids = steamRoot != null && games.Any(g => g.Launcher == GameLaunchers.Steam) ? SteamGridFolders(steamRoot) : [];
			return games.Select(g => (g.Launcher switch {
				GameLaunchers.Steam when steamRoot != null => Steam(steamRoot, g.AppId, grids),
				GameLaunchers.Xbox => Xbox(g.Folder),
				_ => null,
			}) is { } cover ? g with { Cover = cover.Path, CoverShape = cover.Shape } : g).ToList();
		}

		// ------------------------------------------------------------------ Steam

		/// <summary>Each Steam account's custom art folder on this PC: userdata\&lt;account&gt;\config\grid.</summary>
		internal static IReadOnlyList<string> SteamGridFolders(string steamRoot) {
			try {
				return Directory.EnumerateDirectories(System.IO.Path.Combine(steamRoot, "userdata"))
					.Select(account => System.IO.Path.Combine(account, "config", "grid")).Where(Directory.Exists).ToList();
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
		}

		/// <summary>
		/// A Steam game's cover, in Steam's own order: the person's custom portrait (the newest, with more than one account), Steam's
		/// library portrait in the app's folder, then a folder deeper (the newest), then the old flat file, and only then the wide header.
		/// </summary>
		/// <param name="grids">The accounts' custom art folders (<see cref="SteamGridFolders"/>).</param>
		internal static Cover? Steam(string steamRoot, string appId, IReadOnlyList<string> grids) {
			// An app id is digits only: never a piece of a path.
			if (appId.Length == 0 || !appId.All(char.IsAsciiDigit)) return null;
			if (Newest(grids.SelectMany(g => new[] { appId + "p.png", appId + "p.jpg" }.Select(f => System.IO.Path.Combine(g, f)))) is { } custom)
				return new(custom, Portrait);
			string cache = System.IO.Path.Combine(steamRoot, "appcache", "librarycache"), app = System.IO.Path.Combine(cache, appId);
			const string Library = "library_600x900.jpg";
			if (Exists(System.IO.Path.Combine(app, Library)) is { } direct) return new(direct, Portrait);
			if (Newest(SubFolders(app).Select(f => System.IO.Path.Combine(f, Library))) is { } deeper) return new(deeper, Portrait);
			if (Exists(System.IO.Path.Combine(cache, appId + "_" + Library)) is { } flat) return new(flat, Portrait);
			if ((Exists(System.IO.Path.Combine(app, "header.jpg")) ?? Exists(System.IO.Path.Combine(cache, appId + "_header.jpg"))) is { } header)
				return new(header, Wide);
			return null;
		}

		// ------------------------------------------------------------------ Xbox

		/// <summary>The square logos MicrosoftGame.config names, the bigger first.</summary>
		static readonly Regex[] XboxLogos = [.. new[] { "Square480x480Logo", "Square150x150Logo" }
			.Select(name => new Regex("\\b" + name + "\\s*=\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant))];

		/// <summary>
		/// An Xbox game's square logo: the first that MicrosoftGame.config names (the 480×480, else the 150×150) and that is a picture in
		/// the game's Content folder. A name reaching outside it, or a file that isn't there as named, is none.
		/// </summary>
		internal static Cover? Xbox(string gameFolder) {
			string content = System.IO.Path.Combine(gameFolder, "Content");
			string text;
			try { text = File.ReadAllText(System.IO.Path.Combine(content, "MicrosoftGame.config")); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
			string root = GameLibraries.Full(content) + System.IO.Path.DirectorySeparatorChar;
			foreach (Match m in XboxLogos.Select(r => r.Match(text)).Where(m => m.Success)) {
				string named = m.Groups[1].Value.Replace('/', '\\');
				if (named.Contains("..") || System.IO.Path.IsPathRooted(named) || ContentType(named) == null) continue;
				string file = GameLibraries.Full(System.IO.Path.Combine(content, named));
				if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Exists(file) != null) return new(file, Square);
			}
			return null;
		}

		// ------------------------------------------------------------------

		static string? Exists(string file) {
			try { return File.Exists(file) ? file : null; }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
		}

		/// <summary>The most recently written of <paramref name="files"/> that exist; null when none does.</summary>
		static string? Newest(IEnumerable<string> files) {
			string? newest = null;
			DateTime at = DateTime.MinValue;
			foreach (string f in files) {
				try {
					var info = new FileInfo(f);
					if (info.Exists && info.LastWriteTimeUtc > at) (newest, at) = (f, info.LastWriteTimeUtc);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
			}
			return newest;
		}

		static IEnumerable<string> SubFolders(string folder) {
			try { return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder).ToList() : []; }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
		}
	}

	/// <summary>
	/// The covers the last games check recorded, by game id, for the page's /api/games/cover/{id}: read again only when
	/// game-report.json changes, so a page full of covers doesn't read the report once for each. Paths only: no picture is kept.
	/// </summary>
	static class CoverFiles {
		static readonly object gate = new();
		static (DateTime Written, long Length, Dictionary<string, string> Files)? last;

		/// <summary>
		/// The file recorded for game <paramref name="id"/>, opened to send, when there is one, it's a picture the page shows, and it's
		/// still there. Opened so its launcher can still replace or remove it while the page sends it: Heiward never holds it.
		/// </summary>
		public static (FileStream File, string ContentType)? Open(string id) {
			if (Recorded().GetValueOrDefault(id) is not { } file || GameCovers.ContentType(file) is not { } type) return null;
			try {
				return (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 14,
					FileOptions.Asynchronous | FileOptions.SequentialScan), type);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; } // gone, or going, since the check
		}

		static Dictionary<string, string> Recorded() {
			var info = new FileInfo(GameReport.FilePath);
			if (!info.Exists) return [];
			lock (gate) {
				if (last is { } known && known.Written == info.LastWriteTimeUtc && known.Length == info.Length) return known.Files;
				var files = (GameReport.Load()?.Games ?? []).Where(g => g.Cover != null)
					.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First().Cover!, StringComparer.Ordinal);
				last = (info.LastWriteTimeUtc, info.Length, files);
				return files;
			}
		}
	}
}
