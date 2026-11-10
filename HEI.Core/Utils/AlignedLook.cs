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

using System.Linq;

namespace HEI.Core.Utils {
	/// <summary>
	/// Two photos laid over each other: is one the other cropped, scaled or turned a little, with its colours changed at
	/// most? The AI's cosine can't tell an edit from another shot of one scene (both score 94–97%), and the 32×32 gray
	/// frames are squashed to a square, so a crop or a turn shifts every cell. This finds where the one picture sits in the
	/// other (the crop, the scale from <see cref="MinScale"/> to <see cref="MaxScale"/> of the long side, a turn of up to
	/// <see cref="MaxAngle"/>°: a coarse search at <see cref="CoarseSide"/> pixels, refined at <see cref="MidSide"/> and
	/// about <see cref="FineSide"/>), then compares them over a <see cref="Grid"/>×<see cref="Grid"/> grid. Each cell gets
	/// its own gain and offset, so a filter, black and white or a brighter copy still lines up; a different shot doesn't
	/// line up everywhere: the camera moved (near things shift against far ones), or something in it did (an arm, a wave).
	/// </summary>
	internal static class AlignedLook {
		/// <summary>Names how pictures are lined up and judged: an answer kept from another version is asked again.</summary>
		internal const string Version = "aligned1";

		/// <summary>
		/// The same picture lines up with no grid cell more than this apart (0–255) once each cell has its own gain and offset.
		/// Tuned on the look-alike set's tune split alone (docs/LOOKALIKE-BAR.md; 249 originals, 2,577 original~edit pairs and
		/// 670 pairs of different shots of one scene, at 85% AI or more): edits came to 22.0 at most (99% under 11.8), other
		/// shots to 24.3 at least (a landscape and a portrait of one view; then 26.9, 31.3): 23 is between the two.
		/// </summary>
		internal const double CellLimit = 23;
		/// <summary>And correlates at least this well over the whole picture (edits: 0.972 at least): a safety net for a wrong placing.</summary>
		internal const double NccFloor = 0.95;
		/// <summary>A picture this even (the standard deviation of its gray, 0–255) can't be placed: fog, a blank page.</summary>
		const double EvenBelow = 4;

		/// <summary>Whether <paramref name="b"/> is <paramref name="a"/> cropped, scaled or turned a little, its colours changed at most; null when either is too even to tell.</summary>
		internal static bool? SamePicture(Picture a, Picture b) {
			if (a.Even || b.Even) return null;
			return Lines(Compare(a, b, Lines));
		}

		static bool Lines(Fit fit) => fit.WorstCell <= CellLimit && fit.Ncc >= NccFloor;

		/// <summary>The pictures are decoded at this many pixels on their long side.</summary>
		internal const int DecodeSide = 384;
		internal const int CoarseSide = 32, MidSide = 64, FineSide = 160;
		/// <summary>How much of the other's long side the picture covers: a crop to 40%, up to an overhang of 5% (rounding, a border).</summary>
		internal const double MinScale = 0.40, MaxScale = 1.05;
		internal const double MaxAngle = 6;
		internal const int Grid = 8;
		const double ScaleStep = 1.05, AngleStep = 2;
		/// <summary>How far (a share of the other's long side) the placed picture may reach past the other's edge.</summary>
		const double Overhang = 0.04;
		const int Candidates = 3;
		/// <summary>A pose whose correlation at <see cref="MidSide"/> is below this is no fit: it isn't looked at closer.</summary>
		const double MidFloor = 0.9;

		/// <summary>How the picture lines up in the other: where it sits, and how alike they are there.</summary>
		/// <param name="Ncc">The normalised cross-correlation of the two over the whole picture (1: the same).</param>
		/// <param name="WorstCell">The largest of the cells' differences (0–255): the root mean square left after each cell's own gain and offset.</param>
		/// <param name="SecondCell">The second largest.</param>
		/// <param name="Reversed">The kept file was placed in the other one (it's the crop).</param>
		internal readonly record struct Fit(double Ncc, double WorstCell, double SecondCell, double Scale, double Angle, bool Reversed);

