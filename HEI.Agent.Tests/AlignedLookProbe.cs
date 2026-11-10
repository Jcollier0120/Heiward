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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using HEI.Core.Utils;

namespace HEI.Agent.Tests;

/// <summary>
/// The aligned look (<see cref="AlignedLook"/>) over a list of pairs, for tuning it: runs only when HEI_ALIGNED_PAIRS names
/// a tab-separated list (kept file, other file, then anything), with HEI_LOOKALIKE_SET the set's folder. HEI_ALIGNED_SPLIT
/// keeps only the rows whose sixth column is that split. Writes each pair's fit to HEI_ALIGNED_OUT, the row followed by
/// correlation, worst cell, second cell, scale, angle, reversed and milliseconds.
/// </summary>
public class AlignedLookProbe {
	[Fact]
	public void Measure() {
		string? list = Environment.GetEnvironmentVariable("HEI_ALIGNED_PAIRS");
		if (string.IsNullOrEmpty(list))
			return;
		string files = Path.Combine(Environment.GetEnvironmentVariable("HEI_LOOKALIKE_SET")!, "files");
		string outPath = Environment.GetEnvironmentVariable("HEI_ALIGNED_OUT") ?? Path.ChangeExtension(list, ".out.tsv");
		string? split = Environment.GetEnvironmentVariable("HEI_ALIGNED_SPLIT");
		var rows = File.ReadAllLines(list).Where(l => l.Length > 0).Select(l => l.Split('\t'))
			.Where(r => string.IsNullOrEmpty(split) || (r.Length > 5 && r[5] == split)).ToList();
		var pictures = new ConcurrentDictionary<string, Lazy<AlignedLook.Picture?>>();
		AlignedLook.Picture? Load(string name) => pictures.GetOrAdd(name, n => new Lazy<AlignedLook.Picture?>(() => AlignedLook.Picture.Load(Path.Combine(files, n)))).Value;
		var output = new string[rows.Count];
		var all = Stopwatch.StartNew();
		Parallel.For(0, rows.Count, new ParallelOptions { MaxDegreeOfParallelism = int.TryParse(Environment.GetEnvironmentVariable("HEI_ALIGNED_THREADS"), out int threads) ? threads : Math.Max(2, Environment.ProcessorCount / 2) }, k => {
			var r = rows[k];
			var a = Load(r[0]);
			var b = Load(r[1]);
			var time = Stopwatch.StartNew();
			AlignedLook.Fit? fit = a != null && b != null ? AlignedLook.Compare(a, b) : null;
			time.Stop();
			string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
			output[k] = string.Join('\t', r) + "\t" + (fit is { } f
				? $"{F(f.Ncc)}\t{F(f.WorstCell)}\t{F(f.SecondCell)}\t{F(f.Scale)}\t{F(f.Angle)}\t{(f.Reversed ? 1 : 0)}\t{time.ElapsedMilliseconds}"
				: "-1\t-1\t-1\t-1\t-1\t-1\t0");
		});
		File.WriteAllLines(outPath, output);
		File.WriteAllText(outPath + ".time", $"{rows.Count} pairs, {pictures.Count} pictures, {all.Elapsed.TotalSeconds:F1} s\n");
	}
}
