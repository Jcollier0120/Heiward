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
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>A picture as rows of 24-bit BGR, as the text reader reads it.</summary>
	internal sealed class TextPicture(int width, int height, byte[] bgr) {
		public readonly int Width = width, Height = height;
		public readonly byte[] Bgr = bgr;

		/// <summary>Pictures past this aren't decoded: 64 MP is 192 MB of pixels.</summary>
		const long MaxPixels = 64L << 20;

		/// <summary>The file upright at its own size, through WIC; null when it can't be decoded.</summary>
		public static TextPicture? Load(string path) =>
			WicImageDecoder.TryDecodeBgr(path, MaxPixels, out byte[]? bgr, out int w, out int h) ? new TextPicture(w, h, bgr!) : null;

		public static TextPicture? Load(byte[] data) =>
			WicImageDecoder.TryDecodeBgr(data, MaxPixels, out byte[]? bgr, out int w, out int h) ? new TextPicture(w, h, bgr!) : null;

		public TextPicture Crop(int x0, int y0, int x1, int y1) {
			int w = x1 - x0, h = y1 - y0;
			var o = new byte[w * h * 3];
			for (int y = 0; y < h; y++) Buffer.BlockCopy(Bgr, ((y0 + y) * Width + x0) * 3, o, y * w * 3, w * 3);
			return new TextPicture(w, h, o);
		}

		// Resized as Pillow's resize(BILINEAR) does it, which the model was trained with: a triangle filter as wide as the
		// scale when shrinking, fixed-point weights, across then down, rounded to bytes between the two.
		const int Precision = 22;

		static void Coefficients(int inSize, int outSize, out int[] bounds, out int[] weights, out int width) {
			double scale = (double)inSize / outSize, filterScale = Math.Max(scale, 1.0), support = filterScale;
			width = (int)Math.Ceiling(support) * 2 + 1;
			bounds = new int[outSize * 2];
			weights = new int[outSize * width];
			var k = new double[width];
			for (int xx = 0; xx < outSize; xx++) {
				double center = (xx + 0.5) * scale, ss = 1.0 / filterScale;
				int xmin = Math.Max(0, (int)(center - support + 0.5));
				int xmax = Math.Min(inSize, (int)(center + support + 0.5)) - xmin;
				double sum = 0;
				for (int x = 0; x < xmax; x++) {
					double t = Math.Abs((x + xmin - center + 0.5) * ss);
					sum += k[x] = t < 1.0 ? 1.0 - t : 0.0;
				}
				for (int x = 0; x < width; x++) {
					double w = x < xmax && sum != 0 ? k[x] / sum : 0;
					weights[xx * width + x] = (int)(w < 0 ? w * (1 << Precision) - 0.5 : w * (1 << Precision) + 0.5);
				}
				bounds[xx * 2] = xmin;
				bounds[xx * 2 + 1] = xmax;
			}
		}

		static byte Clip(long v) {
			v >>= Precision;
			return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
		}

		public TextPicture Resize(int nw, int nh) {
			TextPicture p = this;
			if (nw != Width) {
				Coefficients(Width, nw, out int[] b, out int[] k, out int kw);
				var o = new byte[nw * Height * 3];
				for (int y = 0; y < Height; y++) {
					int row = y * Width * 3;
					for (int xx = 0; xx < nw; xx++) {
						int xmin = b[xx * 2], xmax = b[xx * 2 + 1];
						long s0 = 1 << (Precision - 1), s1 = s0, s2 = s0;
						for (int x = 0; x < xmax; x++) {
							int w = k[xx * kw + x], i = row + (x + xmin) * 3;
							s0 += Bgr[i] * (long)w;
							s1 += Bgr[i + 1] * (long)w;
							s2 += Bgr[i + 2] * (long)w;
						}
						int j = (y * nw + xx) * 3;
						o[j] = Clip(s0);
						o[j + 1] = Clip(s1);
						o[j + 2] = Clip(s2);
					}
				}
				p = new TextPicture(nw, Height, o);
			}
			if (nh != p.Height) {
				Coefficients(p.Height, nh, out int[] b, out int[] k, out int kw);
				var o = new byte[nw * nh * 3];
				for (int yy = 0; yy < nh; yy++) {
					int ymin = b[yy * 2], ymax = b[yy * 2 + 1];
					for (int x = 0; x < nw * 3; x++) {
						long s = 1 << (Precision - 1);
						for (int y = 0; y < ymax; y++) s += p.Bgr[(y + ymin) * nw * 3 + x] * (long)k[yy * kw + y];
						o[yy * nw * 3 + x] = Clip(s);
					}
				}
				p = new TextPicture(nw, nh, o);
			}
			return p;
		}
	}

	/// <summary>A word as read, and how sure the recognizer was of it (the mean of its characters' probabilities, 0–1).</summary>
	internal readonly record struct TextWord(string Text, float Confidence);

	/// <summary>
	/// The text in a picture, read with the manor's own text-reading model (<see cref="TextComponents"/>): a port of the
	/// reader the Steward's kit measured (1,223 of 1,253 lines exact on a Snapdragon X2's NPU, about 117 ms a picture).
	/// The detector (PP-OCR's DB, axis-aligned) finds the lines in 640×640 tiles overlapping by 64; each line is cut out,
	/// made 48 pixels high and read by the recognizer (CTC) at one of eight widths. The NPU runs every graph at a fixed
	/// shape, compiled once into a cache; the processor runs the same tiles and widths, so both read the same. Before an
	/// NPU reads anything, it reads a picture drawn for the purpose (an embedded PNG) as the processor does, or the
	/// processor reads instead. On the processor it uses two threads in the background, four when someone waits, and runs
	/// in the agent's process, so in Windows' efficiency mode with it.
	/// </summary>
	internal sealed class OcrReader : IDisposable {
		public const int Tile = 640, Overlap = 64, LineHeight = 48;
		static readonly int[] Buckets = { 128, 256, 384, 512, 768, 1024, 1536, 2048 };
		static int MaxWidth => Buckets[^1];
		// The detector's settings: what counts as text, a box's least mean score, and how far a box grows past the shrunk text.
		const float Thresh = 0.3f, BoxThresh = 0.5f, Unclip = 1.6f;
		static readonly float[] Mean = { 0.485f, 0.456f, 0.406f }, Std = { 0.229f, 0.224f, 0.225f };
		static readonly float[][] DetValue = new float[3][];
		static readonly float[] RecValue = new float[256];

		static OcrReader() {
			AiComponents.EnsureResolverInstalled();
			// The picture's bytes in the order they're stored, B, G, R, as PaddleOCR's reader (OpenCV) hands them over.
			for (int c = 0; c < 3; c++) {
				DetValue[c] = new float[256];
				for (int v = 0; v < 256; v++) DetValue[c][v] = (v / 255f - Mean[c]) / Std[c];
			}
			for (int v = 0; v < 256; v++) RecValue[v] = v / 127.5f - 1f;
		}

		/// <summary>One graph: a session, its input and output, and whether its shape is fixed (the NPU's).</summary>
		sealed class Graph(InferenceSession session) : IDisposable {
			readonly string input = session.InputMetadata.Keys.First(), output = session.OutputMetadata.Keys.First();
			readonly RunOptions run = new();

			public float[] Run(float[] x, long[] shape, out long[] outShape) {
				using OrtValue value = OrtValue.CreateTensorValueFromMemory(x, shape);
				using IDisposableReadOnlyCollection<OrtValue> results = session.Run(run, new[] { input }, new[] { value }, new[] { output });
				outShape = results[0].GetTensorTypeAndShape().Shape;
				return results[0].GetTensorDataAsSpan<float>().ToArray();
			}

			public void Dispose() {
				session.Dispose();
				run.Dispose();
			}
		}

		readonly Graph det;
		readonly Dictionary<int, Graph> recs;
		readonly string charset;
		readonly bool npu;

		/// <summary>"NPU" or "CPU": where it reads.</summary>
		public string DeviceName => npu ? "NPU" : "CPU";
		/// <summary>Why it reads on the processor where an NPU was meant to, or null.</summary>
		public string? FallbackReason { get; private set; }
		/// <summary>Pictures read and the time they took, for the scan's log.</summary>
		public int Pictures;
		public readonly Stopwatch Time = new();

		OcrReader(Graph det, Dictionary<int, Graph> recs, string charset, bool npu) =>
			(this.det, this.recs, this.charset, this.npu) = (det, recs, charset, npu);

		public void Dispose() {
			det.Dispose();
			foreach (Graph g in recs.Values.Distinct()) g.Dispose();
		}

		/// <summary>
		/// The reader: on the NPU when <paramref name="useNpu"/> and this PC's NPU can read with the model (Qualcomm's,
		/// through the NPU pack's QNN, or Intel's NPU 3720 through its OpenVINO) and it passes the self-check; otherwise on
		/// the processor. Null when the model or ONNX Runtime isn't here. Never throws.
		/// </summary>
		public static OcrReader? Open(bool useNpu, bool fullSpeed) {
			if (!TextComponents.IsInstalled || !(AiComponents.IsReady || AiComponents.TestOverrideModelPath != null)) return null;
			string folder = TextComponents.Folder;
			OcrReader cpu;
			try {
				string charset = File.ReadAllText(Path.Combine(folder, "charset.txt"), Encoding.UTF8);
				cpu = OpenCpu(folder, charset, fullSpeed ? 4 : 2);
			}
			catch (Exception e) {
				Logger.Instance.Info($"The text reader couldn't start: {e.Message}");
				return null;
			}
			string? why = useNpu ? NpuRoute() : null;
			if (!useNpu || why != null) {
				cpu.FallbackReason = why;
				return cpu;
			}
			OcrReader? onNpu = null;
			try {
				onNpu = OpenNpu(folder, cpu.charset);
				string want = SelfCheck(cpu), got = SelfCheck(onNpu);
				if (got != want)
					throw new InvalidOperationException($"the NPU read the self-check picture differently from the processor ({got.Replace('\n', '|')})");
				cpu.Dispose();
				return onNpu;
			}
			catch (Exception e) {
				onNpu?.Dispose();
				cpu.FallbackReason = $"the {NpuComponents.NpuName} can't read with it: {Accelerators.OneLine(e.Message)}";
				Logger.Instance.Info($"The text reader reads on the processor: {cpu.FallbackReason}");
				return cpu;
			}
		}

		/// <summary>Null when this PC's NPU can read with the model; otherwise why not.</summary>
		static string? NpuRoute() {
			if (!NpuComponents.IsSupportedPlatform) return "this PC has no NPU it reads on";
			if (NpuComponents.Pack is OpenVinoPack && !NpuHardware.IsNpu3720)
				return "only Intel's NPU 3720 has been measured reading with it";
			if (NpuComponents.Pack is not (QnnPack or OpenVinoPack)) return $"the {NpuComponents.NpuName} hasn't been measured reading with it";
			if (!NpuComponents.IsInstalled) return "the NPU pack isn't installed";
			return null;
		}

		static OcrReader OpenCpu(string folder, string charset, int threads) {
			InferenceSession Session(string model) {
				using var options = new SessionOptions { IntraOpNumThreads = threads };
				return new InferenceSession(Path.Combine(folder, model), options);
			}
			var det = new Graph(Session("det.onnx"));
			var rec = new Graph(Session("rec.onnx"));
			return new OcrReader(det, Buckets.ToDictionary(w => w, _ => rec), charset, npu: false);
		}

		/// <summary>Changed whenever how the graphs are compiled changes, so the cache compiles them again.</summary>
		const string Compiled = "c1";

		/// <summary>
		/// The NPU's nine graphs (the detector's tile, the recognizer at each width, a line at a time), every shape fixed and
		/// the processor never taking part. On Qualcomm's NPU each is compiled once into the cache as an EP-context model;
		/// OpenVINO keeps what it compiles in its own cache. Compiling is NPU work: in the NPU's turn.
		/// </summary>
		static OcrReader OpenNpu(string folder, string charset) {
			IReadOnlyList<OrtEpDevice> npus = NpuComponents.GetNpuDevices();
			if (npus.Count == 0) throw new InvalidOperationException(NpuComponents.UnavailableReason ?? "the NPU pack offers no NPU");
			string cache = Path.Combine(AiComponents.AiFolder, NpuComponents.Pack!.CacheFolderName, $"text-v{TextComponents.ModelVersion}-{Compiled}");
			Directory.CreateDirectory(cache);
			bool qnn = NpuComponents.Pack is QnnPack;
			using IDisposable turn = NpuLock.Acquire();
			var opened = new List<Graph>();
			try {
				Graph Open(string model, string name, params (string Dim, long Value)[] dims) {
					var g = new Graph(qnn ? QnnSession(Path.Combine(folder, model), Path.Combine(cache, name + "_ctx.onnx"), npus, dims)
						: OpenVinoSession(Path.Combine(folder, model), cache, npus, dims));
					opened.Add(g);
					return g;
				}
				Graph det = Open("det.onnx", "det", ("h", Tile), ("w", Tile));
				var recs = Buckets.ToDictionary(w => w, w => Open("rec.onnx", $"rec{w}x1", ("n", 1), ("w", w)));
				return new OcrReader(det, recs, charset, npu: true);
			}
			catch {
				foreach (Graph g in opened) g.Dispose();
				throw;
			}
		}

		static SessionOptions NpuOptions(IReadOnlyList<OrtEpDevice> npus, (string Dim, long Value)[] dims, Dictionary<string, string> provider, string? contextOut) {
			var o = new SessionOptions { IntraOpNumThreads = 1 };
			o.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
			foreach (var (dim, value) in dims) o.AddFreeDimensionOverrideByName(dim, value);
			if (contextOut != null) {
				o.AddSessionConfigEntry("ep.context_enable", "1");
				o.AddSessionConfigEntry("ep.context_file_path", contextOut);
				o.AddSessionConfigEntry("ep.context_embed_mode", "1");
			}
			o.AppendExecutionProvider(OrtEnv.Instance(), npus, provider);
			return o;
		}

		static InferenceSession QnnSession(string model, string ctx, IReadOnlyList<OrtEpDevice> npus, (string, long)[] dims) {
			var provider = new Dictionary<string, string> {
				["backend_path"] = Path.Combine(AiComponents.AiFolder, "qnn", "QnnHtp.dll"),
				// Held at power_saver, as the kit holds it: burst reads a few per cent faster but keeps the NPU awake.
				["htp_performance_mode"] = "power_saver",
				["enable_htp_fp16_precision"] = "1",
				// Its most thorough compiling: a line 1,536 or 2,048 wide reads in two thirds of the time.
				["htp_graph_finalization_optimization_mode"] = "3",
			};
			if (!File.Exists(ctx)) {
				using SessionOptions compile = NpuOptions(npus, dims, provider, ctx);
				new InferenceSession(model, compile).Dispose();
			}
			using SessionOptions load = NpuOptions(npus, dims, provider, null);
			try {
				return new InferenceSession(ctx, load);
			}
			catch (OnnxRuntimeException) {
				// A torn cache, or compiled by another runtime: compiled again.
				File.Delete(ctx);
				using (SessionOptions compile = NpuOptions(npus, dims, provider, ctx))
					new InferenceSession(model, compile).Dispose();
				return new InferenceSession(ctx, load);
			}
		}

		static InferenceSession OpenVinoSession(string model, string cache, IReadOnlyList<OrtEpDevice> npus, (string, long)[] dims) {
			using SessionOptions options = NpuOptions(npus, dims, NpuComponents.Pack!.ProviderOptions(cache), null);
			return new InferenceSession(model, options);
		}

		/// <summary>The self-check picture's text as this reader reads it: three lines in Segoe UI, Consolas and Arial, drawn with ClearType.</summary>
		static string SelfCheck(OcrReader reader) {
			using Stream s = typeof(OcrReader).Assembly.GetManifestResourceStream("HEI.Core.Assets.text-selfcheck.png")
				?? throw new InvalidOperationException("the self-check picture is missing");
			using var bytes = new MemoryStream();
			s.CopyTo(bytes);
			TextPicture pic = TextPicture.Load(bytes.ToArray()) ?? throw new InvalidOperationException("the self-check picture can't be decoded");
			return string.Join("\n", reader.Read(pic).Select(l => string.Join(" ", l.SelectMany(b => b).Select(w => w.Text))));
		}

		// ---------------------------------------------------------------- reading

		static int[] Starts(int size) {
			if (size <= Tile) return new[] { 0 };
			var s = new List<int>();
			for (int p = 0; ; p += Tile - Overlap) {
				if (p + Tile >= size) { s.Add(size - Tile); break; }
				s.Add(p);
			}
			return s.ToArray();
		}

		/// <summary>The detector's text probability for each pixel: the picture read in tiles, overlaps taking the higher.</summary>
		float[] Probabilities(TextPicture pic) {
			var prob = new float[pic.Width * pic.Height];
			var x = new float[3 * Tile * Tile];
			foreach (int ty in Starts(pic.Height))
				foreach (int tx in Starts(pic.Width)) {
					int th = Math.Min(Tile, pic.Height - ty), tw = Math.Min(Tile, pic.Width - tx);
					// Whole tiles on the processor too, so it reads as the NPU does: only a picture smaller than a tile leaves part of it 0.
					if (th < Tile || tw < Tile) Array.Clear(x);
					for (int y = 0; y < th; y++) {
						int row = ((ty + y) * pic.Width + tx) * 3;
						for (int xx = 0; xx < tw; xx++) {
							int i = row + xx * 3, o = y * Tile + xx;
							x[o] = DetValue[0][pic.Bgr[i]];
							x[Tile * Tile + o] = DetValue[1][pic.Bgr[i + 1]];
							x[2 * Tile * Tile + o] = DetValue[2][pic.Bgr[i + 2]];
						}
					}
					float[] p = det.Run(x, new long[] { 1, 3, Tile, Tile }, out long[] os);
					int ow = (int)os[3];
					for (int y = 0; y < th; y++)
						for (int xx = 0; xx < tw; xx++) {
							int j = (ty + y) * pic.Width + tx + xx;
							float v = p[y * ow + xx];
							if (v > prob[j]) prob[j] = v;
						}
				}
			return prob;
		}

		/// <summary>The text's boxes: each run of likely-text pixels whose mean score is high enough, grown back to the text's size.</summary>
		List<int[]> Boxes(TextPicture pic) {
			float[] prob = Probabilities(pic);
			int w = pic.Width, h = pic.Height;
			var label = new int[w * h];
			var boxes = new List<int[]>();
			var stack = new int[w * h];
			int next = 0;
			for (int start = 0; start < w * h; start++) {
				if (prob[start] <= Thresh || label[start] != 0) continue;
				next++;
				int top = 0, x0 = w, y0 = h, x1 = -1, y1 = -1, n = 0;
				double sum = 0;
				stack[top++] = start;
				label[start] = next;
				while (top > 0) {
					int i = stack[--top], x = i % w, y = i / w;
					n++;
					sum += prob[i];
					x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
					if (x > 0 && label[i - 1] == 0 && prob[i - 1] > Thresh) { label[i - 1] = next; stack[top++] = i - 1; }
					if (x < w - 1 && label[i + 1] == 0 && prob[i + 1] > Thresh) { label[i + 1] = next; stack[top++] = i + 1; }
					if (y > 0 && label[i - w] == 0 && prob[i - w] > Thresh) { label[i - w] = next; stack[top++] = i - w; }
					if (y < h - 1 && label[i + w] == 0 && prob[i + w] > Thresh) { label[i + w] = next; stack[top++] = i + w; }
				}
				int bw = x1 + 1 - x0, bh = y1 + 1 - y0;
				if (Math.Min(bw, bh) < 3 || sum / n < BoxThresh) continue;
				double d = (double)bw * bh * Unclip / (2.0 * (bw + bh));
				boxes.Add(new[] {
					Math.Max(0, (int)(x0 - d)), Math.Max(0, (int)(y0 - d)),
					Math.Min(w, (int)Math.Ceiling(x1 + 1 + d)), Math.Min(h, (int)Math.Ceiling(y1 + 1 + d)),
				});
			}
			return Distinct(boxes);
		}

		/// <summary>The boxes without those lying mostly within a bigger one (a tall line crossing a tile's seam is found again as a strip of itself).</summary>
		static List<int[]> Distinct(List<int[]> boxes) {
			const double Inside = 0.6;
			var kept = new List<int[]>();
			for (int i = 0; i < boxes.Count; i++) {
				int[] a = boxes[i];
				long area = (long)(a[2] - a[0]) * (a[3] - a[1]);
				bool within = false;
				for (int j = 0; j < boxes.Count && !within; j++) {
					int[] b = boxes[j];
					long other = (long)(b[2] - b[0]) * (b[3] - b[1]);
					if (j == i || other < area || (other == area && j > i)) continue;
					long iw = Math.Min(a[2], b[2]) - Math.Max(a[0], b[0]), ih = Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]);
					within = iw > 0 && ih > 0 && iw * ih >= Inside * area;
				}
				if (!within) kept.Add(a);
			}
			return kept;
		}

		/// <summary>Boxes in rows by how they overlap up and down, each row left to right, the rows top to bottom (stable sorts).</summary>
		static List<List<int[]>> Rows(List<int[]> boxes) {
			var rows = new List<List<int[]>>();
			foreach (int[] b in boxes.OrderBy(b => b[1] + b[3])) {
				double cy = (b[1] + b[3]) / 2.0;
				List<int[]>? into = null;
				foreach (List<int[]> r in rows) {
					int ry0 = r.Min(x => x[1]), ry1 = r.Max(x => x[3]);
					if (ry0 <= cy && cy <= ry1 && Math.Min(b[3], ry1) - Math.Max(b[1], ry0) > 0.5 * Math.Min(b[3] - b[1], ry1 - ry0)) { into = r; break; }
				}
				if (into != null) into.Add(b);
				else rows.Add(new List<int[]> { b });
			}
			return rows.Select(r => r.OrderBy(b => b[0]).ThenBy(b => b[1]).ThenBy(b => b[2]).ThenBy(b => b[3]).ToList())
				.OrderBy(r => r.Min(b => b[1])).ToList();
		}

		static int BucketFor(int width) => Buckets.FirstOrDefault(b => width <= b, MaxWidth);

		sealed class Job {
			public required TextPicture Line;
			public int Bucket;
			public readonly List<TextWord> Words = new();
		}

		static Job Prepare(TextPicture pic, int[] b) {
			int cw = b[2] - b[0], ch = b[3] - b[1];
			int nw = Math.Min(MaxWidth, Math.Max(8, (int)Math.Ceiling(LineHeight * (double)cw / Math.Max(1, ch))));
			return new Job { Line = pic.Crop(b[0], b[1], b[2], b[3]).Resize(nw, LineHeight), Bucket = BucketFor((nw + 7) / 8 * 8) };
		}

		/// <summary>The picture's lines, top to bottom, each its boxes left to right (what the detector found as one stretch of text), each its words.</summary>
		public List<List<List<TextWord>>> Read(TextPicture pic) {
			Time.Start();
			try {
				var rows = Rows(Boxes(pic)).Select(r => r.Select(b => Prepare(pic, b)).ToList()).ToList();
				foreach (Job j in rows.SelectMany(r => r)) Recognize(j);
				Pictures++;
				return rows.Select(r => r.Where(j => j.Words.Count > 0).Select(j => j.Words).ToList()).Where(l => l.Count > 0).ToList();
			}
			finally { Time.Stop(); }
		}

		void Recognize(Job j) {
			int width = j.Bucket, size = 3 * LineHeight * width;
			var x = new float[size];
			// The rest of the width is left 0, the grey the model was trained with.
			for (int y = 0; y < LineHeight; y++)
				for (int xx = 0; xx < j.Line.Width; xx++) {
					int src = (y * j.Line.Width + xx) * 3, dst = y * width + xx;
					x[dst] = RecValue[j.Line.Bgr[src]];
					x[dst + LineHeight * width] = RecValue[j.Line.Bgr[src + 1]];
					x[dst + 2 * LineHeight * width] = RecValue[j.Line.Bgr[src + 2]];
				}
			float[] logits = recs[width].Run(x, new long[] { 1, 3, LineHeight, width }, out long[] os);
			Decode(logits, (int)os[1], (int)os[2], j);
		}

		/// <summary>CTC's greedy reading: the likeliest class each step, repeats and blanks dropped; a space ends a word.</summary>
		void Decode(float[] logits, int steps, int classes, Job j) {
			var sb = new StringBuilder();
			double sure = 0;
			int prev = 0;
			// The model's last layer is a softmax; should an export leave it out, its scores are made probabilities here.
			float first = 0;
			for (int c = 0; c < classes; c++) first += logits[c];
			bool probabilities = Math.Abs(first - 1) < 0.01f && logits.Take(classes).All(v => v >= 0);
			for (int t = 0; t < steps; t++) {
				int at = t * classes, best = 0;
				float bv = logits[at];
				for (int c = 1; c < classes; c++) if (logits[at + c] > bv) { bv = logits[at + c]; best = c; }
				if (best != prev && best != 0 && best <= charset.Length) {
					float p = probabilities ? bv : Softmax(logits, at, classes, best);
					char ch = charset[best - 1];
					if (ch == ' ') Flush();
					else { sb.Append(ch); sure += p; }
				}
				prev = best;
			}
			Flush();

			void Flush() {
				if (sb.Length == 0) return;
				j.Words.Add(new TextWord(sb.ToString(), (float)(sure / sb.Length)));
				sb.Clear();
				sure = 0;
			}
		}

		static float Softmax(float[] logits, int at, int classes, int best) {
			double sum = 0;
			for (int c = 0; c < classes; c++) sum += Math.Exp(logits[at + c] - logits[at + best]);
			return (float)(1 / sum);
		}
	}
}
