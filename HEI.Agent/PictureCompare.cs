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

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using HEI.Core.AI;
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <summary>/api/pictures/compare's question: two sets of pictures, each picture's bytes as base64.</summary>
	sealed record PicturesRequest(List<byte[]?>? A, List<byte[]?>? B);

	/// <param name="A">The picture's index in <c>a</c>.</param>
	/// <param name="B">Its partner's index in <c>b</c>.</param>
	/// <param name="Relation">What one is to the other, as Heiward's review page says it (<see cref="ReportBuilder.Relation"/>).</param>
	/// <param name="Similarity">How alike they are, 0 to 1: the grayscale match, or the AI's cosine for an edit or a variant.</param>
	sealed record PicturePair(int A, int B, string Relation, double Similarity);

	sealed record PicturesUndecodable(List<int> A, List<int> B);

	/// <param name="Same">The two sets hold the same pictures: as many, all decoded, all paired, and every pair a plain copy.</param>
	/// <param name="Device">Where the AI model ran (NPU, GPU or CPU); null when no picture needed it.</param>
	sealed record PicturesAnswer(bool Same, List<PicturePair> Pairs, List<int> UnmatchedA, List<int> UnmatchedB,
		PicturesUndecodable Undecodable, string? Device);

	/// <summary>A decoded picture: its bytes, and the AI's and the scan's frames of it, made as a scan makes a photo's.</summary>
	sealed record Picture(byte[] Bytes, byte[] Hash, byte[] Rgb, byte[] Gray, int Width, int Height, bool Animated) {
		/// <summary>Width plus height, as a scan's <c>FrameSizeInt</c>: the larger is the higher resolution.</summary>
		public int FrameSize => Width + Height;
	}

	/// <summary>Heiward's AI model, lent to a comparison.</summary>
	interface IPictureEmbedder {
		/// <summary>One L2-normalized embedding per 224×224 RGB24 frame, and the device that made them ("NPU", "GPU", "CPU").</summary>
		(float[][] Embeddings, string Device) Embed(IReadOnlyList<byte[]> frames);
	}

	/// <summary>
	/// Compares two sets of pictures by look, for another agent: Chamberlain, finding duplicate documents, asks
	/// whether two documents with the same words hold the same pictures too. Each picture is paired with its best
	/// partner one to one, and each pair judged by Heiward's own rules for photos (<see cref="ReportBuilder.ByLook"/>):
	/// the same bytes are <c>identical</c>; a grayscale match of <see cref="ReportBuilder.PlainCopyPercent"/> or more is
	/// the same picture, pixel for pixel (<c>smaller</c>, <c>compressed</c>, <c>resaved</c>: the plain copies the review
	/// page pre-ticks); below that the AI's cosine says <c>edited</c> or <c>variant</c>. A picture less alike to every
	/// other than <see cref="ReportBuilder.MinAlikePercent"/> has no partner, as it would leave a set on the review page.
	/// </summary>
	static class PictureCompare {
		/// <summary>The route's version, in /api/ping's <c>pictures</c>.</summary>
		public const int Version = 1;
		public const int MaxPictures = 64;
		public const int MaxBodyBytes = 32 * 1024 * 1024;
		/// <summary>A picture claiming more pixels than this isn't decoded: a few bytes can claim a picture too big to decode.</summary>
		public const long MaxPixels = 64_000_000;

		/// <summary>The picture decoded through WIC, as a scan decodes a photo; null when WIC can't read it.</summary>
		internal static Picture? Decode(byte[]? bytes) {
			if (bytes is not { Length: > 0 }) return null;
			if (!WicImageDecoder.TryDecode(bytes, MaxPixels, out byte[]? gray, out byte[]? rgb, out int width, out int height, out int frames) ||
				gray == null || rgb == null)
				return null;
			return new Picture(bytes, SHA256.HashData(bytes), rgb, gray, width, height, frames > 1);
		}

		/// <summary>
		/// The two sets compared. <paramref name="eye"/> is asked only for the pictures the pixels leave undecided: those
		/// not paired as plain copies with a partner. Null: no AI, and such pairs are variants, as on the review page
		/// when the AI is off.
		/// </summary>
		internal static PicturesAnswer Compare(IReadOnlyList<byte[]?> a, IReadOnlyList<byte[]?> b, IPictureEmbedder? eye) {
			Picture?[] pa = a.Select(Decode).ToArray(), pb = b.Select(Decode).ToArray();
			var undecodable = new PicturesUndecodable(
				Enumerable.Range(0, pa.Length).Where(i => pa[i] == null).ToList(),
				Enumerable.Range(0, pb.Length).Where(j => pb[j] == null).ToList());

			// First the pairs the pixels decide: plain copies, the same bytes first, then the closest.
			var plain = new List<(int I, int J, string Relation, float Percent)>();
			for (int i = 0; i < pa.Length; i++)
				for (int j = 0; j < pb.Length; j++)
					if (pa[i] is { } x && pb[j] is { } y && Judge(x, y, ai: null) is var (relation, percent) && ReportBuilder.IsPlainCopy(relation))
						plain.Add((i, j, relation, percent));
			var pairs = new List<PicturePair>();
			bool[] takenA = new bool[pa.Length], takenB = new bool[pb.Length];
			Take(plain);

			// What's left on both sides needs the AI: an edit, or another picture.
			List<int> leftA = Enumerable.Range(0, pa.Length).Where(i => pa[i] != null && !takenA[i]).ToList();
			List<int> leftB = Enumerable.Range(0, pb.Length).Where(j => pb[j] != null && !takenB[j]).ToList();
			string? device = null;
			if (leftA.Count > 0 && leftB.Count > 0) {
				// Quantized as a scan keeps them, so the cosine is the one the thresholds were measured on.
				Dictionary<(char, int), byte[]>? embeddings = null;
				if (eye != null) {
					var frames = leftA.Select(i => pa[i]!.Rgb).Concat(leftB.Select(j => pb[j]!.Rgb)).ToList();
					(float[][] made, device) = eye.Embed(frames);
					embeddings = new();
					for (int k = 0; k < leftA.Count; k++) embeddings[('a', leftA[k])] = EmbeddingMath.QuantizeUnitVector(made[k]);
					for (int k = 0; k < leftB.Count; k++) embeddings[('b', leftB[k])] = EmbeddingMath.QuantizeUnitVector(made[leftA.Count + k]);
				}
				var rest = new List<(int I, int J, string Relation, float Percent)>();
				foreach (int i in leftA)
					foreach (int j in leftB) {
						float? ai = embeddings == null ? null : 100f * EmbeddingMath.CosineSimilarity(embeddings[('a', i)], embeddings[('b', j)]);
						var (relation, percent) = Judge(pa[i]!, pb[j]!, ai);
						if (percent >= ReportBuilder.MinAlikePercent)
							rest.Add((i, j, relation, percent));
					}
				Take(rest);
			}

			pairs.Sort((p, q) => p.A.CompareTo(q.A));
			var unmatchedA = Enumerable.Range(0, pa.Length).Where(i => pa[i] != null && !takenA[i]).ToList();
			var unmatchedB = Enumerable.Range(0, pb.Length).Where(j => pb[j] != null && !takenB[j]).ToList();
			bool same = pa.Length == pb.Length && undecodable.A.Count == 0 && undecodable.B.Count == 0 &&
				unmatchedA.Count == 0 && unmatchedB.Count == 0 && pairs.All(p => ReportBuilder.IsPlainCopy(p.Relation));
			return new PicturesAnswer(same, pairs, unmatchedA, unmatchedB, undecodable, device);

			// One to one, the best first, as the review page fills a set: identical, then plain copies, then the most alike.
			void Take(List<(int I, int J, string Relation, float Percent)> candidates) {
				foreach (var c in candidates
						.OrderByDescending(c => c.Relation == "identical").ThenByDescending(c => ReportBuilder.IsPlainCopy(c.Relation))
						.ThenByDescending(c => c.Percent).ThenBy(c => c.I).ThenBy(c => c.J)) {
					if (takenA[c.I] || takenB[c.J]) continue;
					takenA[c.I] = takenB[c.J] = true;
					pairs.Add(new PicturePair(c.I, c.J, c.Relation, Math.Round(Math.Clamp(c.Percent, 0f, 100f) / 100.0, 4)));
				}
			}
		}

		/// <summary>
		/// What one picture is to the other, and how alike they are (percent), as <see cref="ReportBuilder.Relation"/>
		/// judges two photos: the one Heiward would keep is the higher resolution, then the larger file
		/// (<c>PickPhotoKeeper</c>; pictures in memory have no capture date or file time), and the relation is the
		/// other's to it. <paramref name="ai"/>: the AI's cosine (percent); null when it wasn't asked.
		/// </summary>
		internal static (string Relation, float Percent) Judge(Picture x, Picture y, float? ai) {
			if (x.Bytes.Length == y.Bytes.Length && x.Hash.AsSpan().SequenceEqual(y.Hash))
				return ("identical", 100f);
			bool xKept = x.FrameSize != y.FrameSize ? x.FrameSize > y.FrameSize : x.Bytes.Length >= y.Bytes.Length;
			(Picture keep, Picture other) = xKept ? (x, y) : (y, x);
			float gray = 100f * (1f - GrayBytesUtils.PercentageDifference(other.Gray, keep.Gray));
			// An animated picture is compared by its first frame only: never a plain copy (Relation's MayBeAnimated).
			string relation = x.Animated || y.Animated ? "variant"
				: ReportBuilder.ByLook(gray, () => ai, smaller: other.FrameSize < keep.FrameSize, shorterCut: false,
					fewerBytes: other.Bytes.Length < keep.Bytes.Length);
			// As the page shows it (ReportBuilder.Alike): the AI's cosine for an edit or a variant, otherwise the grayscale match.
			return (relation, relation is "edited" or "variant" && ai is float cosine ? cosine : gray);
		}
	}

	/// <summary>
	/// POST /api/pictures/compare: <see cref="PictureCompare"/> for another agent on this PC, behind the page's token
	/// (ReviewServer). It reads nothing from disk, writes nothing but log lines, and never starts a scan. While a game or a
	/// full-screen program holds the graphics, or Heiward is paused, it answers 503 with Retry-After. Without the AI model
	/// (not installed, or busy with a scan) the pixels alone answer, which is the same answer.
	/// </summary>
	static class PicturesRoute {
		static readonly SemaphoreSlim oneAtATime = new(1, 1);
		static readonly JsonSerializerOptions RequestJson = new() { PropertyNameCaseInsensitive = true };
		static readonly JsonSerializerOptions AnswerJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

		/// <summary>Tests: the model's stand-in (no AI components needed, and never the NPU).</summary>
		internal static IPictureEmbedder? TestEmbedder;
		/// <summary>Tests: what wants the graphics now, instead of asking Windows.</summary>
		internal static Func<string?>? TestGraphicsBusy;

		static ModelEye? model;
		static ThreeDWatch? watch;

		public static async Task<IResult> HandleAsync(HttpContext ctx, Func<bool> scanBusy) {
			if (ctx.Request.ContentLength > PictureCompare.MaxBodyBytes) return TooBig();
			// Kestrel's own limit (30 MB) is below the route's: this request may send up to the route's, and no more.
			if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
				limit.MaxRequestBodySize = PictureCompare.MaxBodyBytes + 1L;
			byte[] body;
			try {
				body = await ReadUpToAsync(ctx.Request.Body, PictureCompare.MaxBodyBytes + 1, ctx.RequestAborted);
			}
			catch (BadHttpRequestException) { return TooBig(); }
			if (body.Length > PictureCompare.MaxBodyBytes) return TooBig();

			PicturesRequest? request;
			try { request = JsonSerializer.Deserialize<PicturesRequest>(body, RequestJson); }
			catch (JsonException) { return Bad("Send JSON: {\"a\": [\"<base64 picture>\", ...], \"b\": [...]}, every picture as base64."); }
			if (request?.A == null || request.B == null) return Bad("Send both sets: {\"a\": [...], \"b\": [...]}.");
			if (request.A.Count > PictureCompare.MaxPictures || request.B.Count > PictureCompare.MaxPictures)
				return Bad($"At most {PictureCompare.MaxPictures} pictures a side.");
			if (request.A.Any(p => p == null) || request.B.Any(p => p == null)) return Bad("Every picture is a base64 string.");

			if (!await oneAtATime.WaitAsync(TimeSpan.FromSeconds(30), ctx.RequestAborted))
				return Unavailable(ctx, "Heiward is comparing other pictures. Ask again in a moment.", 10);
			try {
				if (WhyNotNow() is var (error, retryAfter))
					return Unavailable(ctx, error, retryAfter);
				// The answer (same or not) is the pixels' alone: the model only pairs up pictures that are already not plain
				// copies. So without it (not installed, busy with a scan, or failing) the pixels still answer, and the
				// pairs of what differs are made by grayscale instead.
				IPictureEmbedder? eye = TestEmbedder != null ? (scanBusy() ? null : TestEmbedder)
					: AiComponents.IsReady && !scanBusy() ? (model ??= new ModelEye()) : null;
				try {
					return Results.Json(PictureCompare.Compare(request.A, request.B, eye), AnswerJson);
				}
				catch (Exception e) when (e is not OutOfMemoryException && eye != null) {
					// The model couldn't run: the NPU's or card's turn took too long, or the accelerator failed under it.
					AgentPaths.AppendLog($"pictures compare: the AI model couldn't run, so the pixels alone answered: {Accelerators.OneLine(e.Message)}");
					return Results.Json(PictureCompare.Compare(request.A, request.B, null), AnswerJson);
				}
			}
			finally {
				oneAtATime.Release();
			}
		}

		/// <summary>Why Heiward won't compare pictures now, and in how many seconds to ask again; null when it will.</summary>
		static (string Error, int RetryAfter)? WhyNotNow() {
			if (AgentPause.Load() is { } pause) {
				int seconds = pause.UntilUtc is { } until ? (int)Math.Clamp((until - DateTime.UtcNow).TotalSeconds, 60, 3600) : 3600;
				return ("Heiward is paused.", seconds);
			}
			if (GraphicsBusy() is { } busy)
				return ($"Heiward leaves the PC to {busy} for now.", 300);
			return null;
		}

		/// <summary>A full-screen program, or a game keeping the graphics card busy (<see cref="ThreeDWatch"/>), as a scan steps back for.</summary>
		static string? GraphicsBusy() {
			if (TestGraphicsBusy != null) return TestGraphicsBusy();
			if (watch == null) {
				watch = new ThreeDWatch(GpuAdapters.InUse(AgentConfig.Load().Gpu, GpuAdapters.List()));
				Thread.Sleep(250); // a utilization counter needs two samples some time apart
			}
			return watch.Busy();
		}

		/// <summary>The page is closing: the model's session goes with it.</summary>
		public static void Close() {
			model?.Dispose();
			model = null;
		}

		static async Task<byte[]> ReadUpToAsync(Stream body, int max, CancellationToken ct) {
			using var buffer = new MemoryStream();
			byte[] chunk = new byte[81920];
			int read;
			while (buffer.Length < max && (read = await body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, max - buffer.Length)), ct)) > 0)
				buffer.Write(chunk, 0, read);
			return buffer.ToArray();
		}

		static IResult TooBig() => Bad($"The request is over {PictureCompare.MaxBodyBytes / (1024 * 1024)} MB.");

		static IResult Bad(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

		static IResult Unavailable(HttpContext ctx, string error, int retryAfterSeconds) {
			ctx.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
			return Results.Json(new { error }, statusCode: StatusCodes.Status503ServiceUnavailable);
		}
	}

	/// <summary>
	/// Heiward's AI model in the page's process, opened on the device the settings name (<see cref="OnnxEmbedder.Create"/>:
	/// the NPU's session is compiled or loaded in its turn) and closed after <see cref="Idle"/> without a request, so a run
	/// of questions opens it once and no session stays open on the NPU. Each batch takes its turn in the NPU's (or the
	/// card's) line in the background lane (<see cref="NpuLock"/>), and the turn is given back as soon as the batch is done.
	/// </summary>
	sealed class ModelEye : IPictureEmbedder, IDisposable {
		internal static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);
		readonly object gate = new();
		readonly Timer timer;
		OnnxEmbedder? embedder;
		bool disposed;

		public ModelEye() => timer = new Timer(_ => CloseIfIdle());

		public (float[][] Embeddings, string Device) Embed(IReadOnlyList<byte[]> frames) {
			lock (gate) {
				ObjectDisposedException.ThrowIf(disposed, this);
				try {
					embedder ??= Open();
					return (embedder.EmbedBatch(frames), embedder.DeviceName);
				}
				catch {
					CloseNow(); // a failed session isn't kept: the next request opens a fresh one
					throw;
				}
				finally {
					embedder?.YieldAccelerator(); // the NPU's turn, not held between requests
					timer.Change(Idle, Timeout.InfiniteTimeSpan);
				}
			}
		}

		static OnnxEmbedder Open() {
			AgentConfig cfg = AgentConfig.Load();
			GpuAdapters.Choose(cfg.Gpu);
			OnnxEmbedder opened = OnnxEmbedder.Create(Enum.TryParse(cfg.AiDevice, ignoreCase: true, out AiDevice d) ? d : AiDevice.Auto);
			AgentPaths.AppendLog($"pictures compare: the AI model opened on the {opened.DeviceName}" + (opened.Fallback != null ? $" ({opened.Fallback})" : ""));
			return opened;
		}

		void CloseIfIdle() {
			// A request running now sets the timer again when it's done.
			if (!Monitor.TryEnter(gate)) return;
			try { CloseNow(); }
			finally { Monitor.Exit(gate); }
		}

		void CloseNow() {
			if (embedder == null) return;
			try { embedder.Dispose(); }
			catch (Exception e) { AgentPaths.AppendLog($"pictures compare: closing the AI model: {e.Message}"); }
			embedder = null;
		}

		public void Dispose() {
			lock (gate) {
				disposed = true;
				timer.Dispose();
				CloseNow();
			}
		}
	}
}
