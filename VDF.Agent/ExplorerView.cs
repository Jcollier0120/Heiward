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
	/// <summary>Duplicates that touch a folder: sets with copies to clean up, look-alike sets, and the ticked bytes inside it.</summary>
	sealed record DupStats(int Copies, int Lookalikes, long Reclaim);

	sealed record DriveCard(string Root, string Name, string Type, long TotalBytes, long FreeBytes, bool Scanned,
		DriveScan? Scan, DupStats Duplicates);

	sealed record Hotspot(string Folder, int Copies, long Reclaim);

	sealed record TreeNode(string Name, string Path, string? Exempt, int Files, long Bytes, DupStats Duplicates, bool Expandable);

	sealed record TreeListing(string Path, string? Exempt, int Files, long Bytes, DupStats Duplicates, List<TreeNode> Children, int HiddenEmpty);

	/// <summary>
	/// The review page's Explorer-like view: drives, the folders that hold the duplicates, and a folder
	/// tree listed live from disk, each folder marked scanned or exempt (and why) by the scan's own rules.
	/// </summary>
	static class ExplorerView {
		public static List<DriveCard> Drives(AgentConfig cfg, ScanIndex? index, List<ReportGroup> pending) {
			var cards = new List<DriveCard>();
			var roots = index?.Roots ?? ScanScope.Roots(cfg);
			foreach (DriveInfo drive in DriveInfo.GetDrives()) {
				string type = drive.DriveType switch {
					DriveType.Fixed => "fixed", DriveType.Removable => "removable", DriveType.Network => "network", _ => "",
				};
				if (type == "") continue;
				long total = 0, free = 0;
				string label = "";
				try {
					if (!drive.IsReady) continue;
					total = drive.TotalSize;
					free = drive.TotalFreeSpace;
					label = drive.VolumeLabel;
				}
				catch (IOException) { continue; }
				catch (UnauthorizedAccessException) { }
				string root = drive.RootDirectory.FullName;
				string letter = root.TrimEnd(Path.DirectorySeparatorChar);
				if (string.IsNullOrWhiteSpace(label))
					label = type switch { "removable" => "USB Drive", "network" => "Network Drive", _ => "Local Disk" };
				bool scanned = roots.Any(r => r.Equals(root, StringComparison.OrdinalIgnoreCase));
				DriveScan? scan = null;
				index?.Drives.TryGetValue(root, out scan);
				cards.Add(new DriveCard(root, $"{label} ({letter})", type, total, free, scanned, scanned ? scan : null, Stats(root, pending)));
			}
			// Extra folders that aren't a whole drive (a folder on a USB drive, a network share).
			foreach (string root in roots.Where(r => !cards.Any(c => c.Root.Equals(r, StringComparison.OrdinalIgnoreCase)))) {
				DriveScan? scan = null;
				index?.Drives.TryGetValue(ScanIndex.DriveOf(root), out scan);
				var (files, bytes) = index?.Subtree(root) ?? (0, 0);
				cards.Add(new DriveCard(root, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : root, "folder", 0, 0, true,
					scan == null ? null : scan with { Files = files, Bytes = bytes }, Stats(root, pending)));
			}
			return cards;
		}

		/// <summary>The folders where cleaning up frees the most, by ticked bytes directly inside them.</summary>
		public static List<Hotspot> Hotspots(List<ReportGroup> pending, int count) =>
			pending.SelectMany(g => g.Items.Where(i => i.Suggested).Select(i => (Group: g.Key, Item: i)))
				.GroupBy(x => x.Item.Folder, StringComparer.OrdinalIgnoreCase)
				.Select(f => new Hotspot(f.Key, f.Select(x => x.Group).Distinct().Count(), f.Sum(x => x.Item.Size)))
				.OrderByDescending(h => h.Reclaim)
				.Take(count)
				.ToList();

		/// <summary>
		/// The folder's subfolders as they are on disk now. Below a drive's top level, folders with no
		/// photos or videos (and not exempt) are counted in <see cref="TreeListing.HiddenEmpty"/> unless
		/// <paramref name="all"/>. Null when the folder is outside what the agent scans, or missing.
		/// </summary>
		public static TreeListing? Tree(string path, bool all, AgentConfig cfg, ScanIndex? index, List<ReportGroup> pending) {
			if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
			string full = Path.GetFullPath(path);
			var roots = index?.Roots ?? ScanScope.Roots(cfg);
			// The deepest root: a listed folder inside an exempt one is a root of its own.
			string? root = roots.Where(r => IsSameOrUnder(full, r)).MaxBy(r => r.TrimEnd(Path.DirectorySeparatorChar).Length);
			if (root == null || !Directory.Exists(full)) return null;

			var rules = ScanScope.ExclusionRules(cfg);
			string? exempt = ScanScope.ExemptBelow(root, full, rules);
			var (files, bytes) = index?.Subtree(full) ?? (0, 0);
			var children = new List<TreeNode>();
			int hidden = 0;
			bool topLevel = full.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
			if (exempt == null) {
				IEnumerable<DirectoryInfo> subfolders;
				try {
					subfolders = new DirectoryInfo(full).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
						.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					subfolders = Array.Empty<DirectoryInfo>();
				}
				foreach (DirectoryInfo d in subfolders) {
					string? reason = ScanScope.ExemptReason(d, rules);
					var (f, b) = reason == null ? index?.Subtree(d.FullName) ?? (0, 0) : (0, 0);
					DupStats dup = reason == null ? Stats(d.FullName, pending) : new DupStats(0, 0, 0);
					if (reason == null && f == 0 && dup.Copies + dup.Lookalikes == 0 && !topLevel && !all) {
						hidden++;
						continue;
					}
					bool expandable = reason == null && ((index?.HasFilesBelow(d.FullName) ?? false) || HasSubfolders(d));
					children.Add(new TreeNode(d.Name, d.FullName, reason, f, b, dup, expandable));
				}
			}
			return new TreeListing(full, exempt, files, bytes, Stats(full, pending), children, hidden);
		}

		/// <summary>
		/// Sets with copies to clean up in the folder or below it (a ticked file inside; a set that only
		/// keeps its original here isn't one), look-alike sets with a file inside, and the ticked bytes inside.
		/// </summary>
		internal static DupStats Stats(string folder, List<ReportGroup> pending) {
			string prefix = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			int copies = 0, lookalikes = 0;
			long reclaim = 0;
			foreach (ReportGroup g in pending) {
				bool inside = false;
				long ticked = 0;
				foreach (ReportItem i in g.Items) {
					if (!i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
					inside = true;
					if (i.Suggested) ticked += i.Size;
				}
				if (g.Kind == "similar") {
					if (inside) lookalikes++;
				}
				else if (ticked > 0) {
					copies++;
					reclaim += ticked;
				}
			}
			return new DupStats(copies, lookalikes, reclaim);
		}

		static bool HasSubfolders(DirectoryInfo folder) {
			try {
				return folder.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).Any();
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return false;
			}
		}

		static bool IsSameOrUnder(string path, string root) {
			string r = root.TrimEnd(Path.DirectorySeparatorChar);
			string p = path.TrimEnd(Path.DirectorySeparatorChar);
			return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
	}
}
