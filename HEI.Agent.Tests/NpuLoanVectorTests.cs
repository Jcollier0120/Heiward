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

using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using HEI.Agent.Estate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HEI.Agent.Tests;

/// <summary>
/// Heiward's NPU loan as a twin of the Steward kit's (kit\spec\NPU-LOAN.md): every section of the spec's shared vectors,
/// kit\spec\npu-loan-vectors.json, run unchanged against the code "A twin's checklist" there names; and a loan between
/// two PCs end to end, each PC's Manor faked in-process.
/// </summary>
[Collection(AgentHomeCollection.Name)] // EstateLink.SendOverride is process-wide
public sealed class NpuLoanVectorTests : IDisposable {
	/// <summary>No Castellan in the tests: a loan's pulls are asked again until it's stopped, and never reach a real Manor.</summary>
	public NpuLoanVectorTests() => EstateLink.SendOverride = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("no Castellan in the tests"));

	public void Dispose() => EstateLink.SendOverride = null;

	// ---------------------------------------------------------------- the vectors

	static readonly Lazy<JsonObject> vectors = new(Load);
	static JsonObject V => vectors.Value;

	/// <summary>The vectors, from the kit's spec part in the repository's kit\ folder, which tools\kit.ps1 fills.</summary>
	static JsonObject Load() {
		for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
			string file = Path.Combine(dir.FullName, "kit", "spec", "npu-loan-vectors.json");
			if (File.Exists(file)) return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
			if (Path.Exists(Path.Combine(dir.FullName, ".git"))) break; // the repository's root
		}
		throw new FileNotFoundException(@"kit\spec\npu-loan-vectors.json isn't in this repository: run powershell -NoProfile -File tools\kit.ps1 to fill kit\ with the Steward's kit (its spec part, kit 2.158.0 or later).");
	}

	static JsonArray Section(string name) => V[name]!.AsArray();
	static string Case(JsonNode? c) => (string)c!["case"]!;
	static string Who => (string)V["staff"]!["who"]!;
	static string Agent => (string)V["staff"]!["agent"]!;
	static string Route => (string)V["staff"]!["route"]!;
	static Pc? PcOf(string? key) => key == null ? null : V["pcs"]![key] is JsonObject pc ? new Pc((string)pc["id"]!, (string)pc["name"]!) : null;
	static Pc Laptop => PcOf("laptop")!;

	[Fact]
	public void TimingAndHeaders_AreTheKits() {
		JsonNode t = V["timing"]!;
		Assert.Equal((double)t["beatMs"]!, LoanTiming.Default.Beat.TotalMilliseconds);
		Assert.Equal((double)t["deadMs"]!, LoanTiming.Default.Dead.TotalMilliseconds);
		Assert.Equal((double)t["retryMs"]!, LoanTiming.Default.Retry.TotalMilliseconds);
		Assert.Equal((double)t["graceMs"]!, LoanTiming.Default.Grace.TotalMilliseconds);
		Assert.Equal((int)t["keep"]!, LoanTiming.Default.Keep);
		Assert.Equal((string)V["headers"]!["peer"]!, EstateLink.PeerHeader);
		Assert.Equal((string)V["headers"]!["peerName"]!, EstateLink.PeerNameHeader);
	}

	[Fact]
	public void Askers_ManorsTwoHeadersRead() {
		foreach (JsonNode? c in Section("askers")) {
			var ctx = new DefaultHttpContext();
			foreach (var (name, value) in c!["headers"]!.AsObject()) ctx.Request.Headers[name] = (string)value!;
			Pc? asker = EstateLink.AskerOf(ctx.Request);
			Same(c["expected"], asker == null ? null : new JsonObject { ["id"] = asker.Id, ["name"] = asker.Name }, Case(c));
		}
	}

	[Fact]
	public void Routes_ALoansNames() {
		foreach (JsonNode? c in Section("routes")) {
			var paths = NpuLoan.LoanPaths((string)c!["route"]!);
			Same(c["expected"], paths is { } p ? new JsonObject { ["offer"] = p.Offer, ["out"] = p.Out, ["in"] = p.In } : null, Case(c));
		}
	}

	[Fact]
	public void Asks_ThePathsAskedOfThisPcsManor() {
		foreach (JsonNode? c in Section("asks")) {
			string ask = (string)c!["ask"]!, pc = (string)c["pc"]!;
			string path = ask == "offer" ? NpuLoan.PeerPath(Agent, Route, pc, "")
				: NpuLoan.PullPath(ask == "out" ? NpuLoan.OutPath(Agent, Route, pc, (string)c["loan"]!) : NpuLoan.InPath(Agent, Route, pc, (string)c["loan"]!),
					(string?)c["box"], (long)c["after"]!);
			Same(c["expected"], path, Case(c));
		}
	}

	[Fact]
	public void Outbox_AStreamStartsWithTheBoxTheHeadAndTheLinesKept() {
		foreach (JsonNode? c in Section("outbox")) {
			var box = new Outbox(LoanTiming.Default with { Keep = (int)c!["keep"]! }, (string)c["id"]!);
			foreach (JsonNode? p in c["pushes"]!.AsArray()) {
				LoanAnswers? answers = p!["answers"] is JsonObject a ? new LoanAnswers((long)a["answers"]!, (double)a["ms"]!) : null;
				box.Push(p["line"]!.DeepClone().AsObject(), p["head"]?.GetValue<bool>() == true, answers);
			}
			string? asked = (string?)c["ask"]!["box"];
			double after = (double)c["ask"]!["after"]!;
			List<string> opening = box.Opening(asked, after);
			Assert.True(opening.All(t => t.EndsWith('\n') && !t[..^1].Contains('\n')), $"{Case(c)}: one line each, ending in a newline");
			Same(c["expected"], Lines(opening), Case(c));
			// A stream asked so starts with exactly those lines.
			Outbox.Sink s = box.Stream(asked, after);
			var streamed = new List<string>();
			while (streamed.Count < opening.Count && s.Reader.TryRead(out string? text)) streamed.Add(text);
			s.Close();
			Same(c["expected"], Lines(streamed), Case(c) + ", as streamed");
		}
	}

	[Fact]
	public void Reading_TheOtherSidesStream() {
		foreach (JsonNode? c in Section("reading")) {
			JsonNode state = c!["state"]!;
			var r = new LineReader { Box = (string?)state["box"], Last = (long)state["last"]! };
			var heard = new JsonArray();
			foreach (JsonNode? chunk in c["chunks"]!.AsArray()) {
				List<Heard> h = r.Feed((string)chunk!);
				foreach (Heard x in h) heard.Add(Json(x));
				if (h.Any(x => x is HeardEnd)) break;
			}
			Same(c["expected"]!["heard"], heard, Case(c));
			Same(c["expected"]!["state"], new JsonObject { ["box"] = r.Box, ["last"] = r.Last }, Case(c) + ": where the reader got to");
		}
	}

	[Fact]
	public void Reading_DecodesUtf8AcrossReads() {
		var r = new LineReader();
		byte[] bytes = Encoding.UTF8.GetBytes("{\"n\":1,\"line\":{\"name\":\"游戏电脑\"}}\n");
		int split = Array.IndexOf(bytes, (byte)0xE6) + 1; // inside the first character's three bytes
		var heard = r.Feed(bytes.AsSpan(0, split)).Concat(r.Feed(bytes.AsSpan(split))).ToList();
		Assert.Equal("游戏电脑", (string?)Assert.IsType<HeardLine>(Assert.Single(heard)).Line["name"]);
	}

	[Fact]
	public void Reading_LetsAPartialLineOverOneMiBGo() {
		var r = new LineReader();
		Assert.Empty(r.Feed("{\"n\":1,\"line\":{\"a\":\"" + new string('x', NpuLoan.LineMax)));
		Assert.Equal(0, r.Partial);
		// Its tail, when its newline comes, isn't a line; the next one is.
		Assert.Equal(2, Assert.IsType<HeardLine>(Assert.Single(r.Feed("xx\"}}\n{\"n\":2,\"line\":{}}\n"))).N);
	}

	[Fact]
	public void Pulls_WhatEachStatusMeans() {
		foreach (JsonNode? c in Section("pulls"))
			Same(c!["expected"], NpuLoan.PullAnswerOf((int)c["status"]!, (bool)c["body"]!).ToString().ToLowerInvariant(), Case(c));
	}

	[Fact]
	public void Offers_APcsAnswerReadAndItsWords() {
		foreach (JsonNode? c in Section("offers")) {
			LoanOffer o = NpuLoan.OfferFrom(Who, Laptop, (int?)c!["status"], Element(c["body"]));
			Same(c["expected"], o.Can ? new JsonObject { ["can"] = true, ["npu"] = o.Npu } : new JsonObject { ["can"] = false, ["why"] = o.Why }, Case(c));
		}
	}

	[Fact]
	public void Overs_TheWordsForALoanTheLenderEnded() {
		foreach (JsonNode? c in Section("overs")) {
			JsonNode over = c!["over"]!;
			LoanOver o = over["end"] is JsonNode end ? new LoanOver((string)end!)
				: new LoanOver(null, (int)over["refused"]!, (string?)over["said"]?["error"], (string?)over["said"]?["code"]);
			Same(c["expected"], NpuLoan.OverWords(Who, Laptop, o), Case(c));
		}
	}

	[Fact]
	public void Estates_ManorsPeersReadAndItsWords() {
		foreach (JsonNode? c in Section("estates")) {
			EstateLink.EstateView e = EstateLink.EstateFrom(Agent, Who, (int?)c!["status"], Element(c["body"]));
			var pcs = new JsonArray(e.Pcs.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["here"] = p.Here, ["has"] = p.HasHeiward }).ToArray());
			Same(c["expected"], new JsonObject { ["on"] = e.On, ["pcs"] = pcs, ["problem"] = e.Problem }, Case(c));
		}
	}

	[Fact]
	public async Task Lender_ItsRoutesStepByStep() {
		foreach (JsonNode? c in Section("lender")) {
			int begun = 0, ended = 0;
			string? whyNot = (string?)c!["whyNot"], npu = (string?)c["npu"], beginFails = (string?)c["beginFails"];
			var lending = new Lending(Route, () => whyNot, () => npu, _ => {
				begun++;
				if (beginFails != null) throw new InvalidOperationException(beginFails);
				return new Work(() => ended++);
			});
			try {
				int i = 0;
				foreach (JsonNode? s in c["steps"]!.AsArray()) {
					string step = $"{Case(c)}, step {++i}";
					string? loan = (string?)s!["loan"];
					DefaultHttpContext ctx = Asked(PcOf((string?)s["from"]), loan == null ? "" : $"?loan={loan}");
					Same(s["expected"], await SeenAsync(ctx, (string)s["ask"]! == "offer" ? x => lending.OfferRouteAsync(x) : lending.OutRouteAsync), step);
					Same(PcOf((string?)s["lending"])?.Id, lending.Now?.To.Id, step + ": lending to");
					if (s["begun"] is JsonNode b) Assert.True((int)b == begun, $"{step}: {begun} loan(s) begun");
					if (s["ended"] is JsonNode n) Assert.True((int)n == ended, $"{step}: {ended} loan(s) ended");
				}
			}
			finally {
				lending.Stop();
			}
		}
	}

	[Fact]
	public async Task Borrower_ItsRouteStepByStep() {
		foreach (JsonNode? c in Section("borrower")) {
			var borrowing = new Borrowing(Route, _ => { }, _ => { });
			borrowing.Start(PcOf((string)c!["lender"]!)!);
			string loan = borrowing.Loan!;
			Assert.Matches("^[0-9a-f-]{36}$", loan); // the loan's name is its outbox's
			try {
				int i = 0;
				foreach (JsonNode? s in c["steps"]!.AsArray()) {
					string step = $"{Case(c)}, step {++i}";
					if ((string)s!["ask"]! == "stop") {
						borrowing.Stop();
						continue;
					}
					string? box = (string?)s["box"];
					DefaultHttpContext ctx = Asked(PcOf((string?)s["from"]), box == null ? "" : $"?box={(box == "$loan" ? loan : box)}");
					Same(s["expected"], await SeenAsync(ctx, borrowing.InRouteAsync), step);
				}
			}
			finally {
				borrowing.Stop();
			}
		}
	}

	[Fact]
	public void Stats_MeansRatesTripsAndResumes() {
		JsonNode stats = V["stats"]!;
		foreach (JsonNode? c in stats["means"]!.AsArray())
			Same(c!["expected"], NpuLoan.MeanOf(c["values"]!.AsArray().Select(v => (double)v!).ToArray()), "means: " + Case(c));
		foreach (JsonNode? c in stats["rates"]!.AsArray())
			Same(c!["expected"], NpuLoan.RateOf(c["at"]!.AsArray().Select(v => (long)v!).ToArray(), (long)c["now"]!), "rates: " + Case(c));
		foreach (JsonNode? c in stats["trips"]!.AsArray())
			Same(c!["expected"], NpuLoan.TripOf((double)c["sentAt"]!, (double)c["readAt"]!, (double)c["ms"]!), "trips: " + Case(c));
		foreach (JsonNode? c in stats["resumes"]!.AsArray())
			Same(c!["expected"], NpuLoan.ResumesOf((int)c["joins"]!), "resumes: " + Case(c));
	}

	// ---------------------------------------------------------------- two PCs, end to end

	[Fact]
	public async Task TwoPcs_TheWorkGoesOverAndItsResultsComeBackAsTheyAreMade() {
		Pc gaming = PcOf("gaming")!, laptop = Laptop;
		var results = Channel.CreateUnbounded<JsonObject>();
		var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		var heard = new List<(JsonObject Line, long? N)>();
		var lending = new Lending(Route, () => null, () => "Snapdragon X Elite", loan => new Echo(loan, heard));
		var borrowing = new Borrowing(Route, l => results.Writer.TryWrite(l), why => ended.TrySetResult(why));
		// Each PC's Manor hands an ask on to the other PC's page: the laptop lends, the gaming PC borrows.
		EstateLink.SendOverride = (request, ct) => {
			string url = request.RequestUri!.OriginalString;
			int q = url.IndexOf('?');
			string[] parts = (q < 0 ? url : url[..q]).Split('/'); // /api/peers/<pc>/agents/<agent>/<route>[/out|/in]
			string pc = parts[3], ask = string.Join('/', parts[6..]);
			Func<HttpContext, Task>? page = pc == laptop.Id && ask == Route ? x => lending.OfferRouteAsync(x)
				: pc == laptop.Id && ask == Route + "/out" ? lending.OutRouteAsync
				: pc == gaming.Id && ask == Route + "/in" ? borrowing.InRouteAsync
				: null;
			return page == null ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
				: PageAsync(page, Asked(pc == laptop.Id ? gaming : laptop, q < 0 ? "" : url[q..]), ct);
		};
		using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		try {
			borrowing.Start(laptop);
			borrowing.Offer(new JsonObject { ["type"] = "call" }, head: true);
			borrowing.Offer(new JsonObject { ["piece"] = 1 });
			borrowing.Offer(new JsonObject { ["piece"] = 2 });
			JsonObject first = await results.Reader.ReadAsync(wait.Token), second = await results.Reader.ReadAsync(wait.Token);
			Assert.Equal(1, (int)first["done"]!);
			Assert.Equal(2, (int)second["done"]!);
			// The lender's work had every numbered line, the head among them.
			lock (heard)
				Assert.Equal(new[] { "{\"type\":\"call\"} 1", "{\"piece\":1} 2", "{\"piece\":2} 3" }, heard.Where(h => h.N != null).Select(h => $"{h.Line.ToJsonString()} {h.N}"));
			Assert.Equal(gaming.Id, lending.Now?.To.Id);
			Assert.True(borrowing.Linked);
			LoanStats there = borrowing.Stats!, here = lending.Stats!;
			Assert.Equal(laptop, there.Pc);
			Assert.NotNull(there.WorkMs);
			Assert.NotNull(there.TripMs);
			Assert.True(there.SentPerSec > 0 && there.ReceivedPerSec > 0, $"lines a second: {there.SentPerSec} sent, {there.ReceivedPerSec} received");
			Assert.Equal(gaming, here.Pc);
			Assert.NotNull(here.WorkMs);
			Assert.Null(here.TripMs);
			// The lender ends the loan: the borrower hears why, in the kit's words.
			lending.Stop("its NPU is needed for a call there");
			Assert.Equal("Laptop stopped lending its NPU: its NPU is needed for a call there.", await ended.Task.WaitAsync(wait.Token));
			Assert.Null(borrowing.Lender);
		}
		finally {
			lending.Stop();
			borrowing.Stop();
		}
	}

	/// <summary>The lender's work in the test: each piece of work answered at once, saying which.</summary>
	sealed class Echo(ILoan loan, List<(JsonObject Line, long? N)> heard) : ILoanWork {
		public void Line(JsonObject line, long? n) {
			lock (heard) heard.Add((line.DeepClone().AsObject(), n));
			if (n is long number && line["piece"] is JsonNode piece) loan.Send(new JsonObject { ["done"] = piece.DeepClone() }, answers: number);
		}

		public void Ended() { }
	}

	/// <summary>Work that does nothing, counting its ends.</summary>
	sealed class Work(Action ended) : ILoanWork {
		public void Line(JsonObject line, long? n) { }
		public void Ended() => ended();
	}

	// ---------------------------------------------------------------- the routes, in-process

	static readonly IServiceProvider Services = new ServiceCollection().AddLogging().BuildServiceProvider();

	/// <summary>A request to this PC's page as the other PC's Manor hands it on: naming the asking PC, when one asks over the link.</summary>
	static DefaultHttpContext Asked(Pc? from, string query) {
		var ctx = new DefaultHttpContext { RequestServices = Services };
		ctx.Request.Method = "GET";
		ctx.Request.QueryString = new QueryString(query);
		if (from != null) {
			ctx.Request.Headers[(string)V["headers"]!["peer"]!] = from.Id;
			ctx.Request.Headers[(string)V["headers"]!["peerName"]!] = Uri.EscapeDataString(from.Name);
		}
		return ctx;
	}

	/// <summary>
	/// A route's answer as the vectors say one: its status and JSON, or <c>{"stream": true}</c> for a 200 of
	/// application/x-ndjson whose first line names its box. The asker goes once that line has come.
	/// </summary>
	static async Task<JsonNode> SeenAsync(DefaultHttpContext ctx, Func<HttpContext, Task> route) {
		var body = new Captured();
		ctx.Response.Body = body;
		using var asker = new CancellationTokenSource();
		ctx.RequestAborted = asker.Token;
		Task answered = route(ctx);
		Task first = await Task.WhenAny(answered, body.FirstLine, Task.Delay(TimeSpan.FromSeconds(10)));
		if (first == answered) {
			await answered;
			return new JsonObject { ["status"] = ctx.Response.StatusCode, ["json"] = JsonNode.Parse(body.Text) };
		}
		Assert.True(first == body.FirstLine, "the route answered nothing in 10 s");
		Assert.Equal(200, ctx.Response.StatusCode);
		Assert.Equal("application/x-ndjson", ctx.Response.ContentType);
		JsonNode? line = JsonNode.Parse(await body.FirstLine);
		Assert.True(line?["box"] is JsonValue b && b.GetValueKind() == JsonValueKind.String, "a stream starts with its box: " + line?.ToJsonString());
		asker.Cancel();
		await answered;
		return new JsonObject { ["stream"] = true };
	}

	/// <summary>A response's body, kept, with its first line as soon as it's written.</summary>
	sealed class Captured : Stream {
		readonly MemoryStream bytes = new();
		readonly TaskCompletionSource<string> first = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<string> FirstLine => first.Task;

		public string Text {
			get { lock (bytes) return Encoding.UTF8.GetString(bytes.ToArray()); }
		}

		public override void Write(ReadOnlySpan<byte> buffer) {
			lock (bytes) {
				bytes.Write(buffer);
				string text = Encoding.UTF8.GetString(bytes.ToArray());
				if (text.IndexOf('\n') is int nl and >= 0) first.TrySetResult(text[..nl]);
			}
		}

		public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) {
			Write(buffer.AsSpan(offset, count));
			return Task.CompletedTask;
		}

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) {
			Write(buffer.Span);
			return ValueTask.CompletedTask;
		}

		public override void Flush() { }
		public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
	}

	/// <summary>A page's route run in-process and answered as Manor passes an answer on: its status once it has written, then its body as it comes.</summary>
	static async Task<HttpResponseMessage> PageAsync(Func<HttpContext, Task> route, DefaultHttpContext ctx, CancellationToken ct) {
		var pipe = new Pipe();
		var asker = CancellationTokenSource.CreateLinkedTokenSource(ct);
		ctx.RequestAborted = asker.Token;
		ctx.Response.Body = pipe.Writer.AsStream();
		_ = Task.Run(async () => {
			try { await route(ctx); }
			finally { await pipe.Writer.CompleteAsync(); }
		});
		ReadResult written = await pipe.Reader.ReadAsync(ct);
		pipe.Reader.AdvanceTo(written.Buffer.Start);
		return new HttpResponseMessage((HttpStatusCode)ctx.Response.StatusCode) { Content = new StreamContent(new GoneWhenDisposed(pipe.Reader.AsStream(), asker)) };
	}

	/// <summary>A response's body that, let go, tells the page the asker went.</summary>
	sealed class GoneWhenDisposed(Stream inner, CancellationTokenSource asker) : Stream {
		public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);

		protected override void Dispose(bool disposing) {
			if (disposing) {
				asker.Cancel();
				inner.Dispose();
			}
			base.Dispose(disposing);
		}

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	// ---------------------------------------------------------------- comparing as JSON

	static JsonElement? Element(JsonNode? n) => n == null ? null : JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();

	static JsonArray Lines(IEnumerable<string> texts) => new(texts.Select(t => JsonNode.Parse(t)).ToArray());

	static JsonObject Json(Heard h) => h switch {
		HeardLine l => new JsonObject {
			["n"] = l.N,
			["line"] = l.Line.DeepClone(),
			["answers"] = l.Answers is { } a ? new JsonObject { ["answers"] = a.Answers, ["ms"] = a.Ms } : null,
		},
		HeardHead head => new JsonObject { ["head"] = head.Head.DeepClone() },
		HeardEnd end => new JsonObject { ["end"] = end.End },
		_ => throw new InvalidOperationException("an unknown line: " + h),
	};

	/// <summary>The same JSON, as the vectors compare it: an object's keys in any order, a number by its value.</summary>
	static void Same(JsonNode? expected, JsonNode? actual, string what) {
		string e = Canon(expected), a = Canon(actual);
		if (e != a) Assert.Fail($"{what}\n  expected: {e}\n  actual:   {a}");
	}

	/// <summary>Words as they are in a failure's message: an apostrophe as itself, not escaped.</summary>
	static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	static string Canon(JsonNode? node) {
		var text = new StringBuilder();
		using JsonDocument doc = JsonDocument.Parse(node?.ToJsonString() ?? "null");
		Write(doc.RootElement);
		return text.ToString();

		void Write(JsonElement e) {
			switch (e.ValueKind) {
				case JsonValueKind.Object:
					text.Append('{');
					bool firstKey = true;
					foreach (JsonProperty p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) {
						if (!firstKey) text.Append(',');
						firstKey = false;
						text.Append(JsonSerializer.Serialize(p.Name, Plain)).Append(':');
						Write(p.Value);
					}
					text.Append('}');
					break;
				case JsonValueKind.Array:
					text.Append('[');
					bool firstItem = true;
					foreach (JsonElement item in e.EnumerateArray()) {
						if (!firstItem) text.Append(',');
						firstItem = false;
						Write(item);
					}
					text.Append(']');
					break;
				case JsonValueKind.Number:
					text.Append(e.GetDouble().ToString("R", CultureInfo.InvariantCulture));
					break;
				case JsonValueKind.String:
					text.Append(JsonSerializer.Serialize(e.GetString(), Plain));
					break;
				default:
					text.Append(e.GetRawText());
					break;
			}
		}
	}
}
