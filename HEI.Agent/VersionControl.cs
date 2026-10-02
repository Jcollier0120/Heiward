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
	/// <summary>A repository the developer check found: its version control, and where it sends its changes.</summary>
	/// <param name="Vcs">A <see cref="VersionControl"/> key: git, hg, svn, tfvc, plastic, bzr, fossil, jj, darcs, pijul, perforce.</param>
	/// <param name="Remote">Git's origin (or its first remote), Mercurial's default path, without a password; null when unknown.</param>
	sealed record RepoSource(string Path, string Vcs, string? Remote);

	/// <summary>
	/// Which version control a repository uses, by its marker (<see cref="ScanScope.RepositoryMarkers"/>), and where it
	/// sends its changes, read from its own files: no tool is run.
	/// </summary>
	static class VersionControl {
		// Git first: a Jujutsu repository colocated with git is a git repository as well.
		static readonly (string Marker, string Vcs)[] Markers = {
			(".git", "git"), (".jj", "jj"), (".hg", "hg"), (".svn", "svn"), ("$tf", "tfvc"), (".plastic", "plastic"), (".bzr", "bzr"),
			(".fslckout", "fossil"), ("_FOSSIL_", "fossil"), ("_darcs", "darcs"), (".pijul", "pijul"), (".p4config", "perforce"),
		};

		public static string? Of(string repo) {
			foreach (var (marker, vcs) in Markers)
				if (Path.Exists(Path.Combine(repo, marker))) return vcs;
			return null;
		}

		public static string? RemoteOf(string repo, string vcs) {
			try {
				string? url = vcs switch {
					"git" => GitRemote(File.ReadAllText(Path.Combine(repo, ".git", "config"))),
					"hg" => HgDefault(File.ReadAllText(Path.Combine(repo, ".hg", "hgrc"))),
					_ => null,
				};
				return url == null ? null : WithoutPassword(url);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
		}

		/// <summary>The url of [remote "origin"] in a .git\config, or of its first remote. Not upstream: a fork's own pull requests are on origin.</summary>
		internal static string? GitRemote(string config) {
			string? remote = null, first = null;
			foreach (string raw in config.Split('\n')) {
				string line = raw.Trim();
				if (line.StartsWith('[')) {
					Match m = Regex.Match(line, @"^\[remote\s+""(.+)""\]$", RegexOptions.IgnoreCase);
					remote = m.Success ? m.Groups[1].Value : null;
					continue;
				}
				if (remote == null) continue;
				Match url = Regex.Match(line, @"^url\s*=\s*(.+)$", RegexOptions.IgnoreCase);
				if (!url.Success) continue;
				string value = url.Groups[1].Value.Trim().Trim('"');
				if (remote == "origin") return value;
				first ??= value;
			}
			return first;
		}

		/// <summary>[paths] default in a .hg\hgrc.</summary>
		internal static string? HgDefault(string hgrc) {
			bool paths = false;
			foreach (string raw in hgrc.Split('\n')) {
				string line = raw.Trim();
				if (line.StartsWith('[')) {
					paths = line.Equals("[paths]", StringComparison.OrdinalIgnoreCase);
					continue;
				}
				int eq = line.IndexOf('=');
				if (paths && eq > 0 && line[..eq].Trim() == "default") return line[(eq + 1)..].Trim();
			}
			return null;
		}

		/// <summary>A remote's URL as it may be shown and kept: who signs in, and a password or token written into it, are taken out.</summary>
		internal static string WithoutPassword(string url) =>
			Regex.Replace(url.Trim(), @"^([a-z][a-z0-9+.-]*://)[^/@]*@", "$1", RegexOptions.IgnoreCase);
	}
}
