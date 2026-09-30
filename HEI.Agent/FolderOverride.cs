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

namespace HEI.Agent {
	/// <param name="Scanned">Whether the next scan looks at the folder.</param>
	/// <param name="Rule">Set when the folder can only be included by removing this rule of the user's, which covers more than it.</param>
	sealed record OverrideResult(bool Scanned, string Message, string? Rule = null, string? Error = null);

	/// <summary>
	/// The review page's right-click "Include in scans" / "Leave out of scans", written to the lists the
	/// user can also edit by hand: <see cref="AgentConfig.Folders"/> (scanned even inside a built-in
	/// exclusion) and <see cref="AgentConfig.ExcludeFolders"/>. Each is the other's undo: leaving out a
	/// folder that's in "folders" takes it out of there, and including a folder that's excluded by its
	/// own path takes that path out. The user's rules still win over "folders" (<see cref="ScanScope"/>),
	/// so a folder covered by a wider rule (a name, a wildcard, a folder above) is only included once the
	/// user agrees to remove that rule.
	/// </summary>
	static class FolderOverride {
		public static OverrideResult Include(AgentConfig cfg, string folder, string? removeRule) {
			if (Normalize(folder) is not { } full || !Directory.Exists(full))
				return new(false, "", Error: "That folder doesn't exist.");
			if (IsScanned(cfg, full))
				return new(true, "It's already scanned.");
			cfg.ExcludeFolders.RemoveAll(p => SamePath(p, full));
			if (ScanScope.UserExclusionOver(full, cfg) is { } rule) {
				if (!string.Equals(rule, removeRule, StringComparison.Ordinal))
					return new(false, "", Rule: rule, Error: $"It's left out by your rule \"{rule}\" (excludeFolders in the settings), which covers more than this folder.");
				cfg.ExcludeFolders.Remove(rule);
				if (ScanScope.UserExclusionOver(full, cfg) is { } next)
					return new(false, "", Rule: next, Error: $"It's also left out by your rule \"{next}\".");
			}
			if (!IsScanned(cfg, full) && !cfg.Folders.Any(f => SamePath(f, full)))
				cfg.Folders.Add(full);
			return IsScanned(cfg, full)
				? new(true, "Included: the next scan looks in it.")
				: new(false, "", Error: "Heiward can't scan this folder.");
		}

		public static OverrideResult Exclude(AgentConfig cfg, string folder) {
			if (Normalize(folder) is not { } full)
				return new(false, "", Error: "That isn't a folder.");
			if (!IsScanned(cfg, full))
				return new(false, "It's already left out.");
			cfg.Folders.RemoveAll(f => SamePath(f, full));
			// Still reached from a drive or a folder above it.
			if (IsScanned(cfg, full)) {
				// A rule for a drive's root would leave out everything on it but the files at its top.
				if (Path.GetPathRoot(full) is { } root && SamePath(root, full))
					return new(true, "", Error: "A whole drive can't be left out here: set scanAllDrives to false in the settings and list the folders to scan instead.");
				if (!cfg.ExcludeFolders.Any(p => SamePath(p, full)))
					cfg.ExcludeFolders.Add(full);
			}
			return new(false, "Left out: the next scan skips it, and its sets leave the list then.");
		}

		/// <summary>Whether the next scan looks at <paramref name="folder"/>: it's a scanned root, or one is above it and nothing on the way leaves it out.</summary>
		internal static bool IsScanned(AgentConfig cfg, string folder) {
			string full = Normalize(folder) ?? folder;
			string? root = ScanScope.Roots(cfg).Where(r => IsSameOrUnder(full, r)).MaxBy(r => r.TrimEnd(Path.DirectorySeparatorChar).Length);
			return root != null && ScanScope.ExemptBelow(root, full, ScanScope.ExclusionRules(cfg)) == null;
		}

		/// <summary>The full path without a trailing separator (a drive's root keeps its own: C:\), or null when it isn't one.</summary>
		static string? Normalize(string folder) {
			if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return null;
			try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
		}

		static bool SamePath(string a, string b) {
			try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
		}

		static bool IsSameOrUnder(string path, string root) {
			string r = root.TrimEnd(Path.DirectorySeparatorChar);
			string p = path.TrimEnd(Path.DirectorySeparatorChar);
			return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
	}
}
