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

using HEI.Core;
using HEI.Core.Utils;
using System.Text.RegularExpressions;

namespace HEI.Agent {
	/// <summary>
	/// What a scan looks at: every fixed drive by default, minus the folders that belong to Windows,
	/// to installed apps and games, and to other programs' own libraries. Pictures and videos in those
	/// are parts of something (a game's textures, an app's icons, a photo app's previews) that breaks
	/// if a "duplicate" goes, and they would bury the user's own copies in the report.
	/// <para>
	/// The built-in exclusions only apply below the scanned folders: a folder the user lists in
	/// <c>folders</c> is scanned even when it, or a folder above it, matches one. The user's own
	/// <c>excludeFolders</c> cover the whole path and win over <c>folders</c>: a listed folder inside
	/// one is skipped with a note. Both are explicit, and leaving files alone is the safe way to
	/// settle a contradiction in a tool that suggests deleting them.
	/// </para>
	/// </summary>
	static partial class ScanScope {
		/// <summary>A folder left out of scans, and the reason shown for it on the review page.</summary>
		internal readonly record struct Rule(string Pattern, string Reason);

		const string SystemReason = "Windows", Programs = "Installed programs", AppData = "App data", Games = "Game library",
			Code = "Code", PhotoApp = "Photo app library", Drivers = "Drivers", DevTools = "Developer tools", PackageCache = "Package cache";
		internal const string UserReason = "Excluded in settings";

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

		/// <summary>
		/// A folder holding one of these is a code repository: its pictures belong to the project. Git,
		/// Mercurial, Subversion, a TFVC local workspace, Unity Version Control (Plastic SCM), Bazaar,
		/// Fossil (two names), Jujutsu, Darcs, Pijul, and Perforce's usual P4CONFIG file
		/// (<see cref="VersionControl"/> names them).
		/// </summary>
		internal static readonly string[] RepositoryMarkers =
			{ ".git", ".hg", ".svn", "$tf", ".plastic", ".bzr", ".fslckout", "_FOSSIL_", ".jj", "_darcs", ".pijul", ".p4config" };

		/// <summary>
		/// The folders to scan: every fixed drive that is ready (when enabled), then the extra folders.
		/// An extra folder inside another is scanned with it, unless that walk never gets there (a
		/// built-in exclusion, a code repository or a folder link on the way): then it's scanned on its
		/// own. A folder inside one of the user's own exclusions is skipped, with a note.
		/// <para>
		/// Each by its real path (<see cref="RealPath"/>), so a folder is scanned once whatever path leads to it: C:\Projects,
		/// where the drive D:\ is mounted, is D:\, which the drives' scan has already, and a scan never lists one photo twice
		/// as two copies of itself. A walk doesn't follow a folder link or a mounted drive (it would list a folder twice);
		/// what it skips is reached by its own path: a drive by its letter, a drive mounted only in a folder by that folder.
		/// </para>
		/// </summary>
		public static List<string> Roots(AgentConfig cfg, List<string>? notes = null) {
			var candidates = new List<string>();
			if (cfg.ScanAllDrives) {
				foreach (DriveInfo drive in FixedDrives())
					candidates.Add(drive.RootDirectory.FullName);
				candidates.AddRange(RealPath.DrivesWithoutLetter());
			}
			foreach (string folder in cfg.Folders) {
				if (!Directory.Exists(folder)) {
					notes?.Add($"Folder not found, skipped: {folder}");
					continue;
				}
				string full = RealPath.Of(folder);
				if (UserExclusionOver(full, cfg) is { } exclusion) {
					notes?.Add($"Folder skipped: {Path.TrimEndingDirectorySeparator(folder)} is inside \"{exclusion}\" in excludeFolders (settings.json), which wins over folders. Remove one of the two.");
					continue;
				}
				candidates.Add(full);
			}
			List<Rule>? rules = null; // only needed for a folder inside another
			var roots = new List<string>();
			for (int i = 0; i < candidates.Count; i++) {
				string folder = candidates[i];
				// Covered by an earlier copy of the same folder, or by a folder above it whose scan gets there.
				bool covered = candidates.Where((other, j) => j != i && IsSameOrUnder(folder, other) && (j < i || !IsSameOrUnder(other, folder)))
					.Any(other => ExemptBelow(other, folder, rules ??= BuiltInRules()) == null);
				if (!covered)
					roots.Add(folder);
			}
			return roots;
		}

		/// <summary>
		/// The built-in exclusions, for Settings.SubfolderBlackList: they only apply below the scanned
		/// folders. The user's own go to Settings.BlackList (<see cref="Apply"/>).
		/// </summary>
		public static List<string> Exclusions() =>
			BuiltInRules().Select(r => r.Pattern).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

