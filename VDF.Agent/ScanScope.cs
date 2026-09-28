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

using VDF.Core.Utils;

namespace VDF.Agent {
	/// <summary>
	/// What a scan looks at: every fixed drive by default, minus the folders that belong to Windows,
	/// to installed apps and games, and to other programs' own libraries. Pictures and videos in those
	/// are parts of something (a game's textures, an app's icons, a photo app's previews) that breaks
	/// if a "duplicate" goes, and they would bury the user's own copies in the report.
	/// </summary>
	static class ScanScope {
		/// <summary>A folder left out of scans, and the reason shown for it on the review page.</summary>
		internal readonly record struct Rule(string Pattern, string Reason);

		const string SystemReason = "Windows", Programs = "Installed programs", AppData = "App data", Games = "Game library",
			Code = "Code", PhotoApp = "Photo app library", Drivers = "Drivers", DevTools = "Developer tools";

		/// <summary>
		/// Left out at any depth, by folder name (wildcards allowed). VDF matches a pattern without a
		/// backslash against the folder's name.
		/// </summary>
		internal static readonly Rule[] ExcludedNames = {
			// System and recovery folders ($Recycle.Bin, $WinREAgent, $Windows.~BT, $SysReset).
			new("$*", SystemReason),
			// Programs' own data and tool caches (.git, .vscode, .nuget, .cache, .thumbnails, .Trash-1000).
			new(".*", AppData),
			new("node_modules", Code),
			// Game stores' libraries, wherever the user put them.
			new("steamapps", Games), new("SteamLibrary", Games), new("Epic Games", Games), new("GOG Games", Games),
			new("EA Games", Games), new("Origin Games", Games), new("Riot Games", Games), new("Ubisoft Game Launcher", Games),
			new("XboxGames", Games), new("WindowsApps", Programs), new("ModifiableWindowsApps", Games),
			// Photo and video apps' libraries: originals and previews they manage themselves.
			new("*.photoslibrary", PhotoApp), new("*.photolibrary", PhotoApp), new("*.aplibrary", PhotoApp),
			new("*.lrdata", PhotoApp), new("*.lrlibrary", PhotoApp), new("*.cocatalog", PhotoApp),
			new("Media Cache Files", "Video editor cache"), new("CacheClip", "Video editor cache"),
			// Windows' account pictures: one picture at nine sizes, which would all look like smaller copies.
			new("AccountPictures", "Account pictures"),
		};

		/// <summary>Left out at the root of every drive (<c>?</c> is the drive letter).</summary>
		internal static readonly Rule[] ExcludedAtDriveRoot = {
			new("Windows", SystemReason), new("Windows.old", SystemReason), new("Recovery", SystemReason), new("PerfLogs", SystemReason),
			new("System Volume Information", SystemReason), new("Config.Msi", SystemReason), new("ESD", SystemReason),
			new("Program Files", Programs), new("Program Files (x86)", Programs), new("Program Files (Arm)", Programs),
			new("ProgramData", AppData), new("OneDriveTemp", AppData), new("MSOCache", AppData),
			new("Intel", Drivers), new("AMD", Drivers), new("NVIDIA", Drivers), new("Drivers", Drivers),
			new("inetpub", "Web server"), new("msys64", DevTools), new("cygwin", DevTools), new("cygwin64", DevTools),
		};

		/// <summary>Windows' own profile folders: the template for new accounts and its old-style links.</summary>
		static readonly HashSet<string> WindowsProfiles = new(StringComparer.OrdinalIgnoreCase) { "Default", "Default User", "All Users", "defaultuser0", "defaultuser100000" };

		internal const string RepositoryReason = "Code repository";

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
		public static List<string> Exclusions(AgentConfig cfg) =>
			ExclusionRules(cfg).Select(r => r.Pattern).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

		internal static List<Rule> ExclusionRules(AgentConfig cfg) {
			var rules = new List<Rule>(ExcludedNames);
			rules.AddRange(ExcludedAtDriveRoot.Select(r => r with { Pattern = @"?:\" + r.Pattern }));
			// Every profile's app data: browser caches, app icons, game saves, and this agent's own home.
			rules.Add(new(@"?:\Users\*\AppData", AppData));
			string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			if (!string.IsNullOrEmpty(windows))
				rules.Add(new(windows, SystemReason));
			rules.AddRange(OtherProfiles().Select(p => new Rule(p, WindowsProfiles.Contains(Path.GetFileName(p)) ? SystemReason : "Another account")));
			rules.AddRange(cfg.ExcludeFolders.Select(p => new Rule(p, "Excluded in settings")));
			return rules;
		}

		/// <summary>
		/// Whether a scan walks into <paramref name="folder"/>, and if not, why, by the same rules as the
		/// file enumeration (FileUtils.GetFilesRecursive): exclusions, hidden system folders, folder links
		/// and code repositories. Only the folder itself is judged, not the folders above it.
		/// </summary>
		internal static string? ExemptReason(DirectoryInfo folder, IReadOnlyList<Rule> rules) {
			foreach (Rule rule in rules)
				if (FileUtils.IsExcludedFolder(rule.Pattern, folder))
					return rule.Reason;
			FileAttributes attributes;
			try { attributes = folder.Attributes; }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Not readable"; }
			if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System))
				return SystemReason;
			if ((attributes & FileAttributes.ReparsePoint) != 0) {
				try {
					if (folder.LinkTarget != null) return "Link to another folder";
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Link to another folder"; }
			}
			foreach (string marker in RepositoryMarkers)
				if (Path.Exists(Path.Combine(folder.FullName, marker)))
					return RepositoryReason;
			return null;
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
