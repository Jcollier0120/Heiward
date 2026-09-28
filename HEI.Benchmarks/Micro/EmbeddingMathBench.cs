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

using BenchmarkDotNet.Attributes;
using HEI.Core.AI;

namespace HEI.Benchmarks.Micro;

/// <summary>
/// The AI pass's per-pair cost: cosine of two int8-quantized 384-d embeddings. It runs for every
/// pair the classic check rejects, i.e. nearly every pair of a library.
/// </summary>
[MemoryDiagnoser]
public class EmbeddingMathBench {
	byte[] _a = null!;
	byte[] _b = null!;

	[GlobalSetup]
	public void Setup() {
		var rng = new Random(42);
		_a = Quantized(rng);
		_b = Quantized(rng);
	}

	static byte[] Quantized(Random rng) {
		var v = new float[EmbeddingMath.Dimensions];
		double norm = 0;
		for (int i = 0; i < v.Length; i++) { v[i] = (float)(rng.NextDouble() * 2 - 1); norm += v[i] * v[i]; }
		for (int i = 0; i < v.Length; i++) v[i] /= (float)Math.Sqrt(norm);
		return EmbeddingMath.QuantizeUnitVector(v);
	}

	[Benchmark(Baseline = true)]
	public float CosineSimilarityScalar() => EmbeddingMath.CosineSimilarityScalar(_a, _b);

	[Benchmark]
	public float CosineSimilarity() => EmbeddingMath.CosineSimilarity(_a, _b);
}
