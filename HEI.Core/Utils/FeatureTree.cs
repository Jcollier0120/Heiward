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
	/// A k-d tree over small integer feature vectors, for the compare phase's exact candidate
	/// search. Every item has two feature sets, coarse ones the tree is built on and finer ones it
	/// checks at the leaves, and a radius of its own. A query with features q and radius r finds
	/// every item b within r + radius(b) of q on both sets, in the sum or the Euclidean norm. The
	/// compare index feeds it contractions of the compare's metric (block sums of the frames,
	/// coordinates of the embeddings), so two files within the metric's threshold are always within
	/// it on both sets: the tree never loses a pair that can match, it only skips pairs that
	/// provably cannot.
	/// </summary>
	sealed class FeatureTree {
		// 2 × depth + 2 slots: the depth of a median-split tree is log2(count / LeafSize), far
		// below 30 for any library that fits in memory.
		const int MaxStack = 64;
		const int LeafSize = 16;

		/// <summary>Features per item: <see cref="BoxDims"/> coarse ones, then <see cref="CheckDims"/> finer ones.</summary>
		public readonly int Dims;
		public readonly int BoxDims;
		public readonly int CheckDims;
		// Items in tree order: each node covers a contiguous range of slots.
		readonly int[] features;   // slot-major: slot * Dims + d
		readonly int[] radii;      // per slot
		readonly int[] ids;        // per slot: the caller's id
		readonly int[] slotOfId;   // per caller id: its slot, or -1 when the id is not in the tree
		// Nodes: the slot range, children (-1 for a leaf), the largest item radius, and the
		// bounding box (min and max of each coarse feature, 2 × BoxDims ints per node).
		readonly int[] nodeLo, nodeHi, nodeLeft, nodeRight, nodeMaxRadius, nodeBox;

		public int Count => ids.Length;

		/// <param name="boxDims">The coarse features per item, which the nodes split and bound on.</param>
		/// <param name="checkDims">The finer features per item, checked at the leaves (0 for none).</param>
		/// <param name="itemIds">The caller's id of each item (0 ≤ id &lt; <paramref name="idSpace"/>).</param>
		/// <param name="itemFeatures">Item-major features, <paramref name="boxDims"/> + <paramref name="checkDims"/> per item.</param>
		/// <param name="itemRadii">Each item's own radius, or null for 0.</param>
		/// <param name="idSpace">One more than the largest id.</param>
		public FeatureTree(int boxDims, int checkDims, int[] itemIds, int[] itemFeatures, int[]? itemRadii, int idSpace) {
			BoxDims = boxDims;
			CheckDims = checkDims;
			int dims = Dims = boxDims + checkDims;
			int count = itemIds.Length;
			var order = new int[count];
			for (int i = 0; i < count; i++) order[i] = i;
			var nodes = new Nodes();
			if (count > 0)
				Build(nodes, order, itemFeatures, itemRadii, 0, count);
			nodeLo = nodes.Lo.ToArray();
			nodeHi = nodes.Hi.ToArray();
			nodeLeft = nodes.Left.ToArray();
			nodeRight = nodes.Right.ToArray();
			nodeMaxRadius = nodes.MaxRadius.ToArray();
			nodeBox = nodes.Box.ToArray();

			features = new int[count * dims];
			radii = new int[count];
			ids = new int[count];
			slotOfId = new int[idSpace];
			Array.Fill(slotOfId, -1);
			for (int slot = 0; slot < count; slot++) {
				int item = order[slot];
				Array.Copy(itemFeatures, item * dims, features, slot * dims, dims);
				radii[slot] = itemRadii?[item] ?? 0;
				ids[slot] = itemIds[item];
				slotOfId[itemIds[item]] = slot;
			}
		}

		/// <summary>The bytes this tree holds, for the compare phase's log.</summary>
		public long ApproximateBytes =>
			(long)features.Length * 4 + radii.Length * 4L + ids.Length * 4L + slotOfId.Length * 4L
			+ nodeLo.Length * 4L * 5 + nodeBox.Length * 4L;

		public bool Contains(int id) => slotOfId[id] >= 0;

		/// <summary>The stored features of <paramref name="id"/> (which must be in the tree).</summary>
		public ReadOnlySpan<int> FeaturesOf(int id) => features.AsSpan(slotOfId[id] * Dims, Dims);

		/// <summary>The stored radius of <paramref name="id"/> (which must be in the tree).</summary>
		public int RadiusOf(int id) => radii[slotOfId[id]];

		/// <summary>The norm a <see cref="Query"/> measures in.</summary>
		public enum Norm { Sum, Euclidean }

		/// <summary>
		/// Adds the id of every item b whose distance from <paramref name="query"/> (both feature
		/// sets, each on its own) in the given norm is at most <paramref name="radius"/> + radius(b).
		/// The sum norm (L1) is for features that are block sums of a frame (an L1 contraction of
		/// it); the Euclidean one for coordinates in an orthonormal basis (an L2 contraction).
		/// </summary>
		public void Query(ReadOnlySpan<int> query, double radius, Norm norm, List<int> output) {
			if (ids.Length == 0 || radius + nodeMaxRadius[0] < 0) return;
			bool euclidean = norm == Norm.Euclidean;
			Span<int> stack = stackalloc int[MaxStack];
			int sp = 0;
			stack[sp++] = 0;
			int dims = Dims, boxDims = BoxDims;
			ReadOnlySpan<int> queryBox = query[..boxDims], queryCheck = query[boxDims..];
			while (sp > 0) {
				int node = stack[--sp];
				long limit = Limit(radius + nodeMaxRadius[node], euclidean);
				if (limit < 0) continue;
				ReadOnlySpan<int> box = nodeBox.AsSpan(node * 2 * boxDims, 2 * boxDims);
				long outside = 0;
				for (int d = 0; d < boxDims && outside <= limit; d++) {
					long q = queryBox[d], lo = box[2 * d], hi = box[2 * d + 1];
					long gap = q < lo ? lo - q : q > hi ? q - hi : 0;
					outside += euclidean ? gap * gap : gap;
				}
				if (outside > limit) continue;

				int left = nodeLeft[node];
				if (left >= 0) {
					stack[sp++] = nodeRight[node];
					stack[sp++] = left;
					continue;
				}
				for (int slot = nodeLo[node], end = nodeHi[node]; slot < end; slot++) {
					long itemLimit = Limit(radius + radii[slot], euclidean);
					if (itemLimit < 0) continue;
					ReadOnlySpan<int> item = features.AsSpan(slot * dims, dims);
					if (Distance(queryBox, item[..boxDims], itemLimit, euclidean) <= itemLimit
						&& Distance(queryCheck, item[boxDims..], itemLimit, euclidean) <= itemLimit)
						output.Add(ids[slot]);
				}
			}
		}

		// The largest whole distance (squared, for the Euclidean norm) within reach; -1 for none.
		static long Limit(double reach, bool euclidean) =>
			reach < 0 ? -1 : (long)Math.Floor(Math.Min(euclidean ? reach * reach : reach, long.MaxValue / 4));

		// The distance (squared, for the Euclidean norm), or a value past the limit once it is passed.
		static long Distance(ReadOnlySpan<int> a, ReadOnlySpan<int> b, long limit, bool euclidean) {
			long distance = 0;
			if (euclidean) {
				for (int d = 0; d < a.Length && distance <= limit; d++) {
					long diff = a[d] - b[d];
					distance += diff * diff;
				}
			}
			else {
				for (int d = 0; d < a.Length && distance <= limit; d++)
					distance += Math.Abs((long)a[d] - b[d]);
			}
			return distance;
		}

		sealed class Nodes {
			public readonly List<int> Lo = new(), Hi = new(), Left = new(), Right = new(), MaxRadius = new(), Box = new();
		}

		int Build(Nodes nodes, int[] order, int[] itemFeatures, int[]? itemRadii, int lo, int hi) {
			int dims = Dims, boxDims = BoxDims;
			int node = nodes.Lo.Count;
			nodes.Lo.Add(lo);
			nodes.Hi.Add(hi);
			nodes.Left.Add(-1);
			nodes.Right.Add(-1);
			List<int> box = nodes.Box;
			int boxStart = box.Count;
			for (int d = 0; d < boxDims; d++) {
				box.Add(int.MaxValue);
				box.Add(int.MinValue);
			}
			int maxRadius = 0;
			for (int s = lo; s < hi; s++) {
				int item = order[s];
				int f = item * dims;
				for (int d = 0; d < boxDims; d++) {
					int v = itemFeatures[f + d];
					if (v < box[boxStart + 2 * d]) box[boxStart + 2 * d] = v;
					if (v > box[boxStart + 2 * d + 1]) box[boxStart + 2 * d + 1] = v;
				}
				if (itemRadii != null && itemRadii[item] > maxRadius) maxRadius = itemRadii[item];
			}
			nodes.MaxRadius.Add(maxRadius);
			if (hi - lo <= LeafSize) return node;

			int splitDim = -1;
			long widest = 0;
			for (int d = 0; d < boxDims; d++) {
				long spread = (long)box[boxStart + 2 * d + 1] - box[boxStart + 2 * d];
				if (spread > widest) {
					widest = spread;
					splitDim = d;
				}
			}
			if (splitDim < 0) return node; // all items alike on the coarse features: one leaf

			int mid = (lo + hi) / 2;
			Select(order, itemFeatures, lo, hi - 1, mid, splitDim);
			int leftNode = Build(nodes, order, itemFeatures, itemRadii, lo, mid);
			int rightNode = Build(nodes, order, itemFeatures, itemRadii, mid, hi);
			nodes.Left[node] = leftNode;
			nodes.Right[node] = rightNode;
			return node;
		}

		/// <summary>Quickselect: puts the item of rank <paramref name="k"/> by feature <paramref name="dim"/> at k, smaller ones before it.</summary>
		void Select(int[] order, int[] itemFeatures, int left, int right, int k, int dim) {
			int dims = Dims;
			while (right > left) {
				// Median of three as the pivot keeps sorted or clustered features from going quadratic.
				int midIndex = left + (right - left) / 2;
				int a = itemFeatures[order[left] * dims + dim], b = itemFeatures[order[midIndex] * dims + dim], c = itemFeatures[order[right] * dims + dim];
				int pivot = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
				int i = left, j = right;
				while (i <= j) {
					while (itemFeatures[order[i] * dims + dim] < pivot) i++;
					while (itemFeatures[order[j] * dims + dim] > pivot) j--;
					if (i <= j) {
						(order[i], order[j]) = (order[j], order[i]);
						i++;
						j--;
					}
				}
				if (k <= j) right = j;
				else if (k >= i) left = i;
				else return;
			}
		}
	}
}
