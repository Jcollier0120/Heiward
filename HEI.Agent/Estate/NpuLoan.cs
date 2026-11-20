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
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace HEI.Agent.Estate {
	/// <summary>
	/// How a loan's streams keep time (the kit's NPU loan: beat 2 s, dead 6 s, retry 1 s, grace 20 s, keep 200 lines).
	/// </summary>
	/// <param name="Beat">A beat on a stream that has sent nothing for this long.</param>
	/// <param name="Dead">A stream heard nothing from for this long is dropped and asked again: three beats.</param>
	/// <param name="Retry">How long after a stream drops it is asked again.</param>
	/// <param name="Grace">A loan whose other side has been gone this long ends.</param>
	/// <param name="Keep">Lines each side keeps for a stream asked again.</param>
	sealed record LoanTiming(TimeSpan Beat, TimeSpan Dead, TimeSpan Retry, TimeSpan Grace, int Keep) {
		public static readonly LoanTiming Default = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), 200);
	}

	/// <summary>
	/// Borrowing another PC's NPU over the Estate, Heiward's twin of the Steward's kit's NPU loan (Envoy's npu-loan.ts,
	/// where it began): one PC holds <c>/out</c> open on the PC lending its NPU, and the lender pulls the work from
	/// <c>/in</c> on it. Self-contained: it knows Manor's routes and the link, not what the work is (lines of JSON, the
	/// staff member's own).
	/// <list type="bullet">
	/// <item><c>GET /api/peer/&lt;route&gt;</c>: whether this PC can lend its NPU now, and why not.</item>
	/// <item><c>GET /api/peer/&lt;route&gt;/out?loan=…</c>: the borrower asks the lender for the results. It starts the loan
	/// there (one at a time), and streams the results back as they're made.</item>
	/// <item><c>GET /api/peer/&lt;route&gt;/in?box=…</c>: while a borrower holds /out open, the lender asks it for the work.</item>
	/// </list>
	/// Streams are lines of JSON: <c>{"box":"…"}</c> first (which outbox), <c>{"n":12,"line":{…}}</c> numbered,
	/// <c>{"head":{…}}</c> (the work's details, first on every stream asked afresh), <c>{"end":"why"}</c>, and <c>{}</c>, a
	/// beat. A stream heard nothing from for <see cref="LoanTiming.Dead"/> is dropped and asked again from the line after
	/// the last one read (<c>?box=…&amp;after=12</c>). A borrower gone longer than <see cref="LoanTiming.Grace"/> ends the loan
	/// on the lender, and a lender gone that long ends it on the borrower. Nothing is written to disk on either side.
	/// A result that answers a piece of work names it, with the lender's time on it: <c>{"n":5,"answers":12,"ms":340,"line":{…}}</c>.
	/// </summary>
	static partial class NpuLoan {
		[GeneratedRegex("^[0-9a-f-]{36}$")]
		internal static partial Regex LoanId();

		internal static int AfterOf(IQueryCollection query) => int.TryParse(query["after"], out int after) && after > 0 ? after : 0;

		/// <summary>A line of the stream, as it's sent: one JSON object and a line feed.</summary>
		internal static string Text(JsonObject o) => o.ToJsonString() + "\n";

		/// <summary>Writes a stream's lines to the response as they come, until it ends or the asker goes.</summary>
		internal static async Task WriteAsync(HttpContext ctx, Outbox.Sink sink) {
			ctx.Response.ContentType = "application/x-ndjson";
			ctx.Response.Headers.CacheControl = "no-store";
			try {
				await foreach (string text in sink.ReadAllAsync(ctx.RequestAborted)) {
					await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ctx.RequestAborted);
					await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
				}
			}
			catch (Exception e) when (e is OperationCanceledException or IOException) { /* the asker went */ }
			finally {
				sink.Close();
			}
		}

		/// <summary>An answer in JSON with a status, as the kit's routes give them.</summary>
		internal static IResult Json(int status, object body) => Results.Json(body, statusCode: status);
	}

	/// <summary>Lines for the other PC, numbered, the last few kept for a stream asked again, and the streams reading them.</summary>
	sealed class Outbox {
		/// <summary>Names this outbox: a stream asked again of another (its agent restarted, or a new loan) starts from its first line.</summary>
		public string Id { get; } = Guid.NewGuid().ToString();
		readonly LoanTiming t;
		readonly object gate = new();
		readonly List<(int N, string Text, long At)> kept = new();
		int n;
		string? head;
		readonly List<Sink> streams = new();

		public Outbox(LoanTiming t) => this.t = t;

		public int Open { get { lock (gate) return streams.Count; } }

		/// <summary>A line; <paramref name="isHead"/>: also sent first on every stream asked afresh; <paramref name="answers"/>: the piece of work a result is for, and the lender's time on it.</summary>
		public void Push(JsonObject line, bool isHead = false, (int Answers, long Ms)? answers = null) {
			lock (gate) {
				if (isHead) head = NpuLoan.Text(new JsonObject { ["head"] = line.DeepClone() });
				int number = ++n;
				var o = new JsonObject { ["n"] = number };
				if (answers is var (a, ms)) {
					o["answers"] = a;
					o["ms"] = ms;
				}
				o["line"] = line.DeepClone();
				string text = NpuLoan.Text(o);
				kept.Add((number, text, Stopwatch.GetTimestamp()));
				if (kept.Count > t.Keep) kept.RemoveRange(0, kept.Count - t.Keep);
				foreach (Sink s in streams.ToList()) s.Write(text);
			}
		}

		/// <summary>When line <paramref name="number"/> was handed out (Stopwatch ticks), while it's kept.</summary>
		public long? SentAt(int number) {
			lock (gate) {
				if (kept.Count == 0) return null;
				int i = number - kept[0].N;
				return i >= 0 && i < kept.Count ? kept[i].At : null;
			}
		}

		/// <summary>
		/// A stream from the line after <paramref name="after"/> (of this outbox, <paramref name="box"/>; everything kept,
		/// for another): its name, the head, what's kept since, then lines as they come, and beats while none do. A stream
		/// open already is let go: it's the same PC asking again, its link gone.
		/// </summary>
		public Sink Stream(string? box, int after, Action? closed = null) {
			lock (gate) {
				foreach (Sink old in streams.ToList()) old.End();
				streams.Clear();
				var s = new Sink(this, t, closed);
				streams.Add(s);
				s.Write(NpuLoan.Text(new JsonObject { ["box"] = Id }));
				if (head != null) s.Write(head);
				int from = box == Id ? after : 0;
				foreach (var k in kept)
					if (k.N > from) s.Write(k.Text);
				s.StartBeat();
				return s;
			}
		}

		void Remove(Sink s) {
			lock (gate) streams.Remove(s);
		}

		/// <summary>Ends every stream, saying why first when given.</summary>
		public void End(string? why = null) {
			lock (gate) {
				foreach (Sink s in streams.ToList()) {
					if (why != null) s.Write(NpuLoan.Text(new JsonObject { ["end"] = why }));
					s.End();
				}
				streams.Clear();
			}
		}

		/// <summary>One stream's lines on their way to the other PC; a slow link's isn't let grow past 4 MB: it's dropped.</summary>
		internal sealed class Sink {
			readonly Channel<string> channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
			readonly Outbox box;
			readonly LoanTiming t;
			readonly Action? closed;
			Timer? beat;
			long pendingBytes;
			long lastWrite = Environment.TickCount64;
			int done;

			public Sink(Outbox box, LoanTiming t, Action? closed) {
				this.box = box;
				this.t = t;
				this.closed = closed;
			}

			public ChannelReader<string> Reader => channel.Reader;

			/// <summary>Lines written and not yet read off, in bytes (for a test, and the 4 MB limit).</summary>
			internal long Pending => Interlocked.Read(ref pendingBytes);

			internal void StartBeat() {
				int every = Math.Max(20, (int)(t.Beat.TotalMilliseconds / 2));
				beat = new Timer(_ => {
					if (Environment.TickCount64 - Interlocked.Read(ref lastWrite) >= t.Beat.TotalMilliseconds) Write("{}\n");
				}, null, every, every);
			}

			public void Write(string text) {
				if (Volatile.Read(ref done) != 0) return;
				if (Interlocked.Add(ref pendingBytes, text.Length) > 4 * 1024 * 1024) {
					Close();
					return;
				}
				Interlocked.Exchange(ref lastWrite, Environment.TickCount64);
				channel.Writer.TryWrite(text);
			}

			/// <summary>Read off by the response: counted out of the pending bytes.</summary>
			internal async IAsyncEnumerable<string> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
				await foreach (string text in channel.Reader.ReadAllAsync(ct)) {
					Interlocked.Add(ref pendingBytes, -text.Length);
					yield return text;
				}
			}

			/// <summary>No more lines: the response ends once what's written has gone.</summary>
			public void End() {
				if (Interlocked.Exchange(ref done, 1) != 0) return;
				beat?.Dispose();
				channel.Writer.TryComplete();
				box.Remove(this);
				closed?.Invoke();
			}

			/// <summary>The response is over (ended, or the asker went).</summary>
			public void Close() => End();
		}
	}

	/// <summary>Why a pull is over for good: the other side ended the loan, or refused it (its status and words).</summary>
	sealed record LoanOver(string? End, int Refused = 0, string? Error = null, string? Code = null);

	/// <summary>
	/// Reads the other PC's stream through Manor, and asks again <see cref="LoanTiming.Retry"/> after it drops, from the
	/// line after the last one read, until it's over or stopped. A 410 is over: the other side isn't in this loan any more.
	/// </summary>
	sealed class Puller {
		public bool Linked { get; private set; }
		/// <summary>Since when no stream has been open (Environment.TickCount64), or null while one is.</summary>
		public long? DownSince { get; private set; } = Environment.TickCount64;
		public int Drops { get; private set; }
		public int Joins { get; private set; }
		string? box;
		int last;
		volatile bool stopped;
		CancellationTokenSource? current;
		readonly SemaphoreSlim wake = new(0);
		readonly LoanTiming t;
		readonly string path;
		readonly Action<JsonObject, int, (int Answers, long Ms)?> onLine;
		readonly Action<JsonObject>? onHead;
		readonly Action<LoanOver> onOver;
		readonly Action? onChanged;

		public Puller(LoanTiming t, string path, Action<JsonObject, int, (int Answers, long Ms)?> onLine, Action<LoanOver> onOver,
			Action<JsonObject>? onHead = null, Action? onChanged = null) {
			this.t = t;
			this.path = path;
			this.onLine = onLine;
			this.onOver = onOver;
			this.onHead = onHead;
			this.onChanged = onChanged;
			_ = Task.Run(RunAsync);
		}

		public void Stop() {
			stopped = true;
			try { current?.Cancel(); }
			catch (ObjectDisposedException) { }
			wake.Release();
		}

		async Task RunAsync() {
			while (!stopped) {
				LoanOver? over = null;
				try { over = await OnceAsync(); }
				catch (Exception) { /* dropped: asked again */ }
				bool was = Linked;
				Linked = false;
				DownSince ??= Environment.TickCount64;
				if (stopped) return;
				if (was && over == null) Drops++;
				if (was) onChanged?.Invoke();
				if (over != null) {
					onOver(over);
					return;
				}
				await wake.WaitAsync(t.Retry);
			}
		}

		/// <summary>One stream, read until it drops (null) or is over.</summary>
		async Task<LoanOver?> OnceAsync() {
			using var cts = new CancellationTokenSource();
			current = cts;
			if (stopped) return null;
			cts.CancelAfter(t.Dead);
			string sep = path.Contains('?') ? "&" : "?";
			using HttpResponseMessage r = await EstateLink.AskAsync($"{path}{sep}{(box != null ? $"box={box}&" : "")}after={last}", cts.Token);
			int status = (int)r.StatusCode;
			if (status == 410) return new LoanOver("gone");
			if (status != 200) {
				string? error = null, code = null;
				try {
					using JsonDocument said = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
					error = EstateLink.Str(said.RootElement, "error");
					code = EstateLink.Str(said.RootElement, "code");
				}
				catch (Exception e) when (e is JsonException or InvalidOperationException) { }
				// The other PC answered, and said no: asking again changes nothing. Anything else (its Manor, the link) may pass.
				return status is 403 or 404 or 409 or 503 ? new LoanOver(null, status, error, code) : null;
			}
			await using Stream body = await r.Content.ReadAsStreamAsync(cts.Token);
			var reader = new StreamReader(body, Encoding.UTF8);
			while (true) {
				string? text = await reader.ReadLineAsync(cts.Token);
				if (text == null) return null;
				cts.CancelAfter(t.Dead); // heard from: alive
				if (!Linked) {
					Linked = true;
					Joins++;
					DownSince = null;
					onChanged?.Invoke();
				}
				if (Parse(text) is LoanOver over) return over;
			}
		}

		/// <summary>One line of the other side's stream: an end ends it; anything else is taken in (null).</summary>
		internal LoanOver? Parse(string text) {
			JsonObject? m;
			try { m = JsonNode.Parse(text) as JsonObject; }
			catch (JsonException) { return null; }
			if (m == null) return null;
			if (m["end"] is JsonValue end && end.TryGetValue(out string? why)) return new LoanOver(why);
			if (m["box"] is JsonValue b && b.TryGetValue(out string? name) && name != box) {
				// Another outbox (the other side restarted): its lines are numbered from 1.
				box = name;
				last = 0;
			}
			if (m["head"] is JsonObject h) onHead?.Invoke(h);
			if (m["n"] is JsonValue nv && nv.TryGetValue(out int number) && m["line"] is JsonObject line) {
				if (number <= last) return null; // sent again after a drop: had it
				last = number;
				(int, long)? answers = m["answers"] is JsonValue av && av.TryGetValue(out int a) && m["ms"] is JsonValue mv && mv.TryGetValue(out long ms) && ms >= 0
					? (a, ms) : null;
				onLine(line, number, answers);
			}
			return null;
		}

		/// <summary>Tests: where this puller has read up to.</summary>
		internal (string? Box, int Last) Read => (box, last);
	}

	/// <summary>How a loan is going, for a page: durations only, each measured on one PC's own clock.</summary>
	sealed record LoanStats(Pc Pc, bool Linked, long? WorkMs, long? TripMs, int Drops, int Resumes);

	/// <summary>This PC borrowing another's NPU: one loan at a time.</summary>
	sealed class Borrowing {
		readonly LoanTiming t;
		readonly string route;
		readonly Action<JsonObject> onLine;
		readonly Action<string> onEnd;
		readonly Action? onChange;
		readonly object gate = new();
		Outbox? outbox;
		Puller? puller;
		Timer? gone;
		readonly Queue<long> work = new(), trips = new();

		/// <param name="route">Under /api/peer: "scan" serves /api/peer/scan, /api/peer/scan/out and /api/peer/scan/in.</param>
		/// <param name="onLine">A result from the lender.</param>
		/// <param name="onEnd">The loan is over from the lender's side, in words naming it.</param>
		public Borrowing(string route, Action<JsonObject> onLine, Action<string> onEnd, Action? onChange = null, LoanTiming? timing = null) {
			this.route = route;
			this.onLine = onLine;
			this.onEnd = onEnd;
			this.onChange = onChange;
			t = timing ?? LoanTiming.Default;
		}

		public Pc? Lender { get; private set; }

		/// <summary>Whether the lender's results are coming now (a dropped link is asked again within seconds).</summary>
		public bool Linked => puller?.Linked ?? false;

		/// <summary>The loan's name: its outbox, whose work the lender pulls.</summary>
		public string? Loan => outbox?.Id;

		public LoanStats? Stats {
			get {
				lock (gate) return Lender == null ? null : new LoanStats(Lender, Linked, Mean(work), Mean(trips), puller?.Drops ?? 0, Math.Max(0, (puller?.Joins ?? 0) - 1));
			}
		}

		static long? Mean(Queue<long> q) => q.Count == 0 ? null : (long)q.Average();

		static void Add(Queue<long> q, long v) {
			q.Enqueue(v);
			while (q.Count > 10) q.Dequeue();
		}

		/// <summary>Borrows <paramref name="lender"/>'s NPU: its results come to onLine, and it pulls the work offered from /in.</summary>
		public void Start(Pc lender) {
			Stop();
			lock (gate) {
				Lender = lender;
				var box = new Outbox(t);
				outbox = box;
				work.Clear();
				trips.Clear();
				Puller? p = null;
				p = new Puller(t, $"{EstateLink.PeerPath(lender.Id, route + "/out")}?loan={box.Id}",
					onLine: (l, _, a) => {
						if (a is var (answers, ms)) {
							lock (gate) {
								Add(work, ms);
								if (box.SentAt(answers) is long at) Add(trips, Math.Max(0, (long)Stopwatch.GetElapsedTime(at).TotalMilliseconds - ms));
							}
						}
						onLine(l);
					},
					onOver: o => {
						if (!ReferenceEquals(puller, p)) return;
						Stop();
						onEnd(o.End != null ? $"{lender.Name} stopped: {o.End}." : Refused(lender, o));
						onChange?.Invoke();
					},
					onChanged: () => {
						if (!ReferenceEquals(puller, p)) return;
						WatchGone();
						onChange?.Invoke();
					});
				puller = p;
				WatchGone();
			}
		}

		/// <summary>A lender away longer than the grace is let go: its results stopped, and the borrower told.</summary>
		void WatchGone() {
			lock (gate) {
				gone?.Dispose();
				gone = null;
				Puller? p = puller;
				Pc? lender = Lender;
				if (p == null || lender == null || p.DownSince is not long down) return;
				long wait = Math.Max(0, down + (long)t.Grace.TotalMilliseconds - Environment.TickCount64);
				gone = new Timer(_ => {
					if (!ReferenceEquals(puller, p) || p.Linked) return;
					Stop();
					onEnd($"{lender.Name} stopped answering.");
					onChange?.Invoke();
				}, null, wait, Timeout.Infinite);
			}
		}

		/// <summary>Work for the lender. <paramref name="head"/>: the work's details, sent first again whenever the lender asks afresh.</summary>
		public void Offer(JsonObject line, bool head = false) => outbox?.Push(line, head);

		/// <summary>Ends the loan: the lender's streams close, and its next ask for work is told it's over.</summary>
		public void Stop() {
			lock (gate) {
				gone?.Dispose();
				gone = null;
				puller?.Stop();
				puller = null;
				outbox?.End();
				outbox = null;
				Lender = null;
			}
		}

		/// <summary>GET /api/peer/&lt;route&gt;/in?box=&lt;loan&gt;: the lender pulls the work. Only the PC lending, and only this loan's.</summary>
		public async Task InRouteAsync(HttpContext ctx) {
			Pc? asker = EstateLink.AskerOf(ctx.Request);
			if (asker == null) {
				await NpuLoan.Json(403, new { error = "Only another of your PCs, through Castellan, can ask." }).ExecuteAsync(ctx);
				return;
			}
			Outbox? box = outbox;
			if (box == null || Lender?.Id != asker.Id || ctx.Request.Query["box"] != box.Id) {
				await NpuLoan.Json(410, new { error = "This PC isn't asking that PC for this now." }).ExecuteAsync(ctx);
				return;
			}
			await NpuLoan.WriteAsync(ctx, box.Stream(box.Id, NpuLoan.AfterOf(ctx.Request.Query)));
		}

		static string Refused(Pc lender, LoanOver o) {
			if (o.Code == "busy") return $"{lender.Name} can't now: it's scanning for another PC.";
			if (o.Code == "cant" && o.Error != null) return $"{lender.Name} can't now: {o.Error}.";
			if (o.Refused == 404) return o.Error == "not found" ? $"Heiward on {lender.Name} is too old for this: update Heiward there." : $"Heiward isn't on {lender.Name}.";
			return $"{lender.Name} said no: {o.Error ?? o.Refused.ToString()}.";
		}
	}

	/// <summary>A loan of this PC's NPU, as the staff member's work sees it.</summary>
	interface ILoan {
		Pc From { get; }
		/// <summary>A result for the borrower; <paramref name="answers"/>: the number of the piece of work it's for.</summary>
		void Send(JsonObject line, int? answers = null);
		/// <summary>Ends the loan from this side (the NPU is needed here, or the work failed): the borrower hears why.</summary>
		void End(string why);
	}

	/// <summary>The staff member's work for a loan: each piece of work as it comes (and the head again after a drop), and its end.</summary>
	interface ILoanWork {
		/// <summary><paramref name="n"/>: the piece's number, for a result that answers it; null for the head.</summary>
		void Line(JsonObject line, int? n);
		/// <summary>The loan ended from the borrower's side (or its link was gone too long), or this side stopped: stop the work.</summary>
		void Ended();
	}

	/// <summary>This PC lending its NPU: to one PC at a time.</summary>
	sealed class Lending {
		readonly LoanTiming t;
		readonly string route;
		readonly Func<string?> whyNot;
		readonly Func<string?> npu;
		readonly Func<ILoan, ILoanWork> begin;
		readonly Action? onChange;
		readonly object gate = new();
		Lent? now;

		sealed class Lent : ILoan {
			public required Pc From { get; init; }
			public required string Loan { get; init; }
			public required DateTime Since { get; init; }
			public required Outbox Outbox { get; init; }
			public Puller? Puller;
			public ILoanWork? Work;
			public Timer? Gone;
			public readonly Dictionary<int, long> ReadAt = new();
			public Lending Owner = null!;

			public void Send(JsonObject line, int? answers = null) => Owner.Send(this, line, answers);
			public void End(string why) => Owner.EndFromHere(this, why);
		}

		/// <param name="whyNot">Why this PC can't lend its NPU now, in words for the other PC; null when it can.</param>
		/// <param name="npu">This PC's NPU as a person knows it ("Snapdragon X2 Elite"), for the borrower's page.</param>
		public Lending(string route, Func<string?> whyNot, Func<string?> npu, Func<ILoan, ILoanWork> begin, Action? onChange = null, LoanTiming? timing = null) {
			this.route = route;
			this.whyNot = whyNot;
			this.npu = npu;
			this.begin = begin;
			this.onChange = onChange;
			t = timing ?? LoanTiming.Default;
		}

		/// <summary>The loan now: to which PC, since when, and whether its work is coming.</summary>
		public (Pc To, DateTime Since, bool Linked)? Now {
			get {
				Lent? n = now;
				return n == null ? null : (n.From, n.Since, n.Puller?.Linked ?? false);
			}
		}

		/// <summary>GET /api/peer/&lt;route&gt;: whether this PC can lend its NPU now (and, for Heiward, its build).</summary>
		public object Offer(Pc asker, object? more = null) {
			Lent? n = now;
			string? why = n != null ? (n.From.Id == asker.Id ? null : "it's scanning for another PC") : whyNot();
			var o = new JsonObject();
			if (why != null) {
				o["can"] = false;
				o["why"] = why;
			}
			else {
				o["can"] = true;
				o["npu"] = npu();
			}
			if (more != null && JsonSerializer.SerializeToNode(more, AgentConfig.Json) is JsonObject extra)
				foreach (var (k, v) in extra) o[k] = v?.DeepClone();
			return o;
		}

		/// <summary>GET /api/peer/&lt;route&gt;/out?loan=…: the borrower asks for the results, which starts the loan (or goes on with it).</summary>
		public async Task OutRouteAsync(HttpContext ctx) {
			Pc? asker = EstateLink.AskerOf(ctx.Request);
			if (asker == null) {
				await NpuLoan.Json(403, new { error = "Only another of your PCs, through Castellan, can ask." }).ExecuteAsync(ctx);
				return;
			}
			string loan = ctx.Request.Query["loan"].ToString();
			if (!NpuLoan.LoanId().IsMatch(loan)) {
				await NpuLoan.Json(400, new { error = "Which loan?" }).ExecuteAsync(ctx);
				return;
			}
			Outbox.Sink? stream = null;
			IResult? refused = null;
			lock (gate) {
				if (now != null && now.From.Id != asker.Id)
					refused = NpuLoan.Json(409, new { error = "it's scanning for another PC", code = "busy" });
				else {
					// The same PC asking for a new loan (its Heiward restarted): the old one is over.
					if (now != null && now.Loan != loan) Finish(now);
					if (now == null && (whyNot() ?? Begin(asker, loan)) is string why)
						refused = NpuLoan.Json(503, new { error = why, code = "cant" });
				}
				if (refused == null) stream = Streamed(ctx);
			}
			if (refused != null) {
				await refused.ExecuteAsync(ctx);
				return;
			}
			await NpuLoan.WriteAsync(ctx, stream!);
		}

		/// <summary>The loan's results for the borrower asking now (under the lock): its earlier stream let go.</summary>
		Outbox.Sink Streamed(HttpContext ctx) {
			{
				Lent n = now!;
				n.Gone?.Dispose();
				n.Gone = null;
				return n.Outbox.Stream(ctx.Request.Query["box"], NpuLoan.AfterOf(ctx.Request.Query), () => {
					// The borrower's last stream closed: it has the grace to ask again before the loan ends.
					lock (gate) {
						if (now != n || n.Outbox.Open > 0) return;
						n.Gone?.Dispose();
						n.Gone = new Timer(_ => {
							lock (gate)
								if (now == n && n.Outbox.Open == 0) Finish(n);
						}, null, t.Grace, Timeout.InfiniteTimeSpan);
					}
				});
			}
		}

		/// <summary>Starts a loan: the work begins, and its pieces are pulled from the borrower. Why it couldn't start, or null.</summary>
		string? Begin(Pc from, string loan) {
			var n = new Lent { From = from, Loan = loan, Since = DateTime.UtcNow, Outbox = new Outbox(t), Owner = this };
			now = n;
			try { n.Work = begin(n); }
			catch (Exception e) {
				now = null;
				return e.Message;
			}
			if (now != n) return "it stopped at once";
			ILoanWork work = n.Work;
			n.Puller = new Puller(t, $"{EstateLink.PeerPath(from.Id, route + "/in")}?box={loan}",
				onLine: (l, number, _) => {
					lock (gate) {
						n.ReadAt[number] = Stopwatch.GetTimestamp();
						if (n.ReadAt.Count > t.Keep) n.ReadAt.Remove(n.ReadAt.Keys.Min());
					}
					work.Line(l, number);
				},
				onHead: l => work.Line(l, null),
				// The borrower ended it, or isn't asking this PC any more.
				onOver: _ => {
					lock (gate) Finish(n);
				},
				onChanged: onChange);
			onChange?.Invoke();
			return null;
		}

		void Send(Lent n, JsonObject line, int? answers) {
			lock (gate) {
				if (now != n) return;
				(int, long)? a = answers is int number && n.ReadAt.TryGetValue(number, out long at) ? (number, (long)Stopwatch.GetElapsedTime(at).TotalMilliseconds) : null;
				n.Outbox.Push(line, answers: a);
			}
		}

		void EndFromHere(Lent n, string why) {
			lock (gate) {
				if (now != n) return;
				now = null;
				Close(n, why);
			}
			onChange?.Invoke();
		}

		static void Close(Lent n, string? why = null) {
			n.Gone?.Dispose();
			n.Gone = null;
			n.Puller?.Stop();
			n.Outbox.End(why);
		}

		/// <summary>The loan ends from the borrower's side: no more work comes, its streams close, and the work stops.</summary>
		void Finish(Lent n) {
			if (now != n) return;
			now = null;
			Close(n);
			n.Work?.Ended();
			onChange?.Invoke();
		}

		/// <summary>Heiward is stopping here: a loan ends, the borrower told why.</summary>
		public void Stop(string why = "Heiward stopped there") {
			Lent? n;
			lock (gate) {
				n = now;
				if (n == null) return;
				now = null;
				Close(n, why);
			}
			n.Work?.Ended();
		}
	}
}
