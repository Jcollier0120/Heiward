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

using HEI.Core.FFTools;
using HEI.Core.Utils;

namespace HEI.Benchmarks.Scenarios;

/// <summary>
/// <c>--probe-copies &lt;folder&gt; [--rows]</c>: a photo's copies against the same photo with a small local edit, the
/// way the review decides a plain copy. The folder holds <c>NAME__orig.*</c> beside <c>NAME__KIND.*</c> (a resize, a
/// recompression, or <c>edit-…</c>: a blurred face, an annotation, a changed chat line); each original is also
/// decoded through FFmpeg (<c>decoder</c>), the way an HEIC original and its JPEG meet. Prints, per kind, the 32×32
/// similarity, the largest single-cell difference (<see cref="GrayBytesUtils.MaxCellDifference"/>) and the closer
/// look (<see cref="CloseLook"/>), and how many pass each step. Measured on 40 photos and 8 synthetic chats: every
/// resize (down to a quarter), recompression and PNG copy kept cells ≤ 6 and a close look ≤ 3.6; of 296 edits, all
/// 296 passed the 32×32 match alone, 133 the cells too, and 65 the close look too (blurs over even areas that
/// change almost nothing).
/// </summary>
public static class CopyCellProbe {
	public static int Run(string[] args) {
		if (args.Length < 2 || !Directory.Exists(args[1])) {
			Console.Error.WriteLine("usage: --probe-copies <folder> [--rows] | --probe-copies <folder> --text --state <folder holding ai\\> [--models <folder>] [--npu] [--background] [--reads <file>] [--rows]");
			return 2;
		}
		if (args.Contains("--text"))
			return TextCopyProbe.Run(args);
		var rows = new List<(string Kind, string Name, double Sim, int Cell, double? Close)>();
		foreach (string orig in Directory.EnumerateFiles(args[1], "*__orig.*").Order()) {
			string stem = Path.GetFileName(orig)[..Path.GetFileName(orig).IndexOf("__", StringComparison.Ordinal)];
			if ((Wic(orig) ?? Ffmpeg(orig)) is not { } a) { Console.Error.WriteLine($"can't decode {orig}"); continue; }
			if (Ffmpeg(orig) is { } ff)
				rows.Add(("decoder", stem, Similarity(a, ff), GrayBytesUtils.MaxCellDifference(a, ff), null));
			foreach (string copy in Directory.EnumerateFiles(args[1], stem + "__*.*").Where(f => f != orig)) {
				string kind = Path.GetFileNameWithoutExtension(copy)[(stem.Length + 2)..];
				if ((Wic(copy) ?? Ffmpeg(copy)) is not { } b) { Console.Error.WriteLine($"can't decode {copy}"); continue; }
				rows.Add((kind, stem, Similarity(a, b), GrayBytesUtils.MaxCellDifference(a, b), CloseLook.Difference(orig, copy)));
			}
		}
		Console.WriteLine($"{"kind",-18} {"n",3} {"sim min",8} {"cell max",8} {"close max",9}   pass: 32×32  +cells  +close");
		foreach (var g in rows.GroupBy(r => r.Kind).OrderBy(g => g.Key.StartsWith("edit") ? 1 : 0).ThenBy(g => g.Key)) {
			int sim = g.Count(r => r.Sim >= 99.5), cells = g.Count(r => r.Sim >= 99.5 && r.Cell <= 8);
			int close = g.Count(r => r.Sim >= 99.5 && r.Cell <= 8 && r.Close <= 5);
			Console.WriteLine($"{g.Key,-18} {g.Count(),3} {g.Min(r => r.Sim),8:F2} {g.Max(r => r.Cell),8} {g.Max(r => r.Close ?? 0),9:F1}   {sim,11} {cells,7} {close,7}");
		}
		if (args.Length > 2 && args[2] == "--rows")
			foreach (var r in rows.OrderBy(r => r.Kind).ThenBy(r => r.Name))
				Console.WriteLine($"{r.Kind,-18} {r.Name,-6} {r.Sim,7:F2} {r.Cell,4} {r.Close,6:F1}");
		return 0;
	}

	static byte[]? Wic(string path) => WicImageDecoder.TryDecode(path, out byte[]? gray, out _, out _, out _) ? gray : null;

	static byte[]? Ffmpeg(string path) =>
		FfmpegEngine.GetThumbnail(new FfmpegSettings { File = path, Position = TimeSpan.Zero, Rgb224 = true, SoftwareDecodeOnly = true }, false) is { } rgb
			? GrayBytesUtils.FromRgb224(rgb) : null;

	static double Similarity(byte[] a, byte[] b) => 100.0 * (1.0 - GrayBytesUtils.PercentageDifference(a, b));
}
