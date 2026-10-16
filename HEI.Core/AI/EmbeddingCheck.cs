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

using System.Globalization;
using System.Linq;

namespace HEI.Core.AI {
	/// <summary>
	/// Whether an accelerator's embeddings are right: the same pictures through it and through the FP32 model on the CPU,
	/// compared picture by picture. A plugin can compile the model, run it and still give wrong embeddings, with nothing to
	/// say so: Windows ML's Vitis AI 1.8.75.0 on a Ryzen AI 7 350, compiled for 8 pictures a run, gave the first picture of
	/// each run its embedding (cosine 0.998 to the CPU's) and the other seven ones like no picture's (0.17–0.27), 2026-10-15.
	/// The pictures fill a run and start another, so every slot of a run is checked, and a padded run too.
	/// </summary>
	internal static class EmbeddingCheck {
		/// <summary>
		/// The lowest cosine to the CPU's embedding a picture may have. Vitis AI's BF16 on that NPU gave 0.9979 or more for
		/// 1,024 of these pictures at one a run; a wrong slot gave 0.17–0.27.
		/// </summary>
		public const double MinCosine = 0.98;

		/// <summary>The pictures for an accelerator that runs <paramref name="batch"/> a run (0: any): a full run and one more.</summary>
		public static byte[][] Pictures(int batch) => Enumerable.Range(0, RunSize(batch) + 1).Select(Picture).ToArray();

		/// <summary>The pictures in one run: the accelerator's static batch, or one for a dynamic one.</summary>
		public static int RunSize(int batch) => Math.Max(batch, 1);

		/// <summary>A deterministic 224×224 RGB24 picture with smooth regions, edges and fine noise, so no two embed alike.</summary>
		public static byte[] Picture(int seed) {
			const int side = OnnxEmbedder.InputSide;
			var rng = new Random(seed);
			var frame = new byte[side * side * 3];
			int cx = rng.Next(side), cy = rng.Next(side), radius = 20 + rng.Next(80);
			for (int y = 0; y < side; y++)
				for (int x = 0; x < side; x++) {
					int i = (y * side + x) * 3;
					bool disc = (x - cx) * (x - cx) + (y - cy) * (y - cy) < radius * radius;
					frame[i] = (byte)(disc ? 230 : x + seed * 13);
					frame[i + 1] = (byte)(y * 2 + seed * 7);
					frame[i + 2] = (byte)(disc ? 40 : 128 + rng.Next(-24, 25));
				}
			return frame;
		}

		/// <summary>What the comparison found.</summary>
		/// <param name="Cosines">Each picture's cosine to the CPU's embedding of it (NaN where there was nothing to compare).</param>
		/// <param name="Batch">The pictures in one of the accelerator's runs.</param>
		public sealed record Result(IReadOnlyList<double> Cosines, int Batch) {
			/// <summary>The pictures whose embedding is wrong, by index.</summary>
			public IReadOnlyList<int> Wrong { get; } = Enumerable.Range(0, Cosines.Count).Where(i => !(Cosines[i] >= MinCosine)).ToArray();
			public bool Passed => Wrong.Count == 0;
			/// <summary>The lowest cosine of the pictures that had one; NaN when none did.</summary>
			public double Worst => Cosines.Where(c => !double.IsNaN(c)).DefaultIfEmpty(double.NaN).Min();

			/// <summary>One line, for the probe's answer and a failed check's reason.</summary>
			public string Summary => Passed
				? $"its embeddings match the processor's: {Cosines.Count} pictures, {Runs} {(Runs == 1 ? "run" : "runs")} of {Batch}, cosine {Number(Worst, 4)} or more"
				: $"{Wrong.Count} of {Cosines.Count} pictures came back wrong, in batch {(Slots.Count == 1 ? "slot" : "slots")} {Ranges(Slots)} of {Batch} " +
					$"({string.Join(", ", Details())}; {Number(MinCosine, 2)} needed)";

			IEnumerable<string> Details() {
				if (Wrong.Any(i => !double.IsNaN(Cosines[i])))
					yield return $"cosine to the processor's down to {Number(Wrong.Select(i => Cosines[i]).Where(c => !double.IsNaN(c)).Min(), 3)}";
				if (Wrong.Count(i => double.IsNaN(Cosines[i])) is > 0 and int none)
					yield return $"{none} with nothing to compare: empty, or not the processor's length";
			}

			int Runs => (Cosines.Count + Batch - 1) / Batch;
			/// <summary>Where in a run the wrong pictures were, from 1.</summary>
			IReadOnlyList<int> Slots => Wrong.Select(i => i % Batch + 1).Distinct().Order().ToArray();

			static string Number(double value, int decimals) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);

			/// <summary>"2-8", "1, 3, 5-6": plain ASCII, as a probe's answer passes through the console's code page.</summary>
			static string Ranges(IReadOnlyList<int> sorted) {
				var parts = new List<string>();
				for (int i = 0; i < sorted.Count; i++) {
					int from = sorted[i];
					while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1) i++;
					parts.Add(sorted[i] == from ? $"{from}" : sorted[i] == from + 1 ? $"{from}, {sorted[i]}" : $"{from}-{sorted[i]}");
				}
				return string.Join(", ", parts);
			}
		}

		/// <summary>
		/// The accelerator's embeddings (<paramref name="device"/>) against the CPU's of the same pictures
		/// (<paramref name="reference"/>), picture by picture.
		/// </summary>
		public static Result Compare(IReadOnlyList<float[]> device, IReadOnlyList<float[]> reference, int batch) {
			if (device.Count != reference.Count)
				throw new ArgumentException($"{device.Count} embeddings to compare with {reference.Count}.");
			return new Result(device.Select((d, i) => Cosine(d, reference[i])).ToArray(), RunSize(batch));
		}

		/// <summary>The cosine of two vectors; NaN when they can't be compared (different lengths, or one is all zeros).</summary>
		internal static double Cosine(float[] a, float[] b) {
			if (a.Length != b.Length || a.Length == 0) return double.NaN;
			double dot = 0, aa = 0, bb = 0;
			for (int i = 0; i < a.Length; i++) {
				dot += (double)a[i] * b[i];
				aa += (double)a[i] * a[i];
				bb += (double)b[i] * b[i];
			}
			return aa > 0 && bb > 0 ? dot / Math.Sqrt(aa * bb) : double.NaN;
		}
	}
}
