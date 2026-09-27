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
using VDF.Core.Utils;

namespace VDF.Benchmarks.Scenarios;

/// <summary>
/// <c>--probe-wic &lt;folder&gt; [perType]</c>: times <see cref="WicImageDecoder.TryDecode"/> per file
/// type, on an MTA thread (how the scan's worker threads run) and on an STA thread, to catch
/// codecs that COM marshals across apartments. Reads files only.
/// </summary>
public static class WicDecodeProbe {
	public static int Run(string[] args) {
		if (args.Length < 2 || !Directory.Exists(args[1])) {
			Console.Error.WriteLine("usage: --probe-wic <folder> [files per type, default 10]");
			return 2;
		}
		int perType = args.Length > 2 ? int.Parse(args[2]) : 10;
		var files = Directory.EnumerateFiles(args[1])
			.Where(f => FileUtils.IsImageFile(f) && (File.GetAttributes(f) & (FileAttributes)0x00400000) == 0) // skip cloud placeholders
			.GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
			.ToDictionary(g => g.Key, g => g.Take(perType).ToList());
		foreach (ApartmentState apartment in new[] { ApartmentState.MTA, ApartmentState.STA }) {
			var thread = new Thread(() => {
				foreach ((string ext, List<string> list) in files.OrderBy(kv => kv.Key)) {
					var sw = Stopwatch.StartNew();
					int ok = 0;
					foreach (string f in list)
						if (WicImageDecoder.TryDecode(f, wantRgb: true, out _, out _, out _, out _)) ok++;
					Console.WriteLine($"{apartment} {ext,-6} {list.Count,3} files  {sw.Elapsed.TotalMilliseconds / Math.Max(1, list.Count),7:N1} ms each  ({ok} decoded)");
				}
			});
			thread.SetApartmentState(apartment);
			thread.Start();
			thread.Join();
		}
		// Throughput with the scan's decode parallelism: a codec that serializes (or thrashes)
		// under concurrency shows up here, not in the one-thread timings above.
		foreach ((string ext, List<string> list) in files.OrderBy(kv => kv.Key))
			foreach (int threads in new[] { 1, 2, 4, 8 }) {
				var sw = Stopwatch.StartNew();
				int ok = 0;
				Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = threads }, f => {
					if (WicImageDecoder.TryDecode(f, wantRgb: true, out _, out _, out _, out _)) Interlocked.Increment(ref ok);
				});
				Console.WriteLine($"{threads} thread(s) {ext,-6} {list.Count / sw.Elapsed.TotalSeconds,7:N1} files/s  ({ok}/{list.Count} decoded)");
			}
		return 0;
	}
}
