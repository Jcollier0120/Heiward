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

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HEI.Agent {
	/// <summary>A game a launcher has installed: where, how big, and when it was last played when the launcher records it.</summary>
	/// <param name="Launcher">One of <see cref="GameLaunchers"/>' ids: steam, epic, gog, ea, ubisoft, battlenet, xbox.</param>
	/// <param name="AppId">The launcher's own id for it (Steam's app id, Epic's app name, a registry key).</param>
	/// <param name="Library">The library folder it's in (a Steam library, an Epic Games folder), when the launcher has one.</param>
	/// <param name="LastPlayedUtc">When it was last played; null when the launcher doesn't record it (only Steam does).</param>
	/// <param name="UpdatedUtc">When the launcher last installed or updated it, when it records it.</param>
	sealed record GameInstall(string Id, string Launcher, string AppId, string Name, string Folder, string? Library, long Bytes,
		DateTime? LastPlayedUtc, DateTime? UpdatedUtc) {
		/// <summary>How to move it to another drive or free its space, with its launcher (for the page's game page).</summary>
		public string Advice => GameLaunchers.MoveAdvice(Launcher);
	}

	/// <summary>
	/// Something games or their launchers left on a drive, or something worth knowing about an installed game. <see cref="Blocked"/>
	/// says why it can't go now (a launcher or game running, saves inside); <see cref="Suggested"/> items start ticked on the page.
	/// <see cref="Info"/> items (a game installed twice, one not played in months) are never removed by Heiward: <see cref="Removing"/>
	/// says what to do in the launcher instead.
	/// </summary>
	/// <param name="Kind">orphan, workshop, download, paused, cache, shader, gpu-shader, dump, crash, wer, twice or idle.</param>
	/// <param name="Detail">What it is, in a plain line.</param>
	/// <param name="Removing">What removing it means, in a plain line (for an <see cref="Info"/> item, what to do instead).</param>
	/// <param name="Game">The installed game it belongs to (<see cref="GameInstall.Id"/>); null for a game no longer installed, or none.</param>
	/// <param name="AppId">The launcher's id of the game it belongs to, installed or not.</param>
	sealed record GameItem(string Id, string Kind, string Name, string Location, List<string> Paths, long Bytes,
		DateTime? LastUsedUtc, bool Suggested, string? Blocked, string Detail, string Removing,
		string? Launcher = null, string? Game = null, string? AppId = null, bool Info = false);

	sealed record GameCategory(string Key, string Title, string Explain, List<GameItem> Items);

	/// <summary>The last games check (game-report.json).</summary>
	sealed class GameReport {
		public DateTime ScannedAtUtc { get; set; }
		public double DurationSec { get; set; }
		/// <summary>The installed games every launcher lists, biggest first.</summary>
		public List<GameInstall> Games { get; set; } = new();
		/// <summary>The launchers found on this PC (<see cref="GameLaunchers"/>' ids).</summary>
		public List<string> Launchers { get; set; } = new();
		public List<GameCategory> Categories { get; set; } = new();
		/// <summary>The <see cref="AppBuild"/> that made it: another build's report is set aside, as the duplicates report is.</summary>
		public string? Build { get; set; }

		public static string FilePath => Path.Combine(AgentPaths.Home, "game-report.json");
		static readonly object gate = new();

		public IEnumerable<GameItem> Items => Categories.SelectMany(c => c.Items);

		public static GameReport? Load() {
			try {
				return File.Exists(FilePath) && JsonSerializer.Deserialize<GameReport>(File.ReadAllText(FilePath), AgentConfig.Json) is { } report
					&& report.Build == AppBuild.Current ? report : null;
			}
			catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
				return null;
			}
		}

		public void Save() => AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(this, AgentConfig.Json));

		/// <summary>Replaces one item (or drops it, when <paramref name="replacement"/> is null) in the saved report.</summary>
		public static void Update(string id, GameItem? replacement) {
			lock (gate) {
				GameReport? report = Load();
				if (report == null) return;
				foreach (GameCategory c in report.Categories) {
					int i = c.Items.FindIndex(x => x.Id == id);
					if (i < 0) continue;
					if (replacement == null) c.Items.RemoveAt(i);
					else c.Items[i] = replacement;
				}
				report.Save();
			}
		}
	}

	/// <summary>A program running now: its process name, and its exe's full path when Windows tells.</summary>
	sealed record RunningProgram(string Name, string? Path);

	/// <summary>The registry, as the game finders read it: tests give their own.</summary>
	interface IGameRegistry {
		/// <summary>A value, as text: <paramref name="key"/> like @"HKLM\SOFTWARE\WOW6432Node\Valve\Steam". Null when there's none.</summary>
		string? Value(string key, string name);
		/// <summary>The names of a key's subkeys; none when it isn't there.</summary>
		IReadOnlyList<string> SubKeys(string key);
	}

	/// <summary>Windows' registry, the 64-bit view (paths name WOW6432Node themselves).</summary>
	sealed class WindowsRegistry : IGameRegistry {
		static RegistryKey? Open(string key) {
			int slash = key.IndexOf('\\');
			if (slash < 0) return null;
			RegistryHive? hive = key[..slash].ToUpperInvariant() switch { "HKLM" => RegistryHive.LocalMachine, "HKCU" => RegistryHive.CurrentUser, _ => null };
			if (hive == null) return null;
			try { return RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64).OpenSubKey(key[(slash + 1)..]); }
			catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
		}

		public string? Value(string key, string name) {
			using RegistryKey? k = Open(key);
			try { return k?.GetValue(name)?.ToString(); }
			catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
		}

		public IReadOnlyList<string> SubKeys(string key) {
			using RegistryKey? k = Open(key);
			try { return k?.GetSubKeyNames() ?? []; }
			catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
		}
	}

	/// <summary>
	/// Where the game finders look: this user's and this PC's folders, the drives, the registry, and what's running. Tests make
	/// their own under a temporary folder; <see cref="Current"/> is this PC's.
	/// </summary>
	sealed class GamePlaces {
		public required string LocalAppData { get; init; }
		/// <summary>%USERPROFILE%\AppData\LocalLow.</summary>
		public required string LocalLow { get; init; }
		public required string ProgramData { get; init; }
		public required string ProgramFiles { get; init; }
		public required string ProgramFilesX86 { get; init; }
		public required string Temp { get; init; }
		public required string Profile { get; init; }
		public required string Documents { get; init; }
		/// <summary>The drives to look for Xbox games on: this PC's fixed drives, minus those scanned only when asked.</summary>
		public required IReadOnlyList<string> Drives { get; init; }
		public required IGameRegistry Registry { get; init; }
		public required Func<IReadOnlyList<RunningProgram>> Running { get; init; }

		public static GamePlaces Current(AgentConfig cfg) {
			string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			return new GamePlaces {
				LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				LocalLow = Path.Combine(profile, "AppData", "LocalLow"),
				ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
				ProgramFiles = pf,
				ProgramFilesX86 = string.IsNullOrEmpty(pf86) ? pf : pf86,
				Temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
				Profile = profile,
				Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
				Drives = FixedDrives().Where(d => !cfg.IsOnRequest(d)).ToList(),
				Registry = new WindowsRegistry(),
				Running = RunningPrograms.List,
			};
		}

		static IEnumerable<string> FixedDrives() {
			foreach (DriveInfo d in DriveInfo.GetDrives()) {
				bool ok;
				try { ok = d.DriveType == DriveType.Fixed && d.IsReady; }
				catch (IOException) { ok = false; }
				if (ok) yield return d.RootDirectory.FullName;
			}
		}
	}

	/// <summary>The programs running now, with their exes' paths (a process Windows won't open for this user has none).</summary>
	static class RunningPrograms {
		public static IReadOnlyList<RunningProgram> List() {
			var list = new List<RunningProgram>();
			foreach (Process p in Process.GetProcesses()) {
				using (p) {
					string name;
					try { name = p.ProcessName; }
					catch (InvalidOperationException) { continue; }
					list.Add(new RunningProgram(name, ImagePath(p.Id)));
				}
			}
			return list;
		}

		static string? ImagePath(int pid) {
			IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
			if (h == IntPtr.Zero) return null;
			try {
				var sb = new StringBuilder(1024);
				int size = sb.Capacity;
				return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
			}
			finally { CloseHandle(h); }
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern IntPtr OpenProcess(int access, bool inherit, int pid);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
		static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

		[DllImport("kernel32.dll")]
		static extern bool CloseHandle(IntPtr handle);
	}

	/// <summary>The launchers Heiward reads, by id: their names, the programs that mean one is running, and how to move a game.</summary>
	static class GameLaunchers {
		public const string Steam = "steam", Epic = "epic", Gog = "gog", Ea = "ea", Ubisoft = "ubisoft", BattleNet = "battlenet", Xbox = "xbox";

		public static string Name(string id) => id switch {
			Steam => "Steam",
			Epic => "the Epic Games Launcher",
			Gog => "GOG GALAXY",
			Ea => "the EA app",
			Ubisoft => "Ubisoft Connect",
			BattleNet => "Battle.net",
			Xbox => "the Xbox app",
			_ => id,
		};

		/// <summary>The process names that mean the launcher is running (its own caches are left alone then).</summary>
		public static string[] Processes(string id) => id switch {
			Steam => ["steam", "steamwebhelper", "steamservice"],
			Epic => ["EpicGamesLauncher", "EpicWebHelper"],
			Gog => ["GalaxyClient", "GalaxyClientService"],
			Ea => ["EADesktop", "EABackgroundService"],
			Ubisoft => ["UbisoftConnect", "upc", "UplayWebCore"],
			BattleNet => ["Battle.net"],
			Xbox => ["XboxPcApp"],
			_ => [],
		};

		/// <summary>How to free a game's space, or move it to another drive, with the launcher's own tools: Heiward doesn't move or uninstall games.</summary>
		public static string MoveAdvice(string id) => id switch {
			Steam => "To move it to another drive: in Steam, right-click the game › Properties › Installed Files › Move install folder. Or uninstall it there; your Steam Cloud saves stay.",
			Xbox => "To move it to another drive: in the Xbox app, right-click the game › Manage › Files › Move. Or uninstall it there.",
			_ => $"To free its space, uninstall it in {Name(id)}, or move it with that launcher's own options if it has them.",
		};

		/// <summary>The launcher is running now.</summary>
		public static string? RunningText(string id, IReadOnlyList<RunningProgram> running) {
			string[] names = Processes(id);
			return running.Any(p => names.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
				? $"{char.ToUpperInvariant(Name(id)[0])}{Name(id)[1..]} is running: close it first" : null;
		}
	}

	/// <summary>
	/// The installed games every launcher lists, read from the launchers' own records, never guessed from folder names: Steam's
	/// library list and app manifests, Epic's manifests, and for GOG GALAXY, the EA app, Ubisoft Connect and Battle.net what they
	/// write to the registry; Xbox games from the XboxGames folders. Nothing is measured here (<see cref="GameScanner"/> does).
	/// </summary>
	static class GameLibraries {
		/// <summary>Everything the launchers say: the games, the folders they claim, and Steam's libraries.</summary>
		internal sealed class Found {
			public List<GameInstall> Games { get; } = new();
			public List<string> Launchers { get; } = new();
			/// <summary>Every folder a launcher's record names, even a game that isn't fully there (a download not finished): never a leftover.</summary>
			public HashSet<string> Claimed { get; } = new(StringComparer.OrdinalIgnoreCase);
			public string? SteamRoot { get; set; }
			/// <summary>Steam's libraries, each with the app ids its manifests name.</summary>
			public List<(string Path, HashSet<string> Apps)> SteamLibraries { get; } = new();
			/// <summary>The Steam app ids any library has a manifest for: installed, or downloading.</summary>
			public HashSet<string> SteamApps { get; } = new(StringComparer.OrdinalIgnoreCase);
			/// <summary>The folders Epic installs games in (the parents of its games).</summary>
			public HashSet<string> EpicLibraries { get; } = new(StringComparer.OrdinalIgnoreCase);
		}

		public static Found Find(GamePlaces env) {
			var found = new Found();
			Steam(env, found);
			Epic(env, found);
			Gog(env, found);
			Ea(env, found);
			Ubisoft(env, found);
			BattleNet(env, found);
			Xbox(env, found);
			return found;
		}

		internal static string Full(string path) {
			try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
		}

		static void Add(Found found, string launcher, string appId, string name, string folder, string? library, long bytes, DateTime? played, DateTime? updated) {
			folder = Full(folder);
			found.Claimed.Add(folder);
			if (!Directory.Exists(folder)) return;
			if (!found.Launchers.Contains(launcher)) found.Launchers.Add(launcher);
			found.Games.Add(new GameInstall(IdOf(launcher, appId + "|" + folder), launcher, appId, name.Trim(), folder, library, bytes, played, updated));
		}

		internal static string IdOf(string launcher, string key) =>
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("game|" + launcher + "|" + key.ToLowerInvariant())))[..16].ToLowerInvariant();

		// ------------------------------------------------------------------ Steam

		/// <summary>Steam's own folder: the registry's, else the usual place.</summary>
		internal static string? SteamRoot(GamePlaces env) {
			foreach (string? p in new[] {
				env.Registry.Value(@"HKCU\Software\Valve\Steam", "SteamPath"),
				env.Registry.Value(@"HKLM\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
				env.Registry.Value(@"HKLM\SOFTWARE\Valve\Steam", "InstallPath"),
				Path.Combine(env.ProgramFilesX86, "Steam"),
			}) {
				if (string.IsNullOrWhiteSpace(p)) continue;
				string path = Full(p.Replace('/', '\\'));
				if (Directory.Exists(Path.Combine(path, "steamapps"))) return path;
			}
			return null;
		}

		/// <summary>Steam's library folders from libraryfolders.vdf, Steam's own folder first. Both of its formats.</summary>
		internal static List<string> SteamLibraryFolders(string root) {
			var libs = new List<string> { root };
			foreach (string file in new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "config", "libraryfolders.vdf") }) {
				if (!File.Exists(file)) continue;
				KeyValues? kv = KeyValues.Load(file);
				if (kv?.Child("libraryfolders") is not { } folders) continue;
				foreach (var (key, value) in folders.Entries) {
					if (!key.All(char.IsDigit)) continue;
					string? path = value is KeyValues entry ? entry.Text("path") : value as string; // new format, then the old
					if (!string.IsNullOrWhiteSpace(path)) libs.Add(Full(path));
				}
				break;
			}
			return libs.Distinct(StringComparer.OrdinalIgnoreCase).Where(l => Directory.Exists(Path.Combine(l, "steamapps"))).ToList();
		}

		static void Steam(GamePlaces env, Found found) {
			string? root = SteamRoot(env);
			if (root == null) return;
			found.SteamRoot = root;
			if (!found.Launchers.Contains(GameLaunchers.Steam)) found.Launchers.Add(GameLaunchers.Steam);
			foreach (string lib in SteamLibraryFolders(root)) {
				var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				string steamapps = Path.Combine(lib, "steamapps");
				IEnumerable<string> manifests;
				try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").ToList(); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { manifests = []; }
				foreach (string m in manifests) {
					if (KeyValues.Load(m)?.Child("AppState") is not { } app) continue;
					string? id = app.Text("appid"), dir = app.Text("installdir");
					if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(dir)) continue;
					apps.Add(id);
					found.SteamApps.Add(id);
					string folder = Path.Combine(steamapps, "common", dir);
					// Steamworks' shared redistributables claim their folder, but aren't a game.
					if (id == "228980") { found.Claimed.Add(Full(folder)); continue; }
					Add(found, GameLaunchers.Steam, id, app.Text("name") ?? dir, folder, lib, ParseLong(app.Text("SizeOnDisk")),
						UnixTime(app.Text("LastPlayed")), UnixTime(app.Text("LastUpdated")));
				}
				found.SteamLibraries.Add((lib, apps));
			}
		}

		// ------------------------------------------------------------------ Epic

		internal static string EpicManifests(GamePlaces env) => Path.Combine(env.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");

		static void Epic(GamePlaces env, Found found) {
			string dir = EpicManifests(env);
			if (!Directory.Exists(dir)) return;
			if (!found.Launchers.Contains(GameLaunchers.Epic)) found.Launchers.Add(GameLaunchers.Epic);
			IEnumerable<string> items;
			try { items = Directory.EnumerateFiles(dir, "*.item").ToList(); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
			foreach (string file in items) {
				try {
					using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
					JsonElement r = doc.RootElement;
					string? location = Str(r, "InstallLocation"), app = Str(r, "AppName"), main = Str(r, "MainGameAppName");
					if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(app)) continue;
					found.Claimed.Add(Full(location));
					if (Path.GetDirectoryName(Full(location)) is { } parent) found.EpicLibraries.Add(parent);
					// Add-ons share their game's folder: the game is listed once.
					if (!string.IsNullOrWhiteSpace(main) && !main.Equals(app, StringComparison.OrdinalIgnoreCase)) continue;
					long size = r.TryGetProperty("InstallSize", out JsonElement s) && s.TryGetInt64(out long n) ? n : 0;
					Add(found, GameLaunchers.Epic, app, Str(r, "DisplayName") ?? app, location, Path.GetDirectoryName(Full(location)), size, null, null);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
			}
		}

		static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

		// ------------------------------------------------------------------ GOG GALAXY, the EA app, Ubisoft Connect, Battle.net

		internal const string GogKey = @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games";
		internal const string EaKey = @"HKLM\SOFTWARE\WOW6432Node\Electronic Arts";
		internal const string UbisoftKey = @"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";
		internal const string UninstallKey = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

		static void Gog(GamePlaces env, Found found) {
			foreach (string id in env.Registry.SubKeys(GogKey)) {
				string key = GogKey + "\\" + id;
				string? path = env.Registry.Value(key, "path") ?? env.Registry.Value(key, "PATH");
				if (string.IsNullOrWhiteSpace(path)) continue;
				Add(found, GameLaunchers.Gog, id, env.Registry.Value(key, "gameName") ?? Path.GetFileName(Full(path)), path, null, 0, null, null);
			}
		}

		/// <summary>The EA app's games: each with an "Install Dir", and the __Installer folder the EA app puts in every game it installs.</summary>
		static void Ea(GamePlaces env, Found found) {
			foreach (string sub in env.Registry.SubKeys(EaKey)) {
				string key = EaKey + "\\" + sub;
				string? path = env.Registry.Value(key, "Install Dir");
				if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(Path.Combine(path, "__Installer"))) continue;
				Add(found, GameLaunchers.Ea, sub, env.Registry.Value(key, "DisplayName") ?? sub, path, null, 0, null, null);
			}
		}

		static void Ubisoft(GamePlaces env, Found found) {
			foreach (string id in env.Registry.SubKeys(UbisoftKey)) {
				string? path = env.Registry.Value(UbisoftKey + "\\" + id, "InstallDir");
				if (string.IsNullOrWhiteSpace(path)) continue;
				path = path.Replace('/', '\\');
				Add(found, GameLaunchers.Ubisoft, id, Path.GetFileName(Full(path)), path, null, 0, null, null);
			}
		}

		/// <summary>Blizzard's games, as their uninstall entries name them (Battle.net's own database isn't readable).</summary>
		static void BattleNet(GamePlaces env, Found found) {
			foreach (string sub in env.Registry.SubKeys(UninstallKey)) {
				string key = UninstallKey + "\\" + sub;
				if (env.Registry.Value(key, "Publisher") is not { } publisher || !publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)) continue;
				string? name = env.Registry.Value(key, "DisplayName"), path = env.Registry.Value(key, "InstallLocation");
				if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || name.Trim().Equals("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
				Add(found, GameLaunchers.BattleNet, sub, name, path, null, 0, null, null);
			}
		}

		// ------------------------------------------------------------------ Xbox and the Microsoft Store

		static readonly Regex DisplayName = new("DefaultDisplayName\\s*=\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);

		/// <summary>The XboxGames folder on each drive: a folder per game, its Content holding MicrosoftGame.config.</summary>
		static void Xbox(GamePlaces env, Found found) {
			foreach (string drive in env.Drives) {
				string root = Path.Combine(drive, "XboxGames");
				if (!Directory.Exists(root)) continue;
				IEnumerable<string> folders;
				try { folders = Directory.EnumerateDirectories(root).ToList(); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
				foreach (string folder in folders) {
					string config = Path.Combine(folder, "Content", "MicrosoftGame.config");
					if (!File.Exists(config)) continue;
					string name = Path.GetFileName(folder);
					try {
						if (DisplayName.Match(File.ReadAllText(config)) is { Success: true } m && !m.Groups[1].Value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
							name = m.Groups[1].Value;
					}
					catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
					Add(found, GameLaunchers.Xbox, Path.GetFileName(folder), name, folder, root, 0, null, null);
				}
			}
		}

		static long ParseLong(string? s) => long.TryParse(s, out long n) && n > 0 ? n : 0;

		static DateTime? UnixTime(string? s) => long.TryParse(s, out long t) && t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : null;
	}

	/// <summary>
	/// Valve's KeyValues text, as libraryfolders.vdf and appmanifest_*.acf are written: "key" "value" pairs and "key" { ... }
	/// blocks, with \\ and \" escaped. Keys are matched ignoring case, the first of a repeated key kept.
	/// </summary>
	sealed class KeyValues {
		public List<(string Key, object Value)> Entries { get; } = new();

		public object? this[string key] => Entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
		public string? Text(string key) => this[key] as string;
		public KeyValues? Child(string key) => this[key] as KeyValues;

		public static KeyValues? Load(string file) {
			try { return Parse(File.ReadAllText(file)); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
		}

		/// <summary>The text as a tree; what can't be read (cut short, unbalanced) ends it where it is.</summary>
		public static KeyValues Parse(string text) {
			var root = new KeyValues();
			var stack = new Stack<KeyValues>();
			stack.Push(root);
			string? key = null;
			int i = 0;
			while (i < text.Length) {
				char c = text[i];
				if (char.IsWhiteSpace(c)) { i++; continue; }
				if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') {
					while (i < text.Length && text[i] != '\n') i++;
					continue;
				}
				if (c == '{') {
					var child = new KeyValues();
					stack.Peek().Entries.Add((key ?? "", child));
					stack.Push(child);
					key = null;
					i++;
					continue;
				}
				if (c == '}') {
					if (stack.Count > 1) stack.Pop();
					key = null;
					i++;
					continue;
				}
				string token;
				if (c == '"') {
					var sb = new StringBuilder();
					i++;
					while (i < text.Length && text[i] != '"') {
						if (text[i] == '\\' && i + 1 < text.Length) {
							char n = text[i + 1];
							sb.Append(n switch { 'n' => '\n', 't' => '\t', _ => n });
							i += 2;
						}
						else sb.Append(text[i++]);
					}
					i++; // the closing quote
					token = sb.ToString();
				}
				else {
					int start = i;
					while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
					token = text[start..i];
				}
				if (key == null) key = token;
				else {
					stack.Peek().Entries.Add((key, token));
					key = null;
				}
			}
			return root;
		}
	}

	/// <summary>
	/// What Heiward never removes, whatever a list says: an installed game's own files (its folder, or a folder holding one), and
	/// anyone's saved games (the places games keep them, and any folder with a save in it).
	/// </summary>
	static class GameSafety {
		static readonly HashSet<string> SaveFolders = new(StringComparer.OrdinalIgnoreCase) {
			"save", "saves", "savegame", "savegames", "saved games", "savedata", "save data", "savefiles", "save files", "userdata",
		};

		static readonly HashSet<string> SaveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".sav", ".save", ".savegame", ".sl2", ".ess", ".fos" };

		/// <summary>A file or folder name that looks like a saved game.</summary>
		internal static bool LooksLikeSave(string name, bool folder) =>
			folder ? SaveFolders.Contains(name) : SaveExtensions.Contains(Path.GetExtension(name)) || name.StartsWith("savegame", StringComparison.OrdinalIgnoreCase);

		static bool Inside(string path, string folder) =>
			path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

		/// <summary>Why <paramref name="path"/> must stay, or null. <paramref name="lookInside"/>: also refuse a folder with a save anywhere in it.</summary>
		public static string? Refuse(string path, IEnumerable<GameInstall> installs, GamePlaces env, string? steamRoot, bool lookInside = true) {
			string full = GameLibraries.Full(path);
			if (new DirectoryInfo(full).Parent == null) return "It's a drive";
			foreach (GameInstall g in installs) {
				if (Inside(full, g.Folder)) return $"It's part of {g.Name}, which is installed";
				if (Inside(g.Folder, full)) return $"It holds {g.Name}, which is installed";
			}
			var savePlaces = new List<string> {
				Path.Combine(env.Profile, "Saved Games"), Path.Combine(env.Documents, "My Games"),
			};
			if (steamRoot != null) savePlaces.Add(Path.Combine(steamRoot, "userdata"));
			foreach (string place in savePlaces)
				if (Inside(full, GameLibraries.Full(place)) || Inside(GameLibraries.Full(place), full)) return "Saved games are never touched";
			if (full.Split('\\').Skip(1).Any(part => LooksLikeSave(part, folder: true))) return "Saved games are never touched";
			if (lookInside && Directory.Exists(full) && HoldsSave(full)) return "It may hold saved games: look inside it yourself";
			return null;
		}

		/// <summary>A save anywhere below the folder, links not followed.</summary>
		internal static bool HoldsSave(string folder) {
			try {
				var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
				foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", options))
					if (LooksLikeSave(Path.GetFileName(entry), Directory.Exists(entry))) return true;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; } // can't tell: keep it
			return false;
		}
	}

	/// <summary>
	/// Game mode's daily check: the installed games, and what games and launchers leave behind on the drives. Each item is
	/// recognised by a launcher's own record or a documented cache folder, never by a name alone.
	/// </summary>
	static class GameScanner {
		public const string Leftovers = "leftovers", Caches = "caches", Shaders = "shaders", Dumps = "dumps", Twice = "twice", Idle = "idle";

		/// <summary>A game not played for this many days is listed as not played in months.</summary>
		public const int IdleDays = 90;

		const long MinBytes = 1 << 20; // under 1 MB: not worth a row

		public static GameReport Run(GamePlaces env, CancellationToken ct = default) {
			var timer = Stopwatch.StartNew();
			DateTime now = DateTime.UtcNow;
			GameLibraries.Found found = GameLibraries.Find(env);
			IReadOnlyList<RunningProgram> running = env.Running();
			// Sizes the launcher doesn't record are measured.
			var games = found.Games.Select(g => g.Bytes > 0 || ct.IsCancellationRequested ? g : g with { Bytes = DevScanner.Measure(g.Folder).Bytes })
				.OrderByDescending(g => g.Bytes).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
			var report = new GameReport { ScannedAtUtc = now, Build = AppBuild.Current, Games = games, Launchers = found.Launchers };

			report.Categories.Add(new GameCategory(Leftovers, "Leftovers of uninstalled games",
				"Folders a game left in a launcher's library after it was uninstalled, and workshop and mod downloads of games no longer installed. " +
				"Removing them changes nothing you play; reinstalling such a game downloads it again. Folders with saved games in them are never touched.",
				LeftoverItems(env, found, games, running, ct).OrderByDescending(i => i.Bytes).ToList()));
			report.Categories.Add(new GameCategory(Caches, "Launchers' download caches",
				"What launchers keep while they download and update games, and their web pages' caches. They fill again as needed. Left alone while their launcher runs.",
				CacheItems(env, found, running).OrderByDescending(i => i.Bytes).ToList()));
			report.Categories.Add(new GameCategory(Shaders, "Shader caches",
				"What graphics drivers and Steam build so games' graphics load quickly. Those of uninstalled games are safe to remove. " +
				"Those of installed games rebuild by themselves, but the first launch afterwards may stutter for a while, so they're never ticked for you.",
				ShaderItems(env, found, games, running).OrderByDescending(i => i.Bytes).ToList()));
			report.Categories.Add(new GameCategory(Dumps, "Crash dumps and reports",
				"What games and Windows wrote when a game crashed, for its makers. Nothing uses them once the crash is reported.",
				DumpItems(env, found, games, running).OrderByDescending(i => i.Bytes).ToList()));
			report.Categories.Add(new GameCategory(Twice, "The same game installed twice",
				"A game in two launchers, or in two libraries. Uninstall the copy you don't play from its launcher: Heiward doesn't uninstall games.",
				TwiceItems(games).OrderByDescending(i => i.Bytes).ToList()));
			report.Categories.Add(new GameCategory(Idle, "Games not played in months",
				$"Installed games not played for {IdleDays / 30} months or more, biggest first. Only Steam records when a game was last played. " +
				"Heiward doesn't move or uninstall games: each says how to with its launcher.",
				IdleItems(games, now).OrderByDescending(i => i.Bytes).ToList()));

			report.Categories.RemoveAll(c => c.Items.Count == 0);
			report.DurationSec = Math.Round(timer.Elapsed.TotalSeconds, 1);
			return report;
		}

		internal static string IdOf(string kind, string key) =>
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("gameitem|" + kind + "|" + key.ToLowerInvariant())))[..16].ToLowerInvariant();

		static List<string> Folders(string path) {
			try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Select(GameLibraries.Full).ToList() : []; }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
		}

		/// <summary>A program runs from inside the folder: the game is running.</summary>
		internal static bool RunsFrom(string folder, IReadOnlyList<RunningProgram> running) =>
			running.Any(p => p.Path != null && p.Path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

		/// <summary>One of the installed games is running: shared things (the graphics drivers' shader caches, crash folders) wait.</summary>
		internal static string? AnyGameRunning(IEnumerable<GameInstall> games, IReadOnlyList<RunningProgram> running) =>
			games.FirstOrDefault(g => RunsFrom(g.Folder, running)) is { } g ? $"{g.Name} is running: close it first" : null;

		// ------------------------------------------------------------------ leftovers

		internal static IEnumerable<GameItem> LeftoverItems(GamePlaces env, GameLibraries.Found found, List<GameInstall> games,
			IReadOnlyList<RunningProgram> running, CancellationToken ct = default) {
			// Steam: folders in a library's common folder that no manifest names.
			foreach (var (lib, apps) in found.SteamLibraries) {
				foreach (string folder in Folders(Path.Combine(lib, "steamapps", "common"))) {
					if (ct.IsCancellationRequested) yield break;
					if (found.Claimed.Contains(folder)) continue;
					var (bytes, newest) = DevScanner.Measure(folder);
					if (bytes < MinBytes) continue;
					string? blocked = GameSafety.Refuse(folder, games, env, found.SteamRoot);
					// A library Steam lists no game in may be one it lost track of (Windows reinstalled): adding it in Steam finds them again.
					bool lost = apps.Count == 0;
					yield return new GameItem(IdOf("orphan", folder), "orphan", Path.GetFileName(folder), folder, [folder], bytes, Stamp(newest),
						blocked == null && !lost, blocked,
						lost ? "In a Steam library where Steam lists no game: it may have lost track of the library" : "Left in a Steam library by a game no longer installed",
						lost ? "If Steam has lost track of this library, add it in Steam to find its games again. Otherwise, removing it means downloading the game again if you reinstall it."
							: "Reinstalling the game downloads it again.", GameLaunchers.Steam);
				}
				// Workshop and mod downloads of games no longer installed.
				foreach (string folder in Folders(Path.Combine(lib, "steamapps", "workshop", "content"))) {
					if (ct.IsCancellationRequested) yield break;
					string app = Path.GetFileName(folder);
					if (found.SteamApps.Contains(app)) continue;
					var (bytes, newest) = DevScanner.Measure(folder);
					if (bytes < MinBytes) continue;
					var paths = new List<string> { folder };
					string record = Path.Combine(lib, "steamapps", "workshop", $"appworkshop_{app}.acf");
					if (File.Exists(record)) paths.Add(record);
					string? blocked = GameSafety.Refuse(folder, games, env, found.SteamRoot);
					yield return new GameItem(IdOf("workshop", folder), "workshop", $"Workshop items of Steam game {app}", folder, paths, bytes, Stamp(newest),
						blocked == null, blocked, "Workshop and mod downloads of a game no longer installed",
						"Steam downloads them again if you reinstall the game and stay subscribed.", GameLaunchers.Steam, AppId: app);
				}
			}
			// Epic: folders in its games' folder with Epic's own record inside, that no manifest names.
			foreach (string lib in found.EpicLibraries) {
				foreach (string folder in Folders(lib)) {
					if (ct.IsCancellationRequested) yield break;
					if (found.Claimed.Contains(folder) || !Directory.Exists(Path.Combine(folder, ".egstore"))) continue;
					var (bytes, newest) = DevScanner.Measure(folder);
					if (bytes < MinBytes) continue;
					string? blocked = GameSafety.Refuse(folder, games, env, found.SteamRoot);
					yield return new GameItem(IdOf("orphan", folder), "orphan", Path.GetFileName(folder), folder, [folder], bytes, Stamp(newest),
						blocked == null, blocked, "Left in the Epic Games folder by a game no longer installed",
						"Reinstalling the game downloads it again.", GameLaunchers.Epic);
				}
			}
		}

		// ------------------------------------------------------------------ download caches

		/// <summary>The launchers' cache folders Heiward knows: (launcher, name, folder, ticked, what it is, what removing it means).</summary>
		internal static IEnumerable<(string Launcher, string Name, string Path, bool Suggested, string Detail, string Removing)> KnownCaches(GamePlaces env, GameLibraries.Found found) {
			if (found.SteamRoot is { } steam) {
				yield return (GameLaunchers.Steam, "Steam's download cache", Path.Combine(steam, "depotcache"), true,
					"Steam's notes of the files it downloaded", "Steam fetches them again when it next updates a game.");
				yield return (GameLaunchers.Steam, "Steam's web cache", Path.Combine(steam, "appcache", "httpcache"), true,
					"Pictures and pages Steam's store and library downloaded", "Steam downloads them again as you browse.");
				foreach (var (lib, _) in found.SteamLibraries)
					yield return (GameLaunchers.Steam, "Steam's temporary files", Path.Combine(lib, "steamapps", "temp"), true,
						"What Steam left from installing and updating", "Nothing: Steam makes new ones as it needs them.");
			}
			yield return (GameLaunchers.Steam, "Steam's browser cache", Path.Combine(env.LocalAppData, "Steam", "htmlcache"), true,
				"Steam's built-in browser's cache", "Steam's pages load a little slower the first time.");
			foreach (string pf in new[] { env.ProgramFilesX86, env.ProgramFiles }.Distinct(StringComparer.OrdinalIgnoreCase))
				yield return (GameLaunchers.Epic, "Unreal Engine marketplace downloads", Path.Combine(pf, "Epic Games", "Launcher", "VaultCache"), false,
					"Assets the Epic Games Launcher downloaded from the Unreal Engine marketplace", "They're downloaded again when you add them to a project.");
			string epicSaved = Path.Combine(env.LocalAppData, "EpicGamesLauncher", "Saved");
			foreach (string web in Folders(epicSaved).Where(f => Path.GetFileName(f).StartsWith("webcache", StringComparison.OrdinalIgnoreCase)))
				yield return (GameLaunchers.Epic, "Epic's web cache", web, true, "Pages and pictures the Epic Games Launcher downloaded", "The launcher downloads them again as you browse.");
			yield return (GameLaunchers.Gog, "GOG GALAXY's web cache", Path.Combine(env.ProgramData, "GOG.com", "Galaxy", "webcache"), true,
				"Pages and pictures GOG GALAXY downloaded", "GOG GALAXY downloads them again as you browse.");
			yield return (GameLaunchers.BattleNet, "Battle.net's cache", Path.Combine(env.ProgramData, "Blizzard Entertainment", "Battle.net", "Cache"), true,
				"What Battle.net keeps between updates", "Battle.net fills it again; its next start may take a little longer.");
			yield return (GameLaunchers.BattleNet, "Battle.net's web cache", Path.Combine(env.LocalAppData, "Battle.net", "Cache"), true,
				"Pages and pictures Battle.net downloaded", "Battle.net downloads them again as you browse.");
		}

		internal static IEnumerable<GameItem> CacheItems(GamePlaces env, GameLibraries.Found found, IReadOnlyList<RunningProgram> running) {
			foreach (var (launcher, name, path, suggested, detail, removing) in KnownCaches(env, found)) {
				if (!Directory.Exists(path)) continue;
				var (bytes, newest) = DevScanner.Measure(path);
				if (bytes < MinBytes) continue;
				string? blocked = GameLaunchers.RunningText(launcher, running);
				yield return new GameItem(IdOf("cache", path), "cache", name, GameLibraries.Full(path), [GameLibraries.Full(path)], bytes, Stamp(newest),
					suggested && blocked == null, blocked, detail, removing, launcher);
			}
			// Steam's unfinished downloads: of a game it still lists, a paused download or update; of one it doesn't, a leftover.
			foreach (var (lib, _) in found.SteamLibraries) {
				foreach (string folder in Folders(Path.Combine(lib, "steamapps", "downloading"))) {
					string app = Path.GetFileName(folder);
					var (bytes, newest) = DevScanner.Measure(folder);
					if (bytes < MinBytes) continue;
					bool listed = found.SteamApps.Contains(app);
					GameInstall? game = found.Games.FirstOrDefault(g => g.Launcher == GameLaunchers.Steam && g.AppId == app);
					string? blocked = GameLaunchers.RunningText(GameLaunchers.Steam, running);
					yield return listed
						? new GameItem(IdOf("download", folder), "paused", $"Unfinished download of {game?.Name ?? "Steam game " + app}", folder, [folder], bytes, Stamp(newest),
							false, blocked, "A download or update Steam paused", "Steam starts that download again from the beginning.", GameLaunchers.Steam, game?.Id, app)
						: new GameItem(IdOf("download", folder), "download", $"Unfinished download of Steam game {app}", folder, [folder], bytes, Stamp(newest),
							blocked == null, blocked, "A download of a game no longer installed", "Nothing: the game isn't installed.", GameLaunchers.Steam, AppId: app);
				}
			}
		}

		// ------------------------------------------------------------------ shader caches

		/// <summary>The graphics drivers' and Windows' shader caches: (name, folders).</summary>
		internal static IEnumerable<(string Name, string[] Paths)> DriverShaderCaches(GamePlaces env) => [
			("NVIDIA shader cache", [Path.Combine(env.LocalAppData, "NVIDIA", "DXCache"), Path.Combine(env.LocalAppData, "NVIDIA", "GLCache"),
				Path.Combine(env.LocalLow, "NVIDIA", "PerDriverVersion", "DXCache"), Path.Combine(env.LocalLow, "NVIDIA", "PerDriverVersion", "GLCache")]),
			("AMD shader cache", [Path.Combine(env.LocalAppData, "AMD", "DxCache"), Path.Combine(env.LocalAppData, "AMD", "DxcCache"),
				Path.Combine(env.LocalAppData, "AMD", "GLCache"), Path.Combine(env.LocalAppData, "AMD", "VkCache")]),
			("Intel shader cache", [Path.Combine(env.LocalLow, "Intel", "ShaderCache")]),
			("DirectX shader cache", [Path.Combine(env.LocalAppData, "D3DSCache")]),
		];

		internal static IEnumerable<GameItem> ShaderItems(GamePlaces env, GameLibraries.Found found, List<GameInstall> games, IReadOnlyList<RunningProgram> running) {
			string? anyGame = AnyGameRunning(games, running);
			foreach (var (name, paths) in DriverShaderCaches(env)) {
				var existing = paths.Where(Directory.Exists).Select(GameLibraries.Full).ToList();
				if (existing.Count == 0) continue;
				var sizes = existing.Select(DevScanner.Measure).ToList();
				long bytes = sizes.Sum(s => s.Bytes);
				if (bytes < MinBytes) continue;
				yield return new GameItem(IdOf("gpu-shader", name), "gpu-shader", name, existing[0], existing, bytes, Stamp(sizes.Max(s => s.NewestUtc)), false, anyGame,
					"Shaders your graphics driver built for the games you play",
					"They rebuild by themselves, but each game's first launch afterwards may stutter for a while.");
			}
			string? steamRunning = GameLaunchers.RunningText(GameLaunchers.Steam, running);
			foreach (var (lib, _) in found.SteamLibraries) {
				foreach (string folder in Folders(Path.Combine(lib, "steamapps", "shadercache"))) {
					string app = Path.GetFileName(folder);
					var (bytes, newest) = DevScanner.Measure(folder);
					if (bytes < MinBytes) continue;
					GameInstall? game = games.FirstOrDefault(g => g.Launcher == GameLaunchers.Steam && g.AppId == app);
					if (game == null && !found.SteamApps.Contains(app)) {
						yield return new GameItem(IdOf("shader", folder), "shader", $"Shaders of Steam game {app}", folder, [folder], bytes, Stamp(newest), true, null,
							"Shaders Steam built for a game no longer installed", "Nothing: the game isn't installed.", GameLaunchers.Steam, AppId: app);
						continue;
					}
					string? blocked = (game != null && RunsFrom(game.Folder, running) ? $"{game.Name} is running: close it first" : null) ?? steamRunning;
					yield return new GameItem(IdOf("shader", folder), "shader", $"Shaders of {game?.Name ?? "Steam game " + app}", folder, [folder], bytes, Stamp(newest),
						false, blocked, "Shaders Steam built for an installed game",
						"Steam rebuilds them, but the game's first launch afterwards may stutter for a while.", GameLaunchers.Steam, game?.Id, app);
				}
			}
		}

		// ------------------------------------------------------------------ crash dumps

		/// <summary>The programs in a game's folder, a few levels down (Unreal games keep theirs in Binaries\Win64).</summary>
		internal static List<string> ExesOf(string folder) {
			try {
				var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
				return Directory.EnumerateFiles(folder, "*.exe", options).Select(Path.GetFileName).Where(n => n != null).Select(n => n!)
					.Where(n => !n.StartsWith("unins", StringComparison.OrdinalIgnoreCase) && !n.Contains("crash", StringComparison.OrdinalIgnoreCase)
						&& !n.StartsWith("vc_redist", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("dxsetup", StringComparison.OrdinalIgnoreCase))
					.Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
		}

		/// <summary>The game a Windows Error Reporting folder (AppCrash_game.exe_…) is about: Windows cuts long names short.</summary>
		internal static string? WerExe(string folderName) {
			string[] parts = folderName.Split('_');
			return parts.Length >= 3 && parts[0].StartsWith("App", StringComparison.OrdinalIgnoreCase) && parts[1].Length >= 4 ? parts[1] : null;
		}

		internal static IEnumerable<GameItem> DumpItems(GamePlaces env, GameLibraries.Found found, List<GameInstall> games, IReadOnlyList<RunningProgram> running) {
			var exes = games.Select(g => (Game: g, Exes: ExesOf(g.Folder))).Where(x => x.Exes.Count > 0).ToList();
			GameInstall? GameOf(string exeOrPrefix) => exes.FirstOrDefault(x => x.Exes.Any(e =>
				e.Equals(exeOrPrefix, StringComparison.OrdinalIgnoreCase) || (exeOrPrefix.Length >= 8 && e.StartsWith(exeOrPrefix, StringComparison.OrdinalIgnoreCase)))).Game;
			string? Running(GameInstall g) => RunsFrom(g.Folder, running) ? $"{g.Name} is running: close it first" : null;

			// Windows' crash dumps of the games' programs (game.exe.1234.dmp), a row per game.
			string crashDumps = Path.Combine(env.LocalAppData, "CrashDumps");
			if (Directory.Exists(crashDumps)) {
				var byGame = new Dictionary<GameInstall, List<FileInfo>>();
				foreach (FileInfo f in SafeFiles(crashDumps, "*.dmp")) {
					string exe = f.Name.Split(".exe", StringSplitOptions.None)[0] + ".exe";
					if (GameOf(exe) is { } g) (byGame.TryGetValue(g, out var list) ? list : byGame[g] = new()).Add(f);
				}
				foreach (var (g, files) in byGame)
					yield return new GameItem(IdOf("dump", "crashdumps|" + g.Id), "dump", $"Crash dumps of {g.Name}", crashDumps, files.Select(f => f.FullName).ToList(),
						files.Sum(f => f.Length), files.Max(f => f.LastWriteTimeUtc), Running(g) == null, Running(g),
						$"{files.Count} crash dump(s) Windows saved when {g.Name} crashed", "Nothing: they're only for the game's makers.", g.Launcher, g.Id, g.AppId);
			}
			// Steam's own crash dumps.
			if (found.SteamRoot is { } steam && Path.Combine(steam, "dumps") is { } steamDumps && Directory.Exists(steamDumps)) {
				var files = SafeFiles(steamDumps, "*.dmp");
				if (files.Count > 0)
					yield return new GameItem(IdOf("dump", steamDumps), "dump", "Steam's crash dumps", GameLibraries.Full(steamDumps), files.Select(f => f.FullName).ToList(),
						files.Sum(f => f.Length), files.Max(f => f.LastWriteTimeUtc), true, null, $"{files.Count} crash dump(s) Steam saved",
						"Nothing: they're only for Steam's makers.", GameLaunchers.Steam);
			}
			string? anyGame = AnyGameRunning(games, running);
			// Unreal Engine games' crash folders (%LOCALAPPDATA%\<game>\Saved\Crashes) and Unity games' (%TEMP%\<maker>\<game>\Crashes).
			var crashFolders = Folders(env.LocalAppData).Select(f => Path.Combine(f, "Saved", "Crashes"))
				.Concat(Folders(env.Temp).SelectMany(Folders).Select(f => Path.Combine(f, "Crashes")))
				.Where(Directory.Exists).Select(GameLibraries.Full);
			foreach (string folder in crashFolders) {
				var (bytes, newest) = DevScanner.Measure(folder);
				if (bytes <= 0) continue;
				string owner = Path.GetFileName(Path.GetDirectoryName(folder.EndsWith(@"\Saved\Crashes", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(folder)! : folder)!) ?? "a game";
				yield return new GameItem(IdOf("crash", folder), "crash", $"Crash reports of {owner}", folder, [folder], bytes, Stamp(newest), anyGame == null, anyGame,
					"Reports a game wrote when it crashed", "Nothing: they're only for the game's makers.");
			}
			// Windows Error Reporting's reports of the games' crashes and hangs.
			foreach (string wer in new[] { Path.Combine(env.LocalAppData, "Microsoft", "Windows", "WER"), Path.Combine(env.ProgramData, "Microsoft", "Windows", "WER") }) {
				var byGame = new Dictionary<GameInstall, List<string>>();
				foreach (string queue in new[] { "ReportArchive", "ReportQueue" })
					foreach (string folder in Folders(Path.Combine(wer, queue)))
						if (WerExe(Path.GetFileName(folder)) is { } exe && GameOf(exe) is { } g)
							(byGame.TryGetValue(g, out var list) ? list : byGame[g] = new()).Add(folder);
				foreach (var (g, folders) in byGame) {
					var sizes = folders.Select(DevScanner.Measure).ToList();
					yield return new GameItem(IdOf("wer", wer + "|" + g.Id), "wer", $"Windows' crash reports of {g.Name}", GameLibraries.Full(wer), folders, sizes.Sum(s => s.Bytes),
						Stamp(sizes.Max(s => s.NewestUtc)), Running(g) == null, Running(g), $"{folders.Count} report(s) Windows kept of {g.Name} crashing or hanging",
						"Nothing: Windows has sent what it was going to.", g.Launcher, g.Id, g.AppId);
				}
			}
		}

		static List<FileInfo> SafeFiles(string folder, string pattern) {
			try { return new DirectoryInfo(folder).EnumerateFiles(pattern).ToList(); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
		}

		// ------------------------------------------------------------------ twice, and not played in months

		/// <summary>A game's name as two launchers might both write it: no ™ or ®, no punctuation, one space between words.</summary>
		internal static string SameName(string name) {
			var sb = new StringBuilder();
			foreach (char c in name.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
			return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
		}

		internal static IEnumerable<GameItem> TwiceItems(List<GameInstall> games) {
			foreach (var same in games.GroupBy(g => SameName(g.Name)).Where(g => g.Key.Length > 0 && g.Select(x => x.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)) {
				var copies = same.OrderByDescending(g => g.Bytes).ToList();
				string where = string.Join(" and ", copies.Select(c => $"{GameLaunchers.Name(c.Launcher)} ({c.Folder})"));
				yield return new GameItem(IdOf("twice", same.Key), "twice", copies[0].Name, copies[0].Folder, copies.Select(c => c.Folder).ToList(),
					copies.Skip(1).Sum(c => c.Bytes), copies.Max(c => c.LastPlayedUtc), false, null, "Installed in " + where,
					"Uninstall the copy you don't play from its launcher to free its space. Heiward doesn't uninstall games.",
					copies[0].Launcher, copies[0].Id, copies[0].AppId, Info: true);
			}
		}

		internal static IEnumerable<GameItem> IdleItems(List<GameInstall> games, DateTime now) {
			DateTime before = now.AddDays(-IdleDays);
			foreach (GameInstall g in games) {
				// Never played: counted from when Steam last installed or updated it.
				DateTime? since = g.LastPlayedUtc ?? (g.Launcher == GameLaunchers.Steam ? g.UpdatedUtc : null);
				if (since is not { } at || at >= before) continue;
				int months = Math.Max(3, (int)((now - at).TotalDays / 30));
				yield return new GameItem(IdOf("idle", g.Id), "idle", g.Name, g.Folder, [g.Folder], g.Bytes, g.LastPlayedUtc, false, null,
					g.LastPlayedUtc != null ? $"Last played {months} months ago, in {GameLaunchers.Name(g.Launcher)}" : $"Not played since it was installed or updated {months} months ago",
					GameLaunchers.MoveAdvice(g.Launcher), g.Launcher, g.Id, g.AppId, Info: true);
			}
		}

		static DateTime? Stamp(DateTime newest) => newest == DateTime.MinValue ? null : newest;

		// ------------------------------------------------------------------ before removing

		/// <summary>
		/// Why <paramref name="item"/> can't go now, read fresh: the launchers' records and what's running, as they are now, not as
		/// the check found them. Null when it can.
		/// </summary>
		public static string? Recheck(GameItem item, GamePlaces env) {
			if (item.Info) return "Heiward doesn't move or uninstall games. " + item.Removing;
			if (item.Kind is not ("orphan" or "workshop" or "download" or "paused" or "cache" or "shader" or "gpu-shader" or "dump" or "crash" or "wer"))
				return "Unknown item";
			GameLibraries.Found found = GameLibraries.Find(env);
			IReadOnlyList<RunningProgram> running = env.Running();
			if (item.Kind == "orphan" && item.Paths.Any(p => found.Claimed.Contains(GameLibraries.Full(p)))) return "A launcher lists it as installed again";
			// Of a game no longer installed, which is again since the check.
			if (item.Kind is "workshop" or "shader" or "download" && item.Game == null && item.AppId != null && found.SteamApps.Contains(item.AppId))
				return "The game is installed again";
			// The launcher's own: its caches, its downloads, Steam's shaders of an installed game.
			string? launcher = item.Kind is "cache" ? item.Launcher : item.Kind is "download" or "paused" || item.Kind == "shader" && item.Game != null ? GameLaunchers.Steam : null;
			if (launcher != null && GameLaunchers.RunningText(launcher, running) is { } busy) return busy;
			if (item.Game != null && found.Games.FirstOrDefault(g => g.Id == item.Game) is { } game && RunsFrom(game.Folder, running))
				return $"{game.Name} is running: close it first";
			if (item.Kind is "gpu-shader" or "crash" && AnyGameRunning(found.Games, running) is { } anyGame) return anyGame;
			return null;
		}
	}

	/// <summary>
	/// Removes one game item, the user's choice or automatic cleanup's: checked again first (<see cref="GameScanner.Recheck"/>),
	/// each path checked against what's never touched (<see cref="GameSafety"/>), and everything goes to the Recycle Bin, never
	/// deleted outright: what the Recycle Bin can't take stays, with the reason.
	/// </summary>
	static class GameCleaner {
		public static CleanResult Remove(GameItem item, GamePlaces env) {
			if (GameScanner.Recheck(item, env) is { } why) return new CleanResult(0, 0, why);
			GameLibraries.Found found = GameLibraries.Find(env);
			var allowed = new List<string>();
			string? refused = null, refusedPath = null;
			int refusedCount = 0;
			foreach (string p in item.Paths) {
				if (!Path.Exists(p)) continue;
				// Crash dumps are files of their own: only .dmp files.
				if (item.Kind == "dump" && !p.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)) continue;
				// A cache's or crash folder's contents are the launcher's or Windows': looked inside only where a save could be.
				bool lookInside = item.Kind is "orphan" or "workshop" or "crash";
				if (GameSafety.Refuse(p, found.Games, env, found.SteamRoot, lookInside) is { } reason) {
					refused ??= reason;
					refusedPath ??= p;
					refusedCount++;
					continue;
				}
				allowed.Add(p);
			}
			if (allowed.Count == 0 && refused != null) return new CleanResult(0, 0, refused);
			RecycleResult r = Recycler.RecyclePaths(allowed);
			int left = r.Failed.Count + refusedCount;
			return new CleanResult(r.RecycledBytes, left, null, r.Failed.FirstOrDefault()?.Path ?? refusedPath, r.Failed.FirstOrDefault()?.Reason ?? refused);
		}
	}

	/// <summary>One games check at a time (game-scan.lock), from the scheduled scan or the page.</summary>
	static class GameScan {
		static string LockPath => Path.Combine(AgentPaths.Home, "game-scan.lock");

		/// <summary>After a scheduled scan, with game mode on: at most once a day, so the drives aren't measured every hour.</summary>
		public static bool Due(GameMode mode) =>
			mode.On && (GameReport.Load() is not { } last || DateTime.UtcNow - last.ScannedAtUtc > TimeSpan.FromHours(20));

		public static GameReport? RunAndSave(AgentConfig cfg, CancellationToken ct = default) {
			Directory.CreateDirectory(AgentPaths.Home);
			FileStream? held;
			try { held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
			catch (IOException) { return null; } // another check is running
			using (held) {
				GameReport report = GameScanner.Run(GamePlaces.Current(cfg), ct);
				if (!ct.IsCancellationRequested) report.Save();
				AgentPaths.AppendLog($"games check: {report.Games.Count} game(s), {report.Items.Count(i => !i.Info)} item(s), " +
					$"{Format.Bytes(report.Items.Where(i => i.Suggested).Sum(i => i.Bytes))} ticked, in {report.DurationSec:N0} s");
				return report;
			}
		}

		public static bool IsRunning() {
			if (!File.Exists(LockPath)) return false;
			try {
				using var probe = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
				return false;
			}
			catch (IOException) { return true; }
		}
	}
}
