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
using HEI.Core.AI;
using HEI.Core.Utils;
using HEI.Core.ViewModels;

namespace HEI.Agent {
	/// <summary>One file in a group, as the review page shows it.</summary>
	/// <param name="Relation">
	/// How the file relates to the kept one: <c>keep</c>, <c>identical</c>, <c>smaller</c> (lower
	/// resolution), <c>compressed</c> (same picture, fewer bytes), <c>resaved</c> (same picture saved
	/// again), <c>edited</c> (the AI sees the same picture with changes: colour, filter),
	/// <c>variant</c> (cropped, mirrored, or a different shot that looks alike).
	/// </param>
	/// <param name="Suggested">Pre-ticked for the Recycle Bin: identical files and pixel-level copies only.</param>
	/// <param name="Synced">In a cloud-synced folder: deleting it also deletes it from the cloud and other devices.</param>
	sealed record ReportItem(
		string Path, string Name, string Folder, long Size, int Width, int Height, string? Format,
		double DurationSec, decimal BitrateKbs, float Fps, DateTime ModifiedUtc, float Similarity, bool AiMatched,
		string Relation, bool Keep, bool Suggested, bool Synced = false);

	/// <summary>
	/// A set of files that look like copies of each other. <see cref="Kind"/>: <c>identical</c> (all
	/// byte-for-byte the same), <c>copies</c> (some files are plain copies of the kept one and are
	/// pre-ticked), <c>similar</c> (only edited versions and look-alikes: nothing is pre-ticked).
	/// </summary>
	sealed record ReportGroup(string Key, string Kind, string Media, string KeepPath, string KeepReason,
		long ReclaimBytes, float MinSimilarity, List<ReportItem> Items);

	sealed record Report(int Version, DateTime ScannedAtUtc, double DurationSec, string Device, int FilesScanned,
		List<string> Folders, List<string> ExcludedExtensions, List<string> Notes, List<ReportGroup> Groups) {
		public const int CurrentVersion = 1;

		public static Report? Load() {
			try {
				return File.Exists(AgentPaths.Report)
					? JsonSerializer.Deserialize<Report>(File.ReadAllText(AgentPaths.Report), AgentConfig.Json)
					: null;
			}
			catch (Exception e) {
				AgentPaths.AppendLog($"report.json unreadable: {e.Message}");
				return null;
			}
		}

		public void Save() => AgentPaths.WriteAtomic(AgentPaths.Report, JsonSerializer.Serialize(this, AgentConfig.Json));
	}

	/// <summary>How alike two scanned files are, from their stored fingerprints; null when unknown.</summary>
	interface IFingerprints {
		/// <summary>VDF's classic similarity (percent) of the two files' gray frames, averaged over common positions.</summary>
		float? GrayPercent(string a, string b);
		/// <summary>Cosine (percent) of the two files' AI embeddings, averaged over common positions.</summary>
		float? AiPercent(string a, string b);
	}

	/// <summary>The fingerprints of the scan that just ran: its database's gray frames and its embedding cache.</summary>
	sealed class ScanFingerprints : IFingerprints {
		readonly Dictionary<string, FileEntry> entries;
		readonly UnionEmbeddingStore? embeddings;

		public ScanFingerprints(string? embeddingCacheKey, bool withAi) {
			entries = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
			foreach (FileEntry e in DatabaseUtils.Database) entries[e.Path] = e;
			embeddings = withAi ? UnionEmbeddingStore.Load(embeddingCacheKey) : null;
		}

		public float? GrayPercent(string a, string b) => Average(a, b, e => e.grayBytes,
			(x, y) => x.Length == y.Length ? 100f * (1f - GrayBytesUtils.PercentageDifference(x, y)) : null);

		public float? AiPercent(string a, string b) {
			if (embeddings == null || !entries.TryGetValue(a, out FileEntry? ea) || !entries.TryGetValue(b, out FileEntry? eb)) return null;
			var keys = ea.grayBytes.Keys.Intersect(eb.grayBytes.Keys).DefaultIfEmpty(0d);
			var sims = keys.Select(k => (x: embeddings.GetEmbedding(ea, k), y: embeddings.GetEmbedding(eb, k)))
				.Where(p => p.x != null && p.y != null).Select(p => 100f * EmbeddingMath.CosineSimilarity(p.x!, p.y!)).ToList();
			return sims.Count > 0 ? sims.Average() : null;
		}

