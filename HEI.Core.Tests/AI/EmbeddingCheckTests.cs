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

using HEI.Core.AI;
using HEI.TestSupport;

namespace HEI.Core.Tests.AI;

/// <summary>The NPU check's comparison of its embeddings with the processor's, picture by picture (<see cref="EmbeddingCheck"/>).</summary>
public class EmbeddingCheckTests {
	/// <summary>Unit vectors with nothing in common, as a wrong slot's are with the right ones.</summary>
	static float[][] Unrelated(int count, int seed) {
		var rng = new Random(seed);
		return Enumerable.Range(0, count).Select(_ => Unit(Enumerable.Range(0, EmbeddingMath.Dimensions).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray())).ToArray();
	}

	static float[] Unit(float[] v) {
		float norm = MathF.Sqrt(v.Sum(x => x * x));
		return v.Select(x => x / norm).ToArray();
	}

	/// <summary><paramref name="v"/> turned away from itself until its cosine to it is about <paramref name="cosine"/>.</summary>
	static float[] Near(float[] v, double cosine, int seed) {
		float[] other = Unrelated(1, seed)[0];
		// Gram-Schmidt: the part of other that's orthogonal to v, then v·cos + that·sin.
		double along = v.Zip(other, (a, b) => (double)a * b).Sum();
		float[] orthogonal = Unit(other.Select((x, i) => (float)(x - along * v[i])).ToArray());
		double sin = Math.Sqrt(1 - cosine * cosine);
		return v.Select((x, i) => (float)(x * cosine + orthogonal[i] * sin)).ToArray();
	}

	[Theory]
	[InlineData(8, 9)]
	[InlineData(1, 2)]
	[InlineData(0, 2)] // a dynamic batch: two runs of one
	public void The_pictures_fill_a_run_and_start_another_and_no_two_are_alike(int batch, int pictures) {
		byte[][] made = EmbeddingCheck.Pictures(batch);
		Assert.Equal(pictures, made.Length);
		Assert.All(made, p => Assert.Equal(OnnxEmbedder.InputSide * OnnxEmbedder.InputSide * 3, p.Length));
		for (int i = 0; i < made.Length; i++)
			for (int j = i + 1; j < made.Length; j++)
				Assert.False(made[i].AsSpan().SequenceEqual(made[j]));
		Assert.Equal(made[^1], EmbeddingCheck.Picture(made.Length - 1)); // the same pictures every time
	}

	[Fact]
	public void Embeddings_like_the_processor_s_pass() {
		byte[][] pictures = EmbeddingCheck.Pictures(8);
		using var device = new OnnxEmbedder(TestModels.TinyEmbedderPath);
		using var processor = new OnnxEmbedder(TestModels.TinyEmbedderPath);

		EmbeddingCheck.Result result = EmbeddingCheck.Compare(device.EmbedBatch(pictures), processor.EmbedBatch(pictures), 8);

		Assert.True(result.Passed);
		Assert.Empty(result.Wrong);
		Assert.Equal(1.0, result.Worst, 4);
		Assert.Equal("its embeddings match the processor's: 9 pictures, 2 runs of 8, cosine 1.0000 or more", result.Summary);
	}

	[Fact]
	public void A_run_of_8_that_gets_only_its_first_picture_right_fails_and_says_which_slots() {
		// As Vitis AI 1.8.75.0 did on a Ryzen AI 7 350, compiled for 8: the first of each run right, the other seven not.
		float[][] processor = Unrelated(9, seed: 1), wrong = Unrelated(9, seed: 2);
		float[][] npu = processor.Select((v, i) => i % 8 == 0 ? Near(v, 0.998, seed: 100 + i) : wrong[i]).ToArray();

		EmbeddingCheck.Result result = EmbeddingCheck.Compare(npu, processor, 8);

		Assert.False(result.Passed);
		Assert.Equal(Enumerable.Range(1, 7), result.Wrong);
		Assert.StartsWith("7 of 9 pictures came back wrong, in batch slots 2-8 of 8 (cosine to the processor's down to ", result.Summary);
		Assert.EndsWith("; 0.98 needed)", result.Summary);
	}

	[Theory]
	[InlineData(0.9979, true)] // Vitis AI's BF16 at its worst over 1,024 of these pictures
	[InlineData(0.985, true)]
	[InlineData(0.97, false)]
	[InlineData(0.27, false)]
	[InlineData(-1.0, false)]
	public void Only_a_picture_close_to_the_processor_s_counts_as_right(double cosine, bool right) {
		float[][] processor = Unrelated(2, seed: 3);
		float[][] npu = { processor[0], Near(processor[1], cosine, seed: 4) };
		Assert.Equal(cosine, EmbeddingCheck.Cosine(npu[1], processor[1]), 4);

		EmbeddingCheck.Result result = EmbeddingCheck.Compare(npu, processor, 1);

		Assert.Equal(right, result.Passed);
		if (!right) Assert.StartsWith("1 of 2 pictures came back wrong, in batch slot 1 of 1 ", result.Summary);
	}

	[Fact]
	public void An_empty_unreadable_or_short_embedding_is_wrong_not_skipped() {
		float[][] processor = Unrelated(4, seed: 5);
		float[][] npu = {
			new float[EmbeddingMath.Dimensions], // all zeros
			Enumerable.Repeat(float.NaN, EmbeddingMath.Dimensions).ToArray(),
			processor[2][..100],
			processor[3],
		};

		EmbeddingCheck.Result result = EmbeddingCheck.Compare(npu, processor, 4);

		Assert.Equal(new[] { 0, 1, 2 }, result.Wrong);
		Assert.Equal("3 of 4 pictures came back wrong, in batch slots 1-3 of 4 (3 with nothing to compare: empty, or not the processor's length; 0.98 needed)", result.Summary);
	}

	[Fact]
	public void Slots_apart_are_listed_apart() {
		float[][] processor = Unrelated(9, seed: 6), wrong = Unrelated(9, seed: 7);
		int[] bad = { 0, 2, 4, 5 };
		float[][] npu = processor.Select((v, i) => bad.Contains(i) ? wrong[i] : v).ToArray();

		Assert.Contains("in batch slots 1, 3, 5, 6 of 8", EmbeddingCheck.Compare(npu, processor, 8).Summary);
	}

	[Fact]
	public void Embeddings_for_other_pictures_can_t_be_compared() =>
		Assert.Throws<ArgumentException>(() => EmbeddingCheck.Compare(Unrelated(3, 8), Unrelated(2, 8), 1));
}