		/// <summary>Puts what a scan looks at into the engine's settings.</summary>
		internal static void Apply(Settings s, AgentConfig cfg, List<string>? notes = null) {
			foreach (string root in Roots(cfg, notes)) s.IncludeList.Add(root);
			foreach (string f in Exclusions()) s.SubfolderBlackList.Add(f);
			foreach (string f in UserExclusions(cfg)) s.BlackList.Add(f);
			foreach (string marker in RepositoryMarkers) s.SkipFoldersContaining.Add(marker);
			s.SkipFolderLinks = true;
		}

		/// <summary>Folders left out: the built-in ones, other people's profiles, then the user's own.</summary>
		internal static List<Rule> ExclusionRules(AgentConfig cfg) {
			var rules = BuiltInRules();
			rules.AddRange(UserExclusions(cfg).Select(p => new Rule(p, UserReason)));
			return rules;
		}

		/// <summary>
		/// The user's exclusions (<see cref="AgentConfig.ExcludeFolders"/>), each as written and, for a path, by its real path
		/// too: the scan walks folders by their real paths (C:\Projects\Old is D:\Old when D:\ is mounted in C:\Projects).
		/// </summary>
		internal static List<string> UserExclusions(AgentConfig cfg) {
			var all = new List<string>();
			foreach (string pattern in cfg.ExcludeFolders.Where(p => !string.IsNullOrWhiteSpace(p)))
				foreach (string? form in new[] { pattern, RealPattern(pattern) })
					if (form != null && !all.Contains(form, StringComparer.OrdinalIgnoreCase)) all.Add(form);
			return all;
		}

		/// <summary>
		/// An exclusion with its folder part by its real path: C:\Projects\Old\* as D:\Old\* (the part before the first
		/// wildcard is resolved). Null for a folder name ("node_modules"), a pattern for every drive ("?:\Temp"), or what
		/// doesn't resolve to anything else.
		/// </summary>
		internal static string? RealPattern(string pattern) {
			if (!Path.IsPathFullyQualified(pattern) || pattern.StartsWith(@"\\", StringComparison.Ordinal)) return null;
			string[] parts = pattern.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			int wild = Array.FindIndex(parts, p => p.IndexOfAny(['*', '?']) >= 0);
			if (wild == 0) return null;
			string folder = string.Join(Path.DirectorySeparatorChar, wild < 0 ? parts : parts[..wild]);
			if (folder.Length == 2 && folder[1] == ':') folder += Path.DirectorySeparatorChar;
			string real = RealPath.Of(folder);
			string formed = wild < 0 ? real : Path.Join(real, string.Join(Path.DirectorySeparatorChar, parts[wild..]));
			return string.Equals(Path.TrimEndingDirectorySeparator(formed), Path.TrimEndingDirectorySeparator(pattern), StringComparison.OrdinalIgnoreCase) ? null : formed;
		}

		static List<Rule> BuiltInRules() {
			var rules = new List<Rule>(ExcludedNames);
			rules.AddRange(ExcludedAtDriveRoot.Select(r => r with { Pattern = @"?:\" + r.Pattern }));
			// Every profile's app data: browser caches, app icons, game saves, and this agent's own home.
			rules.Add(new(@"?:\Users\*\AppData", AppData));
			string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			if (!string.IsNullOrEmpty(windows))
				rules.Add(new(windows, SystemReason));
			rules.AddRange(OtherProfiles().Select(p => new Rule(p, WindowsProfiles.Contains(Path.GetFileName(p)) ? SystemReason : "Another account")));
			rules.AddRange(PackageCaches().Select(p => new Rule(p, PackageCache)));
			return rules;
		}

		/// <summary>
		/// The variables that move a package manager's cache, or a toolchain's home, out of its usual place. A Dev Drive is
		/// set up this way (Microsoft's guide moves npm's, NuGet's, pip's, Cargo's, vcpkg's, Gradle's and Maven's there). In
		/// their usual places they're dot-folders or in AppData, which scans leave out already; moved, they're plain folders
		/// ("packages\nuget") full of package icons, and a scan walked them and listed the icons as duplicates of each other.
		/// </summary>
		internal static readonly string[] PackageCacheVariables = {
			"npm_config_cache", "npm_config_store_dir", "YARN_CACHE_FOLDER", "BUN_INSTALL_CACHE_DIR",
			"NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "NUGET_PLUGINS_CACHE_PATH",
			"PIP_CACHE_DIR", "UV_CACHE_DIR", "POETRY_CACHE_DIR",
			"CARGO_HOME", "RUSTUP_HOME", "GOMODCACHE", "GOCACHE",
			"VCPKG_DEFAULT_BINARY_CACHE", "CONAN_HOME", "GRADLE_USER_HOME",
		};

		/// <summary>
		/// The package caches this account's environment names (<see cref="PackageCacheVariables"/>, and Maven's
		/// -Dmaven.repo.local in MAVEN_OPTS), each as written and by its real path. One that is, or holds, the profile or one
		/// of the user's own folders (a variable set to %USERPROFILE% by mistake) is never left out: that would hide their photos.
		/// </summary>
		internal static List<string> PackageCaches(Func<string, string?>? read = null) {
			read ??= EnvironmentValue;
			var found = new List<string>();
			foreach (string name in PackageCacheVariables)
				Add(read(name));
			if (read("MAVEN_OPTS") is { } opts && MavenRepository().Match(opts) is { Success: true } m)
				Add(m.Groups[1].Value.Trim('"'));
			return found;

			void Add(string? value) {
				if (string.IsNullOrWhiteSpace(value)) return;
				string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
				if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return;
				try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
				catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return; }
				if (Path.GetPathRoot(path) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), path, StringComparison.OrdinalIgnoreCase)) return;
				if (UsersOwnFolders().Any(own => IsSameOrUnder(own, path))) return;
				foreach (string form in new[] { path, RealPath.Of(path) })
					if (!found.Contains(form, StringComparer.OrdinalIgnoreCase)) found.Add(form);
			}
		}