		/// <summary>A gray picture at its own shape, with values 0–255.</summary>
		internal sealed class Picture {
			internal readonly float[] Pixels;
			internal readonly int Width, Height;

			internal Picture(float[] pixels, int width, int height) => (Pixels, Width, Height) = (pixels, width, height);

			/// <summary>Too even to place (<see cref="EvenBelow"/>).</summary>
			internal bool Even {
				get {
					double sum = 0, squares = 0;
					foreach (float v in Pixels) { sum += v; squares += v * v; }
					double mean = sum / Pixels.Length;
					return squares / Pixels.Length - mean * mean < EvenBelow * EvenBelow;
				}
			}

			/// <summary>The width and height in units of the long side (the long one is 1).</summary>
			internal double UnitWidth => Width >= Height ? 1 : (double)Width / Height;
			internal double UnitHeight => Height >= Width ? 1 : (double)Height / Width;

			/// <summary>The photo, decoded at <see cref="DecodeSide"/> on its long side (or its own size when smaller); null when it can't be decoded.</summary>
			internal static Picture? Load(string path) {
				if (!WicImageDecoder.TryDecodeLuma(path, DecodeSide, out byte[]? gray, out int w, out int h) || gray == null || w <= 0 || h <= 0)
					return null;
				var pixels = new float[gray.Length];
				for (int k = 0; k < gray.Length; k++) pixels[k] = gray[k];
				return new Picture(pixels, w, h);
			}

			/// <summary>The picture with <paramref name="longSide"/> pixels on its long side.</summary>
			internal Picture Resized(int longSide) {
				(int w, int h) = Shape(Width, Height, longSide);
				return Resized(w, h);
			}

			internal Picture Resized(int width, int height) {
				if (width == Width && height == Height) return this;
				float[] rows = Resample(Pixels, Width, Height, width, horizontal: true);
				return new Picture(Resample(rows, width, Height, height, horizontal: false), width, height);
			}

			/// <summary>A copy with <paramref name="pad"/> pixels of the edge repeated around it, so a sample just past the edge needs no check.</summary>
			internal float[] Padded(int pad, out int stride) {
				stride = Width + 2 * pad;
				var dst = new float[stride * (Height + 2 * pad)];
				for (int y = -pad; y < Height + pad; y++) {
					int sy = Math.Clamp(y, 0, Height - 1);
					for (int x = -pad; x < Width + pad; x++)
						dst[(y + pad) * stride + x + pad] = Pixels[sy * Width + Math.Clamp(x, 0, Width - 1)];
				}
				return dst;
			}
		}

		static (int, int) Shape(int w, int h, int longSide) =>
			w >= h ? (longSide, Math.Max(1, (int)Math.Round((double)longSide * h / w))) : (Math.Max(1, (int)Math.Round((double)longSide * w / h)), longSide);

		/// <summary>One axis resampled: averaged over each new pixel's span when shrinking, interpolated when growing.</summary>
		static float[] Resample(float[] src, int w, int h, int size, bool horizontal) {
			int from = horizontal ? w : h, lines = horizontal ? h : w;
			var dst = new float[horizontal ? size * h : w * size];
			double scale = (double)from / size;
			for (int i = 0; i < size; i++) {
				// The source pixels feeding new pixel i, and their weights.
				Span<(int Index, float Weight)> taps = stackalloc (int, float)[(int)Math.Ceiling(scale) + 2];
				int n = 0;
				if (scale > 1) {
					double start = i * scale, end = start + scale;
					for (int j = (int)start; j < end && j < from; j++) {
						double overlap = Math.Min(end, j + 1) - Math.Max(start, j);
						if (overlap > 0) taps[n++] = (j, (float)(overlap / scale));
					}
				}
				else {
					double x = Math.Clamp((i + 0.5) * scale - 0.5, 0, from - 1);
					int j = Math.Min((int)x, from - 2 < 0 ? 0 : from - 2);
					float f = (float)(x - j);
					taps[n++] = (j, 1 - f);
					if (from > 1) taps[n++] = (j + 1, f);
				}
				for (int line = 0; line < lines; line++) {
					float sum = 0;
					for (int t = 0; t < n; t++)
						sum += taps[t].Weight * (horizontal ? src[line * w + taps[t].Index] : src[taps[t].Index * w + line]);
					if (horizontal) dst[line * size + i] = sum; else dst[i * w + line] = sum;
				}
			}
			return dst;
		}

