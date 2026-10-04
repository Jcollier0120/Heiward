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
	/// listing: a document saved in Documents changes nothing a scan sees. When no drive has a change a
	/// scan would see, a scheduled scan doesn't run at all, and a sleeping hard disk stays asleep.
	///
	/// A drive is walked as before when its journal can't vouch for the last listing: the first scan, no
	/// journal (FAT, exFAT, network drives), a journal made again or overwritten past the last scan
	/// (the PC was off a long time, or the drive very busy), settings changed or Heiward lists differently
	/// (<see cref="ListingPlan.ListingFormat"/>; a new build alone doesn't), folders
	/// were added, moved or deleted where scans look, and once a week regardless.
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
		internal const int ListingFormat = 1;

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
					if (cfg.IsOnRequest(root) && !(scanNow ?? Array.Empty<string>()).Any(d => string.Equals(AgentConfig.DriveOf(d), AgentConfig.DriveOf(root), StringComparison.OrdinalIgnoreCase))) {
						r.Mode = ListingMode.Resting;
						List<string> known = last != null && StoredListing.Load(last.File) is { } kept ? kept.PathsOutside(new HashSet<string>()) : new();
						r.Listing = new ScanEngine.RootListing(Array.Empty<FileInfo>(), known);
						continue;
					}
					string drive = Path.GetPathRoot(root) ?? root;
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
			if (volume.RecordsFrom(last.Usn) is not { } records) { r.Why = "the journal no longer reaches back to the last scan"; return; }
			if (StoredListing.Load(last.File) is not { } listing) { r.Why = "the last listing is missing"; return; }

			// The scanned folder renamed, moved or gone (or one above it): the last listing's paths are no more.
			if (!Directory.Exists(r.Root)) { r.Why = "the folder isn't there"; return; }

			bool Scanned(string folder) => IsScanned(r.Root, folder, rules, scanned);
			var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			r.Changes = records.Count;
			foreach (JournalRecord record in records) {
				ct.ThrowIfCancellationRequested();
				// Where it happened: the folder, or for one deleted since, the nearest folder above it that's left.
				(string? where, bool exact, bool hidden) = volume.Locate(record.Parent);
				if (hidden) continue; // a folder this user can't open: Windows' own, another account's
				if (where == null) {
					r.Why = "something changed in a folder that can't be found any more";
					return;
				}
				if (!Scanned(where)) continue; // Windows, app data, a repository, an exclusion, another drive's folder
				if (record.IsFolder) {
					// A folder added, deleted, moved, or made hidden or a link where scans look: what's below it changed.
					if ((record.Reason & FolderReshaped) == 0) continue;
					r.Why = $"folders were added, moved or deleted in {where}";
					return;
				}
				if (!exact) {
					r.Why = $"files changed in a folder deleted from {where}";
					return;
				}
				dirty.Add(where);
			}

			// Each folder with changes, listed again: is it still the same photos and videos?
			var listed = new List<FileInfo>();
			var relisted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string folder in dirty) {
				ct.ThrowIfCancellationRequested();
				List<FileInfo> now = FileUtils.GetFilesRecursive(folder, settings.IgnoreReadOnlyFolders, settings.IgnoreReparsePoints, recursive: false,
					settings.IncludeImages, new List<string>(), ct, settings.SkipCloudPlaceholders, settings.ExcludedExtensions,
					settings.SkipFoldersContaining, settings.SkipFolderLinks);
				if (listing.Same(folder, now)) continue;
				relisted.Add(folder);
				listed.AddRange(now);
			}
			r.ChangedFolders = relisted.Count;
			r.Mode = relisted.Count == 0 ? ListingMode.Unchanged : ListingMode.Changed;
			r.Listing = new ScanEngine.RootListing(listed, listing.PathsOutside(relisted));
		}

		/// <summary>A scan of <paramref name="root"/> walks into <paramref name="folder"/>, by the rules of the walk itself (<see cref="ScanScope.ExemptReason"/>).</summary>
		internal static bool IsScanned(string root, string folder, IReadOnlyList<ScanScope.Rule> rules, Dictionary<string, bool> scanned) {
			if (!IsUnder(folder, root)) return false;
			string rel = Path.GetRelativePath(root, folder);
			if (rel == ".") return true;
			string current = Path.TrimEndingDirectorySeparator(root);
			foreach (string part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
				current = Path.Combine(current, part);
				if (!scanned.TryGetValue(current, out bool walks))
					scanned[current] = walks = ScanScope.ExemptReason(new DirectoryInfo(current), rules) == null;
				if (!walks) return false;
			}
			return true;
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
					stored.Roots[r.Root] = last with { Usn = r.Usn };
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

			public required string Drive { get; init; }
			bool readOn;

			public static Volume? Open(string drive) => VolumeJournal.Open(drive) is { } journal ? new Volume { Journal = journal, Drive = drive } : null;

			/// <summary>
			/// Folders deleted or moved since this scan opened the journal: a folder emptied before then and
			/// removed just after has its files' records in this scan's part, and its own in the next part.
			/// </summary>
			void ReadOn() {
				if (readOn) return;
				readOn = true;
				using VolumeJournal? now = VolumeJournal.Open(Drive);
				if (now == null || now.Id != Journal.Id) return;
				foreach (JournalRecord r in now.Read(Journal.NextUsn) ?? new())
					if (r.IsFolder) parentOf.TryAdd(r.File, r.Parent);
			}

			/// <summary>The changes since <paramref name="usn"/>, or null when the journal no longer has them all.</summary>
			public List<JournalRecord>? RecordsFrom(long usn) {
				if (records == null || usn < readFrom) {
					records = Journal.Read(usn);
					readFrom = usn;
					parentOf.Clear();
					readOn = false;
					foreach (JournalRecord r in records ?? new())
						if (r.IsFolder) parentOf[r.File] = r.Parent;
				}
				return records?.Where(r => r.Usn >= usn).ToList();
			}

			(string? Path, bool Denied) PathOf(FileId id) {
				if (!paths.TryGetValue(id, out var found)) {
					string? path = Journal.PathOf(id, out bool denied);
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
					ReadOn(); // gone since the journal was opened: its own record comes after
				}
				return (null, false, false);
			}

			public void Dispose() => Journal.Dispose();
		}

		/// <param name="File">The listing's file in <see cref="StoredListing.Folder"/>.</param>
		/// <param name="JournalId">Null for a drive without a journal: its listing is kept, but it's walked every time it's scanned.</param>
		internal sealed record StoredRoot(ulong? JournalId, long Usn, string Key, DateTime FullWalkUtc, string File);

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
			before ??= new();
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
