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
	/// The scan's pace, which the accelerators follow from their next piece of work on: the NPU's clocks
	/// (<see cref="AI.NpuPack.RunConfig"/>) and how much the GPU's video decoder takes off the CPU
	/// (<see cref="FFTools.FFmpegNative.HardwareVideoDecode"/>, <see cref="FFTools.FFmpegNative.HeifHardwareLane"/>).
	/// </summary>
	public static class Pace {
		/// <summary>True for a scan someone waits for, false in the background. True unless the agent says otherwise.</summary>
		public static volatile bool FullSpeed = true;

		/// <summary>
		/// Whether the scan may use more memory to finish sooner: more videos on the GPU's decoder at once
		/// (<see cref="FFTools.FFmpegNative.HardwareVideoDecode.Slots"/>). The agent's setting, off while a game
		/// or another 3D program runs.
		/// </summary>
		public static volatile bool MoreMemory = true;
	}
}
