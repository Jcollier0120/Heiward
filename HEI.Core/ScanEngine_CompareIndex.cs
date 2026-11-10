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
// The compare phase used to judge every pair of files: 50,000 photos are 1.25 billion pairs.
// The compare index finds, for each file, the later files that CAN match it, and the compare
// loop judges only those, in the order it always did, with the same pair check. Nothing in here
// decides a match: every bound below only proves that a pair cannot match, so the pairs it skips
// are pairs the old loop rejected, and the groups, flags and differences come out identical
// (CompareIndexEquivalenceTests runs both loops side by side).
//
// The bounds, per verdict:
// - Grayscale: the difference is the frames' sum of absolute differences (L1) over the pixel
//   count. Summing a frame's pixels by blocks can only shrink that sum (|Σa − Σb| ≤ Σ|a − b|), so
//   two files within the limit are within it on their block sums too: 2×2 blocks per position
//   build a k-d tree, and 4×4 blocks are checked in its leaves.
// - Ignore black/white pixels: compared on clamped frames, where an ignored pixel can hide at most
//   IgnoredPixelSpan, so each file's reach grows by that much per ignored pixel it has.
// - Flipped: the flipped frame's own block sums, against the same tree.
// - pHash: a position matches within maxBits bits only when one of maxBits + 1 bit chunks is
//   equal (pigeonhole), and a pair needs its quorum of such positions.
// - AI: a mean similarity over positions reaches the threshold only if one position's does, and a
//   cosine threshold is a ball in the embeddings' Euclidean space. Coordinates on a few principal
//   directions can only shrink distances: 6 of them build a k-d tree, 32 are checked in its leaves.
// - Videos: every candidate also lies within the entry's duration tolerance.

using System.Runtime.InteropServices;
using HEI.Core.Utils;

namespace HEI.Core {
	public sealed partial class ScanEngine {
		/// <summary>Tests and the bench: false judges every pair, the reference the compare index is proven against.</summary>
		internal bool UseCompareIndex = true;

		/// <summary>The last compare phase: the bytes its index held, the pairs it judged and the pairs that matched.</summary>
		internal long LastCompareIndexBytes;
		internal long LastComparePairsJudged;
		internal long LastComparePairsMatched;

		// Each compare thread's candidate list, reused from row to row.
		[ThreadStatic] static List<int>? compareCandidates;

		sealed class CompareIndex {
			const int FrameLength = GrayBytesUtils.Side * GrayBytesUtils.Side;
			// Gray: 2×2 blocks of 16×16 pixels per position build the tree (a whole-frame sum per
			// position above MaxCoarsePositions), and 4×4 blocks of 8×8 pixels are checked in its leaves.
			const int BlocksPerSide = 4;
			const int Blocks = BlocksPerSide * BlocksPerSide;
			const int MaxCoarsePositions = 4;
			// AI: principal directions per embedding that build the tree and that are checked in its
			// leaves, and their scale to whole numbers.
			const int AiBoxDims = 6;
			const int AiCheckDims = 32;
			const int AiProjectionScale = 16;
			const int PrincipalSample = 1024;
			// A pHash chunk narrower than this matches too many unrelated files to be worth indexing.
			const int MinChunkBits = 8;

			readonly List<FileEntry> entries;
			readonly int count;
			readonly bool videos;
			readonly bool bucketOrder;
			readonly int positions;

			/// <summary>Some verdict has no bound: every later file is a candidate (inside the duration window, for videos).</summary>
			readonly bool unbounded;

			// Videos: durations, and the files sorted by duration for the window search.
			readonly double[]? durations;
			readonly double[]? tolerances;
			readonly int[]? byDuration;
			readonly double[]? sortedDurations;

			// Grayscale.
			readonly bool grayOn;
			readonly bool ignoreBlack, ignoreWhite, clampFrames;
			readonly int grayReach;
			readonly int coarseBlocks, coarseDims, fineDims;
			FeatureTree? grayTree;

			// pHash.
			readonly bool pHashOn;
			readonly int pHashChunks, pHashRequired;
			ulong[][][]? chunkKeys; // [position][chunk] sorted chunk values
			int[][][]? chunkIds;    // [position][chunk] the file of each value