		/// <summary>The two photos' fit (<see cref="Compare(Picture, Picture)"/>); null when either can't be decoded.</summary>
		internal static Fit? Compare(string keep, string other) =>
			Picture.Load(keep) is { } a && Picture.Load(other) is { } b ? Compare(a, b) : null;

		/// <summary>
		/// <paramref name="b"/> placed in <paramref name="a"/>, and if that doesn't line up, <paramref name="a"/> in <paramref name="b"/>
		/// (the kept one may be the crop): the better of the two.
		/// </summary>
		internal static Fit Compare(Picture a, Picture b, Func<Fit, bool>? good = null) {
			Fit forward = Place(a, b, reversed: false);
			if (good?.Invoke(forward) == true) return forward;
			Fit back = Place(b, a, reversed: true);
			return Better(back, forward) ? back : forward;
		}

		static bool Better(Fit x, Fit y) => x.WorstCell < y.WorstCell - 1e-9 || (Math.Abs(x.WorstCell - y.WorstCell) <= 1e-9 && x.Ncc > y.Ncc);

		/// <summary>Where <paramref name="b"/> sits in <paramref name="a"/>: in units of a's long side, b's centre (X, Y), how much of a's long side b's long side covers (S) and its turn (T, radians).</summary>
		record struct Pose(double X, double Y, double S, double T);

		/// <summary>The coarse search's best few poses, each refined, and the best of them compared cell by cell.</summary>
		static Fit Place(Picture a, Picture b, bool reversed) {
			var poses = Coarse(a, b);
			Fit best = new(-1, 255, 255, 0, 0, reversed);
			if (poses.Count == 0) return best;
			var mid = new Level(a, b, MidSide);
			var refined = poses.Select(p => Refine(mid, p, 0.02, 0.6 * Math.PI / 180, 1.0 / MidSide, rounds: 4, moves: 8)).OrderByDescending(p => p.Score).Take(2).ToList();
			foreach (var (pose, score) in refined) {
				if (score < MidFloor) {
					if (score > best.Ncc) best = best with { Ncc = score, Scale = pose.S, Angle = pose.T * 180 / Math.PI };
					continue;
				}
				var fine = new Level(a, b, (int)Math.Round(Math.Min(DecodeSide, FineSide / Math.Max(pose.S, MinScale))));
				var (p, _) = Refine(fine, pose, 0.004, 0.15 * Math.PI / 180, 0.5 / fine.Side, rounds: 2, moves: 4);
				Fit fit = new Level(a, b, DecodeSide, bSide: fine.Side).Cells(p, reversed);
				if (Better(fit, best)) best = fit;
			}
			return best;
		}

