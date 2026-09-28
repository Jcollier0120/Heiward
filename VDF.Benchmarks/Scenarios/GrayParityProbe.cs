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

using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VDF.Benchmarks.Scenarios;

/// <summary>
/// <c>--probe-gray &lt;folder&gt; [n]</c>: the same photos through WIC and through FFmpeg must give
/// matching 32×32 gray frames, or a JPEG (WIC) and its HEIC original (FFmpeg) never reach the
/// plain-copy similarity. Compares FFmpeg's own gray frame and the RGB-derived one
/// (<see cref="GrayBytesUtils.FromRgb224"/>, what every photo's gray frame is) against WIC's.
/// Measured on 30 camera JPEGs: FFmpeg's own gray 96.35% (worst 93.53%), RGB-derived 99.85% (worst 99.79%).
/// </summary>
public static class GrayParityProbe {
	public static int Run(string[] args) {
		if (args.Length < 2 || !Directory.Exists(args[1])) {
			Console.Error.WriteLine("usage: --probe-gray <folder> [photos, default 20]");
			return 2;
		}
		int n = args.Length > 2 ? int.Parse(args[2]) : 20;
		var own = new List<double>();
		var derived = new List<double>();
		foreach (string f in Directory.EnumerateFiles(args[1], "*.jpg").Take(n)) {
			if (!WicImageDecoder.TryDecode(f, out byte[]? wicGray, out _, out _, out _) || wicGray == null) continue;
			byte[]? ffGray = FfmpegEngine.GetThumbnail(new FfmpegSettings { File = f, Position = TimeSpan.Zero, GrayScale = 1, SoftwareDecodeOnly = true }, false);
			byte[]? ffRgb = FfmpegEngine.GetThumbnail(new FfmpegSettings { File = f, Position = TimeSpan.Zero, Rgb224 = true, SoftwareDecodeOnly = true }, false);
			if (ffGray != null) own.Add(Similarity(wicGray, ffGray));
			if (ffRgb != null) derived.Add(Similarity(wicGray, GrayBytesUtils.FromRgb224(ffRgb)));
		}
		Report("FFmpeg's own gray frame", own);
		Report("gray derived from FFmpeg's RGB frame", derived);
		return 0;
	}

	static double Similarity(byte[] a, byte[] b) => 100.0 * (1.0 - GrayBytesUtils.PercentageDifference(a, b));

	static void Report(string label, List<double> sims) {
		if (sims.Count > 0)
			Console.WriteLine($"WIC vs {label,-38}: {sims.Count} photos, mean {sims.Average():F2}%, worst {sims.Min():F2}%");
	}
}
