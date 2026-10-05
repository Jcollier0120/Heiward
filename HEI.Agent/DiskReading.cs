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

using System.IO.Enumeration;
using System.Text.Json;

namespace HEI.Agent {
	/// <summary>A fixed drive in a disk reading.</summary>
	/// <param name="Root">"C:\".</param>
	/// <param name="Level">"ok", "warn" (under <see cref="DiskWatch.WarnPercentFree"/>% free) or "alert" (under <see cref="DiskWatch.AlertPercentFree"/>%).</param>
	sealed record DiskDrive(string Root, string Label, long TotalBytes, long FreeBytes, double PercentFree, string Level);

	/// <summary>A tool's cache in a disk reading.</summary>
	/// <param name="Name">"gradle", "npm-cache (Claude package)": the names Reeve's disk-caches job gave them.</param>
	/// <param name="GrewBytes">Its growth since the reading before (less than 0: it shrank); null when that reading didn't have it.</param>
	/// <param name="SameAs">
	/// For a Claude package copy that shows exactly the files of the cache it's named after (the reading was taken from a
	/// process whose AppData is redirected into the Claude package): that cache's name, and it isn't counted twice.
	/// </param>
	sealed record DiskCache(string Name, string Path, long Bytes, long Files, long? GrewBytes, string? SameAs = null);

	/// <summary>The last disk reading (disk.json): the drives' space and the big tool caches' sizes. <see cref="DiskWatch"/> takes it.</summary>
	sealed class DiskReading {
		public DateTime MeasuredAtUtc { get; set; }
		/// <summary>The reading before, which <see cref="DiskCache.GrewBytes"/> compares with; null for the first.</summary>
		public DateTime? PreviousAtUtc { get; set; }
		public double DurationSec { get; set; }
		public List<DiskDrive> Drives { get; set; } = new();
		public List<DiskCache> Caches { get; set; } = new();

		public static string FilePath => Path.Combine(AgentPaths.Home, "disk.json");

		public static DiskReading? Load() {
			try {
				return File.Exists(FilePath) ? JsonSerializer.Deserialize<DiskReading>(File.ReadAllText(FilePath), AgentConfig.Json) : null;
			}
			catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
				return null;
			}
		}