		float? Average(string a, string b, Func<FileEntry, Dictionary<double, byte[]?>> frames, Func<byte[], byte[], float?> compare) {
			if (!entries.TryGetValue(a, out FileEntry? ea) || !entries.TryGetValue(b, out FileEntry? eb)) return null;
			var sims = frames(ea).Where(kv => kv.Value != null && frames(eb).TryGetValue(kv.Key, out byte[]? v) && v != null)
				.Select(kv => compare(kv.Value!, frames(eb)[kv.Key]!)).Where(v => v != null).Select(v => v!.Value).ToList();
			return sims.Count > 0 ? sims.Average() : null;
		}
	}

	/// <summary>
	/// Turns the engine's duplicate groups into the review report. Code decides everything here —
	/// which file to keep and why, and what each other file is to it — and only plain copies are
	/// pre-ticked. What the AI alone matched (an edit, a crop, a look-alike shot) is labelled for the
	/// user to judge, never pre-ticked: a model's "these look the same" is not a reason to delete.
	/// </summary>
	static class ReportBuilder {
		/// <summary>AI matches at or above this cosine (percent) are the same picture edited; below it, crops and look-alikes.</summary>
		internal const float SamePictureAiPercent = 97f;
		/// <summary>
		/// A classic (grayscale) match at or above this is the same picture, pixel for pixel: a plain copy
		/// that may be pre-ticked. Calibrated on photos; videos should get their own value once measured.
		/// </summary>
		internal const float PlainCopyPercent = 99.5f;
		/// <summary>
		/// A file less alike than this to the kept one (the percentage the page shows) is a different
		/// picture, not a look-alike: it leaves the set. VDF's groups chain (A is like B, B is like C), so
		/// without this a set could hold a picture that looks nothing like the one kept.
		/// </summary>
		internal const float MinAlikePercent = 75f;

		public static List<ReportGroup> Build(IEnumerable<DuplicateItem> duplicates, IFingerprints fingerprints) {
			var hashes = new ContentHashes();
			var bursts = new BurstSeries();
			List<List<DuplicateItem>> members = duplicates.GroupBy(d => d.GroupId)
				.Select(g => g.Where(d => File.Exists(d.Path)).ToList())
				.ToList();
			GatherSplitCopies(members, hashes, fingerprints);
			return members
				.SelectMany(items => SplitByPicture(items, hashes, fingerprints, bursts))
				.SelectMany(items => LeaveOutUnrelated(items, hashes, fingerprints, bursts))
				.Where(items => items.Count >= 2)
				.Select(items => BuildGroup(items, hashes, fingerprints, bursts))
				.OrderBy(g => g.Kind == "similar" ? 1 : 0)
				.ThenByDescending(g => g.ReclaimBytes)
				.ToList();
		}

		/// <summary>
		/// VDF puts each file in one group, so byte-identical files can end up apart: a burst shot's
		/// original grouped with its neighbour as a look-alike, and its copy elsewhere grouped with an
		/// export of it, kept there as the best file. Each set of identical files moves into the group
		/// holding its closest other match (a group of nothing but those copies counts as closest), and
		/// a group left with one file is dropped.
		/// </summary>
		static void GatherSplitCopies(List<List<DuplicateItem>> groups, ContentHashes hashes, IFingerprints fingerprints) {
			var splitBySize = groups
				.SelectMany((items, g) => items.Select(item => (Item: item, Group: g)))
				.GroupBy(x => x.Item.SizeLong)
				.Where(same => same.Select(x => x.Group).Distinct().Count() > 1) // hash only what could be split
				.ToList();
			foreach (var sameSize in splitBySize)
				foreach (var identical in sameSize.GroupBy(x => hashes.Get(x.Item.Path)).Where(h => h.Key != null)) {
					List<int> apart = identical.Select(x => x.Group).Distinct().ToList();
					if (apart.Count < 2)
						continue;
					var copies = identical.Select(x => x.Item).ToHashSet();
					int target = apart.MaxBy(g => Closeness(groups[g], copies, fingerprints));
					foreach (int g in apart.Where(g => g != target))
						groups[g].RemoveAll(copies.Contains);
					groups[target].AddRange(copies.Where(c => !groups[target].Contains(c)));
				}
		}

