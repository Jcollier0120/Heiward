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
using System.Text.Json;
using HEI.Agent;
using HEI.Core;
using HEI.Core.AI;
using HEI.Core.Utils;

namespace HEI.Agent.Tests;

/// <summary>
/// The look-alike set's measurement (docs/LOOKALIKE-BAR.md). It runs only when HEI_LOOKALIKE_SET names the set's
/// folder (made by HEI.Benchmarks/Lookalike/make-edits.py), and passes without doing anything otherwise. Run it on its
/// own, with HEIWARD_HOME set to a scratch folder:
/// <code>
/// HEI_LOOKALIKE_SET=D:\Datasets\heiward-lookalike HEI_LOOKALIKE_STATE=&lt;folder holding ai\&gt; HEI_LOOKALIKE_DEVICE=cpu|gpu
/// HEI_LOOKALIKE_OUT=&lt;result.json&gt; dotnet test HEI.Agent.Tests -c Release --filter LookalikeProbe
/// </code>
/// It scans the set's files\ the way the review's scans do (photos, AI matching on, Windows' decoder), then for each
/// setting in HEI_LOOKALIKE_SETTINGS ("96/94,96/92": gray/AI percent; default the defaults) compares again and builds
/// the review's sets. The result has the engine's groups and the review's sets per setting, each file's facts, and the
/// gray and AI scores of every pair either one puts at 80% or more. tools/lookalike-score.ts scores it.
/// </summary>
public class LookalikeProbe {
	[Fact]
	public async Task Measure() {
		string? set = Environment.GetEnvironmentVariable("HEI_LOOKALIKE_SET");
		if (string.IsNullOrEmpty(set))
			return;
		string state = Environment.GetEnvironmentVariable("HEI_LOOKALIKE_STATE") ?? throw new InvalidOperationException("HEI_LOOKALIKE_STATE");
		string outPath = Environment.GetEnvironmentVariable("HEI_LOOKALIKE_OUT") ?? Path.Combine(set, "result.json");
		AiDevice device = Enum.Parse<AiDevice>(Environment.GetEnvironmentVariable("HEI_LOOKALIKE_DEVICE") ?? "Cpu", ignoreCase: true);
		var runs = (Environment.GetEnvironmentVariable("HEI_LOOKALIKE_SETTINGS") ?? "96/94").Split(',', StringSplitOptions.RemoveEmptyEntries)
			.Select(s => s.Split('/')).Select(p => (Gray: float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), Ai: float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture))).ToList();
		CoreUtils.UseStateFolder(state);
		string files = Path.Combine(set, "files");
		string db = Path.Combine(state, "db-" + device.ToString().ToLowerInvariant());
		Directory.CreateDirectory(db);

		var settings = new Settings {
			IncludeImages = true,
			IncludeSubDirectories = true,
			UseAiMatching = true,
			AiDevice = device,
			UseWindowsImageDecoder = true,
			ExcludeHardLinks = true,
			ListingProvesExistence = true,
			MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2),
			CustomDatabaseFolder = db,
			Percent = runs[0].Gray,
			AiPercent = runs[0].Ai,
		};
		settings.IncludeList.Add(files);
		var engine = new ScanEngine { Settings = settings };
		// The engine's log, beside the result: why a scan stopped, which device the AI ran on.
		var log = new List<string>();
		Logger.Instance.LogEntryAdded += e => { lock (log) log.Add($"{e.Timestamp:HH:mm:ss} {e.Severity} {e.Message}"); };
		try { await Scan(); }
		finally { lock (log) File.WriteAllLines(Path.ChangeExtension(outPath, ".log"), log); }

		async Task Scan() {
		var scanTime = Stopwatch.StartNew();
		await Until(engine, () => engine.StartSearch(searchAndCompare: false), compare: false);
		scanTime.Stop();
		string? cacheKey = engine.AiCacheKeyUsed ?? NpuComponents.CacheKeyFor(device);
		string? deviceUsed = engine.AiDeviceUsed;

		// Each file's facts, and every pair's scores at 80% or more by either measure.
		var entries = DatabaseUtils.Database.Where(e => e.IsImage && e.Path.StartsWith(files, StringComparison.OrdinalIgnoreCase))
			.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
		UnionEmbeddingStore store = UnionEmbeddingStore.Load(cacheKey);
		var gray32 = entries.Select(e => e.grayBytes.Values.FirstOrDefault(v => v != null)).ToArray();
		var vectors = entries.Select(e => e.grayBytes.Keys.Select(k => store.GetEmbedding(e, k)).FirstOrDefault(v => v != null)).ToArray();
		var facts = entries.Select(e => {
			// A tag can be there twice (two unknown tags read under one name): the first counts.
			var tags = ExifReader.ReadAllTags(e.Path).Where(t => !t.IsGps).GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First().Value);
			return new {
				file = Path.GetFileName(e.Path), bytes = e.FileSize, frame = e.mediaInfo?.Streams?.FirstOrDefault() is { } s ? $"{s.Width}x{s.Height}" : null,
				taken = tags.GetValueOrDefault("DateTimeOriginal"), subsec = tags.GetValueOrDefault("SubSecTimeOriginal"),
				camera = tags.TryGetValue("Model", out string? m) ? (tags.GetValueOrDefault("Make") + " " + m).Trim() : null,
			};
		}).ToList();
		var pairs = new List<float[]>();
		for (int i = 0; i < entries.Count; i++)
			for (int j = i + 1; j < entries.Count; j++) {
				float g = gray32[i] is { } a && gray32[j] is { } b && a.Length == b.Length ? 100f * (1f - GrayBytesUtils.PercentageDifference(a, b)) : -1f;
				float s = vectors[i] is { } x && vectors[j] is { } y ? 100f * EmbeddingMath.CosineSimilarity(x, y) : -1f;
				if (g >= 80f || s >= 80f)
					pairs.Add(new[] { i, j, MathF.Round(g, 3), MathF.Round(s, 3), gray32[i] is { } c && gray32[j] is { } d && c.Length == d.Length ? GrayBytesUtils.MaxCellDifference(c, d) : -1 });
			}

		// Then each setting: the engine's groups, and the review's sets (HEI_LOOKALIKE_REVIEW=0: the engine's alone). The
		// result is written after each, so a long review doesn't lose the ones before it. The review keeps its hashes, close
		// looks and words in HEIWARD_HOME as the review's scans do, so the next run reads only what it hasn't yet.
		bool review = Environment.GetEnvironmentVariable("HEI_LOOKALIKE_REVIEW") != "0";
		var results = new List<object>();
		foreach (var (gray, ai) in runs) {
			settings.Percent = gray;
			settings.AiPercent = ai;
			var compare = Stopwatch.StartNew();
			await Until(engine, engine.StartCompare, compare: true);
			compare.Stop();
			var groups = engine.Duplicates.GroupBy(d => d.GroupId).Select(g => g.Select(d => Path.GetFileName(d.Path)).Order(StringComparer.Ordinal).ToList()).ToList();
			var report = Stopwatch.StartNew();
			List<ReportGroup> sets = new();
			if (review) {
				var fingerprints = new ScanFingerprints(cacheKey, withAi: true) { TextDevice = device };
				var hashes = ReportBuilder.ContentHashes.Load();
				try { sets = ReportBuilder.Build(engine.Duplicates.ToList(), fingerprints, hashes); }
				finally { fingerprints.FinishReading(); hashes.Save(); }
			}
			report.Stop();
			results.Add(new {
				gray, ai, compareMs = compare.ElapsedMilliseconds, reportMs = review ? report.ElapsedMilliseconds : -1, engineGroups = groups,
				sets = sets.Select(s => new {
					kind = s.Kind, keep = Path.GetFileName(s.KeepPath), reason = s.KeepReason,
					items = s.Items.Select(i => new { file = i.Name, relation = i.Relation, suggested = i.Suggested, similarity = i.Similarity, byAi = i.AiMatched }),
				}).ToList(),
			});
			await using var output = File.Create(outPath);
			await JsonSerializer.SerializeAsync(output, new {
				device = device.ToString(), deviceUsed, cacheKey, scanMs = scanTime.ElapsedMilliseconds, files = facts, runs = results, pairs,
			});
		}
		}
	}

	static async Task Until(ScanEngine engine, Action start, bool compare) {
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void Done(object? s, EventArgs e) => done.TrySetResult();
		void Aborted(object? s, EventArgs e) => done.TrySetException(new InvalidOperationException("the scan was aborted"));
		if (compare) engine.ScanDone += Done; else engine.BuildingHashesDone += Done;
		engine.ScanAborted += Aborted;
		try {
			start();
			await done.Task;
		}
		finally {
			if (compare) engine.ScanDone -= Done; else engine.BuildingHashesDone -= Done;
			engine.ScanAborted -= Aborted;
		}
	}
}