			// AI.
			readonly bool aiOn;
			readonly int aiDotFloor;
			double aiReach;
			FeatureTree?[]? aiTrees; // per position

			public long Bytes { get; private set; }
			public bool Unbounded => unbounded;

			public CompareIndex(ScanEngine engine, List<FileEntry> entries, bool videos, bool bucketOrder, bool usePHashing, int parallelism, CancellationToken cancel) {
				Settings settings = engine.Settings;
				this.entries = entries;
				count = entries.Count;
				this.videos = videos;
				this.bucketOrder = bucketOrder;
				positions = videos ? engine.positionList.Count : 1;

				// Which verdicts can say yes: the classic check's algorithm(s), and the AI pass.
				grayOn = !videos || settings.CombineGrayscaleAndPHash || !settings.UsePHashing;
				pHashOn = videos && usePHashing && (settings.CombineGrayscaleAndPHash || settings.UsePHashing);
				aiOn = settings.UseAiMatching;
				ignoreBlack = settings.IgnoreBlackPixels;
				ignoreWhite = settings.IgnoreWhitePixels;
				clampFrames = ignoreBlack || ignoreWhite;
				coarseBlocks = positions <= MaxCoarsePositions ? 4 : 1;
				coarseDims = positions * coarseBlocks;
				fineDims = positions * Blocks;

				if (videos) {
					durations = new double[count];
					tolerances = new double[count];
					byDuration = new int[count];
					for (int i = 0; i < count; i++) {
						durations[i] = entries[i].mediaInfo!.Duration.TotalSeconds;
						tolerances[i] = settings.GetDurationToleranceSeconds(durations[i]);
						byDuration[i] = i;
					}
					sortedDurations = (double[])durations.Clone();
					Array.Sort(sortedDurations, byDuration);
				}

				bool bounded = positions > 0;

				// Grayscale: the old loop's limit on the summed per-position differences as a whole
				// number of L1 units (a difference of 1 is 262144 of them), with room to spare for the
				// float rounding of the old sum. More room only lets more pairs through to the real check.
				float grayLimit = (1.0f - settings.Percent / 100f) * positions;
				if (float.IsNaN(grayLimit)) bounded = false;
				int maxL1 = 255 * FrameLength * Math.Max(1, positions);
				double reach = Math.Floor(grayLimit * 262144.0 * (1 + 1e-6 * (positions + 2))) + positions + 1;
				grayReach = (int)Math.Clamp(reach, -1, maxL1 + 1);

				// pHash: the same per-position bit limit and quorum TryComparePHashes applies.
				double pHashPercent = settings.Percent / 100f;
				if (pHashOn) {
					if (double.IsNaN(pHashPercent) || pHashPercent < 0 || pHashPercent > 1)
						bounded = false; // the old check throws on these; let it
					else {
						int maxBits = (int)Math.Floor((1.0 - pHashPercent) * 64.0);
						pHashChunks = maxBits + 1;
						if (pHashChunks > 64 || 64 / pHashChunks < MinChunkBits)
							bounded = false;
						pHashRequired = engine.matchingRequiredSampleMatches
							?? Math.Max(1, (int)Math.Ceiling(positions * Math.Clamp(settings.PHashRequiredMatchingSampleRatio, 0.01f, 1f)));
					}
				}

				// AI: a pair's mean similarity reaches AiPercent only if one position's does; a little
				// below it, for the float rounding of the old mean. As a 127²-scaled dot product.
				if (aiOn) {
					double target = settings.AiPercent / 100f - 1e-3;
					if (double.IsNaN(target) || target <= -1)
						bounded = false;
					else
						aiDotFloor = target > 1 ? int.MaxValue : (int)Math.Ceiling((target - 1e-6) * 127.0 * 127.0);
				}

				if (bounded) {
					if (grayOn) BuildGray(parallelism, cancel);
					if (pHashOn) BuildPHash();
					if (aiOn && !BuildAi(parallelism, cancel)) bounded = false;
				}
				unbounded = !bounded;
				if (unbounded) {
					grayTree = null;
					chunkKeys = null;
					chunkIds = null;
					aiTrees = null;
					Bytes = 0;
				}
			}