		/// <summary>How close the group's other files are to the copies in it (100 when there are none).</summary>
		static float Closeness(List<DuplicateItem> group, HashSet<DuplicateItem> copies, IFingerprints fingerprints) {
			var others = group.Where(i => !copies.Contains(i)).ToList();
			if (others.Count == 0)
				return 100f;
			var here = group.Where(copies.Contains).ToList();
			return others.SelectMany(o => here.Select(c => fingerprints.GrayPercent(o.Path, c.Path) ?? 0f)).Max();
		}

		/// <summary>
		/// A VDF group can hold more than one set of copies: two look-alike burst shots, each imported
		/// twice. Judged against a single kept file, the second shot's copy is only a look-alike and
		/// isn't ticked. Such a group becomes one group per picture, each with its own kept file; files
		/// that copy none of the others (look-alikes) leave the report.
		/// </summary>
		static IEnumerable<List<DuplicateItem>> SplitByPicture(List<DuplicateItem> items, ContentHashes hashes, IFingerprints fingerprints, BurstSeries bursts) {
			if (items.Count < 4) { // two sets of copies take four files
				yield return items;
				yield break;
			}
			var sets = new List<List<DuplicateItem>>();
			var rest = new List<DuplicateItem>(items);
			while (rest.Count > 0) {
				DuplicateItem keep = rest.Count == 1 ? rest[0] : PickKeeper(rest).Item1;
				var set = rest.Where(i => ReferenceEquals(i, keep) || IsPlainCopy(Relation(i, keep, hashes, fingerprints, bursts))).ToList();
				sets.Add(set);
				rest.RemoveAll(set.Contains);
			}
			var copies = sets.Where(s => s.Count >= 2).ToList();
			if (copies.Count < 2) {
				yield return items;
				yield break;
			}
			foreach (var set in copies)
				yield return set;
		}

		/// <summary>
		/// What stays with each kept file: its copies, edits and look-alikes at <see cref="MinAlikePercent"/>
		/// or more, with one shot of each burst (<see cref="BurstSeries"/>) at most: the kept file, or else
		/// the shot most like it. The burst's other shots leave, however alike, as do less alike pictures.
		/// Without that, IMG_0569 and IMG_0570 both stayed as look-alikes of an older IMG_0538, and a
		/// burst shot scoring 99.9% against a renamed copy of its neighbour was ticked as a resaved copy.
		/// What's left over is judged again around its own kept file, so a burst shot's copy in a backup
		/// folder still finds its original; a shot whose burst a set already shows goes only with its own
		/// copies, not into a second set of the same look-alikes. A set of one is no set.
		/// </summary>
		static IEnumerable<List<DuplicateItem>> LeaveOutUnrelated(List<DuplicateItem> items, ContentHashes hashes, IFingerprints fingerprints, BurstSeries bursts) {
			var rest = new List<DuplicateItem>(items);
			var shown = new HashSet<DuplicateItem>(); // shots of a burst that a set already shows
			while (rest.Count >= 2) {
				DuplicateItem keep = PickKeeper(rest).Item1;
				var set = new List<DuplicateItem> { keep };
				var burstMates = new List<DuplicateItem>();
				// The closest first: of two shots of one burst, the one more like the kept file stays.
				var candidates = rest.Where(i => !ReferenceEquals(i, keep))
					.Select(i => (Item: i, Relation: Relation(i, keep, hashes, fingerprints, bursts)))
					.Select(c => (c.Item, c.Relation, Alike(c.Item, keep, c.Relation, fingerprints).Percent))
					.OrderByDescending(c => c.Relation == "identical").ThenByDescending(c => IsPlainCopy(c.Relation)).ThenByDescending(c => c.Percent)
					.ToList();
				foreach (var (i, relation, percent) in candidates) {
					if (relation == "identical") {
						set.Add(i);
						continue;
					}
					if (relation == "burst" || set.Skip(1).Any(s => bursts.AreSiblings(s.Path, i.Path) && !(s.SizeLong == i.SizeLong && hashes.Same(s.Path, i.Path)))) {
						burstMates.Add(i);
						continue;
					}
					if (percent < MinAlikePercent || (!IsPlainCopy(relation) && (shown.Contains(keep) || shown.Contains(i))))
						continue;
					set.Add(i);
				}
				rest.RemoveAll(set.Contains);
				if (set.Count >= 2) {
					shown.UnionWith(burstMates);
					yield return set;
				}
			}
		}

