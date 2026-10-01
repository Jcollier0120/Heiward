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

using HEI.Core.Utils;

namespace HEI.Core.FFTools.FFmpegNative {
	/// <summary>
	/// How many files a GPU lane takes at once at full speed on this PC, learnt as the scan goes. A file on
	/// the GPU ties up one of the scan's workers, so at full speed, where the workers fill the cores, it pays
	/// only while the GPU gets through a file about as fast as a CPU worker does. A desktop's graphics card
	/// keeps every slot; a GPU slower than the cores gives them back, down to an occasional file that keeps
	/// measuring. The averages are moving ones, so a lane follows a change (a game starting on the GPU).
	/// In the background the lanes keep their share whatever the times: the CPU is capped there, and saving
	/// it is the point.
	///
	/// Every file's time also adds to the scan's totals, for the log at any pace (<see cref="Describe"/>).
	/// </summary>
	sealed class LaneTuner {
		/// <summary>Weight of a new file's time in the moving averages.</summary>
		const double Weight = 0.15;
		/// <summary>Files of each kind before the first decision, and between decisions.</summary>
		const int Warmup = 6, Every = 8;
		/// <summary>With no slots left, one file in this many still goes to the GPU, to keep measuring.</summary>
		const int ProbeEvery = 40;
		/// <summary>The GPU gives a slot back when it takes this much longer than the CPU, and gets one when it is this much quicker.</summary>
		const double Slower = 1.2, Quicker = 0.9;

		readonly string what;
		readonly object gate = new();
		readonly int max;
		int limit;
		double gpuAverage, cpuAverage;
		int gpuSamples, cpuSamples, sinceDecision, sinceProbe;
		// The scan's totals, at any pace.
		double gpuSeconds, cpuSeconds;
		int gpuFiles, cpuFiles;

		/// <param name="what">The files, for the log: "videos", "photos".</param>
		public LaneTuner(string what, int max) {
			this.what = what;
			this.max = limit = max;
		}

		/// <summary>The most files on the GPU at once at full speed now.</summary>
		public int Limit => Volatile.Read(ref limit);

		/// <summary>With no slot left at full speed, whether this file goes to the GPU anyway, to keep measuring.</summary>
		public bool Probe() {
			lock (gate) {
				if (limit > 0 || ++sinceProbe < ProbeEvery)
					return false;
				sinceProbe = 0;
				return true;
			}
		}

		/// <summary>A file took <paramref name="seconds"/> on the GPU (true) or the CPU.</summary>
		public void Record(bool onGpu, double seconds) {
			lock (gate) {
				if (onGpu) {
					gpuSeconds += seconds;
					gpuFiles++;
				}
				else {
					cpuSeconds += seconds;
					cpuFiles++;
				}
				if (!Pace.FullSpeed)
					return;
				if (onGpu)
					gpuAverage = gpuSamples++ == 0 ? seconds : gpuAverage + Weight * (seconds - gpuAverage);
				else
					cpuAverage = cpuSamples++ == 0 ? seconds : cpuAverage + Weight * (seconds - cpuAverage);
				if (gpuSamples < Warmup || cpuSamples < Warmup || ++sinceDecision < Every)
					return;
				sinceDecision = 0;
				int before = limit;
				if (gpuAverage > cpuAverage * Slower && limit > 0)
					limit--;
				else if (gpuAverage < cpuAverage * Quicker && limit < max)
					limit++;
				if (limit != before)
					Logger.Instance.Info($"GPU decoding of {what}: {gpuAverage:N2} s a file against {cpuAverage:N2} s on the CPU, so {limit} at once at full speed.");
			}
		}

		/// <summary>A new scan: totals start over; what was learnt about this PC stays.</summary>
		public void ResetTotals() {
			lock (gate) {
				gpuSeconds = cpuSeconds = 0;
				gpuFiles = cpuFiles = 0;
			}
		}

		/// <summary>For the scan's log: "; on the GPU 0.41 s a file, on the CPU 0.62 s", or empty when one side had none.</summary>
		public string Describe() {
			lock (gate) {
				if (gpuFiles == 0 || cpuFiles == 0)
					return "";
				return $"; {gpuSeconds / gpuFiles:N2} s a file on the GPU, {cpuSeconds / cpuFiles:N2} s on the CPU";
			}
		}
	}
}
