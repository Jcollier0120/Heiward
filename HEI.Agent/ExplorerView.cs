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
	/// <summary>Duplicates that touch a folder: sets with copies to clean up, look-alike sets, and the checked bytes inside it.</summary>
	sealed record DupStats(int Copies, int Lookalikes, long Reclaim);

	/// <param name="OnRequest">Scanned only when asked, from its page (<see cref="AgentConfig.OnRequestDrives"/>).</param>
	/// <param name="Label">The drive's own name in Windows ("Games"), or "" when it has none: <paramref name="Name"/> says "Local Disk" then.</param>
	/// <param name="Scans">"auto": scheduled scans read it; "manual": only a scan you start does (<see cref="ExplorerView.ScanMode"/>).</param>
	/// <param name="ManualWhy">Why it's "manual": <see cref="ExplorerView.ByDrive"/>, <see cref="ExplorerView.OnDemand"/> or <see cref="ExplorerView.DevBuildWhy"/>; null for "auto".</param>
	sealed record DriveCard(string Root, string Name, string Type, long TotalBytes, long FreeBytes, bool Scanned,
		DriveScan? Scan, DupStats Duplicates, bool OnRequest = false, string Label = "", string Scans = "auto", string? ManualWhy = null);

	sealed record Hotspot(string Folder, int Copies, long Reclaim);

	sealed record TreeNode(string Name, string Path, string? Exempt, int Files, long Bytes, DupStats Duplicates, bool Expandable);

	sealed record TreeListing(string Path, string? Exempt, int Files, long Bytes, DupStats Duplicates, List<TreeNode> Children, int HiddenEmpty);

	/// <summary>
	/// The review page's Explorer-like view: drives, the folders that hold the duplicates, and a folder
	/// tree listed live from disk, each folder marked scanned or exempt (and why) by the scan's own rules.
	/// </summary>
	static class ExplorerView {
		/// <summary>The drive is scanned only when asked, from its page (<see cref="AgentConfig.OnRequestDrives"/>): Scan now leaves it alone too.</summary>
		public const string ByDrive = "drive";
		/// <summary>Heiward scans only when you press Scan now (<see cref="AgentConfig.ScanEveryMinutes"/> 0, the setup's "Only when I press Scan now").</summary>
		public const string OnDemand = "onDemand";
		/// <summary>A development build has no scheduled scans.</summary>
		public const string DevBuildWhy = "devBuild";

		/// <summary>
		/// Whether scheduled scans read a drive ("auto") or only a scan someone starts does ("manual"), and why. Both settings
		/// count, and neither is copied into the other: the setup's "Only when I press Scan now" (no scheduled scans at all, so
		/// every drive is manual) and the drive's own "Scan only when I ask" (<see cref="AgentConfig.OnRequestDrives"/>, which
		/// Scan now and the developer check leave alone too). The drive's own comes first: it says the most.
		/// </summary>
		internal static (string Scans, string? Why) ScanMode(AgentConfig cfg, bool onRequest, bool devBuild) =>
			onRequest ? ("manual", ByDrive)
			: devBuild ? ("manual", DevBuildWhy)
			: cfg.ScanEveryMinutes <= 0 ? ("manual", OnDemand)
			: ("auto", null);

		/// <summary>
		/// The drives, and the extra folders, the next scan looks at: from the settings rather than the last
		/// scan, so a folder the user includes or leaves out on the page shows so at once.
		/// </summary>
		/// <param name="devBuild">This is a development build, with no scheduled scans; null: whether it is (<see cref="DevBuild.Current"/>).</param>
		public static List<DriveCard> Drives(AgentConfig cfg, ScanIndex? index, List<ReportGroup> pending, bool? devBuild = null) {
			bool dev = devBuild ?? DevBuild.Current;
			var cards = new List<DriveCard>();
			var roots = ScanScope.Roots(cfg);
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
				string own = (label ?? "").Trim();
				if (string.IsNullOrWhiteSpace(label))
					label = type switch { "removable" => "USB Drive", "network" => "Network Drive", _ => "Local Disk" };
				bool scanned = roots.Any(r => r.Equals(root, StringComparison.OrdinalIgnoreCase));
				DriveScan? scan = null;
				index?.Drives.TryGetValue(root, out scan);
				bool onRequest = scanned && cfg.IsOnRequest(root);
				var (scans, why) = ScanMode(cfg, onRequest, dev);
				cards.Add(new DriveCard(root, $"{label} ({letter})", type, total, free, scanned, scanned ? scan : null, Stats(root, pending), onRequest, own, scans, why));
			}
			// Extra folders that aren't a whole drive (a folder on a USB drive, a network share).
			foreach (string root in roots.Where(r => !cards.Any(c => c.Root.Equals(r, StringComparison.OrdinalIgnoreCase)))) {
				DriveScan? scan = null;
				index?.Drives.TryGetValue(ScanIndex.DriveOf(root), out scan);
				var (files, bytes) = index?.Subtree(root) ?? (0, 0);
				bool onRequest = cfg.IsOnRequest(root);
				var (scans, why) = ScanMode(cfg, onRequest, dev);
				cards.Add(new DriveCard(root, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : root, "folder", 0, 0, true,
					scan == null ? null : scan with { Files = files, Bytes = bytes }, Stats(root, pending), onRequest, "", scans, why));
			}
			return cards;
		}

		/// <summary>The folders where cleaning up frees the most, by checked bytes directly inside them.</summary>
		public static List<Hotspot> Hotspots(List<ReportGroup> pending, int count) =>
			pending.SelectMany(g => g.Items.Where(i => i.Suggested).Select(i => (Group: g.Key, Item: i)))
				.GroupBy(x => x.Item.Folder, StringComparer.OrdinalIgnoreCase)
				.Select(f => new Hotspot(f.Key, f.Select(x => x.Group).Distinct().Count(), f.Sum(x => x.Item.Size)))
				.OrderByDescending(h => h.Reclaim)
				.Take(count)
				.ToList();

		/// <summary>
		/// The folder's subfolders as they are on disk now, each scanned or exempt by the settings as they
		/// are now. Below a drive's top level, folders with no photos or videos (and not exempt) are
		/// counted in <see cref="TreeListing.HiddenEmpty"/> unless <paramref name="all"/>. Null when the
		/// folder is outside what the agent scans, or missing.
		/// </summary>
		public static TreeListing? Tree(string path, bool all, AgentConfig cfg, ScanIndex? index, List<ReportGroup> pending) {
			var roots = ScanScope.Roots(cfg);
			if (Shown(path, roots) is not var (full, root)) return null;

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
					// A folder in "folders" inside one the rules leave out is scanned as a root of its own.
					string? reason = roots.Any(r => r.TrimEnd(Path.DirectorySeparatorChar).Equals(d.FullName, StringComparison.OrdinalIgnoreCase))
						? null : ScanScope.ExemptReason(d, rules);
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
		/// Sets with copies to clean up in the folder or below it (a checked file inside; a set that only
		/// keeps its original here isn't one), look-alike sets with a file inside, and the checked bytes inside.
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

		/// <summary>
		/// The folder as the tree shows it, by its real path (as scans walk it: the roots are real paths, ScanScope.Roots), and
		/// the scanned folder it's in, the deepest (a listed folder inside an exempt one is a root of its own); null when the
		/// path isn't a full one, isn't there, or is outside what Heiward scans.
		/// </summary>
		internal static (string Full, string Root)? Shown(string? path, IReadOnlyList<string> roots) {
			if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
			string full = RealPath.Of(path);
			string? root = roots.Where(r => IsSameOrUnder(full, r)).MaxBy(r => r.TrimEnd(Path.DirectorySeparatorChar).Length);
			return root == null || !Directory.Exists(full) ? null : (full, root);
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