		/// <summary>The coarse search: every scale, turn and whole-pixel place at <see cref="CoarseSide"/> pixels, scored by correlation.</summary>
		static List<Pose> Coarse(Picture a, Picture b) {
			Picture ac = a.Resized(CoarseSide);
			// b shrunk once to twice the largest size the search draws it at, and each size drawn from that.
			Picture small = b.Resized(Math.Min(Math.Max(b.Width, b.Height), 2 * (int)Math.Ceiling(MaxScale * CoarseSide)));
			const int pad = 2;
			float[] padded = ac.Padded(pad, out int stride);
			// A's pixels per unit along each axis (the rounding of its shape makes them differ a little).
			double fx = ac.Width / a.UnitWidth, fy = ac.Height / a.UnitHeight;
			var top = new List<(Pose Pose, double Score)>();
			for (double s = MinScale; s <= MaxScale + 1e-9; s *= ScaleStep) {
				if (s * b.UnitWidth > a.UnitWidth + 2 * Overhang || s * b.UnitHeight > a.UnitHeight + 2 * Overhang) continue;
				Picture bc = small.Resized(Math.Max(4, (int)Math.Round(s * CoarseSide)));
				int n = bc.Pixels.Length;
				double sb = 0, sbb = 0;
				foreach (float v in bc.Pixels) { sb += v; sbb += v * v; }
				double vb = sbb - sb * sb / n;
				if (vb <= 1e-6 * n) continue; // a flat picture lines up anywhere: no fit
				var offsets = new int[n];
				var weights = new float[n * 4];
				for (double t = -MaxAngle; t <= MaxAngle + 1e-9; t += AngleStep) {
					double rad = t * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
					int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
					for (int v = 0, k = 0; v < bc.Height; v++)
						for (int u = 0; u < bc.Width; u++, k++) {
							// b's pixel centre from its middle, in units, turned and scaled into a's pixels.
							double qx = ((u + 0.5) / bc.Width - 0.5) * b.UnitWidth * s, qy = ((v + 0.5) / bc.Height - 0.5) * b.UnitHeight * s;
							double dx = (cos * qx - sin * qy) * fx - 0.5, dy = (sin * qx + cos * qy) * fy - 0.5;
							int ix = (int)Math.Floor(dx), iy = (int)Math.Floor(dy);
							float wx = (float)(dx - ix), wy = (float)(dy - iy);
							offsets[k] = iy * stride + ix;
							weights[4 * k] = (1 - wx) * (1 - wy); weights[4 * k + 1] = wx * (1 - wy);
							weights[4 * k + 2] = (1 - wx) * wy; weights[4 * k + 3] = wx * wy;
							minX = Math.Min(minX, ix); maxX = Math.Max(maxX, ix + 1); minY = Math.Min(minY, iy); maxY = Math.Max(maxY, iy + 1);
						}
					// Whole-pixel centres whose samples all fall in the padded picture.
					for (int cy = -pad - minY; cy <= ac.Height - 1 + pad - maxY; cy++)
						for (int cx = -pad - minX; cx <= ac.Width - 1 + pad - maxX; cx++) {
							int origin = (cy + pad) * stride + cx + pad;
							double sa = 0, saa = 0, sab = 0;
							for (int k = 0; k < n; k++) {
								int o = origin + offsets[k];
								float va = weights[4 * k] * padded[o] + weights[4 * k + 1] * padded[o + 1] + weights[4 * k + 2] * padded[o + stride] + weights[4 * k + 3] * padded[o + stride + 1];
								sa += va; saa += va * va; sab += va * bc.Pixels[k];
							}
							double va2 = saa - sa * sa / n;
							if (va2 <= 1e-6 * n) continue;
							double ncc = (sab - sa * sb / n) / Math.Sqrt(va2 * vb);
							Keep(top, new Pose(cx / fx, cy / fy, s, rad), ncc);
						}
				}
			}
			return top.Select(c => c.Pose).ToList();
		}

		/// <summary>Keeps the <see cref="Candidates"/> best poses, one per neighbourhood: a near pose that scores less is the same peak.</summary>
		static void Keep(List<(Pose Pose, double Score)> top, Pose pose, double score) {
			if (top.Count == Candidates && score <= top[^1].Score) return;
			for (int k = 0; k < top.Count; k++) {
				Pose p = top[k].Pose;
				if (Math.Abs(Math.Log(p.S / pose.S)) < 0.09 && Math.Abs(p.T - pose.T) < 2.5 * Math.PI / 180 &&
						Math.Abs(p.X - pose.X) < 0.07 && Math.Abs(p.Y - pose.Y) < 0.07) {
					if (score <= top[k].Score) return;
					top.RemoveAt(k);
					break;
				}
			}
			int at = top.FindIndex(c => c.Score < score);
			top.Insert(at < 0 ? top.Count : at, (pose, score));
			if (top.Count > Candidates) top.RemoveAt(top.Count - 1);
		}

