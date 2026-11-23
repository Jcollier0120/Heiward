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

namespace HEI.Core.FFTools.FFmpegNative {
	/// <summary>
	/// What reaching one position of a video may cost: the frames decoded on from the keyframe its seek lands on, and the
	/// time that takes. A video is sampled by seeking to a position and decoding forward to it, which costs nothing much
	/// while keyframes are a second or two apart, as cameras, phones and game captures write them. Some videos have one
	/// keyframe for the whole of them (a screen recording, a timelapse): every position is decoded to from the start. On a
	/// scan of another PC one such 4K video took 21 minutes, 10,000 frames decoded twice (the graphics card's decoder, then
	/// the processor's) and none sampled, while the scan waited for it to end.
	/// Past either bound, the position can't be reached (<see cref="KeyframesTooFarException"/>), and the video is left out,
	/// as one that can't be decoded is: one file never holds a scan up.
	/// </summary>
	internal sealed class ForwardBudget {
		/// <summary>
		/// 5 minutes of a video at 30 frames a second: far more than a camera's or a recorder's keyframes are apart (x264's default
		/// is 250 frames). Just short of the decoder's own 10,000 passes, which ended such a decode as a failure, tried again on the
		/// processor and then by the FFmpeg process: this ends it as what it is.
		/// </summary>
		internal const int DefaultFrames = 9_000;
		/// <summary>
		/// However few frames: in the background with its processor capped, a 4K frame can take a tenth of a second. A video whose
		/// keyframes are a few seconds apart is reached in a few seconds even so.
		/// </summary>
		internal static readonly TimeSpan DefaultTime = TimeSpan.FromSeconds(60);

		readonly int maxFrames;
		readonly long maxTicks;
		readonly TimeProvider clock;
		long started;

		public ForwardBudget(int maxFrames = DefaultFrames, TimeSpan? maxTime = null, TimeProvider? clock = null) {
			this.maxFrames = maxFrames;
			this.clock = clock ?? TimeProvider.System;
			maxTicks = (long)((maxTime ?? DefaultTime).TotalSeconds * this.clock.TimestampFrequency);
		}

		/// <summary>Frames decoded on since the last <see cref="Start"/>.</summary>
		public int Decoded { get; private set; }

		/// <summary>Time since the last <see cref="Start"/>.</summary>
		public TimeSpan Elapsed => clock.GetElapsedTime(started);

		/// <summary>A new position: nothing decoded for it yet.</summary>
		public void Start() {
			Decoded = 0;
			started = clock.GetTimestamp();
		}

		/// <summary>One more frame decoded on, short of the position: throws once that's past the bound.</summary>
		public void Decode(double position) {
			Decoded++;
			if (Decoded > maxFrames || clock.GetTimestamp() - started > maxTicks)
				throw new KeyframesTooFarException(
					$"its keyframes are too far apart: {Decoded:N0} frames decoded in {Elapsed.TotalSeconds:N1} s didn't reach {position:N1} s");
		}
	}

	/// <summary>A position of a video that its <see cref="ForwardBudget"/> doesn't reach: the video is left out of the scan.</summary>
	internal sealed class KeyframesTooFarException(string message) : Exception(message);
}
