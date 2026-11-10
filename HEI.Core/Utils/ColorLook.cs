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

namespace HEI.Core.Utils {
	/// <summary>
	/// Two photos' colours compared, which every gray frame leaves out: a black-and-white copy or a warmer one matches its
	/// original's gray frames and close look pixel for pixel. Both are decoded at <see cref="Side"/>×<see cref="Side"/>
	/// (squashed and turned upright like the gray frames), and each of <see cref="Cells"/>×<see cref="Cells"/> cells is
	/// compared by its average colour apart from its brightness: blue minus gray and red minus gray.
	/// </summary>
	internal static class ColorLook {
		/// <summary>The side the colours are compared at. At 32 a resize's colours came up to 7.6 apart in a cell, at 64 4.3.</summary>
		internal const int Side = 64;
		/// <summary>Cells a side: a cell covers an eighth of the picture each way, so a colour change in one part of it still shows.</summary>
		internal const int Cells = 8;

		/// <summary>The largest colour difference (0–255) of any cell of the two pictures; null when either can't be decoded.</summary>
		internal static double? Difference(string a, string b) =>
			Frame(a) is { } ca && Frame(b) is { } cb ? Difference(ca, cb, Side) : null;

		/// <summary>The picture's <see cref="Side"/>×<see cref="Side"/> frame of 24-bit BGR, compared by <see cref="Difference(byte[], byte[], int)"/>; null when it can't be decoded.</summary>
		internal static byte[]? Frame(string path) => WicImageDecoder.TryDecodeColor(path, Side, out byte[]? bgr) ? bgr : null;

		/// <summary>
		/// The largest colour difference (0–255) of any cell of two <paramref name="side"/>×<paramref name="side"/> frames of
		/// 24-bit BGR: how far the cell's average blue-minus-gray or red-minus-gray moved, whichever moved more. Brightness
		/// alone moves neither, so this sees what the gray frames can't.
		/// </summary>
		internal static double Difference(byte[] a, byte[] b, int side) {
			if (a.Length != b.Length || a.Length != side * side * 3 || side % Cells != 0)
				throw new ArgumentException($"Expected two {side}×{side} BGR frames, a multiple of {Cells} on a side.");
			int cell = side / Cells;
			var blue = new double[Cells * Cells];
			var red = new double[Cells * Cells];
			for (int y = 0; y < side; y++) {
				int row = y / cell * Cells;
				for (int x = 0; x < side; x++) {
					int i = (y * side + x) * 3, c = row + x / cell;
					var (cbA, crA) = Chroma(a, i);
					var (cbB, crB) = Chroma(b, i);
					blue[c] += cbA - cbB;
					red[c] += crA - crB;
				}
			}
			double worst = 0;
			for (int c = 0; c < blue.Length; c++)
				worst = Math.Max(worst, Math.Max(Math.Abs(blue[c]), Math.Abs(red[c])));
			return worst / (cell * cell);

			// Blue and red apart from the pixel's brightness (Rec. 601 luma, as the gray frames weigh them).
			static (double Blue, double Red) Chroma(byte[] bgr, int i) {
				double y = 0.114 * bgr[i] + 0.587 * bgr[i + 1] + 0.299 * bgr[i + 2];
				return (bgr[i] - y, bgr[i + 2] - y);
			}
		}
	}
}
