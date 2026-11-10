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

namespace HEI.TestSupport;

/// <summary>One made-up file: its gray frames (32×32, one per sampled position) and int8 embeddings.</summary>
public sealed record SyntheticFile(string Path, string Folder, bool IsImage, double DurationSeconds, long FileSize, byte[][] Frames, byte[]?[] Embeddings);

/// <summary>
/// Made-up photo and video libraries shaped like real ones, for the compare phase's bench and its
/// side-by-side proof: smooth low-frequency gray thumbnails over a wide brightness range (night
/// shots and blown-out skies included), bursts of similar shots, resaved copies in other folders,
/// and DINOv2-like embeddings (a shared mean direction, a low-rank scene part, per-shot noise).
/// Pairs are planted right at the thresholds: exactly at the gray limit and one unit past it, plain
/// and mirrored, and embeddings a hair above and below the AI threshold. Seeded: the same arguments
/// give the same library.
/// </summary>
public static class SyntheticLibrary {
	public const int Side = 32;
	public const int FrameLength = Side * Side;
	public const int EmbeddingDimensions = 384;
	const int LatentDimensions = 32;

	/// <summary>The largest L1 (sum of absolute differences) of one frame that still matches at <paramref name="percent"/>.</summary>
	public static int GrayLimit(float percent) => (int)Math.Floor((double)(1.0f - percent / 100f) * 262144);

	public static List<SyntheticFile> Photos(int count, int seed, float percent = 96f, float aiPercent = 94f) =>
		Build(count, seed, positions: 1, images: true, percent, aiPercent);

	public static List<SyntheticFile> Videos(int count, int seed, int positions, float percent = 96f, float aiPercent = 94f) =>
		Build(count, seed, positions, images: false, percent, aiPercent);

	static List<SyntheticFile> Build(int count, int seed, int positions, bool images, float percent, float aiPercent) {
		var rng = new Random(seed);
		var world = new EmbeddingWorld(new Random(seed ^ 0x5EED));
		var files = new List<SyntheticFile>(count);
		int grayLimit = GrayLimit(percent) * positions;
		double aiCos = aiPercent / 100.0;
		int album = 0, shot = 0;

		while (files.Count < count) {
			album++;
			string folder = images ? $@"C:\synthetic\{2010 + album % 15}\album{album:D5}" : $@"C:\synthetic\videos\set{album:D5}";
			int kind = rng.Next(100);
			if (kind < 6) {
				// Planted pairs at the thresholds: one at the limit, one a unit past it, plain or mirrored.
				var baseFile = NewScene(rng, world, positions, images);
				bool mirrored = rng.Next(2) == 0;
				int over = rng.Next(3) == 0 ? 1 : 0;
				byte[][] targetFrames = mirrored ? baseFile.Frames.Select(Mirror).ToArray() : baseFile.Frames;
				byte[][] twin = AtDistance(rng, targetFrames, grayLimit + over);
				double cos = aiCos + (rng.Next(3) - 1) * 0.002;
				Add(files, folder, ref shot, images, baseFile.Duration, baseFile.Frames, baseFile.Embeddings, rng);
				Add(files, folder, ref shot, images, baseFile.Duration * (1 + (rng.NextDouble() - 0.5) * 0.004), twin,
					baseFile.Embeddings.Select(e => e == null ? null : world.AtCosine(rng, e, cos)).ToArray(), rng);
				continue;
			}

			// A scene: mostly singletons, some pairs and triples, a few bursts.
			int size = kind < 60 ? 1 : kind < 85 ? rng.Next(2, 4) : kind < 97 ? rng.Next(4, 9) : rng.Next(9, 26);
			var scene = NewScene(rng, world, positions, images);
			double spread = 2 + rng.NextDouble() * 14; // how much the burst's shots differ
			for (int m = 0; m < size && files.Count < count; m++) {
				var frames = m == 0 ? scene.Frames : scene.Frames.Select(f => Vary(rng, f, spread)).ToArray();
				var embeddings = scene.Embeddings.Select(e => e == null ? null : world.Near(rng, e, m == 0 ? 0 : 0.15 + spread / 30)).ToArray();
				double duration = scene.Duration * (1 + (rng.NextDouble() - 0.5) * 0.004);
				Add(files, folder, ref shot, images, duration, frames, embeddings, rng);
				// A resaved copy in another folder now and then.
				if (rng.Next(20) == 0 && files.Count < count) {
					var copy = frames.Select(f => Vary(rng, f, 0.6)).ToArray();
					Add(files, $@"C:\synthetic\copies\from{album:D5}", ref shot, images, duration, copy,
						embeddings.Select(e => e == null ? null : world.Near(rng, e, 0.05)).ToArray(), rng);
				}
			}
		}
		return files;
	}