		/// <summary>Steps each of the pose's four numbers while the correlation grows, halving the steps when none does.</summary>
		static (Pose Pose, double Score) Refine(Level level, Pose start, double scaleStep, double angleStep, double placeStep, int rounds, int moves) {
			Pose p = start;
			cornerShape = level.BShape;
			double best = level.Correlation(p);
			double ds = scaleStep, dt = angleStep, dp = placeStep;
			for (int round = 0; round < rounds; round++) {
				bool moved = true;
				for (int guard = 0; moved && guard < moves; guard++) {
					moved = false;
					foreach (Pose q in Corners(p, ds).Concat(new[] {
						p with { X = p.X + dp }, p with { X = p.X - dp }, p with { Y = p.Y + dp }, p with { Y = p.Y - dp },
						p with { S = p.S * (1 + ds) }, p with { S = p.S / (1 + ds) }, p with { T = p.T + dt }, p with { T = p.T - dt } })) {
						double score = level.Correlation(q);
						if (score > best + 1e-7) { best = score; p = q; moved = true; }
					}
				}
				ds /= 2; dt /= 2; dp /= 2;
			}
			return (p, best);
		}

		/// <summary>The pose scaled by 1 ± <paramref name="ds"/> about each of b's corners. Needs b's shape: set by <see cref="Level"/>.</summary>
		static IEnumerable<Pose> Corners(Pose p, double ds) {
			double cos = Math.Cos(p.T), sin = Math.Sin(p.T), hw = 0.5 * cornerShape.W * p.S, hh = 0.5 * cornerShape.H * p.S;
			foreach (double f in new[] { 1 + ds, 1 / (1 + ds) })
				for (int sx = -1; sx <= 1; sx += 2)
					for (int sy = -1; sy <= 1; sy += 2) {
						double ox = cos * sx * hw - sin * sy * hh, oy = sin * sx * hw + cos * sy * hh;
						yield return new Pose(p.X + ox * (1 - f), p.Y + oy * (1 - f), p.S * f, p.T);
					}
		}

		[ThreadStatic] static (double W, double H) cornerShape;

		/// <summary>The two pictures at one size: a at <paramref name="side"/> pixels on its long side, b at the size of its place in a.</summary>
		sealed class Level {
			readonly Picture a;
			readonly Picture source;
			readonly double fx, fy;
			internal readonly int Side;
			internal readonly (double W, double H) BShape;
			Picture? b;

			/// <summary>Set: b is drawn at this size whatever a's, and each of b's pixels is a's average over its footprint (not one sample).</summary>
			readonly int? bSide;

			internal Level(Picture a, Picture b, int side, int? bSide = null) {
				this.bSide = bSide;
				Side = Math.Min(side, Math.Max(a.Width, a.Height));
				this.a = a.Resized(Side);
				source = b;
				fx = this.a.Width / a.UnitWidth;
				BShape = (b.UnitWidth, b.UnitHeight);
				fy = this.a.Height / a.UnitHeight;
			}

			/// <summary>b at the size it takes in a at scale <paramref name="s"/>, made again only when that size changes by a pixel.</summary>
			Picture B(double s) {
				int side = Math.Clamp((int)Math.Round(s * (bSide ?? Side)), 4, Math.Max(source.Width, source.Height));
				if (b != null && Math.Max(b.Width, b.Height) == side) return b;
				if (!drawn.TryGetValue(side, out var known)) {
					Picture made = source.Resized(side);
					double sum = 0, squares = 0;
					foreach (float v in made.Pixels) { sum += v; squares += v * v; }
					drawn[side] = known = (made, sum, squares);
				}
				(b, bSum, bSquares) = known;
				return b;
			}

			/// <summary>a with its edge repeated around it (<see cref="Pad"/> pixels), so a sample just past the edge needs no check.</summary>
			float[]? padded;
			int stride, pad;