		/// <summary>A variable from this process's environment, else the account's own (a scan started before it was set).</summary>
		static string? EnvironmentValue(string name) =>
			Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
			: OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null;

		/// <summary>The profile and the folders people keep their own pictures and videos in.</summary>
		static IEnumerable<string> UsersOwnFolders() {
			string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			foreach (string f in new[] {
				profile, Path.Combine(profile, "Downloads"),
				Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
				Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
				Environment.GetEnvironmentVariable("OneDrive") ?? "" })
				if (!string.IsNullOrEmpty(f) && f.Length > 3) yield return Path.TrimEndingDirectorySeparator(f);
		}

		[GeneratedRegex(@"-Dmaven\.repo\.local=(""[^""]+""|\S+)")]
		private static partial Regex MavenRepository();

		/// <summary>
		/// The folder, or the one above it, that a built-in rule leaves out, and the rule: for a listed
		/// folder that is scanned anyway. Null when none does.
		/// </summary>
		internal static (string Folder, Rule Rule)? BuiltInExclusionOver(string folder) {
			List<Rule> rules = BuiltInRules();
			for (string? f = Path.TrimEndingDirectorySeparator(folder); !string.IsNullOrEmpty(f); f = Path.GetDirectoryName(f)) {
				string name = Path.GetFileName(f);
				if (name.Length == 0) break; // the drive's root
				foreach (Rule rule in rules)
					if (FileUtils.IsExcludedFolder(rule.Pattern, f, name))
						return (f, rule);
			}
			return null;
		}

		/// <summary>The user's exclusion that covers the folder or one above it, as the engine reads it (ScanEngine.IsBlackListed).</summary>
		internal static string? UserExclusionOver(string folder, AgentConfig cfg) {
			foreach (string pattern in cfg.ExcludeFolders) {
				if (string.IsNullOrWhiteSpace(pattern)) continue;
				string asRead = pattern;
				// The engine resolves a full path without wildcards before matching (NormalizeScanPaths).
				if (pattern.IndexOfAny(['*', '?']) < 0 && Path.IsPathRooted(pattern)) {
					try { asRead = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pattern)); }
					catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
				}
				// The scan has it by its real path too (UserExclusions).
				if (ScanEngine.IsBlackListed(folder, asRead) || RealPattern(pattern) is { } real && ScanEngine.IsBlackListed(folder, real))
					return pattern;
			}
			return null;
		}

		/// <summary>
		/// Why a scan of <paramref name="root"/> doesn't walk down to <paramref name="folder"/> inside it:
		/// the reason for the first folder on the way (the folder itself included, the root not) that it
		/// leaves out. Null when it gets there.
		/// </summary>
		internal static string? ExemptBelow(string root, string folder, IReadOnlyList<Rule> rules) {
			string rel = Path.GetRelativePath(root, folder);
			if (rel == ".") return null;
			string current = root;
			foreach (string part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
				current = Path.Combine(current, part);
				if (ExemptReason(new DirectoryInfo(current), rules) is { } reason)
					return reason;
			}
			return null;
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