		/// <summary>
		/// How alike the file is to the kept one, as the page shows it: the AI's cosine where the pixels
		/// differ (an edit or a variant), otherwise the grayscale match.
		/// </summary>
		static (float Percent, bool ByAi) Alike(DuplicateItem i, DuplicateItem keep, string relation, IFingerprints fingerprints) {
			if (ReferenceEquals(i, keep)) return (100f, false);
			float? ai = relation is "edited" or "variant" ? fingerprints.AiPercent(i.Path, keep.Path) : null;
			return ai != null ? (ai.Value, true) : (fingerprints.GrayPercent(i.Path, keep.Path) ?? i.Similarity, false);
		}

		static (DuplicateItem, string) PickKeeper(List<DuplicateItem> items) => items[0].IsImage ? PickPhotoKeeper(items) : PickVideoKeeper(items);

		/// <summary>The relations that are the same picture, pixel for pixel: the ones pre-ticked.</summary>
		static bool IsPlainCopy(string relation) => relation is "identical" or "smaller" or "compressed" or "resaved";

		static ReportGroup BuildGroup(List<DuplicateItem> items, ContentHashes hashes, IFingerprints fingerprints, BurstSeries bursts) {
			bool isImage = items[0].IsImage;
			(DuplicateItem keep, string reason) = PickKeeper(items);

			var reportItems = new List<ReportItem>(items.Count);
			foreach (DuplicateItem i in items.OrderByDescending(i => ReferenceEquals(i, keep)).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)) {
				string relation = ReferenceEquals(i, keep) ? "keep" : Relation(i, keep, hashes, fingerprints, bursts);
				(int w, int h) = ParseFrameSize(i.FrameSize);
				bool suggested = IsPlainCopy(relation);
				// Shown similarity: to the kept file (the AI's cosine where the pixels differ).
				(float shown, bool byAi) = Alike(i, keep, relation, fingerprints);
				reportItems.Add(new ReportItem(i.Path, Path.GetFileName(i.Path), Path.GetDirectoryName(i.Path) ?? "", i.SizeLong, w, h, i.Format,
					i.Duration.TotalSeconds, i.BitRateKbs, i.Fps, i.DateModified.ToUniversalTime(), shown, byAi,
					relation, relation == "keep", suggested, CloudFiles.IsSynced(i.Path)));
			}
			string kind = reportItems.All(i => i.Relation is "keep" or "identical") ? "identical"
				: reportItems.Any(i => i.Suggested) ? "copies" : "similar";
			if (kind == "identical")
				reason = "identical files; kept the one with the original-looking name and folder";
			else if (kind == "similar")
				reason = "your pick: these are different shots or edits (suggested: " +
					(reason.StartsWith("highest resolution", StringComparison.Ordinal) ? "the highest resolution" : "the first one taken") + ")";
			return new ReportGroup(GroupKey(items.Select(i => i.Path)), kind, isImage ? "image" : "video", keep.Path, reason,
				reportItems.Where(i => i.Suggested).Sum(i => i.Size), items.Min(i => i.Similarity), reportItems);
		}

