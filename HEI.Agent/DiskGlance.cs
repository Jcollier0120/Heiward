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

using System.Runtime.InteropServices;
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <summary>One part of the used space: photos, videos, developer files, games, the Recycle Bin, everything else.</summary>
	sealed record GlanceKind(string Key, string Label, long Bytes, int Files);

	/// <summary>One file type among the photos and videos the last scan found: "mp4", a video.</summary>
	sealed record GlanceType(string Type, string Kind, long Bytes, int Files);

	/// <summary>A drive that's nearly full: under a tenth of it free, as its card's red bar says.</summary>
	sealed record GlanceLow(string Root, string Name, long FreeBytes, double UsedShare);

	/// <summary>What can be freed now: the duplicates to review, the developer files ticked, the Recycle Bin, the game leftovers ticked.</summary>
	sealed record GlanceReclaim(long Duplicates, long Developer, long RecycleBin, long Games = 0) {
		public long Total => Duplicates + Developer + RecycleBin + Games;
	}

	/// <param name="Drives">The drives counted: this PC's own (fixed and USB), ready.</param>
	/// <param name="NetworkDrives">Network drives, shown on their own cards but not counted: their space isn't this PC's.</param>
	/// <param name="Kinds">The used space by kind, biggest first, ending with everything else Heiward doesn't sort.</param>
	/// <param name="Types">The photos and videos by file type, biggest first; the rest are in <paramref name="OtherTypes"/>.</param>
	/// <param name="Typed">Whether the last scan sorted its files by type (an index from before didn't).</param>
	sealed record DiskGlance(int Drives, int NetworkDrives, long TotalBytes, long UsedBytes, long FreeBytes,
		List<GlanceKind> Kinds, List<GlanceType> Types, GlanceType? OtherTypes, bool Typed,
		List<GlanceLow> Low, GlanceReclaim Reclaim, DateTime? ScannedAtUtc) {

		/// <summary>The file types listed by name; the rest are summed.</summary>
		public const int TypesShown = 6;

		/// <summary>
		/// This PC's drives in one card, from what the page already has: the drive cards' sizes, the last
		/// scan's photos and videos per drive and type, the developer check's totals, the games check's (the installed games
		/// and what they left, with game mode on) and the Recycle Bin. Code adds it up; nothing is walked for it.
		/// </summary>
		public static DiskGlance Build(IReadOnlyList<DriveCard> drives, ScanIndex? index, long developerBytes, long developerTicked,
			long duplicatesBytes, long recycleBinBytes, long gamesBytes = 0, long gamesTicked = 0) {
			var local = drives.Where(d => d.Type is "fixed" or "removable" && d.TotalBytes > 0).ToList();
			long total = local.Sum(d => d.TotalBytes), free = local.Sum(d => d.FreeBytes), used = total - free;

			// The photos and videos the last scan found on these drives, by type.
			var byType = new Dictionary<string, (long Bytes, int Files)>(StringComparer.OrdinalIgnoreCase);
			bool typed = false;
			if (index != null) {
				foreach (var (root, onDrive) in index.Types) {
					if (!local.Any(d => d.Root.Equals(root, StringComparison.OrdinalIgnoreCase))) continue;
					typed = true;
					foreach (var (type, f) in onDrive) {
						byType.TryGetValue(type, out var t);
						byType[type] = (t.Bytes + f.Bytes, t.Files + f.Files);
					}
				}
			}
			var types = byType.Select(kv => new GlanceType(kv.Key, KindOf(kv.Key), kv.Value.Bytes, kv.Value.Files))
				.OrderByDescending(t => t.Bytes).ThenBy(t => t.Type, StringComparer.Ordinal).ToList();

			var kinds = new List<GlanceKind> {
				new("videos", "Videos", types.Where(t => t.Kind == "video").Sum(t => t.Bytes), types.Where(t => t.Kind == "video").Sum(t => t.Files)),
				new("photos", "Photos", types.Where(t => t.Kind == "photo").Sum(t => t.Bytes), types.Where(t => t.Kind == "photo").Sum(t => t.Files)),
				new("developer", "Developer files", developerBytes, 0),
				new("games", "Games", gamesBytes, 0),
				new("bin", "Recycle Bin", recycleBinBytes, 0),
			};
			kinds = kinds.Where(k => k.Bytes > 0).OrderByDescending(k => k.Bytes).ToList();
			// What's sorted can't be more than what's used (a file counted twice, or a drive filling up since).
			long sorted = kinds.Sum(k => k.Bytes);
			if (sorted > used && sorted > 0) {
				double scale = (double)used / sorted;
				kinds = kinds.Select(k => k with { Bytes = (long)(k.Bytes * scale) }).ToList();
				sorted = kinds.Sum(k => k.Bytes);
			}
			kinds.Add(new GlanceKind("other", "Everything else", Math.Max(0, used - sorted), 0));

			var shown = types.Take(TypesShown).ToList();
			var rest = types.Skip(TypesShown).ToList();
			GlanceType? others = rest.Count == 0 ? null : new GlanceType($"{rest.Count} more", "mixed", rest.Sum(t => t.Bytes), rest.Sum(t => t.Files));

			var low = local.Where(d => (double)d.FreeBytes / d.TotalBytes < 0.1)
				.Select(d => new GlanceLow(d.Root, d.Name, d.FreeBytes, Math.Round(1 - (double)d.FreeBytes / d.TotalBytes, 3)))
				.OrderByDescending(d => d.UsedShare).ToList();

			return new DiskGlance(local.Count, drives.Count(d => d.Type == "network"), total, used, free, kinds, shown, others, typed,
				low, new GlanceReclaim(duplicatesBytes, developerTicked, recycleBinBytes, gamesTicked), index?.ScannedAtUtc);
		}

		/// <summary>"photo" or "video", by Heiward's own lists of the types it scans; anything else added in the settings, "other".</summary>
		public static string KindOf(string type) {
			string ext = "." + type;
			if (FileUtils.VideoExtensions.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase))) return "video";
			if (FileUtils.ImageExtensions.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase))) return "photo";
			return "other";
		}
	}

	/// <summary>
	/// How much is in the Recycle Bin on this PC's fixed drives, read at most once a minute: the page polls
	/// every few seconds, and the Recycle Bin can hold many files. Drives scanned only when asked are left
	/// out, so a disk that sleeps isn't woken for it.
	/// </summary>
	static class RecycleBinSize {
		static readonly object Gate = new();
		static long cachedBytes;
		static long cachedAt = long.MinValue;
		static string cachedKey = "";

		public static long Of(IReadOnlyList<DriveCard> drives) {
			var roots = drives.Where(d => d.Type == "fixed" && !d.OnRequest && d.TotalBytes > 0).Select(d => d.Root).ToList();
			string key = string.Join("|", roots);
			lock (Gate) {
				if (key == cachedKey && Environment.TickCount64 - cachedAt < 60_000) return cachedBytes;
				long bytes = 0;
				foreach (string root in roots) bytes += Query(root);
				(cachedBytes, cachedAt, cachedKey) = (bytes, Environment.TickCount64, key);
				return bytes;
			}
		}

		static long Query(string root) {
			try {
				var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
				return SHQueryRecycleBin(root, ref info) == 0 ? Math.Max(0, info.i64Size) : 0;
			}
			catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException) {
				return 0;
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		struct SHQUERYRBINFO {
			public int cbSize;
			public long i64Size;
			public long i64NumItems;
		}

		[DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
		static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
	}
}