			/// <summary>b's sums for the correlation, made with b.</summary>
			double bSum, bSquares;
			/// <summary>b at each size it was drawn at: refining steps the scale back and forth over a few sizes.</summary>
			readonly Dictionary<int, (Picture Picture, double Sum, double Squares)> drawn = new();

			/// <summary>
			/// Where b's first pixel falls in padded a at the pose, and the step to the next one along each of b's axes; false when b
			/// reaches past a's edge by more than <see cref="Overhang"/>.
			/// </summary>
			bool Place(Pose p, out Picture bp, out double x0, out double y0, out double ux, out double uy, out double vx, out double vy) {
				bp = B(p.S);
				x0 = y0 = ux = uy = vx = vy = 0;
				double cos = Math.Cos(p.T), sin = Math.Sin(p.T);
				double hw = 0.5 * source.UnitWidth * p.S, hh = 0.5 * source.UnitHeight * p.S;
				double reachX = Math.Abs(cos) * hw + Math.Abs(sin) * hh, reachY = Math.Abs(sin) * hw + Math.Abs(cos) * hh;
				if (p.X - reachX < -Overhang || p.X + reachX > a.UnitWidth + Overhang || p.Y - reachY < -Overhang || p.Y + reachY > a.UnitHeight + Overhang)
					return false;
				if (padded == null) {
					pad = (int)Math.Ceiling(Overhang * Math.Max(fx, fy)) + 4;
					padded = a.Padded(pad, out stride);
				}
				// One of b's pixels in a's pixels, along each of b's axes.
				double du = source.UnitWidth * p.S / bp.Width, dv = source.UnitHeight * p.S / bp.Height;
				ux = cos * du * fx; uy = sin * du * fy;
				vx = -sin * dv * fx; vy = cos * dv * fy;
				double qx = (0.5 / bp.Width - 0.5) * source.UnitWidth * p.S, qy = (0.5 / bp.Height - 0.5) * source.UnitHeight * p.S;
				x0 = (p.X + cos * qx - sin * qy) * fx - 0.5 + pad;
				y0 = (p.Y + sin * qx + cos * qy) * fy - 0.5 + pad;
				return true;
			}

			float At(double x, double y) {
				int xi = (int)x, yi = (int)y;
				float wx = (float)(x - xi), wy = (float)(y - yi);
				int o = yi * stride + xi;
				float[] px = padded!;
				return (1 - wy) * ((1 - wx) * px[o] + wx * px[o + 1]) + wy * ((1 - wx) * px[o + stride] + wx * px[o + stride + 1]);
			}

			/// <summary>a sampled under each of b's pixels at the pose, each averaged over its footprint in a when <see cref="bSide"/> is set; null where b doesn't fit.</summary>
			float[]? Sample(Pose p, out Picture bp) {
				if (!Place(p, out bp, out double x0, out double y0, out double ux, out double uy, out double vx, out double vy)) return null;
				var samples = new float[bp.Pixels.Length];
				int taps = bSide == null ? 1 : Math.Clamp((int)Math.Round(Math.Sqrt(ux * ux + uy * uy)), 1, 3);
				for (int v = 0, k = 0; v < bp.Height; v++)
					for (int u = 0; u < bp.Width; u++, k++) {
						double x = x0 + u * ux + v * vx, y = y0 + u * uy + v * vy;
						if (taps == 1) { samples[k] = At(x, y); continue; }
						float sum = 0;
						for (int j = 0; j < taps; j++)
							for (int i = 0; i < taps; i++) {
								double oi = (i + 0.5) / taps - 0.5, oj = (j + 0.5) / taps - 0.5;
								sum += At(x + oi * ux + oj * vx, y + oi * uy + oj * vy);
							}
						samples[k] = sum / (taps * taps);
					}
				return samples;
			}