		/// <summary>
		/// What <paramref name="i"/> is to the kept file, from the two files' own fingerprints (VDF's
		/// per-member similarity is to whichever member pulled it into the group, so it says nothing
		/// about the keeper). A grayscale match at or above <see cref="PlainCopyPercent"/> is the same
		/// picture pixel for pixel. Measured on the test set with RGB-derived gray frames: resized
		/// 99.91–99.98%, recompressed 99.79–99.97%, while crops scored 97.35–97.66%, colour edits
		/// 97.12–97.99%, flips 96.16–97.32% and two different shots 94.3%. Below that, the AI's
		/// cosine says "same picture, edited" (≥ <see cref="SamePictureAiPercent"/>) or "variant".
		/// <c>burst</c>: another shot of the kept file's burst, which is no duplicate however alike.
		/// </summary>
		static string Relation(DuplicateItem i, DuplicateItem keep, ContentHashes hashes, IFingerprints fingerprints, BurstSeries bursts) {
			if (i.SizeLong == keep.SizeLong && hashes.Same(i.Path, keep.Path))
				return "identical";
			// Named as a photo and its edit (Google Photos' "-edited", Samsung's and Picasa's
			// "_Original"): however light the edit (one scored 99.68%), both stay; the user decides.
			if (IsOriginalAndEdit(i.Path, keep.Path))
				return "edited";
			// IMG_1234 next to IMG_1235: a burst or a retake. Burst shots match 99% and more, which
			// below would pass for a resaved copy and be ticked.
			if (bursts.AreSiblings(i.Path, keep.Path))
				return "burst";
			// An animated picture is compared by its first frame only: two GIFs that start alike, or a
			// still taken from one, are not copies of each other.
			if (MayBeAnimated(i.Path) || MayBeAnimated(keep.Path))
				return "variant";
			if (fingerprints.GrayPercent(i.Path, keep.Path) is float gray && gray >= PlainCopyPercent) {
				if (i.FrameSizeInt > 0 && i.FrameSizeInt < keep.FrameSizeInt)
					return "smaller";
				if (!i.IsImage && Math.Round(i.Duration.TotalSeconds) < Math.Round(keep.Duration.TotalSeconds))
					return "variant"; // a shorter cut of the video is not a plain copy
				return i.SizeLong < keep.SizeLong ? "compressed" : "resaved";
			}
			return fingerprints.AiPercent(i.Path, keep.Path) >= SamePictureAiPercent ? "edited" : "variant";
		}

		static readonly string[] EditSuffixes = { "_original", "-original", " (original)", "-edited", "_edited", " (edited)" };

		/// <summary>One name is the other's plus an original/edit suffix: IMG_1.jpg and IMG_1_Original.jpg.</summary>
		internal static bool IsOriginalAndEdit(string a, string b) {
			string sa = Path.GetFileNameWithoutExtension(a), sb = Path.GetFileNameWithoutExtension(b);
			return EditSuffixes.Any(s => sa.Equals(sb + s, StringComparison.OrdinalIgnoreCase) || sb.Equals(sa + s, StringComparison.OrdinalIgnoreCase));
		}

		static bool MayBeAnimated(string path) =>
			Path.GetExtension(path).ToLowerInvariant() is ".gif" or ".webp" or ".apng" or ".heics";

		/// <summary>Stable across scans for the same set of files, so a "keep all" answer sticks.</summary>
		public static string GroupKey(IEnumerable<string> paths) {
			string joined = string.Join("\n", paths.Select(p => p.ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal));
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
		}

		/// <summary>
		/// Photos: the highest resolution; then the camera original (has an EXIF capture date; copies
		/// made by apps and messengers often lose it); then the oldest file (a copy is newer than what it
		/// copied); then the largest; then the original-looking name. File size alone is not quality: an
		/// edit that boosts colour makes a bigger JPEG than the untouched original.
		/// </summary>
		static (DuplicateItem, string) PickPhotoKeeper(List<DuplicateItem> items) {
			int best = items.Max(i => i.FrameSizeInt);
			var top = items.Where(i => i.FrameSizeInt == best).ToList();
			if (top.Count == 1)
				return (top[0], $"highest resolution ({top[0].FrameSize?.Replace("x", " × ")})");
			var withExif = top.Where(i => ExifReader.TryGetDateTaken(i.Path, out _)).ToList();
			if (withExif.Count == 1)
				return (withExif[0], "the camera original (the only copy with its capture date)");
			if (withExif.Count > 1) top = withExif;
			DateTime oldest = top.Min(i => i.DateModified);
			var first = top.Where(i => i.DateModified - oldest < TimeSpan.FromSeconds(2)).ToList();
			if (first.Count == 1)
				return (first[0], "the original: same resolution, and the oldest file");
			long biggest = first.Max(i => i.SizeLong);
			var largest = first.Where(i => i.SizeLong == biggest).ToList();
			if (largest.Count == 1)
				return (largest[0], "same resolution and age; least compressed (largest file)");
			return (PreferOriginalLooking(largest), "same picture and size; kept the original-looking name and folder");
		}