			// ---- Grayscale ----

			void BuildGray(int parallelism, CancellationToken cancel) {
				int dims = coarseDims + fineDims;
				var features = new int[count * dims];
				int[]? radii = clampFrames ? new int[count] : null;
				int span = GrayBytesUtils.IgnoredPixelSpan(ignoreBlack, ignoreWhite);
				Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancel },
					() => new byte[FrameLength],
					(i, _, scratch) => {
						int ignored = GrayFeatures(entries[i].compareGray!, features.AsSpan(i * dims, dims), scratch);
						if (radii != null) radii[i] = (int)Math.Min(int.MaxValue / 4, (long)span * ignored);
						return scratch;
					},
					_ => { });

				var ids = new int[count];
				for (int i = 0; i < count; i++) ids[i] = i;
				grayTree = new FeatureTree(coarseDims, fineDims, ids, features, radii, count);
				Bytes += grayTree.ApproximateBytes;
			}

			/// <summary>
			/// The file's (or flipped file's) block sums, clamped like the compare: the coarse ones
			/// (2×2 blocks or whole frames) per position, then the 4×4 blocks per position.
			/// Returns how many of its pixels the ignore modes skip.
			/// </summary>
			int GrayFeatures(byte[]?[] frames, Span<int> features, byte[] scratch) {
				const int side = GrayBytesUtils.Side, block = side / BlocksPerSide;
				int ignored = 0;
				for (int j = 0; j < positions; j++) {
					ReadOnlySpan<byte> frame = frames[j]!;
					if (clampFrames) {
						ignored += GrayBytesUtils.ClampIgnoredPixels(frame, scratch, ignoreBlack, ignoreWhite);
						frame = scratch;
					}
					Span<int> sums = features.Slice(coarseDims + j * Blocks, Blocks);
					sums.Clear();
					for (int y = 0; y < side; y++) {
						Span<int> row = sums.Slice(y / block * BlocksPerSide, BlocksPerSide);
						ReadOnlySpan<byte> line = frame.Slice(y * side, side);
						for (int bx = 0; bx < BlocksPerSide; bx++) {
							int sum = 0;
							foreach (byte b in line.Slice(bx * block, block)) sum += b;
							row[bx] += sum;
						}
					}
					if (coarseBlocks == 4) {
						for (int by = 0; by < 2; by++)
							for (int bx = 0; bx < 2; bx++) {
								int top = by * 2 * BlocksPerSide + bx * 2, bottom = top + BlocksPerSide;
								features[j * 4 + by * 2 + bx] = sums[top] + sums[top + 1] + sums[bottom] + sums[bottom + 1];
							}
					}
					else {
						int sum = 0;
						foreach (int b in sums) sum += b;
						features[j] = sum;
					}
				}
				return ignored;
			}

			void GrayCandidates(int i, byte[]?[]? flippedGray, List<int> result) {
				FeatureTree tree = grayTree!;
				double radius = grayReach + tree.RadiusOf(i);
				tree.Query(tree.FeaturesOf(i), radius, FeatureTree.Norm.Sum, result);
				if (flippedGray != null) {
					// A mirror image moves the same pixels: the same ignored count, so the same radius.
					var query = new int[tree.Dims];
					GrayFeatures(flippedGray, query, new byte[FrameLength]);
					tree.Query(query, radius, FeatureTree.Norm.Sum, result);
				}
			}

			// ---- pHash ----

			void BuildPHash() {
				chunkKeys = new ulong[positions][][];
				chunkIds = new int[positions][][];
				for (int j = 0; j < positions; j++) {
					chunkKeys[j] = new ulong[pHashChunks][];
					chunkIds[j] = new int[pHashChunks][];
					for (int c = 0; c < pHashChunks; c++) {
						var keys = new ulong[count];
						var ids = new int[count];
						for (int i = 0; i < count; i++) {
							keys[i] = Chunk(entries[i].comparePHashes![j], c);
							ids[i] = i;
						}
						Array.Sort(keys, ids);
						chunkKeys[j][c] = keys;
						chunkIds[j][c] = ids;
						Bytes += count * 12L;
					}
				}
			}