			/// <summary>The correlation of b and a at the pose over all of b; −1 where it doesn't fit. One pass, nothing kept: the refining asks it hundreds of times.</summary>
			internal double Correlation(Pose p) {
				if (p.S < MinScale * 0.97 || p.S > MaxScale * 1.03 || Math.Abs(p.T) > (MaxAngle + 1) * Math.PI / 180) return -1;
				if (!Place(p, out Picture bp, out double x0, out double y0, out double ux, out double uy, out double vx, out double vy)) return -1;
				float[] bpx = bp.Pixels;
				double sa = 0, saa = 0, sab = 0;
				for (int v = 0, k = 0; v < bp.Height; v++) {
					double x = x0 + v * vx, y = y0 + v * vy;
					for (int u = 0; u < bp.Width; u++, k++, x += ux, y += uy) {
						float va = At(x, y);
						sa += va; saa += va * va; sab += va * bpx[k];
					}
				}
				int n = bpx.Length;
				double varA = saa - sa * sa / n, varB = bSquares - bSum * bSum / n;
				return varA <= 1e-6 * n || varB <= 1e-6 * n ? -1 : (sab - sa * bSum / n) / Math.Sqrt(varA * varB);
			}

			static double Ncc(float[] x, float[] y) {
				double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
				int n = x.Length;
				for (int k = 0; k < n; k++) { sx += x[k]; sy += y[k]; sxx += x[k] * x[k]; syy += y[k] * y[k]; sxy += x[k] * y[k]; }
				double vx = sxx - sx * sx / n, vy = syy - sy * sy / n;
				return vx <= 1e-6 * n || vy <= 1e-6 * n ? -1 : (sxy - sx * sy / n) / Math.Sqrt(vx * vy);
			}

			/// <summary>The fit at the pose: the correlation, and each grid cell's difference after its own gain (not below 0) and offset.</summary>
			internal Fit Cells(Pose p, bool reversed) {
				if (Sample(p, out Picture bp) is not { } sa) return new Fit(-1, 255, 255, p.S, p.T * 180 / Math.PI, reversed);
				var (worst, second) = Grade(sa, bp.Pixels, bp.Width, bp.Height);
				return new Fit(Ncc(sa, bp.Pixels), worst, second, p.S, p.T * 180 / Math.PI, reversed);
			}

			/// <summary>The grid cells' differences after each one's own gain and offset: the worst and the second worst.</summary>
			static (double Worst, double Second) Grade(float[] sa, float[] sb, int w, int h) {
				var worst = new List<double>(Grid * Grid);
				for (int gy = 0; gy < Grid; gy++)
					for (int gx = 0; gx < Grid; gx++) {
						int x0 = gx * w / Grid, x1 = (gx + 1) * w / Grid, y0 = gy * h / Grid, y1 = (gy + 1) * h / Grid;
						double s1 = 0, s2 = 0, s11 = 0, s22 = 0, s12 = 0;
						int n = 0;
						for (int y = y0; y < y1; y++)
							for (int x = x0; x < x1; x++) {
								float va = sa[y * w + x], vb = sb[y * w + x];
								s1 += va; s2 += vb; s11 += va * va; s22 += vb * vb; s12 += va * vb; n++;
							}
						if (n == 0) continue;
						double varA = Math.Max(0, s11 / n - (s1 / n) * (s1 / n)), varB = Math.Max(0, s22 / n - (s2 / n) * (s2 / n));
						double cov = s12 / n - (s1 / n) * (s2 / n);
						// Both ways: something in one picture over an even patch of the other (an arm against the sky) leaves little of the even one.
						double leftB = varA > 1e-6 && cov > 0 ? Math.Max(0, varB - cov * cov / varA) : varB;
						double leftA = varB > 1e-6 && cov > 0 ? Math.Max(0, varA - cov * cov / varB) : varA;
						worst.Add(Math.Sqrt(Math.Max(leftA, leftB)));
					}
				worst.Sort();
				return (worst.Count > 0 ? worst[^1] : 255, worst.Count > 1 ? worst[^2] : 255);
			}
		}
	}
}
