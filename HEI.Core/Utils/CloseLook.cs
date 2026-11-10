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

using System.Linq;

namespace HEI.Core.Utils {
	/// <summary>
	/// Two photos compared closer than their 32×32 gray frames: decoded again at the largest of 512, 256, 128 or 64
	/// pixels a side that both have (so a small copy isn't held against detail it never had), and compared block by
	/// block (<see cref="GrayBytesUtils.BlockDifference"/>). From the same decode, each picture's <see cref="Detail(byte[], int)"/>:
	/// a blur over the whole photo keeps every block's average, but not its detail.
	/// </summary>
	internal static class CloseLook {
		static readonly int[] Sides = { 512, 256, 128, 64 };

		/// <summary>
		/// Two photos looked at up close: their largest block difference (0–255), the side they were decoded at (0: too small to
		/// look closer at), and each one's detail at that side (<see cref="Detail(byte[], int)"/>; 0 at side 0).
		/// </summary>
		internal readonly record struct Result(double Difference, int Side, double DetailA, double DetailB);

		/// <summary>The pictures' largest block difference (0–255) up close; 0 for pictures too small to look closer at; null when either can't be decoded.</summary>
		internal static double? Difference(string a, string b) => Look(a, b)?.Difference;

		/// <summary>The pictures looked at up close (<see cref="Result"/>), both decoded once; null when either can't be decoded.</summary>
		internal static Result? Look(string a, string b) {
			if (SideFor(a, b) is not int side)
				return null;
			if (side == 0)
				return new Result(0, 0, 0, 0); // under 64 pixels a side, the 32×32 frame already sees the whole picture
			if (!WicImageDecoder.TryDecodeGray(a, side, out byte[]? ga) || !WicImageDecoder.TryDecodeGray(b, side, out byte[]? gb))
				return null;
			return new Result(GrayBytesUtils.BlockDifference(ga!, gb!, side), side, Detail(ga!, side), Detail(gb!, side));
		}

		/// <summary>The side two photos are looked at up close: the largest of 512, 256, 128 and 64 both have; 0 under 64; null when either's size can't be read.</summary>
		internal static int? SideFor(string a, string b) {
			if (!WicImageDecoder.TryGetSize(a, out int wa, out int ha) || !WicImageDecoder.TryGetSize(b, out int wb, out int hb))
				return null;
			int least = Math.Min(Math.Min(wa, ha), Math.Min(wb, hb));
			return Sides.FirstOrDefault(s => s <= least);
		}

		/// <summary>The picture's <see cref="Detail(byte[], int)"/> decoded at <paramref name="side"/>, as <see cref="Look"/> decodes it; null when it can't be decoded.</summary>
		internal static double? Detail(string path, int side) =>
			WicImageDecoder.TryDecodeGray(path, side, out byte[]? gray) ? Detail(gray!, side) : null;

		/// <summary>
		/// How much detail a <paramref name="side"/>×<paramref name="side"/> gray frame holds: the average step (0–510) from each
		/// pixel to the next one right and the next one down. A blur lowers it; brightness alone doesn't move it.
		/// </summary>
		internal static double Detail(byte[] gray, int side) {
			if (gray.Length != side * side || side < 2)
				throw new ArgumentException($"Expected a {side}×{side} gray frame.", nameof(gray));
			long sum = 0;
			for (int y = 0; y < side - 1; y++)
				for (int x = 0, i = y * side; x < side - 1; x++, i++)
					sum += Math.Abs(gray[i + 1] - gray[i]) + Math.Abs(gray[i + side] - gray[i]);
			return sum / (double)((side - 1) * (side - 1));
		}

		/// <summary>How much of the sharper picture's detail the softer one keeps, 0 to 1: 1 for two as sharp as each other, or two with none.</summary>
		internal static double DetailKept(double a, double b) => Math.Max(a, b) <= 0 ? 1 : Math.Min(a, b) / Math.Max(a, b);
	}
}
