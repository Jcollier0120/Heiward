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
using System.Security.Cryptography;
using System.Text;
using HEI.Core;
using HEI.Core.Utils;
using HEI.TestSupport;

namespace HEI.Benchmarks.Scenarios;

/// <summary>
/// The compare phase at library scale, on made-up libraries (<see cref="SyntheticLibrary"/>): no
/// files, no FFmpeg, nothing read from or written to an installed Heiward. One configuration per
/// run, so the peak working set is that configuration's own:
///
///   dotnet run -c Release --project HEI.Benchmarks -- --probe-compare-scale --size 50000 --mode new [--ai] [--flip]
///     [--ignore-black] [--ignore-white] [--videos --positions 3 [--phash|--combined]] [--seed 1]
///
/// --mode old judges every pair (the loop before the compare index). The digest covers every
/// group, difference and flag: the same digest for old and new means the same results.
/// </summary>
public static class CompareScaleProbe {
	public static int Run(string[] args) {
		int size = IntArg(args, "--size", 10_000);
		int seed = IntArg(args, "--seed", 1);
		int positions = IntArg(args, "--positions", 3);
		bool videos = args.Contains("--videos");
		string mode = StringArg(args, "--mode", "new");

		// Never fall back to an installed Heiward's embedding sidecar.
		HEI.Core.AI.UnionEmbeddingStore.TestOverrideStorePath = Path.Combine(Path.GetTempPath(), $"hei-bench-{Environment.ProcessId}.bin");

		long before = GC.GetTotalMemory(true);
		var library = videos ? SyntheticLibrary.Videos(size, seed, positions) : SyntheticLibrary.Photos(size, seed);
		var engine = new ScanEngine();
		engine.Settings.ThumbnailCount = videos ? positions : 1;
		engine.Settings.Percent = 96f;
		engine.Settings.UseAiMatching = args.Contains("--ai");
		engine.Settings.CompareHorizontallyFlipped = args.Contains("--flip");
		engine.Settings.IgnoreBlackPixels = args.Contains("--ignore-black");
		engine.Settings.IgnoreWhitePixels = args.Contains("--ignore-white");
		engine.Settings.UsePHashing = args.Contains("--phash");
		engine.Settings.CombineGrayscaleAndPHash = args.Contains("--combined");
		engine.Settings.DatabaseCheckpointIntervalMinutes = 0;
		engine.EnsureThumbnailPositions();
		engine.UseCompareIndex = mode != "old";
		engine.ElapsedTimer.Start();

		DatabaseUtils.Database.Clear();
		var store = new HEI.Core.AI.UnionEmbeddingStore();
		foreach (SyntheticFile file in library) {
			var entry = new FileEntry {
				_Path = file.Path,
				Folder = file.Folder,
				FileSize = file.FileSize,
				invalid = false,
				IsImage = file.IsImage,
				mediaInfo = new MediaInfo { Duration = TimeSpan.FromSeconds(file.DurationSeconds) },
			};
			for (int j = 0; j < file.Frames.Length; j++) {
				double key = file.IsImage ? 0 : entry.GetGrayBytesIndex(engine.positionList[j], engine.Settings.MaxSamplingDurationSeconds);
				entry.grayBytes[key] = file.Frames[j];
				if (file.Embeddings[j] is byte[] embedding)
					store.Put(entry, key, embedding);
			}
			DatabaseUtils.Database.Add(entry);
		}
		library = null!;
		long libraryBytes = GC.GetTotalMemory(true) - before;

		engine.unionEmbeddingStore = store;
		long allocated = GC.GetTotalAllocatedBytes(true);
		var sw = Stopwatch.StartNew();
		engine.ScanForDuplicates();
		sw.Stop();
		allocated = GC.GetTotalAllocatedBytes(true) - allocated;
		var highlight = Stopwatch.StartNew();
		engine.HighlightBestMatches();
		highlight.Stop();

		int groups = engine.Duplicates.Select(d => d.GroupId).Distinct().Count();
		using var process = Process.GetCurrentProcess();
		Console.WriteLine($"{(videos ? $"videos×{positions}" : "photos")} size={size} mode={mode} settings=[{Describe(args)}] " +
			$"seconds={sw.Elapsed.TotalSeconds:F2} highlightSeconds={highlight.Elapsed.TotalSeconds:F2} pairsJudged={engine.LastComparePairsJudged} matched={engine.LastComparePairsMatched} duplicates={engine.Duplicates.Count} groups={groups} " +
			$"digest={Digest(engine)} libraryMB={libraryBytes / 1048576.0:F0} indexKB={engine.LastCompareIndexBytes / 1024.0:F0} " +
			$"allocatedMB={allocated / 1048576.0:F0} peakWorkingSetMB={process.PeakWorkingSet64 / 1048576.0:F0}");
		DatabaseUtils.Database.Clear();
		return 0;
	}

	/// <summary>Every item's group (by its members), difference and flags, hashed.</summary>
	internal static string Digest(ScanEngine engine) {
		var groupNames = engine.Duplicates.GroupBy(d => d.GroupId)
			.ToDictionary(g => g.Key, g => g.Select(d => d.Path).Min(StringComparer.Ordinal)!);
		var lines = engine.Duplicates
			.Select(d => $"{d.Path}|{groupNames[d.GroupId]}|{BitConverter.SingleToInt32Bits(d.Similarity)}|{(int)d.Flags}")
			.OrderBy(l => l, StringComparer.Ordinal);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..16];
	}

	static string Describe(string[] args) =>
		string.Join(",", args.Where(a => a is "--ai" or "--flip" or "--ignore-black" or "--ignore-white" or "--phash" or "--combined").Select(a => a[2..]));

	static int IntArg(string[] args, string name, int fallback) {
		int at = Array.IndexOf(args, name);
		return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int v) ? v : fallback;
	}

	static string StringArg(string[] args, string name, string fallback) {
		int at = Array.IndexOf(args, name);
		return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
	}
}