			ulong Chunk(ulong hash, int chunk) {
				int start = chunk * 64 / pHashChunks;
				int width = (chunk + 1) * 64 / pHashChunks - start;
				ulong mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
				return (hash >> start) & mask;
			}

			/// <summary>Adds the files sharing a chunk with <paramref name="hashes"/> at no fewer positions than the quorum.</summary>
			void PHashCandidates(ulong[] hashes, List<int> output) {
				// Per file: the last position it shared a chunk at, and how many positions did.
				var shared = new Dictionary<int, (int Position, int Count)>();
				for (int j = 0; j < positions; j++) {
					for (int c = 0; c < pHashChunks; c++) {
						ulong[] keys = chunkKeys![j][c];
						int[] ids = chunkIds![j][c];
						ulong key = Chunk(hashes[j], c);
						for (int at = LowerBound(keys, key); at < keys.Length && keys[at] == key; at++) {
							int id = ids[at];
							shared.TryGetValue(id, out var seen);
							if (seen.Count > 0 && seen.Position == j) continue;
							shared[id] = (j, seen.Count + 1);
						}
					}
				}
				foreach (var (id, seen) in shared)
					if (seen.Count >= pHashRequired)
						output.Add(id);
			}

			// ---- AI ----

			bool BuildAi(int parallelism, CancellationToken cancel) {
				aiTrees = new FeatureTree?[positions];
				int length = -1;
				long maxNorm = 0;
				var members = new List<int>[positions];
				for (int j = 0; j < positions; j++) {
					members[j] = new List<int>();
					for (int i = 0; i < count; i++) {
						FileEntry e = entries[i];
						// The positions ComputeAiSimilarity counts: an embedding, not marked invalid.
						byte[]? v = e.compareEmbeddings != null && j < e.compareEmbeddings.Length ? e.compareEmbeddings[j] : null;
						bool[]? valid = e.compareEmbeddingValid;
						if (v == null || (valid != null && j < valid.Length && !valid[j])) continue;
						if (length < 0) length = v.Length;
						else if (v.Length != length) return false; // mixed widths: the distance identity below needs one
						maxNorm = Math.Max(maxNorm, AI.EmbeddingMath.Dot(v, v));
						members[j].Add(i);
					}
				}
				if (length <= 0) return true; // nothing to match on: the AI pass says no to every pair

				// cos ≥ target ⇔ dot ≥ T ⇒ |x − y|² = |x|² + |y|² − 2·dot ≤ 2·max|x|² − 2T: a ball of
				// radius R. Coordinates in an orthonormal basis can only shrink distances, so two such
				// vectors' coordinates are within R too. Rounding the scaled coordinates moves a pair by
				// under one unit per coordinate, and the basis is orthonormal to rounding: the margins
				// cover both.
				int dims = Math.Min(AiCheckDims, length), boxDims = Math.Min(AiBoxDims, dims);
				long radiusSquared = 2 * maxNorm - 2L * aiDotFloor;
				aiReach = radiusSquared < 0 ? -1 : Math.Sqrt(radiusSquared) * AiProjectionScale * (1 + 1e-6) + Math.Sqrt(dims) + 1;
				var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancel };
				for (int j = 0; j < positions; j++) {
					List<int> ids = members[j];
					if (ids.Count == 0) continue;
					var vectors = new byte[ids.Count][];
					for (int m = 0; m < ids.Count; m++) vectors[m] = entries[ids[m]].compareEmbeddings![j]!;

					double[][] basis = PrincipalDirections(vectors, length, dims, options);
					// The leading coordinates build the tree; all of them (those again) are checked in its leaves.
					int stride = boxDims + dims;
					var features = new int[ids.Count * stride];
					Parallel.For(0, ids.Count, options, m => {
						ReadOnlySpan<sbyte> v = MemoryMarshal.Cast<byte, sbyte>(vectors[m]);
						for (int k = 0; k < dims; k++) {
							double[] u = basis[k];
							double projection = 0;
							for (int d = 0; d < length; d++) projection += v[d] * u[d];
							int coordinate = (int)Math.Round(projection * AiProjectionScale);
							features[m * stride + boxDims + k] = coordinate;
							if (k < boxDims) features[m * stride + k] = coordinate;
						}
					});
					FeatureTree tree = aiTrees[j] = new FeatureTree(boxDims, dims, ids.ToArray(), features, null, count);
					Bytes += tree.ApproximateBytes + (long)dims * length * 8;
				}
				return true;
			}

			void AiCandidates(int i, List<int> result) {
				foreach (FeatureTree? tree in aiTrees!)
					if (tree != null && tree.Contains(i))
						tree.Query(tree.FeaturesOf(i), aiReach, FeatureTree.Norm.Euclidean, result);
			}

			/// <summary>
			/// An orthonormal basis of <paramref name="dims"/> directions along which a sample of the
			/// vectors varies most (subspace iteration on their covariance). Only the speed depends
			/// on how good the directions are; the bound holds for any orthonormal basis.
			/// </summary>
			static double[][] PrincipalDirections(byte[][] vectors, int length, int dims, ParallelOptions options) {
				int[] sample = Sample(vectors.Length, PrincipalSample);
				var mean = new double[length];
				foreach (int s in sample) {
					ReadOnlySpan<sbyte> v = MemoryMarshal.Cast<byte, sbyte>(vectors[s]);
					for (int d = 0; d < length; d++) mean[d] += v[d];
				}
				for (int d = 0; d < length; d++) mean[d] /= sample.Length;
				var centered = new double[sample.Length][];
				for (int r = 0; r < sample.Length; r++) {
					ReadOnlySpan<sbyte> v = MemoryMarshal.Cast<byte, sbyte>(vectors[sample[r]]);
					centered[r] = new double[length];
					for (int d = 0; d < length; d++) centered[r][d] = v[d] - mean[d];
				}
				var rng = new Random(20261109);
				var basis = new double[dims][];
				for (int k = 0; k < dims; k++) {
					basis[k] = new double[length];
					for (int d = 0; d < length; d++) basis[k][d] = rng.NextDouble() - 0.5;
				}
				Orthonormalize(basis);
				for (int iteration = 0; iteration < 10; iteration++) {
					// basis ← covariance × basis, as centeredᵀ (centered basis): never forms the matrix.
					var next = new double[dims][];
					double[][] current = basis;
					Parallel.For(0, dims, options, k => {
						double[] u = current[k];
						var w = new double[length];
						foreach (double[] row in centered) {
							double score = 0;
							for (int d = 0; d < length; d++) score += row[d] * u[d];
							for (int d = 0; d < length; d++) w[d] += score * row[d];
						}
						next[k] = w;
					});
					basis = next;
					Orthonormalize(basis);
				}
				Orthonormalize(basis); // twice: rounding left orthonormal to ~1e-15
				return basis;
			}

			/// <summary>Modified Gram–Schmidt; a direction that collapses is replaced by a fresh axis.</summary>
			static void Orthonormalize(double[][] basis) {
				int length = basis[0].Length;
				int axis = 0;
				for (int k = 0; k < basis.Length; k++) {
					double[] u = basis[k];
					for (int p = 0; p < k; p++) {
						double dot = 0;
						for (int d = 0; d < length; d++) dot += u[d] * basis[p][d];
						for (int d = 0; d < length; d++) u[d] -= dot * basis[p][d];
					}
					double norm = 0;
					for (int d = 0; d < length; d++) norm += u[d] * u[d];
					norm = Math.Sqrt(norm);
					if (norm < 1e-9) {
						Array.Clear(u);
						u[axis++ % length] = 1;
						k--; // orthogonalize that axis instead: one of the first k + 1 is outside the span
						continue;
					}
					for (int d = 0; d < length; d++) u[d] /= norm;
				}
			}

			static int[] Sample(int n, int size) {
				size = Math.Min(n, size);
				var sample = new int[size];
				for (int s = 0; s < size; s++)
					sample[s] = (int)((long)s * n / size);
				return sample;
			}

			// ---- Queries ----

			/// <summary>
			/// The later files (list index above <paramref name="i"/>) that can match file i, in the
			/// order the old loop visited them: by index, or for the bucketed video loop by duration
			/// bucket, then index. False when no bound applies and the caller keeps the old loop.
			/// </summary>
			public bool Candidates(int i, byte[]?[]? flippedGray, ulong[]? flippedPHashes, List<int> result) {
				result.Clear();
				if (unbounded) {
					if (!videos) return false;
					DurationWindow(i, result);
				}
				else {
					if (grayOn) GrayCandidates(i, flippedGray, result);
					if (pHashOn) {
						PHashCandidates(entries[i].comparePHashes!, result);
						if (flippedPHashes != null)
							PHashCandidates(flippedPHashes, result);
					}
					if (aiOn) AiCandidates(i, result);
				}

				// Later files only (the old loop's n > i), inside the duration window for videos.
				int kept = 0;
				for (int r = 0; r < result.Count; r++) {
					int j = result[r];
					if (j <= i) continue;
					if (videos && !WithinDuration(i, j)) continue;
					result[kept++] = j;
				}
				result.RemoveRange(kept, result.Count - kept);

				if (bucketOrder) {
					// The bucketed loop walks the buckets of the entry's key range, each in list order.
					double durationSeconds = durations![i];
					double maxDiffSeconds = tolerances![i];
					int minKey = (int)Math.Floor(Math.Max(0d, durationSeconds - maxDiffSeconds) / BucketSizeSeconds);
					int maxKey = (int)Math.Floor((durationSeconds + maxDiffSeconds) / BucketSizeSeconds);
					var keyed = new List<long>(result.Count);
					foreach (int j in result) {
						int key = BucketKey(durations[j]);
						if (key < minKey || key > maxKey) continue;
						keyed.Add(((long)key << 32) | (uint)j);
					}
					keyed.Sort();
					result.Clear();
					long last = long.MinValue;
					foreach (long k in keyed) {
						if (k == last) continue;
						last = k;
						result.Add((int)(k & 0xFFFFFFFF));
					}
				}
				else {
					result.Sort();
					int unique = 0;
					for (int r = 0; r < result.Count; r++)
						if (unique == 0 || result[unique - 1] != result[r])
							result[unique++] = result[r];
					result.RemoveRange(unique, result.Count - unique);
				}
				return true;
			}

			// The bucketed loop's duration bucket (whole seconds), the same expression ScanForDuplicates uses.
			internal static int BucketKey(double durationSeconds) => (int)Math.Floor(durationSeconds / BucketSizeSeconds);

			// A pair outside the entry's own tolerance fails the pair check's duration gate (which uses
			// the smaller of both tolerances); the margin covers rounding, the real gate decides.
			bool WithinDuration(int i, int j) =>
				Math.Abs(durations![i] - durations[j]) <= tolerances![i] * (1 + 1e-9) + 1e-9;

			void DurationWindow(int i, List<int> result) {
				double reach = tolerances![i] * (1 + 1e-6) + 1e-6;
				double low = durations![i] - reach, high = durations[i] + reach;
				for (int at = LowerBound(sortedDurations!, low); at < sortedDurations!.Length && sortedDurations[at] <= high; at++)
					result.Add(byDuration![at]);
			}

			static int LowerBound(ulong[] keys, ulong key) {
				int lo = 0, hi = keys.Length;
				while (lo < hi) {
					int mid = (lo + hi) >>> 1;
					if (keys[mid] < key) lo = mid + 1;
					else hi = mid;
				}
				return lo;
			}

			static int LowerBound(double[] keys, double key) {
				int lo = 0, hi = keys.Length;
				while (lo < hi) {
					int mid = (lo + hi) >>> 1;
					if (keys[mid] < key) lo = mid + 1;
					else hi = mid;
				}
				return lo;
			}
		}
	}
}
