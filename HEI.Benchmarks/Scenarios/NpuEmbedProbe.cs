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

using System.Diagnostics;
using HEI.Core.AI;
using HEI.Core.Utils;

namespace HEI.Benchmarks.Scenarios;

/// <summary>
/// The NPU embedder on its own, with synthetic frames: how long opening it takes (compiling the graph,
/// or loading the cached one) and how fast it embeds, for the batch size HEI_NPU_BATCH sets (the pack's
/// own by default), at full speed or at a background scan's pace (--pace background).
/// --save writes the embeddings and --compare reports how close they are to a saved run's (cosine), so
/// two batch sizes or paces can be checked against each other.
///
///   HEI.Benchmarks --probe-npu --state &lt;folder holding ai\&gt; [--frames 1024] [--pace background] [--save file] [--compare file]
/// </summary>
static class NpuEmbedProbe {
	public static int Run(string[] args) {
		string? Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
		if (Arg("--state") is { } state)
			CoreUtils.UseStateFolder(state);
		// The scan's pace, which sets how hard the NPU works (NpuPack.RunConfig).
		NpuComponents.FullSpeed = Arg("--pace") != "background";
		int count = int.Parse(Arg("--frames") ?? "1024");
		byte[][] frames = Enumerable.Range(0, count).Select(Frame).ToArray();

		var open = Stopwatch.StartNew();
		using OnnxEmbedder embedder = OnnxEmbedder.Create(AiDevice.Npu);
		open.Stop();
		Console.WriteLine($"{embedder.DeviceName}, batch {embedder.FixedBatch}; opened in {open.Elapsed.TotalSeconds:N2} s");
		if (embedder.DeviceName != "NPU") {
			Console.Error.WriteLine("No NPU: nothing to measure.");
			return 1;
		}

		// Warm up (first runs set up buffers and clocks), then measure.
		embedder.EmbedBatch(frames.Take(Math.Max(embedder.FixedBatch, 1) * 4).ToList());
		EmbedderStats s = embedder.Stats;
		(long runs, long images, long model, long input, long lockTicks) before = (s.Runs, s.Images, s.ModelTicks, s.InputTicks, s.LockTicks);
		var wall = Stopwatch.StartNew();
		float[][] embeddings = embedder.EmbedBatch(frames);
		wall.Stop();
		embedder.YieldNpu();

		long runs = s.Runs - before.runs, images = s.Images - before.images;
		double model = EmbedderStats.Seconds(s.ModelTicks - before.model), input = EmbedderStats.Seconds(s.InputTicks - before.input);
		Console.WriteLine($"{images:N0} frames in {runs:N0} runs: {images / wall.Elapsed.TotalSeconds:N0} frames/s overall, " +
			$"{model * 1000 / runs:N2} ms per run ({model * 1000 / images:N2} ms per frame), input {input * 1000 / runs:N3} ms per run, " +
			$"NPU lock {EmbedderStats.Seconds(s.LockTicks - before.lockTicks) * 1000:N0} ms in all");

		if (Arg("--save") is { } save) {
			using var w = new BinaryWriter(File.Create(save));
			w.Write(embeddings.Length);
			w.Write(embeddings[0].Length);
			foreach (float[] e in embeddings)
				foreach (float v in e)
					w.Write(v);
			Console.WriteLine($"Saved to {save}");
		}
		if (Arg("--compare") is { } compare) {
			using var r = new BinaryReader(File.OpenRead(compare));
			int n = r.ReadInt32(), dim = r.ReadInt32();
			var cosines = new double[Math.Min(n, embeddings.Length)];
			for (int i = 0; i < n; i++) {
				double dot = 0;
				for (int d = 0; d < dim; d++) {
					float v = r.ReadSingle();
					if (i < cosines.Length)
						dot += v * embeddings[i][d];
				}
				if (i < cosines.Length)
					cosines[i] = dot; // both are unit vectors
			}
			Array.Sort(cosines);
			Console.WriteLine($"Cosine to {Path.GetFileName(compare)}: min {cosines[0]:F5}, median {cosines[cosines.Length / 2]:F5}, mean {cosines.Average():F5}");
		}
		return 0;
	}

	/// <summary>A deterministic 224×224 RGB24 frame with smooth regions, edges and fine noise, so no two embed alike.</summary>
	static byte[] Frame(int seed) {
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
}