	static void Add(List<SyntheticFile> files, string folder, ref int shot, bool images, double duration, byte[][] frames, byte[]?[] embeddings, Random rng) {
		shot++;
		string name = images ? $"IMG_{shot:D6}.jpg" : $"VID_{shot:D6}.mp4";
		// One in fifty failed to embed.
		var kept = embeddings.Select(e => rng.Next(50) == 0 ? null : e).ToArray();
		files.Add(new SyntheticFile($@"{folder}\{name}", folder, images, images ? 0 : duration, 500_000 + rng.Next(8_000_000), frames, kept));
	}

	sealed record Scene(byte[][] Frames, byte[]?[] Embeddings, double Duration);

	static Scene NewScene(Random rng, EmbeddingWorld world, int positions, bool images) {
		var frames = new byte[positions][];
		var embeddings = new byte[]?[positions];
		for (int j = 0; j < positions; j++) {
			frames[j] = NewFrame(rng);
			embeddings[j] = world.NewScene(rng);
		}
		double duration = images ? 0 : rng.Next(3) switch {
			0 => 3 + rng.NextDouble() * 90,                 // phone clips
			1 => 1320 + Gaussian(rng) * 60,                 // episodes
			_ => Math.Max(600, 6000 + Gaussian(rng) * 900), // films
		};
		return new Scene(frames, embeddings, duration);
	}

	/// <summary>A smooth picture: a brightness, a contrast and a few low-frequency waves, like a photo shrunk to 32×32.</summary>
	static byte[] NewFrame(Random rng) {
		int look = rng.Next(100);
		double mean = look < 75 ? 120 + Gaussian(rng) * 35
			: look < 85 ? 35 + Gaussian(rng) * 15      // dim rooms
			: look < 95 ? 205 + Gaussian(rng) * 25     // skies, snow, documents
			: 12 + Gaussian(rng) * 6;                  // night
		double contrast = 8 + rng.NextDouble() * 55;
		double[] field = Field(rng);
		var frame = new byte[FrameLength];
		for (int p = 0; p < FrameLength; p++)
			frame[p] = Clamp(mean + contrast * field[p] + Gaussian(rng) * 2);
		return frame;
	}

	/// <summary>Another shot of the same scene: a brightness shift, a low-frequency change of about <paramref name="amount"/> levels, sensor noise.</summary>
	static byte[] Vary(Random rng, byte[] frame, double amount) {
		double shift = Gaussian(rng) * amount * 0.4;
		double[] field = Field(rng);
		var result = new byte[FrameLength];
		for (int p = 0; p < FrameLength; p++)
			result[p] = Clamp(frame[p] + shift + amount * field[p] + Gaussian(rng) * Math.Min(2, amount));
		return result;
	}

	static double[] Field(Random rng) {
		var field = new double[FrameLength];
		for (int u = 0; u <= 5; u++)
			for (int v = 0; v <= 5; v++) {
				if (u == 0 && v == 0) continue;
				double c = Gaussian(rng) / Math.Pow(1 + u + v, 1.3);
				double pu = rng.NextDouble() * Math.PI, pv = rng.NextDouble() * Math.PI;
				for (int y = 0; y < Side; y++) {
					double cy = Math.Cos(Math.PI * v * (y + 0.5) / Side + pv);
					for (int x = 0; x < Side; x++)
						field[y * Side + x] += c * Math.Cos(Math.PI * u * (x + 0.5) / Side + pu) * cy;
				}
			}
		double sumSq = 0;
		foreach (double f in field) sumSq += f * f;
		double scale = 1 / Math.Sqrt(sumSq / FrameLength + 1e-12);
		for (int p = 0; p < FrameLength; p++) field[p] *= scale;
		return field;
	}

