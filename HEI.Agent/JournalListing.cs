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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HEI.Core;
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <summary>How a scan lists one of its folders.</summary>
	enum ListingMode {
		/// <summary>Walked, folder by folder, as every scan did before: no journal, or it can't vouch for the last listing.</summary>
		Walk,
		/// <summary>The journal names folders where photos or videos changed: only those are listed again.</summary>
		Changed,
		/// <summary>Nothing that a scan lists changed: the last listing stands, and the disk isn't read.</summary>
		Unchanged,
		/// <summary>A drive scanned only when you ask (<see cref="AgentConfig.OnRequestDrives"/>): not touched at all, not even its journal; its last listing stands.</summary>
		Resting,
	}

	/// <summary>One scanned folder (usually a drive): how this scan lists it, and why.</summary>
	sealed class RootPlan {
		public required string Root { get; init; }
		public ListingMode Mode { get; set; }
		public string Why { get; set; } = "";
		/// <summary>Where this scan read the journal up to, kept for the next one; null without a journal.</summary>
		public ulong? JournalId { get; set; }
		public long Usn { get; set; }
		public DateTime FullWalkUtc { get; set; }
		/// <summary>For <see cref="ListingMode.Changed"/> and <see cref="ListingMode.Unchanged"/>: what the engine lists instead of walking.</summary>
		public ScanEngine.RootListing? Listing { get; set; }
		public int ChangedFolders { get; set; }
		public int Changes { get; set; }
	}

	/// <summary>
	/// What changed on each scanned drive since the last scan, from the drive's change journal
	/// (<see cref="VolumeJournal"/>), so a scan doesn't walk a drive to find out. Each change is placed in
	/// its folder; folders a scan never walks (Windows, programs, app data, repositories, exclusions) are
	/// let be. Folders the scan does walk are listed again, one by one, and compared with the last
	/// listing: a document saved in Documents changes nothing a scan sees. A folder added, moved in or
	/// made visible where scans look is listed with what's below it, and no more; one deleted or moved
	/// away takes what was listed in it with it, as does one made a repository, hidden or a link. When
	/// no drive has a change a scan would see, a scheduled scan doesn't run at all, and a sleeping hard
	/// disk stays asleep.
	///
	/// A drive is walked as before when its journal can't vouch for the last listing: the first scan, no
	/// journal (FAT, exFAT, network drives), a journal made again or overwritten past the last scan and the
	/// review page's last look (<see cref="ListingPlan.Keep"/>: the PC was off a long time, or the drive very busy), settings changed or Heiward lists differently
	/// (<see cref="ListingPlan.ListingFormat"/>; a new build alone doesn't), a change in a folder that
	/// can't be found, and once a week regardless.
	/// </summary>
	sealed class ListingPlan {
		/// <summary>A drive the journal has vouched for this long is walked anyway.</summary>
		internal static readonly TimeSpan FullWalkEvery = TimeSpan.FromDays(7);

		/// <summary>
		/// How a listing is made and what it means, as a version: part of every listing's key, so raising it walks
		/// every drive once. Raise it when a build lists differently with the same settings: the listing files'
		/// format (<see cref="StoredListing"/>), which files a walk lists (FileUtils.GetFilesRecursive), which
		/// folders it walks into beyond the settings (<see cref="ScanScope.ExemptReason"/>'s own checks), or how
		/// a change in the journal is placed (<see cref="Decide"/>). Nothing else: the key once held the build, and
		/// every new build walked every drive (six full walks of C:\, 58 to 96 s each, in a day of builds). The
		/// settings that decide a listing are in the key already, the built-in exclusions among them
		/// (Settings.SubfolderBlackList).
		/// </summary>
		internal const int ListingFormat = 2;

		const uint FileCreate = 0x100, FileDelete = 0x200, BasicInfoChange = 0x8000, RenameOldName = 0x1000, RenameNewName = 0x2000, ReparsePointChange = 0x100000;
		/// <summary>A folder created, deleted, moved, or made hidden or a link: what's below it changes for a scan.</summary>
		const uint FolderReshaped = FileCreate | FileDelete | RenameOldName | RenameNewName | BasicInfoChange | ReparsePointChange;

		public List<RootPlan> Roots { get; } = new();
		string scanKey = "", listingKey = "";

		/// <summary>Nothing a scan sees changed, on any drive (a resting one isn't looked at).</summary>
		public bool NothingChanged => Roots.Count > 0 && Roots.All(r => r.Mode is ListingMode.Unchanged or ListingMode.Resting);

		/// <summary>
		/// For <see cref="ScanEngine.MayRead"/> and the report: false for a file on a resting drive, which this
		/// scan mustn't read nor ask the disk about. Null when no drive rests.
		/// </summary>
		public Func<string, bool>? MayRead {
			get {
				var resting = Roots.Where(r => r.Mode == ListingMode.Resting).Select(r => r.Root).ToList();
				return resting.Count == 0 ? null : path => !resting.Any(root => IsUnder(path, root));
			}
		}

		/// <summary>The last scan had these same settings and this same build: its report stands as it is.</summary>
		public bool SameScanAsLast => Stored.Load().ScanKey == scanKey;

		/// <summary>For <see cref="ScanEngine.ListRoot"/>: the listing for a folder the journal vouched for, null to walk it.</summary>
		public ScanEngine.RootListing? ListingFor(string root) =>
			Roots.FirstOrDefault(r => string.Equals(Normal(r.Root), Normal(root), StringComparison.OrdinalIgnoreCase))?.Listing;

		/// <summary>One line for the log: each drive, and how it's listed.</summary>
		public string Describe() => string.Join("; ", Roots.Select(r => $"{r.Root} {r.Mode switch {
			ListingMode.Unchanged => r.Changes == 0 ? "unchanged" : $"unchanged ({r.Changes:N0} changes elsewhere)",
			ListingMode.Changed => $"{r.ChangedFolders:N0} folder(s) listed again",
			ListingMode.Resting => "left alone (scanned when you ask)",
			_ => "walked: " + r.Why,
		}}"));

		/// <summary>Plans this scan's listing of <paramref name="settings"/>' folders.</summary>
		/// <param name="scanNow">Drives scanned only when asked that this scan is asked to read ("D:\"); the others rest.</param>
		public static ListingPlan Make(Settings settings, AgentConfig cfg, DateTime nowUtc, CancellationToken ct, IReadOnlyCollection<string>? scanNow = null) {
			string key = ListingKey(settings);
			var plan = new ListingPlan { scanKey = ScanKey(settings), listingKey = key };
			Stored stored = Stored.Load();
			var rules = ScanScope.ExclusionRules(cfg);
			var scanned = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			var volumes = new Dictionary<string, Volume?>(StringComparer.OrdinalIgnoreCase);
			try {
				foreach (string root in settings.IncludeList) {
					var r = new RootPlan { Root = root, FullWalkUtc = nowUtc };
					plan.Roots.Add(r);
					stored.Roots.TryGetValue(root, out StoredRoot? last);
					// Scanned only when asked, and not asked now: nothing on the drive is touched, not even its
					// journal. Its photos and videos as its last scan listed them still count.
					if (cfg.IsOnRequest(root) && !AgentConfig.IsOnDrive(root, scanNow)) {
						r.Mode = ListingMode.Resting;
						List<string> known = last != null && StoredListing.Load(last.File) is { } kept ? kept.PathsOutside(new HashSet<string>()) : new();
						r.Listing = new ScanEngine.RootListing(Array.Empty<FileInfo>(), known);
						continue;
					}
					// The drive it's really on: C:\Projects may be the drive D:\, whose journal C:\'s doesn't hold.
					string drive = RealPath.VolumeOf(root) ?? Path.GetPathRoot(root) ?? root;
					if (!volumes.TryGetValue(drive, out Volume? volume))
						volumes[drive] = volume = Volume.Open(drive);
					if (volume == null) {
						r.Mode = ListingMode.Walk;
						r.Why = "no change journal";
						continue;
					}
					r.JournalId = volume.Journal.Id;
					r.Usn = volume.Journal.NextUsn;
					Decide(r, last, key, volume, settings, rules, scanned, nowUtc, ct);
				}
			}
			finally {
				foreach (Volume? v in volumes.Values) v?.Dispose();
			}
			return plan;
		}

		static void Decide(RootPlan r, StoredRoot? last, string key, Volume volume, Settings settings, IReadOnlyList<ScanScope.Rule> rules,
			Dictionary<string, bool> scanned, DateTime nowUtc, CancellationToken ct) {
			r.Mode = ListingMode.Walk;
			if (last == null) { r.Why = "no listing from the journal yet"; return; }
			r.FullWalkUtc = last.FullWalkUtc;
			if (last.Key != key) { r.Why = "settings changed, or how Heiward lists"; return; }
			if (last.JournalId != volume.Journal.Id) { r.Why = "the drive's journal was made again"; return; }
			if (nowUtc - last.FullWalkUtc > FullWalkEvery) { r.Why = "a week since the last full listing"; return; }
			// The review page found a reason to walk between scans (Keep).
			if (last.Pending?.Walk is { } walk) { r.Why = walk; return; }
			if (volume.RecordsFrom(last.Usn) is not { } records) { r.Why = Unread(volume.Journal, "the last scan"); return; }
			if (StoredListing.Load(last.File) is not { } listing) { r.Why = "the last listing is missing"; return; }

			// The scanned folder renamed, moved or gone (or one above it): the last listing's paths are no more.
			if (!Directory.Exists(r.Root)) { r.Why = "the folder isn't there"; return; }

			// What the review page placed between scans, and what changed since.
			Placed placed = Placed.From(last.Pending);
			if (Place(r.Root, records, volume, rules, scanned, placed, ct) is { } why) { r.Why = why; return; }
			r.Changes = placed.Changes;
			string? LeftOut(string folder) => FirstLeftOut(r.Root, folder, rules, scanned);
			// A folder placed a while ago (by the page) may have become a repository, or gone, since: judged as it is now.
			foreach (string folder in placed.Subtrees.Concat(placed.Dirty))
				if (LeftOut(folder) is { } notWalked) placed.LeftOut.Add(notWalked);

			// The last listing's folders whose files this listing replaces, and the files listed again.
			var comparer = StringComparer.OrdinalIgnoreCase;
			var relisted = new HashSet<string>(comparer);
			var listed = new List<FileInfo>();
			foreach (string folder in placed.LeftOut)
				relisted.UnionWith(listing.FoldersUnder(folder));
			foreach (string folder in placed.Emptied)
				relisted.UnionWith(listing.FoldersUnder(folder).Where(f => !Directory.Exists(f)));
			// A folder added inside another one added is listed with it.
			List<string> subtrees = placed.Subtrees.Where(s => LeftOut(s) == null).ToList();
			List<string> outermost = subtrees.Where(s => !subtrees.Any(o => !comparer.Equals(o, s) && IsUnder(s, o))).ToList();
			// As the engine walks a scanned folder: the exclusions judge each folder below it.
			var walkedOut = settings.BlackList.Concat(settings.SubfolderBlackList).ToList();
			foreach (string folder in outermost) {
				ct.ThrowIfCancellationRequested();
				List<FileInfo> now = Directory.Exists(folder) ? FileUtils.GetFilesRecursive(folder, settings.IgnoreReadOnlyFolders, settings.IgnoreReparsePoints,
					settings.IncludeSubDirectories, settings.IncludeImages, walkedOut, ct, settings.SkipCloudPlaceholders, settings.ExcludedExtensions,
					settings.SkipFoldersContaining, settings.SkipFolderLinks) : new();
				if (listing.SameBelow(folder, now)) continue;
				relisted.UnionWith(listing.FoldersUnder(folder));
				listed.AddRange(now);
			}
			// Each other folder with changes, listed again: is it still the same photos and videos?
			foreach (string folder in placed.Dirty) {
				ct.ThrowIfCancellationRequested();
				if (LeftOut(folder) != null || outermost.Any(s => IsUnder(folder, s))) continue;
				List<FileInfo> now = FileUtils.GetFilesRecursive(folder, settings.IgnoreReadOnlyFolders, settings.IgnoreReparsePoints, recursive: false,
					settings.IncludeImages, new List<string>(), ct, settings.SkipCloudPlaceholders, settings.ExcludedExtensions,
					settings.SkipFoldersContaining, settings.SkipFolderLinks);
				if (listing.Same(folder, now)) continue;
				relisted.Add(folder);
				listed.AddRange(now);
			}
			var changed = new HashSet<string>(relisted, comparer);
			foreach (FileInfo file in listed) changed.Add(file.DirectoryName ?? "");
			r.ChangedFolders = changed.Count;
			r.Mode = changed.Count == 0 ? ListingMode.Unchanged : ListingMode.Changed;
			r.Listing = new ScanEngine.RootListing(listed, listing.PathsOutside(relisted));
		}

		/// <summary>Why the journal's changes since <paramref name="since"/> couldn't be read: overwritten since, or Windows wouldn't read them.</summary>
		static string Unread(VolumeJournal journal, string since) => journal.ReadError == 0
			? $"the journal no longer reaches back to {since}"
			: $"the journal couldn't be read (Windows error {journal.ReadError})";

		/// <summary>
		/// Places each of <paramref name="records"/> in the folder of <paramref name="root"/> it happened in, as a scan
		/// walks it, into <paramref name="placed"/>: nothing is listed. Returns why the folder must be walked instead, or null.
		/// </summary>
		static string? Place(string root, List<JournalRecord> records, Volume volume, IReadOnlyList<ScanScope.Rule> rules,
			Dictionary<string, bool> scanned, Placed placed, CancellationToken ct) {
			string? LeftOut(string folder) => FirstLeftOut(root, folder, rules, scanned);
			// The journal names each folder by its real path (D:\Photos); the scan walks the folder as it's written (C:\Projects\Photos).
			string realRoot = RealPath.Of(root);
			placed.Changes += records.Count;
			// Most changes come in a few folders (an app's cache, a build's output): each folder is placed once.
			var spots = new Dictionary<FileId, (string? Where, bool Exact, string? NotWalked, bool Lost)>();
			foreach (JournalRecord record in records) {
				ct.ThrowIfCancellationRequested();
				if (!spots.TryGetValue(record.Parent, out var spot)) {
					// Where it happened: the folder, or for one deleted since, the nearest folder above it that's left.
					// Hidden: a folder this user can't open (Windows' own, another account's); outside the scanned
					// folder: elsewhere on the drive.
					(string? found, bool foundExact, bool hidden) = volume.Locate(record.Parent);
					string? at = hidden || found == null ? null : AsWalked(found, realRoot, root);
					spots[record.Parent] = spot = (at, foundExact, at == null ? null : LeftOut(at), !hidden && found == null);
				}
				if (spot.Lost) return "something changed in a folder that can't be found any more";
				if (spot.Where is not { } where) continue;
				bool exact = spot.Exact;
				// Windows, app data, a repository, an exclusion: not walked. One that has just become so (a
				// folder made a repository) takes what the last scan listed in it with it.
				if (spot.NotWalked is { } notWalked) {
					placed.LeftOut.Add(notWalked);
					continue;
				}
				if (record.IsFolder) {
					// A folder added, deleted, moved, or made hidden or a link where scans look: what's below it
					// changed, and nothing else. Where it is now is listed with what's below it.
					if ((record.Reason & FolderReshaped) == 0) continue;
					(string? now, bool nowExact, _) = volume.Locate(record.File);
					string? self = nowExact && now != null ? AsWalked(now, realRoot, root) : null;
					if (self != null) {
						if (LeftOut(self) is { } selfLeftOut) placed.LeftOut.Add(selfLeftOut);
						else placed.Subtrees.Add(self);
					}
					// Where it was: deleted, renamed or moved away (to elsewhere, or where scans don't look).
					if (self == null || (record.Reason & (FileDelete | RenameOldName)) != 0) placed.Emptied.Add(where);
					continue;
				}
				if (!exact) {
					placed.Emptied.Add(where); // files changed in a folder deleted since
					continue;
				}
				placed.Dirty.Add(where);
			}
			return null;
		}

		/// <summary>
		/// The review page's look at the journals between scans (<see cref="JournalKeeper"/>): each scanned folder's changes
		/// since the last scan (or look) are placed, and kept in listing\index.json with where the journal was read up to, so
		/// the next scan reads only what came after. A busy drive's journal can be overwritten within the hour between two
		/// scans, and a scan that finds it so walks the whole drive. Only a journal at risk is read: one a quarter full of
		/// changes since then (a quiet drive's waits for the scan). Nothing is listed, and nothing is written while a scan runs:
		/// a scan's own listing always wins. Returns what is worth a line in the log: a folder the next scan walks, and why.
		/// </summary>
		/// <param name="atRisk">How full of changes since the last scan or look a journal is when it's read: a quarter.</param>
		internal static List<string> Keep(AgentConfig cfg, DateTime nowUtc, Func<bool> scanRunning, CancellationToken ct, double atRisk = 0.25) {
			var kept = new List<string>();
			if (scanRunning()) return kept;
			Stored stored = Stored.Load();
			var rules = ScanScope.ExclusionRules(cfg);
			var scanned = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			var volumes = new Dictionary<string, Volume?>(StringComparer.OrdinalIgnoreCase);
			var updated = new Dictionary<string, (StoredRoot Was, StoredRoot Now)>(StringComparer.OrdinalIgnoreCase);
			try {
				foreach ((string root, StoredRoot last) in stored.Roots) {
					// No journal, a drive scanned only when asked (whose journal isn't touched), or one the next scan walks anyway.
					if (last.JournalId == null || cfg.IsOnRequest(root) || last.Pending?.Walk != null || nowUtc - last.FullWalkUtc > FullWalkEvery) continue;
					if (!Directory.Exists(root)) continue;
					string drive = RealPath.VolumeOf(root) ?? Path.GetPathRoot(root) ?? root;
					if (!volumes.TryGetValue(drive, out Volume? volume))
						volumes[drive] = volume = Volume.Open(drive);
					if (volume == null || volume.Journal.Id != last.JournalId) continue;
					// Not at risk yet: the next scan reads it, as it would without the page. Placed now, while it's a quarter
					// full of changes since the last scan or look, it isn't overwritten before the next look.
					long unread = volume.Journal.NextUsn - last.Usn;
					if (volume.Journal.MaximumSize > 0 && unread >= 0 && unread < volume.Journal.MaximumSize * atRisk) continue;
					Placed placed = Placed.From(last.Pending);
					string? walk = volume.RecordsFrom(last.Usn) is { } records
						? Place(root, records, volume, rules, scanned, placed, ct)
						: Unread(volume.Journal, "the last scan or the page's last look");
					// A read Windows refused may work next time: the next look, or scan, tries again from the same place.
					if (walk != null && volume.Journal.ReadError != 0) {
						kept.Add($"{root}: {walk}");
						continue;
					}
					if (walk == null && placed.Changes == (last.Pending?.Changes ?? 0)) continue; // nothing new
					updated[root] = (last, last with { Usn = volume.Journal.NextUsn, Pending = placed.ToStored(walk) });
					if (walk != null) kept.Add($"{root}: {walk}");
				}
			}
			finally {
				foreach (Volume? v in volumes.Values) v?.Dispose();
			}
			if (updated.Count == 0 || scanRunning()) return kept;
			// Only over what it read: a scan that saved since has the last word.
			Stored now = Stored.Load();
			foreach ((string root, (StoredRoot was, StoredRoot keep)) in updated)
				if (now.Roots.TryGetValue(root, out StoredRoot? current) && current.SameListing(was))
					now.Roots[root] = keep;
			now.Save();
			return kept;
		}

		/// <summary>
		/// <paramref name="real"/>, a folder by its real path as the journal names it, by the path a scan of <paramref name="root"/>
		/// (whose real path is <paramref name="realRoot"/>) reaches it by; null when it isn't in that folder.
		/// </summary>
		internal static string? AsWalked(string real, string realRoot, string root) {
			if (!IsUnder(real, realRoot)) return null;
			string rel = Path.GetRelativePath(realRoot, real);
			return rel == "." ? Path.TrimEndingDirectorySeparator(root) : Path.Combine(root, rel);
		}

		/// <summary>
		/// The first folder on the way from <paramref name="root"/> down to <paramref name="folder"/> inside it (the
		/// folder itself included, the root not) that a scan doesn't walk into, by the rules of the walk itself
		/// (<see cref="ScanScope.ExemptReason"/>); null when it gets there.
		/// </summary>
		internal static string? FirstLeftOut(string root, string folder, IReadOnlyList<ScanScope.Rule> rules, Dictionary<string, bool> scanned) {
			string rel = Path.GetRelativePath(root, folder);
			if (rel == ".") return null;
			string current = Path.TrimEndingDirectorySeparator(root);
			foreach (string part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
				current = Path.Combine(current, part);
				if (!scanned.TryGetValue(current, out bool walks))
					scanned[current] = walks = ScanScope.ExemptReason(new DirectoryInfo(current), rules) == null;
				if (!walks) return current;
			}
			return null;
		}

		/// <summary><paramref name="path"/> is <paramref name="folder"/> or inside it (a drive's root holds everything on the drive).</summary>
		internal static bool IsUnder(string path, string folder) {
			string f = Path.TrimEndingDirectorySeparator(folder), p = Path.TrimEndingDirectorySeparator(path);
			if (!p.StartsWith(f, StringComparison.OrdinalIgnoreCase)) return false;
			return p.Length == f.Length || Path.EndsInDirectorySeparator(f) || p[f.Length] == Path.DirectorySeparatorChar;
		}

		/// <summary>A folder as the engine names it (<see cref="ScanEngine"/> normalizes the include list): full, without a trailing separator.</summary>
		static string Normal(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

		/// <summary>
		/// After a scan the engine ran: each folder's listing (<see cref="ScanEngine.FoundFiles"/>), and where
		/// the journal was read up to, for the next scan.
		/// </summary>
		public void Save(IEnumerable<(string Path, long Size)> found, Func<string, DateTime?> modifiedUtc, DateTime nowUtc) {
			Stored stored = Stored.Load();
			var byRoot = Roots.ToDictionary(r => r, _ => new List<(string, long, DateTime)>());
			foreach ((string path, long size) in found) {
				RootPlan? owner = Roots.Where(r => IsUnder(path, r.Root) is true).MaxBy(r => r.Root.Length);
				if (owner != null) byRoot[owner].Add((path, size, modifiedUtc(path) ?? DateTime.MinValue));
			}
			foreach (RootPlan r in Roots) {
				if (r.Mode == ListingMode.Resting) continue; // not read: its last listing stands as it was
				// A drive without a journal keeps its listing too: should it be scanned only on request, a
				// scheduled scan still counts its files. It's walked again whenever it's scanned.
				string file = stored.Roots.TryGetValue(r.Root, out StoredRoot? last) ? last.File : StoredListing.NewFile();
				StoredListing.Save(file, byRoot[r]);
				stored.Roots[r.Root] = new StoredRoot(r.JournalId, r.Usn, listingKey, r.Mode == ListingMode.Walk ? nowUtc : r.FullWalkUtc, file);
			}
			stored.ScanKey = scanKey;
			stored.Save();
		}

		/// <summary>After a scan that didn't run: the journal was read this far, and nothing in it mattered.</summary>
		public void SaveSkipped() {
			Stored stored = Stored.Load();
			foreach (RootPlan r in Roots)
				if (r.JournalId != null && stored.Roots.TryGetValue(r.Root, out StoredRoot? last))
					stored.Roots[r.Root] = last with { Usn = r.Usn, Pending = null }; // what the page placed was read too
			stored.Save();
		}

		/// <summary>
		/// The settings that decide what a listing holds, and how Heiward lists (<see cref="ListingFormat"/>), but not
		/// the build: a listing stays good across builds that list the same way.
		/// </summary>
		internal static string ListingKey(Settings s, int format = ListingFormat) => Hash(ListingKeyText(s, format));

		/// <summary>What <see cref="ListingKey"/> hashes.</summary>
		internal static string ListingKeyText(Settings s, int format = ListingFormat) => string.Join("\n",
			"listing " + format, string.Join("|", s.BlackList.Order()), string.Join("|", s.SubfolderBlackList.Order()), string.Join("|", s.ExcludedExtensions.Order()),
			string.Join("|", s.SkipFoldersContaining.Order()), s.IgnoreReadOnlyFolders, s.IgnoreReparsePoints, s.SkipFolderLinks, s.SkipCloudPlaceholders,
			s.IncludeImages, s.IncludeSubDirectories);

		/// <summary>Everything that decides a report, but not how fast a scan runs.</summary>
		static string ScanKey(Settings s) {
			try {
				int parallelism = s.MaxDegreeOfParallelism;
				s.MaxDegreeOfParallelism = 0;
				try { return Hash(AppBuild.Current + "\n" + JsonSerializer.Serialize(s, new JsonSerializerOptions { IncludeFields = true })); }
				finally { s.MaxDegreeOfParallelism = parallelism; }
			}
			catch (Exception e) when (e is NotSupportedException or JsonException or InvalidOperationException) {
				return Guid.NewGuid().ToString("N"); // never the same: the scan runs
			}
		}

		static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

		/// <summary>One volume's journal and what this scan read from it, shared by the scanned folders on it.</summary>
		sealed class Volume : IDisposable {
			public required VolumeJournal Journal { get; init; }
			long readFrom = long.MaxValue;
			List<JournalRecord>? records;
			readonly Dictionary<FileId, (string? Path, bool Denied)> paths = new();
			/// <summary>Each folder record's folder: where a folder deleted since was.</summary>
			readonly Dictionary<FileId, FileId> parentOf = new();
			/// <summary>
			/// Folders the journal saw deleted: gone for good (an id isn't given again), so Windows isn't asked where they are.
			/// Most folders a busy hour's changes come in are a build's or an installer's, made and deleted within it.
			/// </summary>
			readonly HashSet<FileId> deleted = new();

			public required string Drive { get; init; }
			/// <summary>Where <see cref="ReadOn"/> has read the journal on to, past this scan's part: 0 before it has.</summary>
			long readOnTo;

			public static Volume? Open(string drive) => VolumeJournal.Open(drive) is { } journal ? new Volume { Journal = journal, Drive = drive } : null;

			/// <summary>
			/// Folders deleted or moved since this scan opened the journal: a folder emptied before then and
			/// removed just after has its files' records in this scan's part, and its own in the next part.
			/// Read on again from where it stopped each time a folder can't be found: on a busy drive, folders
			/// are deleted while the changes are placed.
			/// </summary>
			void ReadOn() {
				using VolumeJournal? now = VolumeJournal.Open(Drive);
				if (now == null || now.Id != Journal.Id) return;
				long from = Math.Max(readOnTo, Journal.NextUsn);
				if (from >= now.NextUsn) return;
				foreach (JournalRecord r in now.Read(from) ?? new())
					if (r.IsFolder) Saw(r, overwrite: false);
				readOnTo = now.NextUsn;
			}

			void Saw(JournalRecord folder, bool overwrite) {
				if (overwrite) parentOf[folder.File] = folder.Parent;
				else parentOf.TryAdd(folder.File, folder.Parent);
				if ((folder.Reason & FileDelete) != 0) deleted.Add(folder.File);
			}

			/// <summary>The changes since <paramref name="usn"/>, or null when the journal no longer has them all.</summary>
			public List<JournalRecord>? RecordsFrom(long usn) {
				if (records == null || usn < readFrom) {
					records = Journal.Read(usn);
					readFrom = usn;
					parentOf.Clear();
					deleted.Clear();
					readOnTo = 0;
					foreach (JournalRecord r in records ?? new())
						if (r.IsFolder) Saw(r, overwrite: true);
				}
				return records?.Where(r => r.Usn >= usn).ToList();
			}

			(string? Path, bool Denied) PathOf(FileId id) {
				if (!paths.TryGetValue(id, out var found)) {
					bool denied = false;
					string? path = deleted.Contains(id) ? null : Journal.PathOf(id, out denied);
					paths[id] = found = (path, denied);
				}
				return found;
			}

			/// <summary>
			/// Where a folder is: its path (exact), or for one deleted since, the nearest folder above it that
			/// still exists (not exact). <c>Hidden</c> for a folder this user may not open, which a scan can't
			/// walk into either; null when it can't be found at all.
			/// </summary>
			public (string? Path, bool Exact, bool Hidden) Locate(FileId folder) {
				var (path, denied) = PathOf(folder);
				if (path != null) return (path, true, false);
				if (denied) return (null, false, true);
				for (int attempt = 0; attempt < 2; attempt++) {
					var seen = new HashSet<FileId>();
					for (FileId id = folder; parentOf.TryGetValue(id, out FileId parent) && seen.Add(id); id = parent) {
						(string? above, bool aboveDenied) = PathOf(parent);
						if (above != null) return (above, false, false);
						if (aboveDenied) return (null, false, true);
					}
					if (attempt == 0) ReadOn(); // gone since the journal was opened: its own record comes after
				}
				return (null, false, false);
			}

			public void Dispose() => Journal.Dispose();
		}

		/// <param name="File">The listing's file in <see cref="StoredListing.Folder"/>.</param>
		/// <param name="JournalId">Null for a drive without a journal: its listing is kept, but it's walked every time it's scanned.</param>
		/// <param name="Pending">What the review page placed since the last scan (<see cref="Keep"/>), up to <paramref name="Usn"/>.</param>
		internal sealed record StoredRoot(ulong? JournalId, long Usn, string Key, DateTime FullWalkUtc, string File, PendingChanges? Pending = null) {
			/// <summary>The same listing, read up to the same place: no scan has saved since.</summary>
			public bool SameListing(StoredRoot o) => JournalId == o.JournalId && Usn == o.Usn && Key == o.Key && FullWalkUtc == o.FullWalkUtc && File == o.File;
		}

		/// <summary>
		/// What the review page placed between scans, for the next scan to list (folders as the scan walks them, as
		/// <see cref="Placed"/> holds them); <paramref name="Walk"/>, why the next scan walks instead.
		/// </summary>
		internal sealed record PendingChanges(List<string> Dirty, List<string> Subtrees, List<string> Emptied, List<string> LeftOut, string? Walk, int Changes);

		/// <summary>The changes the journal named where a scan looks, placed in their folders (<see cref="Place"/>) but not listed yet.</summary>
		sealed class Placed {
			static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
			/// <summary>Folders where files changed: each listed again by itself.</summary>
			public HashSet<string> Dirty { get; } = new(Paths);
			/// <summary>Folders added, moved in or made visible: each listed with what's below it.</summary>
			public HashSet<string> Subtrees { get; } = new(Paths);
			/// <summary>Folders below which one was deleted or moved away: the last listing's folders below them that are gone go.</summary>
			public HashSet<string> Emptied { get; } = new(Paths);
			/// <summary>The first folder on the way that scans don't walk into, for each change in one: what the last listing held in it goes.</summary>
			public HashSet<string> LeftOut { get; } = new(Paths);
			/// <summary>The journal's records placed since the last scan, the page's and the scan's own.</summary>
			public int Changes { get; set; }

			public static Placed From(PendingChanges? kept) {
				var placed = new Placed { Changes = kept?.Changes ?? 0 };
				if (kept == null) return placed;
				placed.Dirty.UnionWith(kept.Dirty ?? new());
				placed.Subtrees.UnionWith(kept.Subtrees ?? new());
				placed.Emptied.UnionWith(kept.Emptied ?? new());
				placed.LeftOut.UnionWith(kept.LeftOut ?? new());
				return placed;
			}

			public PendingChanges ToStored(string? walk) =>
				new(Dirty.Order(Paths).ToList(), Subtrees.Order(Paths).ToList(), Emptied.Order(Paths).ToList(), LeftOut.Order(Paths).ToList(), walk, Changes);
		}

		/// <summary>Where each scanned folder's journal was read up to, and its listing's file (listing\index.json).</summary>
		sealed class Stored {
			public Dictionary<string, StoredRoot> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
			public string ScanKey { get; set; } = "";

			static string FilePath => Path.Combine(StoredListing.Folder, "index.json");

			public static Stored Load() {
				try {
					if (File.Exists(FilePath) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath), AgentConfig.Json) is { } s) {
						s.Roots = new Dictionary<string, StoredRoot>(s.Roots, StringComparer.OrdinalIgnoreCase);
						return s;
					}
				}
				catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
				return new Stored();
			}

			public void Save() {
				Directory.CreateDirectory(StoredListing.Folder);
				AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(this, AgentConfig.Json));
			}
		}
	}

	/// <summary>
	/// The review page's clock for <see cref="ListingPlan.Keep"/>: every ten minutes while it's up. C:\'s journal holds
	/// 32 MB, which a developer's PC can write in about 80 minutes on a busy afternoon. Scans an hour apart found it
	/// overwritten five times in one day, and walked the whole drive each time. Read every ten minutes, the journal holds
	/// everything since the last look.
	/// </summary>
	static class JournalKeeper {
		internal static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

		public static async Task RunAsync(Func<bool> scanRunning, CancellationToken ct) {
			string lastLogged = "";
			try {
				while (!ct.IsCancellationRequested) {
					await Task.Delay(Every, ct);
					List<string> notes;
					try {
						notes = await Task.Factory.StartNew(() => {
							ThreadPriority was = Thread.CurrentThread.Priority;
							Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
							try { return ListingPlan.Keep(AgentConfig.Load(), DateTime.UtcNow, scanRunning, ct); }
							finally { Thread.CurrentThread.Priority = was; }
						}, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
					}
					catch (Exception e) when (e is not OperationCanceledException) {
						notes = new() { "not read: " + e.Message };
					}
					// Once each: a read Windows refuses is tried again every ten minutes.
					string line = string.Join("; ", notes);
					if (line.Length > 0 && line != lastLogged) AgentPaths.AppendLog("change journal, between scans: " + line);
					lastLogged = line;
				}
			}
			catch (OperationCanceledException) { /* the page is closing */ }
		}
	}

	/// <summary>
	/// The photos and videos the last scan listed in one scanned folder, with each file's size and
	/// last-modified time: a folder listed again is compared with it, and an unchanged one's files are
	/// handed to the engine from it.
	/// </summary>
	sealed class StoredListing {
		public static string Folder => Path.Combine(AgentPaths.Home, "listing");

		readonly Dictionary<string, List<(string Path, long Size, DateTime ModifiedUtc)>> byFolder = new(StringComparer.OrdinalIgnoreCase);

		public static string NewFile() => Guid.NewGuid().ToString("N")[..12] + ".tsv";

		public static StoredListing? Load(string file) {
			string path = Path.Combine(Folder, file);
			try {
				if (!File.Exists(path)) return null;
				var listing = new StoredListing();
				foreach (string line in File.ReadLines(path)) {
					string[] parts = line.Split('\t', 3);
					if (parts.Length != 3 || !long.TryParse(parts[0], out long size) || !long.TryParse(parts[1], out long ticks)) return null;
					string folder = System.IO.Path.GetDirectoryName(parts[2]) ?? "";
					if (!listing.byFolder.TryGetValue(folder, out var files)) listing.byFolder[folder] = files = new();
					files.Add((parts[2], size, new DateTime(ticks, DateTimeKind.Utc)));
				}
				return listing;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return null;
			}
		}

		public static void Save(string file, IEnumerable<(string Path, long Size, DateTime ModifiedUtc)> files) {
			Directory.CreateDirectory(Folder);
			var text = new StringBuilder();
			foreach (var (path, size, modified) in files)
				text.Append(size).Append('\t').Append(modified.Ticks).Append('\t').Append(path).Append('\n');
			AgentPaths.WriteAtomic(Path.Combine(Folder, file), text.ToString());
		}

		/// <summary>The folder holds the same photos and videos as last time: names, sizes and last-modified times.</summary>
		public bool Same(string folder, List<FileInfo> now) {
			byFolder.TryGetValue(Path.TrimEndingDirectorySeparator(folder), out var before);
			return Same(before ?? new(), now);
		}

		/// <summary>The folder and the folders below it hold the same photos and videos as last time.</summary>
		public bool SameBelow(string folder, List<FileInfo> now) =>
			Same(FoldersUnder(folder).SelectMany(f => byFolder[f]).ToList(), now);

		/// <summary>The folders listed last time that are <paramref name="folder"/> or below it.</summary>
		public List<string> FoldersUnder(string folder) => byFolder.Keys.Where(f => ListingPlan.IsUnder(f, folder)).ToList();

		static bool Same(List<(string Path, long Size, DateTime ModifiedUtc)> before, List<FileInfo> now) {
			if (before.Count != now.Count) return false;
			var then = before.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
			foreach (FileInfo file in now)
				if (!then.TryGetValue(file.FullName, out var f) || f.Size != file.Length || f.ModifiedUtc != file.LastWriteTimeUtc)
					return false;
			return true;
		}

		/// <summary>Every file listed last time, but for those in the folders listed again.</summary>
		public List<string> PathsOutside(IReadOnlySet<string> relisted) =>
			byFolder.Where(f => !relisted.Contains(f.Key)).SelectMany(f => f.Value.Select(x => x.Path)).ToList();
	}
}