		public void Save() => AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(this, AgentConfig.Json));
	}

	/// <summary>
	/// Disk measurement for the whole manor: free space on every fixed drive, and the sizes of the big tool caches (the
	/// same ones, by the same names, as Reeve's disk-caches job measured, plus NuGet's and pip's), with how much each grew
	/// since the reading before. Taken by the review page's process, which stays up: a minute after it starts, then once
	/// an hour, on a thread below normal priority. Never for a request: GET /api/disk answers from the last reading
	/// (disk.json). Read-only: it lists folders, never follows a link, and changes nothing.
	/// </summary>
	static class DiskWatch {
		public const int EveryMinutes = 60;
		public const double AlertPercentFree = 10, WarnPercentFree = 15;
		/// <summary>A cache that grew more than this since the reading before is worth a warning.</summary>
		public const long GrowthBytes = 5L << 30;
		const string Twin = " (Claude package)";

		/// <summary>
		/// The caches measured, in order, as Reeve's job had them: <paramref name="profile"/> is %USERPROFILE%,
		/// <paramref name="local"/> %LOCALAPPDATA%, <paramref name="npmCache"/> npm_config_cache when it's set, and
		/// <paramref name="claudeLocal"/> the Claude package's LocalCache\Local, where AppData writes made from a Claude
		/// session land, when there is one.
		/// </summary>
		internal static List<(string Name, string Path)> Caches(string profile, string local, string? npmCache, string? claudeLocal) {
			var list = new List<(string, string)> {
				("foundry", Path.Combine(profile, ".foundry")),
				("geniex-cache", Path.Combine(profile, ".cache", "geniex")),
				("reeve", Path.Combine(profile, ".reeve")),
				("gradle", Path.Combine(profile, ".gradle")),
				("npm-cache", string.IsNullOrWhiteSpace(npmCache) ? Path.Combine(local, "npm-cache") : npmCache),
				("pnpm", Path.Combine(local, "pnpm")),
				("android-sdk", Path.Combine(local, "Android", "Sdk")),
				("nuget", Path.Combine(profile, ".nuget", "packages")),
				("pip", Path.Combine(local, "pip", "Cache")),
			};
			if (claudeLocal != null) {
				list.Add(("npm-cache" + Twin, Path.Combine(claudeLocal, "npm-cache")));
				list.Add(("pnpm" + Twin, Path.Combine(claudeLocal, "pnpm")));
				list.Add(("android-sdk" + Twin, Path.Combine(claudeLocal, "Android", "Sdk")));
			}
			return list;
		}

		/// <summary>The caches measured on this PC.</summary>
		static List<(string Name, string Path)> Caches() {
			string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } l ? l : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			string? claudeLocal = null;
			try {
				string packages = Path.Combine(local, "Packages");
				if (Directory.Exists(packages) && Directory.EnumerateDirectories(packages, "Claude_*").FirstOrDefault() is { } pkg)
					claudeLocal = Path.Combine(pkg, "LocalCache", "Local");
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			return Caches(profile, local, Environment.GetEnvironmentVariable("npm_config_cache"), claudeLocal);
		}

		/// <summary>"alert", "warn" or "ok", for a drive this much of which is free.</summary>
		internal static string LevelOf(double percentFree) => percentFree < AlertPercentFree ? "alert" : percentFree < WarnPercentFree ? "warn" : "ok";

		/// <summary>Every fixed drive that's ready, as Windows lists them.</summary>
		static List<DiskDrive> Drives() {
			var drives = new List<DiskDrive>();
			foreach (DriveInfo d in DriveInfo.GetDrives()) {
				try {
					if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
					drives.Add(DriveOf(d.RootDirectory.FullName, d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace));
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* gone, or not ours to read */ }
			}
			return drives;
		}

		internal static DiskDrive DriveOf(string root, string label, long total, long free) {
			double pct = Math.Round(100.0 * free / total, 1);
			return new DiskDrive(root, label, total, free, pct, LevelOf(pct));
		}

		/// <summary>Bytes and files below a folder, never following links (no loops, nothing counted twice), skipping what it may not read.</summary>
		internal static (long Bytes, long Files) Measure(string path, CancellationToken ct = default) {
			long bytes = 0, files = 0;
			try {
				var all = new FileSystemEnumerable<long>(path, (ref FileSystemEntry e) => e.Length,
					new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }) {
					ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
					ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
				};
				foreach (long length in all) {
					if (ct.IsCancellationRequested) break;
					bytes += length;
					files++;
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			return (bytes, files);
		}

		/// <summary>
		/// A reading of <paramref name="drives"/> and <paramref name="caches"/> (those that exist), each cache's growth
		/// against <paramref name="previous"/>, and a Claude package copy that shows the same files as its cache marked as such.
		/// </summary>
		internal static DiskReading Take(DiskReading? previous, List<DiskDrive> drives, IReadOnlyList<(string Name, string Path)> caches, DateTime nowUtc,
			CancellationToken ct = default) {
			var timer = System.Diagnostics.Stopwatch.StartNew();
			var measured = new List<DiskCache>();
			foreach (var (name, path) in caches) {
				if (ct.IsCancellationRequested) break;
				if (!Directory.Exists(path)) continue;
				var (bytes, files) = Measure(path, ct);
				long? grew = previous?.Caches.FirstOrDefault(c => c.Name == name) is { } before ? bytes - before.Bytes : null;
				measured.Add(new DiskCache(name, path, bytes, files, grew));
			}
			// From inside the Claude package, the real AppData path shows the package's copy: the same size and file count is
			// the same files, counted once.
			for (int i = 0; i < measured.Count; i++) {
				DiskCache c = measured[i];
				if (!c.Name.EndsWith(Twin, StringComparison.Ordinal)) continue;
				string of = c.Name[..^Twin.Length];
				if (measured.FirstOrDefault(m => m.Name == of) is { } original && original.Files > 0 && original.Bytes == c.Bytes && original.Files == c.Files)
					measured[i] = c with { SameAs = of };
			}
			return new DiskReading {
				MeasuredAtUtc = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc),
				PreviousAtUtc = previous?.MeasuredAtUtc,
				DurationSec = Math.Round(timer.Elapsed.TotalSeconds, 1),
				Drives = drives,
				Caches = measured,
			};
		}

		/// <summary>A reading is due: none yet, the last an hour old or more, or one from a clock set back.</summary>
		internal static bool Due(DiskReading? last, DateTime nowUtc) =>
			last == null || nowUtc - last.MeasuredAtUtc >= TimeSpan.FromMinutes(EveryMinutes) || last.MeasuredAtUtc > nowUtc.AddMinutes(5);

		/// <summary>
		/// The review page's own clock: a minute after it starts, then whenever a reading is due, until it stops. Each
		/// reading runs on a thread below normal priority, and is saved to disk.json.
		/// </summary>
		public static async Task RunAsync(CancellationToken ct) {
			try {
				await Task.Delay(TimeSpan.FromMinutes(1), ct);
				while (!ct.IsCancellationRequested) {
					DiskReading? last = DiskReading.Load();
					if (Due(last, DateTime.UtcNow)) {
						try {
							DiskReading reading = await Task.Factory.StartNew(() => {
								ThreadPriority was = Thread.CurrentThread.Priority;
								Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
								try { return Take(last, Drives(), Caches(), DateTime.UtcNow, ct); }
								finally { Thread.CurrentThread.Priority = was; }
							}, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
							if (!ct.IsCancellationRequested) {
								reading.Save();
								last = reading;
							}
						}
						catch (Exception e) when (e is not OperationCanceledException) {
							AgentPaths.AppendLog("disk reading failed: " + e.Message);
						}
					}
					DateTime next = (last?.MeasuredAtUtc ?? DateTime.UtcNow).AddMinutes(EveryMinutes);
					TimeSpan wait = next - DateTime.UtcNow;
					await Task.Delay(wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait > TimeSpan.FromMinutes(EveryMinutes) ? TimeSpan.FromMinutes(EveryMinutes) : wait, ct);
				}
			}
			catch (OperationCanceledException) { /* the page is closing */ }
		}

		/// <summary>GET /api/disk's answer, from the last reading (null before the first): see the README.</summary>
		internal static object Answer(DiskReading? r) {
			var warnings = new List<string>();
			string status = "unknown";
			if (r != null) {
				foreach (DiskDrive d in r.Drives)
					if (d.Level != "ok")
						warnings.Add($"{d.Root} is below {(d.Level == "alert" ? AlertPercentFree : WarnPercentFree)}% free: {Format.Bytes(d.FreeBytes)} free of {Format.Bytes(d.TotalBytes)} ({d.PercentFree}%)");
				foreach (DiskCache c in r.Caches)
					if (c.GrewBytes > GrowthBytes)
						warnings.Add($"{c.Name} grew {Format.Bytes(c.GrewBytes.Value)} since the reading before ({Format.Bytes(c.Bytes - c.GrewBytes.Value)} to {Format.Bytes(c.Bytes)})");
				status = r.Drives.Any(d => d.Level == "alert") ? "alert" : warnings.Count > 0 ? "warn" : "ok";
			}
			return new {
				app = "heiward",
				measuredAt = r?.MeasuredAtUtc,
				previousAt = r?.PreviousAtUtc,
				everyMinutes = EveryMinutes,
				status,
				warnings,
				thresholds = new { alertPercentFree = AlertPercentFree, warnPercentFree = WarnPercentFree, cacheGrowthBytes = GrowthBytes },
				drives = (r?.Drives ?? new()).Select(d => new { root = d.Root, label = d.Label, totalBytes = d.TotalBytes, freeBytes = d.FreeBytes, percentFree = d.PercentFree, level = d.Level }),
				caches = (r?.Caches ?? new()).Select(c => new { name = c.Name, path = c.Path, bytes = c.Bytes, files = c.Files, grewBytes = c.GrewBytes, sameAs = c.SameAs }),
				cachesBytes = (r?.Caches ?? new()).Where(c => c.SameAs == null).Sum(c => c.Bytes),
			};
		}
	}
}
