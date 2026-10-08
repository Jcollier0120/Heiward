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

using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HEI.Core.Utils;

namespace HEI.Agent.Tests;

/// <summary>
/// Heiward's eye for pictures, lent to another agent (POST /api/pictures/compare): two sets of pictures are the same
/// only when every picture has a plain copy on the other side, judged by the review page's own rules. The pictures are
/// made here (BMP, PNG, and JPEG through WIC); the AI model is a stand-in, so nothing here needs the AI components or
/// touches the NPU.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME and the route's test seams are process-wide
public sealed class PictureCompareTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-pictures-" + Guid.NewGuid().ToString("N"));
	readonly string? manorHome = Environment.GetEnvironmentVariable("MANOR_HOME");
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public PictureCompareTests() {
		Directory.CreateDirectory(Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("MANOR_HOME", Path.Combine(dir, "manor"));
		PicturesRoute.TestEmbedder = new FakeEye();
		PicturesRoute.TestGraphicsBusy = () => null;
	}

	public void Dispose() {
		PicturesRoute.TestEmbedder = null;
		PicturesRoute.TestGraphicsBusy = null;
		Environment.SetEnvironmentVariable("MANOR_HOME", manorHome);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	// ---- Pictures

	/// <summary>A smooth scene with shapes, RGB24 rows top-down: something a camera or a slide could hold.</summary>
	static byte[] Scene(int w, int h, bool edited = false) {
		var px = new byte[w * h * 3];
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++) {
				double u = x / (double)w, v = y / (double)h;
				double r = 200 * u + 30, g = 180 * v + 40, b = 120 + 100 * Math.Sin(6 * u) * Math.Cos(4 * v);
				double dx = u - 0.35, dy = v - 0.45;
				if (dx * dx + dy * dy < 0.04) (r, g, b) = (240, 220, 60); // a sun
				if (u > 0.6 && u < 0.85 && v > 0.55 && v < 0.85) (r, g, b) = (40, 60, 160); // a house
				if (edited && u < 0.5 && v > 0.5) (r, g, b) = (r * 0.15, g * 0.15, b * 0.15); // a quarter blacked out
				int o = (y * w + x) * 3;
				px[o] = (byte)Math.Clamp(r, 0, 255);
				px[o + 1] = (byte)Math.Clamp(g, 0, 255);
				px[o + 2] = (byte)Math.Clamp(b, 0, 255);
			}
		return px;
	}

	static byte[] Crop(byte[] rgb, int w, int x0, int y0, int cw, int ch) {
		var px = new byte[cw * ch * 3];
		for (int y = 0; y < ch; y++)
			Buffer.BlockCopy(rgb, ((y0 + y) * w + x0) * 3, px, y * cw * 3, cw * 3);
		return px;
	}

	static byte[] Bmp(byte[] rgb, int w, int h) {
		int stride = (w * 3 + 3) & ~3;
		var file = new byte[54 + stride * h];
		file[0] = (byte)'B'; file[1] = (byte)'M';
		BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
		BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), 54);
		BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14), 40);
		BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), w);
		BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22), h); // bottom-up
		BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(26), 1);
		BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(28), 24);
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++) {
				int s = (y * w + x) * 3, d = 54 + (h - 1 - y) * stride + x * 3;
				file[d] = rgb[s + 2]; file[d + 1] = rgb[s + 1]; file[d + 2] = rgb[s];
			}
		return file;
	}

	static byte[] Png(byte[] rgb, int w, int h) {
		using var raw = new MemoryStream();
		using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
			for (int y = 0; y < h; y++) {
				z.WriteByte(0); // no filter
				z.Write(rgb, y * w * 3, w * 3);
			}
		using var png = new MemoryStream();
		png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
		var header = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(header, w);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), h);
		header[8] = 8; header[9] = 2; // 8-bit RGB
		Chunk(png, "IHDR", header);
		Chunk(png, "IDAT", raw.ToArray());
		Chunk(png, "IEND", []);
		return png.ToArray();

		static void Chunk(Stream s, string type, byte[] data) {
			Span<byte> n = stackalloc byte[4];
			BinaryPrimitives.WriteInt32BigEndian(n, data.Length);
			s.Write(n);
			byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
			s.Write(typed);
			BinaryPrimitives.WriteUInt32BigEndian(n, Crc32(typed));
			s.Write(n);
		}

		static uint Crc32(byte[] data) {
			uint crc = 0xFFFFFFFF;
			foreach (byte b in data) {
				crc ^= b;
				for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
			}
			return ~crc;
		}
	}

	/// <summary>The picture saved again as a JPEG by Windows, at most <paramref name="maxSide"/> on its longer side.</summary>
	byte[] Jpeg(byte[] picture, int maxSide) {
		string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".bmp");
		File.WriteAllBytes(path, picture);
		Assert.True(WicImageDecoder.TryThumbnailJpeg(path, maxSide, out byte[]? jpeg));
		return jpeg!;
	}

	const int W = 480, H = 360;
	static readonly byte[] SceneRgb = Scene(W, H);
	static readonly byte[] Original = Bmp(SceneRgb, W, H);

	/// <summary>
	/// The model's stand-in: an 8×8 colour thumbnail, centred and normalized, so near pictures score near 1. It
	/// counts the frames it's asked for.
	/// </summary>
	sealed class FakeEye : IPictureEmbedder {
		public int Frames;
		public (float[][] Embeddings, string Device) Embed(IReadOnlyList<byte[]> frames) {
			Interlocked.Add(ref Frames, frames.Count);
			return (frames.Select(Thumb).ToArray(), "CPU");
		}

		static float[] Thumb(byte[] rgb) {
			var v = new float[8 * 8 * 3];
			for (int y = 0; y < 224; y++)
				for (int x = 0; x < 224; x++)
					for (int c = 0; c < 3; c++)
						v[((y / 28) * 8 + x / 28) * 3 + c] += rgb[(y * 224 + x) * 3 + c];
			float mean = v.Average();
			double norm = Math.Sqrt(v.Sum(f => (double)(f - mean) * (f - mean)));
			return v.Select(f => (float)((f - mean) / norm)).ToArray();
		}
	}

	// ---- The rules

	[Fact]
	public void SameBytes_AreIdentical_AndTheModelIsntAsked() {
		var eye = new FakeEye();
		PicturesAnswer answer = PictureCompare.Compare([Original, Png(SceneRgb, W, H)], [Png(SceneRgb, W, H), Original], eye);
		Assert.True(answer.Same);
		Assert.Equal([(0, 1, "identical"), (1, 0, "identical")], answer.Pairs.Select(p => (p.A, p.B, p.Relation)));
		Assert.All(answer.Pairs, p => Assert.Equal(1.0, p.Similarity));
		Assert.Equal(0, eye.Frames);
		Assert.Null(answer.Device);
	}

	[Fact]
	public void SavedAgain_AsAJpeg_IsTheSamePicture() {
		var eye = new FakeEye();
		byte[] jpeg = Jpeg(Original, 4096); // same size, re-compressed
		PicturesAnswer answer = PictureCompare.Compare([Original], [jpeg], eye);
		Assert.True(answer.Same, JsonSerializer.Serialize(answer));
		PicturePair pair = Assert.Single(answer.Pairs);
		Assert.Equal("compressed", pair.Relation); // fewer bytes than the BMP
		Assert.True(pair.Similarity >= ReportBuilder.PlainCopyPercent / 100, $"{pair.Similarity}");
		Assert.Equal(0, eye.Frames);

		// The other way round, and a PNG of it: the same bytes for the pixels are a plain copy whichever is "a".
		Assert.True(PictureCompare.Compare([jpeg, Png(SceneRgb, W, H)], [Original, jpeg], eye).Same);
	}

	[Fact]
	public void ScaledDown_IsSmaller_AndStillTheSame() {
		PicturesAnswer answer = PictureCompare.Compare([Jpeg(Original, 320)], [Original], new FakeEye());
		Assert.True(answer.Same, JsonSerializer.Serialize(answer));
		Assert.Equal("smaller", Assert.Single(answer.Pairs).Relation);
	}

	[Fact]
	public void Cropped_IsNotTheSame() {
		byte[] cropped = Bmp(Crop(SceneRgb, W, 0, 0, W * 2 / 3, H * 2 / 3), W * 2 / 3, H * 2 / 3);
		var eye = new FakeEye();
		PicturesAnswer answer = PictureCompare.Compare([Original], [cropped], eye);
		Assert.False(answer.Same);
		Assert.True(answer.Pairs.All(p => p.Relation is "edited" or "variant"), JsonSerializer.Serialize(answer));
		Assert.Equal(2, eye.Frames); // only what the pixels left undecided went to the model
		Assert.Equal("CPU", answer.Device);
	}

	[Fact]
	public void Retouched_IsNotTheSame() {
		byte[] edited = Bmp(Scene(W, H, edited: true), W, H);
		PicturesAnswer answer = PictureCompare.Compare([Original, Png(SceneRgb, W, H)], [Jpeg(Original, 4096), edited], new FakeEye());
		Assert.False(answer.Same);
		// The untouched one pairs with its copy; the edit is left as an edit or a variant, or with no partner.
		Assert.Contains(answer.Pairs, p => p.Relation == "compressed");
		Assert.DoesNotContain(answer.Pairs, p => p.B == 1 && ReportBuilder.IsPlainCopy(p.Relation));
	}

	[Fact]
	public void APictureAdded_OrRemoved_IsNotTheSame() {
		byte[] other = Bmp(Scene(W, H, edited: true), W, H);
		PicturesAnswer added = PictureCompare.Compare([Original], [Original, other], new FakeEye());
		Assert.False(added.Same);
		PicturesAnswer removed = PictureCompare.Compare([Original, Original], [Original], new FakeEye());
		Assert.False(removed.Same);
		Assert.Equal([1], removed.UnmatchedA);
		Assert.Empty(removed.UnmatchedB);
	}

	[Fact]
	public void Undecodable_IsNotTheSame() {
		byte[] junk = Encoding.ASCII.GetBytes("not a picture at all");
		PicturesAnswer answer = PictureCompare.Compare([Original, junk], [Original, junk], new FakeEye());
		Assert.False(answer.Same);
		Assert.Equal([1], answer.Undecodable.A);
		Assert.Equal([1], answer.Undecodable.B);
		Assert.Equal((0, 0), (Assert.Single(answer.Pairs).A, answer.Pairs[0].B));
	}

	[Fact]
	public void NoPictures_OnEitherSide_AreTheSame() {
		PicturesAnswer answer = PictureCompare.Compare([], [], new FakeEye());
		Assert.True(answer.Same);
		Assert.Empty(answer.Pairs);
	}

	[Fact]
	public void UnlikePictures_HaveNoPartner() {
		// A flat grey and the scene: far below the review page's 75%, so neither is paired.
		var flat = new byte[W * H * 3];
		Array.Fill(flat, (byte)128);
		var eye = new FixedEye(0.2f);
		PicturesAnswer answer = PictureCompare.Compare([Original], [Bmp(flat, W, H)], eye);
		Assert.False(answer.Same);
		Assert.Empty(answer.Pairs);
		Assert.Equal([0], answer.UnmatchedA);
		Assert.Equal([0], answer.UnmatchedB);
	}

	/// <summary>Every two frames score <c>cosine</c>: the first frame is a unit vector, every other one at that angle to it.</summary>
	sealed class FixedEye(float cosine) : IPictureEmbedder {
		public (float[][] Embeddings, string Device) Embed(IReadOnlyList<byte[]> frames) {
			float sine = MathF.Sqrt(1 - cosine * cosine);
			return (frames.Select((_, k) => k == 0 ? new float[] { 1, 0 } : new float[] { cosine, sine }).ToArray(), "NPU");
		}
	}

	[Theory]
	// The review page's thresholds: a grayscale match of 99.5% or more is a plain copy; below it the AI says edited at 97%.
	[InlineData(99.5f, null, false, false, "resaved")]
	[InlineData(99.9f, null, false, true, "compressed")]
	[InlineData(99.9f, null, true, true, "smaller")]
	[InlineData(99.4f, 97f, false, false, "edited")]
	[InlineData(99.4f, 96.9f, false, false, "variant")]
	[InlineData(99.4f, null, false, false, "variant")]
	public void ByLook_IsTheReviewPagesRule(float gray, float? ai, bool smaller, bool fewerBytes, string relation) {
		Assert.Equal(relation, ReportBuilder.ByLook(gray, () => ai, smaller, shorterCut: false, fewerBytes));
	}

	[Fact]
	public void AnEdit_TheModelCallsTheSamePicture_IsEditedNotACopy() {
		byte[] edited = Bmp(Scene(W, H, edited: true), W, H);
		PicturesAnswer answer = PictureCompare.Compare([Original], [edited], new FixedEye(0.99f));
		Assert.False(answer.Same);
		PicturePair pair = Assert.Single(answer.Pairs);
		Assert.Equal("edited", pair.Relation);
		Assert.Equal(0.99, pair.Similarity, 2); // the AI's cosine, as the page shows an edit
		Assert.Equal("NPU", answer.Device);
	}

	[Fact]
	public void AnAnimatedPicture_IsNeverAPlainCopy() {
		Picture still = PictureCompare.Decode(Original)!, alsoStill = PictureCompare.Decode(Jpeg(Original, 4096))!;
		Assert.True(ReportBuilder.IsPlainCopy(PictureCompare.Judge(still, alsoStill, null).Relation));
		Assert.Equal("variant", PictureCompare.Judge(still, alsoStill with { Animated = true }, null).Relation);
		Assert.Equal("identical", PictureCompare.Judge(still with { Animated = true }, still with { Animated = true }, null).Relation);
	}

	// ---- The route

	sealed class Page : IAsyncDisposable {
		readonly int port;
		readonly Task<int> server;
		public readonly HttpClient Http;
		public readonly string Token;

		Page(int port, Task<int> server, HttpClient http, string token) => (this.port, this.server, Http, Token) = (port, server, http, token);

		public static async Task<Page> StartAsync() {
			var cfg = new AgentConfig { ScanEveryMinutes = 0 };
			using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
				probe.Start();
				cfg.Port = ((IPEndPoint)probe.LocalEndpoint).Port;
			}
			cfg.Save();
			Task<int> server = ReviewServer.RunAsync(cfg, openBrowser: false, CancellationToken.None);
			for (int i = 0; i < 40 && !await ReviewServer.IsUpAsync(cfg.Port); i++) await Task.Delay(250);
			var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{cfg.Port}/"), Timeout = TimeSpan.FromSeconds(60) };
			return new Page(cfg.Port, server, http, ReviewServer.TokenIn(await http.GetStringAsync("/"))!);
		}

		/// <summary>Posts as Chamberlain does: the token from the page, and no Origin.</summary>
		public async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Answer)> CompareAsync(HttpContent content, bool token = true) {
			using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pictures/compare") { Content = content };
			if (token) request.Headers.Add("X-Agent-Token", Token);
			HttpResponseMessage answer = await Http.SendAsync(request);
			return (answer.StatusCode, await answer.Content.ReadAsStringAsync(), answer);
		}

		public Task<(HttpStatusCode Status, string Body, HttpResponseMessage Answer)> CompareAsync(object body, bool token = true) =>
			CompareAsync(JsonContent.Create(body), token);

		public async ValueTask DisposeAsync() {
			if (!server.IsCompleted) await ReviewServer.AskToCloseAsync(port);
			await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
			Http.Dispose();
		}
	}

	static string B64(byte[] bytes) => Convert.ToBase64String(bytes);

	[Fact]
	public async Task TheRoute_ComparesPictures_ForAnotherAgent() {
		await using Page page = await Page.StartAsync();

		// No token, no answer: another web page can't use Heiward's model.
		Assert.Equal(HttpStatusCode.Forbidden, (await page.CompareAsync(new { a = Array.Empty<string>(), b = Array.Empty<string>() }, token: false)).Status);

		var (status, body, _) = await page.CompareAsync(new { a = new[] { B64(Original) }, b = new[] { B64(Jpeg(Original, 4096)) } });
		Assert.Equal(HttpStatusCode.OK, status);
		using (JsonDocument answer = JsonDocument.Parse(body)) {
			JsonElement root = answer.RootElement;
			Assert.True(root.GetProperty("same").GetBoolean());
			JsonElement pair = root.GetProperty("pairs")[0];
			Assert.Equal(0, pair.GetProperty("a").GetInt32());
			Assert.Equal(0, pair.GetProperty("b").GetInt32());
			Assert.Equal("compressed", pair.GetProperty("relation").GetString());
			Assert.InRange(pair.GetProperty("similarity").GetDouble(), 0.995, 1);
			Assert.Equal(0, root.GetProperty("unmatchedA").GetArrayLength());
			Assert.Equal(0, root.GetProperty("unmatchedB").GetArrayLength());
			Assert.Equal(0, root.GetProperty("undecodable").GetProperty("a").GetArrayLength());
			Assert.Equal(JsonValueKind.Null, root.GetProperty("device").ValueKind); // the pixels decided: no model asked
		}

		(status, body, _) = await page.CompareAsync(new { a = new[] { B64(Original) }, b = new[] { B64(Bmp(Scene(W, H, edited: true), W, H)), "bm90IGEgcGljdHVyZQ==" } });
		Assert.Equal(HttpStatusCode.OK, status);
		using (JsonDocument answer = JsonDocument.Parse(body)) {
			Assert.False(answer.RootElement.GetProperty("same").GetBoolean());
			Assert.Equal(1, answer.RootElement.GetProperty("undecodable").GetProperty("b")[0].GetInt32());
		}

		(status, body, _) = await page.CompareAsync(new { a = Array.Empty<string>(), b = Array.Empty<string>() });
		Assert.Equal(HttpStatusCode.OK, status);
		Assert.Contains("\"same\":true", body);

		// The ping says the route is here, and what it had says stays.
		using JsonDocument ping = JsonDocument.Parse(await page.Http.GetStringAsync("/api/ping"));
		Assert.Equal(1, ping.RootElement.GetProperty("pictures").GetInt32());
		Assert.Equal("heiward", ping.RootElement.GetProperty("app").GetString());
		Assert.True(ping.RootElement.GetProperty("tour").GetBoolean());
	}

	[Fact]
	public async Task TheRoute_RefusesWhatItCantTake() {
		await using Page page = await Page.StartAsync();
		string tiny = B64(Png(new byte[3], 1, 1));

		var (status, body, _) = await page.CompareAsync(new { a = Enumerable.Repeat(tiny, 65).ToArray(), b = new[] { tiny } });
		Assert.Equal(HttpStatusCode.BadRequest, status);
		Assert.Contains("At most 64 pictures", body);

		(status, _, _) = await page.CompareAsync(new StringContent("{\"a\": [\"***\"], \"b\": []}", Encoding.UTF8, "application/json"));
		Assert.Equal(HttpStatusCode.BadRequest, status); // not base64

		(status, _, _) = await page.CompareAsync(new StringContent("{\"a\": [", Encoding.UTF8, "application/json"));
		Assert.Equal(HttpStatusCode.BadRequest, status); // not JSON

		(status, _, _) = await page.CompareAsync(new { a = new[] { tiny } });
		Assert.Equal(HttpStatusCode.BadRequest, status); // one set only

		// Over 32 MB: refused before it's read.
		var big = new string('A', PictureCompare.MaxBodyBytes);
		(status, body, _) = await page.CompareAsync(new StringContent($"{{\"a\": [\"{big}\"], \"b\": []}}", Encoding.UTF8, "application/json"));
		Assert.Equal(HttpStatusCode.BadRequest, status);
		Assert.Contains("over 32 MB", body);

		// Just under it is read, though over the web server's own 30 MB (and bytes that aren't a picture are undecodable).
		string almost = new('A', 31 * 1024 * 1024);
		(status, body, _) = await page.CompareAsync(new StringContent($"{{\"a\": [\"{almost}\"], \"b\": []}}", Encoding.UTF8, "application/json"));
		Assert.Equal(HttpStatusCode.OK, status);
		Assert.Contains("\"same\":false", body);
	}

	[Fact]
	public async Task TheRoute_StepsBack_WhileTheModelIsBusyOrShouldntRun() {
		await using Page page = await Page.StartAsync();
		object one = new { a = new[] { B64(Original) }, b = new[] { B64(Original) } };

		// A scan holds scan.lock, as `hei scan` does.
		using (new FileStream(AgentPaths.ScanLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) {
			var (status, body, answer) = await page.CompareAsync(one);
			Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
			Assert.Contains("A scan is running", body);
			Assert.True(answer.Headers.RetryAfter?.Delta > TimeSpan.Zero);
		}
		Assert.False(File.Exists(AgentPaths.ScanLock)); // the route left nothing behind

		PicturesRoute.TestGraphicsBusy = () => "a full-screen game";
		{
			var (status, body, answer) = await page.CompareAsync(one);
			Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
			Assert.Contains("a full-screen game", body);
			Assert.NotNull(answer.Headers.RetryAfter);
		}
		PicturesRoute.TestGraphicsBusy = () => null;

		AgentPause.Start(30, DateTime.UtcNow);
		{
			var (status, body, answer) = await page.CompareAsync(one);
			Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
			Assert.Contains("paused", body);
			Assert.InRange(answer.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 60, 1800);
		}
		AgentPause.Resume();

		PicturesRoute.TestEmbedder = null; // the real model, whose components this test home doesn't have
		string? ai = HEI.Core.AI.AiComponents.IsReady ? "ready" : null;
		if (ai == null) {
			var (status, body, _) = await page.CompareAsync(one);
			Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
			Assert.Contains("isn't installed", body);
		}
		PicturesRoute.TestEmbedder = new FakeEye();

		Assert.Equal(HttpStatusCode.OK, (await page.CompareAsync(one)).Status);
	}
}
