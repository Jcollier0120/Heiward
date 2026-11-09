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
	/// block (<see cref="GrayBytesUtils.BlockDifference"/>).
	/// </summary>
	internal static class CloseLook {
		static readonly int[] Sides = { 512, 256, 128, 64 };

		/// <summary>The pictures' largest block difference (0–255) up close; 0 for pictures too small to look closer at; null when either can't be decoded.</summary>
		internal static double? Difference(string a, string b) {
			if (!WicImageDecoder.TryGetSize(a, out int wa, out int ha) || !WicImageDecoder.TryGetSize(b, out int wb, out int hb))
				return null;
			int least = Math.Min(Math.Min(wa, ha), Math.Min(wb, hb));
			int side = Sides.FirstOrDefault(s => s <= least);
			if (side == 0)
				return 0; // under 64 pixels a side, the 32×32 frame already sees the whole picture
			if (!WicImageDecoder.TryDecodeGray(a, side, out byte[]? ga) || !WicImageDecoder.TryDecodeGray(b, side, out byte[]? gb))
				return null;
			return GrayBytesUtils.BlockDifference(ga!, gb!, side);
		}
	}
}
