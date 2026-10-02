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
	/// When a decode counts as hung: FFmpeg has kept asking the interrupt callback for the whole timeout
	/// with no progress in between. The first ask after <see cref="Progress"/> starts the timeout again,
	/// so the time the decoder spends between asks doesn't count, however slow it is (a background
	/// scan's capped processor, a busy PC), and the frames don't depend on how busy the PC was. What
	/// bounds a slow decode is the work it may do (the packet caps in
	/// <see cref="VideoStreamDecoder.TryDecodeFrame"/>), not the clock.
	/// </summary>
	sealed class ProgressDeadline {
		readonly TimeProvider _clock;
		readonly long _timeoutTicks;
		long _deadlineTicks;
		bool _progressed = true;

		public ProgressDeadline(TimeSpan timeout, TimeProvider? clock = null) {
			_clock = clock ?? TimeProvider.System;
			_timeoutTicks = (long)(timeout.TotalSeconds * _clock.TimestampFrequency);
		}

		/// <summary>Something moved: the next <see cref="Expired"/> starts the whole timeout again.</summary>
		public void Progress() => _progressed = true;

		/// <summary>
		/// The interrupt callback's question: has nothing moved for the whole timeout? The first ask
		/// after progress (or ever) starts the timeout and is never expired.
		/// </summary>
		public bool Expired() {
			long now = _clock.GetTimestamp();
			if (_progressed) {
				_progressed = false;
				_deadlineTicks = now + _timeoutTicks;
				return false;
			}
			return now > _deadlineTicks;
		}
	}
}
