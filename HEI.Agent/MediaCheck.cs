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
using System.IO.Enumeration;
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <summary>What a look on disk found in a folder, before any scan has counted it.</summary>
	enum MediaOnDisk {
		/// <summary>No photo or video anywhere a scan would look.</summary>
		None,
		/// <summary>At least one photo or video a scan would list.</summary>
		Found,
		/// <summary>Too much to look through now: it may hold some.</summary>
		Unfinished,
	}

	/// <summary>
	/// Whether a folder, or any folder below it, holds a photo or video a scan would list, looked up on disk: for the review
	/// page's folder tree, where the last scan has counted nothing (no scan yet, a drive scanned only when asked and never
	/// asked, a folder new since). It goes by the scan's own rules (FileUtils.GetFilesRecursive, as ScanScope sets it up):
	/// the photo and video types, whatever their case, less the user's excludeExtensions and cloud-only files; never into a
	/// folder the scan leaves out (an exclusion, a hidden system folder, a folder link, a code repository). It stops at the
	/// first one found, and opens no file but a .ts one, which it reads the start of as the scan does (TypeScript's source
	/// files share the extension). Folders are looked through a level at a time, so media near the top is found first.
	/// <para>
	/// One check is one listing's (<see cref="ExplorerView.Tree"/>): it looks through at most <see cref="MaxFolders"/> folders
	/// in <see cref="MaxTime"/> in all, so a huge tree with nothing in it never holds the page up; past that, what's left is
	/// <see cref="MediaOnDisk.Unfinished"/>.
	/// </para>
	/// </summary>
	sealed class MediaCheck {
		/// <summary>The most folders one listing's check looks through.</summary>
		internal const int MaxFolders = 3_000;

		/// <summary>The longest one listing's check takes.</summary>
		internal static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(1);

		// Cloud-sync placeholders: the scan leaves them out (Settings.SkipCloudPlaceholders), and reading one downloads it.
		const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000, RecallOnOpen = (FileAttributes)0x00040000;
		const FileAttributes CloudPlaceholder = RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline;
		const FileAttributes HiddenSystem = FileAttributes.Hidden | FileAttributes.System;

		static readonly EnumerationOptions Listing = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None };
		static readonly HashSet<string> Markers = new(ScanScope.RepositoryMarkers, StringComparer.OrdinalIgnoreCase);

		readonly IReadOnlyList<ScanScope.Rule> rules;
		readonly HashSet<string> excluded;
		readonly int maxFolders;
		readonly TimeSpan maxTime;
		readonly Stopwatch clock = Stopwatch.StartNew();
		int folders;

		/// <param name="rules">The folders scans leave out (<see cref="ScanScope.ExclusionRules"/>).</param>
		/// <param name="maxFolders">At most this many folders looked through in all; <see cref="MaxFolders"/> by default.</param>
		/// <param name="maxTime">For at most this long in all; <see cref="MaxTime"/> by default.</param>
		public MediaCheck(AgentConfig cfg, IReadOnlyList<ScanScope.Rule> rules, int? maxFolders = null, TimeSpan? maxTime = null) {
			this.rules = rules;
			// As AgentScanner.BuildSettings reads them: with or without the dot.
			excluded = new(cfg.ExcludeExtensions.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.StartsWith('.') ? e : "." + e),
				StringComparer.OrdinalIgnoreCase);
			this.maxFolders = maxFolders ?? MaxFolders;
			this.maxTime = maxTime ?? MaxTime;
		}

		/// <summary>The photos and videos directly in <paramref name="folder"/>, not its subfolders'.</summary>
		public MediaOnDisk Own(string folder) => Look(folder, below: false);

		/// <summary>
		/// The photos and videos in <paramref name="folder"/> and every folder below it a scan walks into. The folder itself
		/// is taken as scanned: its own reason to be left out is the caller's to judge (<see cref="ScanScope.ExemptReason"/>).
		/// </summary>
		public MediaOnDisk Below(string folder) => Look(folder, below: true);

		MediaOnDisk Look(string folder, bool below) {
			var waiting = new Queue<(string Path, bool Judged)>();
			waiting.Enqueue((folder, false));
			var subfolders = new List<(string Path, string Name, FileAttributes Attributes)>();
			while (waiting.Count > 0) {
				if (folders >= maxFolders || clock.Elapsed >= maxTime) return MediaOnDisk.Unfinished;
				folders++;
				var (dir, judged) = waiting.Dequeue();
				subfolders.Clear();
				bool media = false, marked = false;
				try {
					foreach (var (name, isFolder, attributes) in new FileSystemEnumerable<(string, bool, FileAttributes)>(dir,
							(ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.Attributes), Listing)) {
						// A folder below the one asked about that holds a repository's marker is left out with all in it, as
						// the scan leaves it out (Settings.SkipFoldersContaining): what's in it counts for nothing.
						if (judged && Markers.Contains(name)) {
							marked = true;
							break;
						}
						if (isFolder) {
							if (below) subfolders.Add((Path.Join(dir, name), name, attributes));
						}
						else if (!media && IsMedia(dir, name, attributes)) {
							media = true;
							// Nothing else in it can take it back: only a folder the scan judges can still turn out left out.
							if (!judged) return MediaOnDisk.Found;
						}
					}
				}
				catch (DirectoryNotFoundException) { continue; }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					// What was listed before it broke off counts, as the scan counts it.
				}
				if (marked) continue;
				if (media) return MediaOnDisk.Found;
				foreach (var (path, name, attributes) in subfolders)
					if (!LeftOut(path, name, attributes)) waiting.Enqueue((path, true));
			}
			return MediaOnDisk.None;
		}

		/// <summary>A file the scan would list: a photo or video type it reads, not excluded, and on this PC (not cloud-only).</summary>
		bool IsMedia(string dir, string name, FileAttributes attributes) {
			string extension = Path.GetExtension(name);
			if (!FileUtils.IsMediaExtension(extension) || excluded.Contains(extension) || (attributes & CloudPlaceholder) != 0) return false;
			if (!FileUtils.IsTransportStreamExtension(name)) return true;
			// A .ts file may be TypeScript: the scan reads its start and leaves text out (ScanEngine, EntryFlags.NotMedia).
			// Reading one counts as looking through a folder, so a project full of them is soon Unfinished, never slow.
			folders++;
			return FileUtils.IsTextNotVideo(Path.Join(dir, name)) != true;
		}

		/// <summary>
		/// Whether the scan's walk leaves the folder out, with everything in it, by what its parent's listing says of it: the
		/// exclusions, Windows' hidden system folders, links to another folder (ScanScope.ExemptReason, without the markers,
		/// which the folder's own listing gives).
		/// </summary>
		bool LeftOut(string path, string name, FileAttributes attributes) {
			foreach (ScanScope.Rule rule in rules)
				if (FileUtils.IsExcludedFolder(rule.Pattern, path, name)) return true;
			if ((attributes & HiddenSystem) == HiddenSystem) return true;
			if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
			try {
				return new DirectoryInfo(path).LinkTarget != null;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return true;
			}
		}
	}
}