		/// <summary>Videos: VDF's own default order (duration, resolution, bitrate, fps), then the oldest, then the smaller file.</summary>
		static (DuplicateItem, string) PickVideoKeeper(List<DuplicateItem> items) {
			var ordered = items
				.OrderByDescending(i => Math.Round(i.Duration.TotalSeconds))
				.ThenByDescending(i => i.FrameSizeInt)
				.ThenByDescending(i => i.BitRateKbs)
				.ThenByDescending(i => i.Fps)
				.ThenBy(i => i.DateModified)
				.ThenBy(i => i.SizeLong)
				.ToList();
			DuplicateItem keep = ordered[0], next = ordered[1];
			string reason =
				Math.Round(keep.Duration.TotalSeconds) > Math.Round(next.Duration.TotalSeconds) ? "longest (the others are shorter cuts)" :
				keep.FrameSizeInt > next.FrameSizeInt ? $"highest resolution ({keep.FrameSize?.Replace("x", " × ")})" :
				keep.BitRateKbs > next.BitRateKbs ? $"highest bitrate ({keep.BitRateKbs:N0} kb/s)" :
				keep.Fps > next.Fps ? $"highest frame rate ({keep.Fps:0.##} fps)" :
				"same quality; the oldest file";
			return (keep, reason);
		}

		static readonly string[] CopyMarkers = { " - copy", " copy", "(1)", "(2)", "(3)", "_1.", "-1." };
		static readonly string[] TransientFolders = { "\\downloads\\", "\\temp\\", "\\tmp\\", "\\desktop\\", "\\whatsapp", "\\telegram" };

		/// <summary>
		/// The synced copy (removing it would remove it from the cloud too), then outside
		/// Downloads/Desktop/temp, without "copy"/"(1)" in the name, oldest, shortest name.
		/// </summary>
		static DuplicateItem PreferOriginalLooking(List<DuplicateItem> items) =>
			items
				.OrderBy(i => CloudFiles.IsSynced(i.Path) ? 0 : 1)
				.ThenBy(i => TransientFolders.Any(f => (i.Path.ToLowerInvariant() + "\\").Contains(f)) ? 1 : 0)
				.ThenBy(i => CopyMarkers.Any(m => Path.GetFileName(i.Path).Contains(m, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
				.ThenBy(i => i.DateModified)
				.ThenBy(i => Path.GetFileName(i.Path).Length)
				.ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
				.First();

		/// <summary>Content hashes, computed once per file and only for files whose sizes match.</summary>
		sealed class ContentHashes {
			readonly Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);

			public bool Same(string a, string b) {
				string? ha = Get(a), hb = Get(b);
				return ha != null && ha == hb;
			}

			public string? Get(string path) {
				if (!cache.TryGetValue(path, out string? h))
					cache[path] = h = Compute(path);
				return h;
			}

			/// <summary>SHA-256 of the file; beyond 256 MB, of its size plus nine 4 MB samples across it.</summary>
			static string? Compute(string path) {
				const long Full = 256L << 20, Chunk = 4L << 20;
				try {
					using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
					if (fs.Length <= Full)
						return Convert.ToHexString(SHA256.HashData(fs));
					using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
					sha.AppendData(BitConverter.GetBytes(fs.Length));
					var buffer = new byte[Chunk];
					for (int k = 0; k < 9; k++) {
						fs.Position = (fs.Length - Chunk) * k / 8;
						int read = fs.ReadAtLeast(buffer, (int)Chunk, throwOnEndOfStream: false);
						sha.AppendData(buffer, 0, read);
					}
					return Convert.ToHexString(sha.GetHashAndReset());
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return null;
				}
			}
		}

		static (int, int) ParseFrameSize(string? frameSize) {
			if (string.IsNullOrEmpty(frameSize)) return (0, 0);
			int x = frameSize.IndexOf('x');
			return x > 0 && int.TryParse(frameSize.AsSpan(0, x), out int w) && int.TryParse(frameSize.AsSpan(x + 1), out int h) ? (w, h) : (0, 0);
		}
	}
}