	/// <summary>Frames whose summed L1 distance from <paramref name="frames"/> is exactly <paramref name="target"/>.</summary>
	static byte[][] AtDistance(Random rng, byte[][] frames, int target) {
		var result = frames.Select(f => (byte[])f.Clone()).ToArray();
		int remaining = target;
		int total = frames.Length * FrameLength;
		var order = Enumerable.Range(0, total).OrderBy(_ => rng.Next()).ToArray();
		for (int pass = 0; remaining > 0 && pass < 64; pass++)
			foreach (int at in order) {
				if (remaining == 0) break;
				byte[] f = result[at / FrameLength];
				int p = at % FrameLength;
				int original = frames[at / FrameLength][p];
				int step = Math.Min(remaining, 1 + rng.Next(12));
				// Move further away from the original, in whichever direction has room.
				if (f[p] >= original && f[p] + step <= 255) { f[p] = (byte)(f[p] + step); remaining -= step; }
				else if (f[p] <= original && f[p] - step >= 0) { f[p] = (byte)(f[p] - step); remaining -= step; }
			}
		return result;
	}

	/// <summary>Each row reversed: the mirror image, as the flipped compare sees it.</summary>
	public static byte[] Mirror(byte[] frame) {
		var result = new byte[frame.Length];
		for (int y = 0; y < Side; y++)
			for (int x = 0; x < Side; x++)
				result[y * Side + x] = frame[y * Side + Side - 1 - x];
		return result;
	}

	static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

	static double Gaussian(Random rng) {
		double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
		return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
	}

	/// <summary>Embeddings like DINOv2's: every picture shares a mean direction, scenes differ in a low-rank part, shots add noise.</summary>
	sealed class EmbeddingWorld {
		readonly double[] mean;
		readonly double[][] basis; // LatentDimensions × EmbeddingDimensions
		// Scene variance falls off over the latent directions, as a real embedding space's does.
		static double LatentWeight(int k) => 1 / (1 + 0.15 * k);
		static readonly double latentWeightSum = Enumerable.Range(0, LatentDimensions).Sum(LatentWeight);

		public EmbeddingWorld(Random rng) {
			mean = Normalize(Enumerable.Range(0, EmbeddingDimensions).Select(_ => Gaussian(rng)).ToArray());
			basis = new double[LatentDimensions][];
			for (int k = 0; k < LatentDimensions; k++)
				basis[k] = Enumerable.Range(0, EmbeddingDimensions).Select(_ => Gaussian(rng) / Math.Sqrt(EmbeddingDimensions)).ToArray();
		}

		public byte[] NewScene(Random rng) {
			var v = new double[EmbeddingDimensions];
			for (int d = 0; d < EmbeddingDimensions; d++) v[d] = 0.55 * mean[d] + Gaussian(rng) * 0.25 / Math.Sqrt(EmbeddingDimensions);
			for (int k = 0; k < LatentDimensions; k++) {
				double z = Gaussian(rng) * 0.92 * Math.Sqrt(LatentWeight(k) / latentWeightSum);
				for (int d = 0; d < EmbeddingDimensions; d++) v[d] += z * basis[k][d];
			}
			return Quantize(Normalize(v));
		}

		/// <summary>A vector about <paramref name="noise"/> (as a fraction of the length) away from <paramref name="q"/>.</summary>
		public byte[] Near(Random rng, byte[] q, double noise) {
			if (noise <= 0) return q;
			double[] v = Dequantize(q);
			for (int d = 0; d < EmbeddingDimensions; d++) v[d] += Gaussian(rng) * noise / Math.Sqrt(EmbeddingDimensions);
			return Quantize(Normalize(v));
		}

		/// <summary>A vector at cosine <paramref name="cos"/> from <paramref name="q"/> (before quantization).</summary>
		public byte[] AtCosine(Random rng, byte[] q, double cos) {
			double[] x = Normalize(Dequantize(q));
			double[] u = Enumerable.Range(0, EmbeddingDimensions).Select(_ => Gaussian(rng)).ToArray();
			double along = u.Zip(x, (a, b) => a * b).Sum();
			for (int d = 0; d < EmbeddingDimensions; d++) u[d] -= along * x[d];
			u = Normalize(u);
			double sin = Math.Sqrt(Math.Max(0, 1 - cos * cos));
			return Quantize(x.Zip(u, (a, b) => a * cos + b * sin).ToArray());
		}

		static double[] Dequantize(byte[] q) => q.Select(b => (sbyte)b / 127.0).ToArray();

		static double[] Normalize(double[] v) {
			double norm = Math.Sqrt(v.Sum(x => x * x));
			return v.Select(x => x / norm).ToArray();
		}

		// The same rounding as the embedding pipeline's int8 quantization.
		static byte[] Quantize(double[] v) {
			var q = new byte[v.Length];
			for (int i = 0; i < v.Length; i++)
				q[i] = unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round((float)v[i] * 127f), -127, 127));
			return q;
		}
	}
}
