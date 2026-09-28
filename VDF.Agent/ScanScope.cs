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

namespace VDF.Agent {
	/// <summary>
	/// What a scan looks at: every fixed drive by default, minus the folders that belong to Windows,
	/// to installed apps and games, and to other programs' own libraries. Pictures and videos in those
	/// are parts of something (a game's textures, an app's icons, a photo app's previews) that breaks
	/// if a "duplicate" goes, and they would bury the user's own copies in the report.
	/// </summary>
	static class ScanScope {
		/// <summary>
		/// Left out at any depth, by folder name (wildcards allowed). VDF matches a pattern without a
		/// backslash against the folder's name.
		/// </summary>
		internal static readonly string[] ExcludedNames = {
			// System and recovery folders ($Recycle.Bin, $WinREAgent, $Windows.~BT, $SysReset).
			"$*",
			// Programs' own data and tool caches (.git, .vscode, .nuget, .cache, .thumbnails, .Trash-1000).
			".*",
			"node_modules",
			// Game stores' libraries, wherever the user put them.
			"steamapps", "SteamLibrary", "Epic Games", "GOG Games", "EA Games", "Origin Games", "Riot Games",
			"Ubisoft Game Launcher", "XboxGames", "WindowsApps", "ModifiableWindowsApps",
			// Photo and video apps' libraries: originals and previews they manage themselves.
			"*.photoslibrary", "*.photolibrary", "*.aplibrary", "*.lrdata", "*.lrlibrary", "*.cocatalog",
			"Media Cache Files", "CacheClip",
			// Windows' account pictures: one picture at nine sizes, which would all look like smaller copies.
			"AccountPictures",
		};

		/// <summary>Left out at the root of every drive (<c>?</c> is the drive letter).</summary>
		internal static readonly string[] ExcludedAtDriveRoot = {
			"Windows", "Windows.old", "Program Files", "Program Files (x86)", "Program Files (Arm)", "ProgramData", "Recovery", "PerfLogs", "System Volume Information",
			"OneDriveTemp", "MSOCache", "Config.Msi", "ESD", "Intel", "AMD", "NVIDIA", "Drivers", "inetpub",
			"msys64", "cygwin", "cygwin64",
		};

		/// <summary>A folder holding one of these is a code repository: its pictures belong to the project.</summary>
		internal static readonly string[] RepositoryMarkers = { ".git", ".hg", ".svn" };

		/// <summary>The folders to scan: every fixed drive that is ready (when enabled), then the extra folders.</summary>
		public static List<string> Roots(AgentConfig cfg, List<string>? notes = null) {
			var roots = new List<string>();
			if (cfg.ScanAllDrives)
				foreach (DriveInfo drive in FixedDrives())
					roots.Add(drive.RootDirectory.FullName);
			foreach (string folder in cfg.Folders) {
				if (!Directory.Exists(folder)) {
					notes?.Add($"Folder not found, skipped: {folder}");
					continue;
				}
				string full = Path.GetFullPath(folder);
				if (!roots.Any(r => IsSameOrUnder(full, r)))
					roots.Add(full);
			}
			return roots;
		}

		/// <summary>Folders left out: the built-in ones, other people's profiles, then the user's own.</summary>
		public static List<string> Exclusions(AgentConfig cfg) {
			var excluded = new List<string>(ExcludedNames);
			excluded.AddRange(ExcludedAtDriveRoot.Select(name => @"?:\" + name));
			// Every profile's app data: browser caches, app icons, game saves, and this agent's own home.
			excluded.Add(@"?:\Users\*\AppData");
			string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			if (!string.IsNullOrEmpty(windows))
				excluded.Add(windows);
			excluded.AddRange(OtherProfiles());
			excluded.AddRange(cfg.ExcludeFolders);
			return excluded.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		}

		/// <summary>
		/// Other accounts' profiles and Windows' template profiles. The agent works for the person
		/// signed in; C:\Users\Public stays in, since it holds shared pictures and videos.
		/// </summary>
		static IEnumerable<string> OtherProfiles() {
			string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string? users = Path.GetDirectoryName(profile);
			if (string.IsNullOrEmpty(users) || !Directory.Exists(users))
				yield break;
			string[] others;
			try {
				others = Directory.GetDirectories(users);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				yield break;
			}
			foreach (string other in others)
				if (!other.Equals(profile, StringComparison.OrdinalIgnoreCase) &&
					!Path.GetFileName(other).Equals("Public", StringComparison.OrdinalIgnoreCase))
					yield return other;
		}

		/// <summary>Internal fixed drives and external disks that report as fixed; not USB sticks, card readers or network drives.</summary>
		internal static IEnumerable<DriveInfo> FixedDrives() {
			foreach (DriveInfo drive in DriveInfo.GetDrives()) {
				bool ready;
				try { ready = drive.DriveType == DriveType.Fixed && drive.IsReady; }
				catch (IOException) { ready = false; }
				if (ready)
					yield return drive;
			}
		}

		static bool IsSameOrUnder(string path, string root) {
			string r = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
			return path.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
				(path + Path.DirectorySeparatorChar).StartsWith(r, StringComparison.OrdinalIgnoreCase);
		}
	}
}
