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
using System.Globalization;
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

	/// <summary>What a result says of the piece of work it answers: its number, and the lender's time on it (ms).</summary>
	readonly record struct LoanAnswers(long Answers, double Ms);

	/// <summary>Whether a PC can lend its NPU now, as this PC reads its answer: its NPU's name when it can, why not in words when it can't.</summary>
	sealed record LoanOffer(bool Can, string? Npu, string? Why);

	/// <summary>What a pull does with the other side's answer: reads its stream, is over (gone, or refused), or asks again.</summary>
	enum PullAnswer { Read, Gone, Refused, Retry }

	/// <summary>
	/// Borrowing another PC's NPU over the Estate: Heiward's C# twin of the Steward kit's NPU loan (kit/spec/NPU-LOAN.md,
	/// whose npu-loan-vectors.json it runs unchanged; the kit's node/npu-loan.ts is the original, Envoy's npu-loan.ts
	/// where it began). One PC holds <c>/out</c> open on the PC lending its NPU, and the lender pulls the work from
	/// <c>/in</c> on it. Self-contained: it knows Manor's routes and the link, not what the work is (lines of JSON, the
	/// staff member's own).
	/// <list type="bullet">
	/// <item><c>GET /api/peer/&lt;route&gt;</c>: whether this PC can lend its NPU now, and why not.</item>
	/// <item><c>GET /api/peer/&lt;route&gt;/out?loan=…</c>: the borrower asks the lender for the results. It starts the loan
	/// there (one at a time), and streams the results back as they're made.</item>
	/// <item><c>GET /api/peer/&lt;route&gt;/in?box=…</c>: while a borrower holds /out open, the lender asks it for the work.</item>
	/// </list>
	/// Streams are lines of JSON: <c>{"box":"…"}</c> first (which outbox), <c>{"head":{…}}</c> (the work's details, which a
	/// stream asked again needs first), <c>{"n":12,"line":{…}}</c> numbered, <c>{"end":"why"}</c>, and <c>{}</c>, a beat. A
	/// stream heard nothing from for <see cref="LoanTiming.Dead"/> is dropped and asked again from the line after the last
	/// one read (<c>?box=…&amp;after=12</c>). A borrower gone longer than <see cref="LoanTiming.Grace"/> ends the loan on the
	/// lender, and a lender gone that long ends it on the borrower. Nothing is written to disk on either side. A result
	/// that answers a piece of work names it, with the lender's time on it: <c>{"n":5,"answers":12,"ms":340,"line":{…}}</c>.
	/// The words each side says are the kit's, exactly, with the names put in.
	/// </summary>
	static partial class NpuLoan {
		/// <summary>A partial line longer than this (UTF-16 code units) is let go.</summary>
		public const int LineMax = 1024 * 1024;
		/// <summary>A stream whose reader has this much unread (bytes) is dropped, and asked again from where it got to.</summary>
		public const long UnreadMax = 4 * 1024 * 1024;
		/// <summary>Durations averaged: the last this many.</summary>
		public const int DurationsKept = 10;
		/// <summary>Lines a second are counted over this long (ms).</summary>
		public const long RateWindowMs = 5000;
		/// <summary>Why a lender lending to one PC says no to another.</summary>
		public const string Busy = "its NPU is working for another PC";
		/// <summary>Every route's answer to a request that didn't come over the link.</summary>
		public const string NotOverTheLink = "Only another of your PCs, through Castellan, can ask.";
		/// <summary>The borrower's answer to an ask for work that isn't its lender's, for its loan.</summary>
		public const string NotBorrowing = "This PC isn't borrowing that PC's NPU now.";

		[GeneratedRegex(@"^[0-9a-f-]{36}\z")]
		internal static partial Regex LoanId();

		/// <summary>A route name under /api/peer: lower case letters, digits and dashes.</summary>
		[GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}\z")]
		private static partial Regex RouteName();

		/// <summary>The kit's own GET routes under /api/peer, which every agent answers: no loan's route may take them.</summary>
		static readonly string[] KitPeerRoutes = ["/api/peer/work", "/api/peer/result"];

		/// <summary>The paths a loan named <paramref name="route"/> answers on the page; null for a name that can't be one.</summary>
		internal static (string Offer, string Out, string In)? LoanPaths(string route) {
			if (!RouteName().IsMatch(route)) return null;
			string offer = $"/api/peer/{route}";
			(string, string, string) paths = (offer, offer + "/out", offer + "/in");
			return KitPeerRoutes.Contains(paths.Item1) || KitPeerRoutes.Contains(paths.Item2) || KitPeerRoutes.Contains(paths.Item3) ? null : paths;
		}

		/// <summary>A loan's route name, checked: one that can't name a loan's routes throws, as the kit's loanRoutes() does.</summary>
		internal static string Route(string route) => LoanPaths(route) != null ? route
			: throw new ArgumentException($"\"{route}\" can't name a loan's routes: a plain name under /api/peer (lower case, digits and dashes) that isn't one of the kit's ({string.Join(", ", KitPeerRoutes)})", nameof(route));

		/// <summary>Where on this PC's Manor another PC's staff member's route is: <c>/api/peers/&lt;pc&gt;/agents/&lt;agent&gt;/&lt;route&gt;&lt;rest&gt;</c>.</summary>
		internal static string PeerPath(string agent, string route, string pc, string rest) => $"/api/peers/{Uri.EscapeDataString(pc)}/agents/{agent}/{route}{rest}";

		/// <summary>The borrower's ask for a loan's results, of the lender (before <see cref="PullPath"/> says where to start).</summary>
		internal static string OutPath(string agent, string route, string lender, string loan) => $"{PeerPath(agent, route, lender, "/out")}?loan={loan}";

		/// <summary>The lender's ask for a loan's work, of the borrower: by the loan's name, which is the borrower's outbox.</summary>
		internal static string InPath(string agent, string route, string borrower, string loan) => $"{PeerPath(agent, route, borrower, "/in")}?box={loan}";

		/// <summary>
		/// A pull of <paramref name="path"/>, from the line after <paramref name="after"/> of the other side's outbox
		/// <paramref name="box"/> (null before one was read). The lender's ask for work names its box twice once it has read
		/// one (<c>?box=&lt;loan&gt;&amp;box=&lt;loan&gt;&amp;after=5</c>): the borrower reads the first.
		/// </summary>
		internal static string PullPath(string path, string? box, long after) =>
			$"{path}{(path.Contains('?') ? "&" : "?")}{(string.IsNullOrEmpty(box) ? "" : $"box={box}&")}after={after.ToString(CultureInfo.InvariantCulture)}";

		/// <summary>A query's first value of <paramref name="key"/>, as a URL's search parameters give it; null when there's none.</summary>
		internal static string? First(IQueryCollection query, string key) => query.TryGetValue(key, out var v) && v.Count > 0 ? v[0] : null;

		/// <summary>Where a stream asked starts after: <c>after</c> as a number, 0 when it's none, not a number, or below 0.</summary>
		internal static double AfterOf(IQueryCollection query) {
			string? text = First(query, "after");
			if (text == null || text.AsSpan().Trim().IsEmpty) return 0;
			return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double after) && !double.IsNaN(after) ? Math.Max(0, after) : 0;
		}

		/// <summary>
		/// What a pull does with the other side's answer: read its stream (a 2xx with a body), stop (gone: a 410, the other
		/// side isn't in this loan; refused: it answered, and said no, so asking again changes nothing), or ask again after
		/// <see cref="LoanTiming.Retry"/> (anything else: its Manor, the link, may pass).
		/// </summary>
		internal static PullAnswer PullAnswerOf(int status, bool hasBody) {
			if (status == 410) return PullAnswer.Gone;
			if (status is >= 200 and <= 299 && hasBody) return PullAnswer.Read;
			if (status is 403 or 404 or 409 or 503) return PullAnswer.Refused;
			return PullAnswer.Retry;
		}

		/// <summary>The words a sentence ends with: <paramref name="s"/> and a full stop, unless it ends in one already.</summary>
		internal static string Sentence(string s) => s.Length > 0 && s[^1] is '.' or '!' or '?' ? s : s + ".";

		/// <summary>
		/// What a PC's answer to <c>GET /api/peer/&lt;route&gt;</c> says, through this PC's Manor (<paramref name="status"/>
		/// null: no answer at all), in <paramref name="who"/>'s words (the staff member's name, "Heiward").
		/// </summary>
		internal static LoanOffer OfferFrom(string who, Pc pc, int? status, JsonElement? said) {
			if (status is not int code) return new LoanOffer(false, null, $"{pc.Name} didn't answer.");
			JsonElement? s = said is { ValueKind: JsonValueKind.Object } o ? o : null;
			if (code == 404) {
				string error = s is { } e ? EstateLink.Str(e, "error") ?? "" : "";
				// The other PC's Manor has no such staff member; its staff member's page has no such route (one from before the loan).
				if (error.StartsWith("Nobody called", StringComparison.Ordinal)) return new LoanOffer(false, null, $"{who} isn't on {pc.Name}.");
				if (error == "not found") return new LoanOffer(false, null, $"{who} on {pc.Name} is too old to lend its NPU: it updates itself soon.");
				return new LoanOffer(false, null, $"{pc.Name} isn't on the Estate now.");
			}
			// Manor here couldn't reach it over the link.
			if (code == 502) return new LoanOffer(false, null, $"{pc.Name} is asleep or off.");
			if (code is < 200 or > 299 || s is not { } answer) return new LoanOffer(false, null, $"{pc.Name} didn't answer.");
			if (answer.TryGetProperty("can", out JsonElement can) && can.ValueKind == JsonValueKind.True) {
				string? npu = EstateLink.Str(answer, "npu");
				return new LoanOffer(true, npu is { Length: > 80 } ? npu[..80] : npu, null);
			}
			return new LoanOffer(false, null, $"{pc.Name} can't now: {EstateLink.Str(answer, "why") ?? "it said no"}.");
		}

		/// <summary>The words for a loan the lender ended or refused, naming it, in <paramref name="who"/>'s words.</summary>
		internal static string OverWords(string who, Pc lender, LoanOver o) {
			if (o.End != null) return $"{lender.Name} stopped lending its NPU: {Sentence(o.End)}";
			if (o.Code == "busy") return $"{lender.Name} can't now: {Busy}.";
			if (o.Code == "cant" && !string.IsNullOrEmpty(o.Error)) return $"{lender.Name} can't now: {Sentence(o.Error)}";
			if (o.Refused == 404) return o.Error == "not found" ? $"{who} on {lender.Name} is too old to lend its NPU: it updates itself soon." : $"{who} isn't on {lender.Name}.";
			return $"{lender.Name} said no: {Sentence(o.Error ?? o.Refused.ToString(CultureInfo.InvariantCulture))}";
		}

		/// <summary>The last <see cref="DurationsKept"/> durations' mean, in whole ms (halves up); null for none.</summary>
		internal static long? MeanOf(IReadOnlyList<double> values) {
			if (values.Count == 0) return null;
			double sum = 0;
			int from = Math.Max(0, values.Count - DurationsKept);
			for (int i = from; i < values.Count; i++) sum += values[i];
			return (long)Math.Round(sum / (values.Count - from), MidpointRounding.AwayFromZero);
		}

		/// <summary>Lines a second at <paramref name="now"/>, from when each passed (ms): those in the last <see cref="RateWindowMs"/>, to a tenth (halves up).</summary>
		internal static double RateOf(IEnumerable<long> at, long now) {
			int n = at.Count(t => now - t <= RateWindowMs);
			return Math.Round(n / (RateWindowMs / 1000.0) * 10, MidpointRounding.AwayFromZero) / 10;
		}

		/// <summary>The way there and back: from handing a piece out to reading its result, on the borrower's clock, less the lender's time; never below 0.</summary>
		internal static double TripOf(double sentAt, double readAt, double lenderMs) => Math.Max(0, readAt - sentAt - lenderMs);

		/// <summary>Times a stream was joined again after a drop: every stream linked but the first.</summary>
		internal static int ResumesOf(int joins) => Math.Max(0, joins - 1);

		/// <summary>This PC's clock for durations, in ms: monotonic, finer than the tick count.</summary>
		internal static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

		/// <summary>A line of the stream, as it's sent: one JSON object and a line feed.</summary>
		internal static string Text(JsonObject o) => o.ToJsonString() + "\n";

		/// <summary>Writes a stream's lines to the response as they come, until it ends or the asker goes. Its first bytes go at once.</summary>
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

	/// <summary>
	/// Lines for the other PC, numbered, the last few kept for a stream asked again, and the streams reading them.
	/// <see cref="Id"/> names it: a new random one unless given (the vectors give theirs).
	/// </summary>
	sealed class Outbox {
		/// <summary>Names this outbox: a stream asked again of another (its agent restarted, or a new loan) starts from its first line.</summary>
		public string Id { get; }
		readonly LoanTiming t;
		readonly object gate = new();
		/// <summary>Each line kept, and when it was handed out (<see cref="NpuLoan.NowMs"/>), to time its result.</summary>
		readonly List<(long N, string Text, double At)> kept = new();
		long n;
		string? head;
		readonly List<Sink> streams = new();

		public Outbox(LoanTiming t, string? id = null) {
			this.t = t;
			Id = id ?? Guid.NewGuid().ToString();
		}

		public int Open { get { lock (gate) return streams.Count; } }

		/// <summary>
		/// A line; <paramref name="isHead"/>: the work's details, also sent first on every stream asked afresh (a head is
		/// also the next numbered line, and a new one takes the old one's place); <paramref name="answers"/>: the piece of
		/// work a result is for, and the lender's time on it.
		/// </summary>
		public void Push(JsonObject line, bool isHead = false, LoanAnswers? answers = null) {
			lock (gate) {
				if (isHead) head = NpuLoan.Text(new JsonObject { ["head"] = line.DeepClone() });
				long number = ++n;
				var o = new JsonObject { ["n"] = number };
				if (answers is { } a) {
					o["answers"] = a.Answers;
					o["ms"] = a.Ms;
				}
				o["line"] = line.DeepClone();
				string text = NpuLoan.Text(o);
				kept.Add((number, text, NpuLoan.NowMs()));
				if (kept.Count > t.Keep) kept.RemoveRange(0, kept.Count - t.Keep);
				foreach (Sink s in streams.ToList()) s.Write(text);
			}
		}

		/// <summary>When line <paramref name="number"/> was handed out (<see cref="NpuLoan.NowMs"/>), while it's kept.</summary>
		public double? SentAt(long number) {
			lock (gate) {
				if (kept.Count == 0) return null;
				long i = number - kept[0].N;
				return i >= 0 && i < kept.Count ? kept[(int)i].At : null;
			}
		}

		/// <summary>
		/// What a stream asked from the line after <paramref name="after"/> of outbox <paramref name="box"/> starts with, line
		/// by line: this outbox's name, the head, and what's kept since (everything kept, when <paramref name="box"/> is
		/// another outbox or none).
		/// </summary>
		public List<string> Opening(string? box, double after) {
			lock (gate) {
				double from = box == Id ? after : 0;
				var lines = new List<string> { NpuLoan.Text(new JsonObject { ["box"] = Id }) };
				if (head != null) lines.Add(head);
				foreach (var k in kept)
					if (k.N > from) lines.Add(k.Text);
				return lines;
			}
		}

		/// <summary>
		/// A stream from the line after <paramref name="after"/> (of this outbox, <paramref name="box"/>; everything kept,
		/// for another): its opening, then lines as they come, and beats while none do. A stream open already is let go:
		/// it's the same PC asking again, its link gone.
		/// </summary>
		public Sink Stream(string? box, double after, Action? closed = null) {
			lock (gate) {
				List<Sink> old = streams.ToList();
				streams.Clear();
				var s = new Sink(this, t, closed);
				// The new stream is open before the old ones close: they don't leave the outbox looking unread.
				streams.Add(s);
				foreach (Sink o in old) o.End();
				foreach (string text in Opening(box, after)) s.Write(text);
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
					if (!string.IsNullOrEmpty(why)) s.Write(NpuLoan.Text(new JsonObject { ["end"] = why }));
					s.End();
				}
				streams.Clear();
			}
		}

		/// <summary>One stream's lines on their way to the other PC; a slow link's isn't let grow past 4 MiB unread: it's dropped.</summary>
		internal sealed class Sink {
			readonly Channel<string> channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
			readonly Outbox box;
			readonly LoanTiming t;
			readonly Action? closed;
			Timer? beat;
			long pendingBytes;
			long lastWrite = Environment.TickCount64;
			int done;
			volatile bool dropped;

			public Sink(Outbox box, LoanTiming t, Action? closed) {
				this.box = box;
				this.t = t;
				this.closed = closed;
			}

			public ChannelReader<string> Reader => channel.Reader;

			/// <summary>Lines written and not yet read off, in bytes (for a test, and the 4 MiB limit).</summary>
			internal long Pending => Interlocked.Read(ref pendingBytes);

			internal void StartBeat() {
				int every = Math.Max(20, (int)Math.Round(t.Beat.TotalMilliseconds / 2));
				beat = new Timer(_ => {
					if (Environment.TickCount64 - Interlocked.Read(ref lastWrite) >= t.Beat.TotalMilliseconds) Write("{}\n");
				}, null, every, every);
			}

			public void Write(string text) {
				if (Volatile.Read(ref done) != 0) return;
				if (Interlocked.Read(ref pendingBytes) > NpuLoan.UnreadMax) {
					// Dropped, not drained: the other side asks again from where it got to.
					dropped = true;
					End();
					return;
				}
				Interlocked.Add(ref pendingBytes, Encoding.UTF8.GetByteCount(text));
				Interlocked.Exchange(ref lastWrite, Environment.TickCount64);
				channel.Writer.TryWrite(text);
			}

			/// <summary>Read off by the response: counted out of the pending bytes. A dropped stream stops at once.</summary>
			internal async IAsyncEnumerable<string> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
				await foreach (string text in channel.Reader.ReadAllAsync(ct)) {
					if (dropped) yield break;
					Interlocked.Add(ref pendingBytes, -Encoding.UTF8.GetByteCount(text));
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

	/// <summary>Why a pull is over for good: the other side ended the loan (<paramref name="End"/>), or refused it (its status, and its error and code).</summary>
	sealed record LoanOver(string? End, int Refused = 0, string? Error = null, string? Code = null);

	/// <summary>What one of a stream's lines says, for its reader: a numbered line (with what it answers), the head, or the end.</summary>
	abstract record Heard;

	sealed record HeardLine(long N, JsonObject Line, LoanAnswers? Answers) : Heard;

	sealed record HeardHead(JsonObject Head) : Heard;

	sealed record HeardEnd(string End) : Heard;

	/// <summary>
	/// Reads the other PC's stream as its bytes come, across its drops: the outbox it's from (<see cref="Box"/>) and the
	/// last numbered line read (<see cref="Last"/>), which a stream asked again starts after. A line sent again after a
	/// drop is had already, and passed over; another outbox (the other side restarted, or a new loan) numbers from 1.
	/// Beats, and lines that aren't a JSON object, are passed over.
	/// </summary>
	sealed class LineReader {
		public string? Box { get; set; }
		public long Last { get; set; }
		string rest = "";
		Decoder decoder = NewDecoder();
		bool fresh = true;

		static Decoder NewDecoder() => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();

		/// <summary>Tests: the partial line held for the next bytes, in UTF-16 code units.</summary>
		internal int Partial => rest.Length;

		/// <summary>A new stream: what's left of the last one's partial line, and of its last character, is let go.</summary>
		public void Restart() {
			rest = "";
			decoder = NewDecoder();
			fresh = true;
		}

		/// <summary>The stream's next bytes, decoded as UTF-8 across reads (a character split between two reads is read whole).</summary>
		public List<Heard> Feed(ReadOnlySpan<byte> bytes) {
			char[] chars = new char[decoder.GetCharCount(bytes, flush: false)];
			int count = decoder.GetChars(bytes, chars, flush: false);
			var text = new string(chars, 0, count);
			if (fresh && text.Length > 0) {
				// As a TextDecoder does: a byte order mark at the start of a stream is no part of it.
				if (text[0] == (char)0xFEFF) text = text[1..];
				fresh = false;
			}
			return Feed(text);
		}

		/// <summary>The lines in <paramref name="text"/>, the stream's next characters, as heard; nothing after an end.</summary>
		public List<Heard> Feed(string text) {
			string[] parts = (rest + text).Split('\n');
			rest = parts[^1];
			if (rest.Length > NpuLoan.LineMax) rest = "";
			var heard = new List<Heard>();
			for (int i = 0; i < parts.Length - 1; i++) {
				if (Read(parts[i]) is not { } m) continue;
				if (m.End != null) {
					heard.Add(new HeardEnd(m.End));
					rest = "";
					return heard;
				}
				if (m.Box != null && m.Box != Box) {
					// Another outbox (the other side restarted): its lines are numbered from 1.
					Box = m.Box;
					Last = 0;
				}
				if (m.Head != null) heard.Add(new HeardHead(m.Head));
				if (m.N is long n && m.Line != null) {
					if (n <= Last) continue; // sent again after a drop: had it
					Last = n;
					heard.Add(new HeardLine(n, m.Line, m.Answers));
				}
			}
			return heard;
		}

		/// <summary>One line's parts that a reader takes, or null when it isn't a JSON object. Keys it doesn't know are for others.</summary>
		static (string? End, string? Box, JsonObject? Head, long? N, JsonObject? Line, LoanAnswers? Answers)? Read(string text) {
			try {
				if (JsonNode.Parse(text) is not JsonObject m) return null;
				string? end = StringOf(m["end"]);
				string? box = StringOf(m["box"]);
				JsonObject? head = m["head"] as JsonObject;
				long? n = WholeOf(m["n"]) is long w && w > 0 ? w : null;
				JsonObject? line = m["line"] as JsonObject;
				LoanAnswers? answers = WholeOf(m["answers"]) is long a && m["ms"] is JsonValue mv && mv.GetValueKind() == JsonValueKind.Number
					&& mv.TryGetValue(out double ms) && double.IsFinite(ms) && ms >= 0 ? new LoanAnswers(a, ms) : null;
				// Taken out of the line: the staff member's own, with no parent.
				if (head != null) m.Remove("head");
				if (line != null) m.Remove("line");
				return (end, box, head, n, line, answers);
			}
			catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException) {
				return null;
			}
		}

		static string? StringOf(JsonNode? v) => v is JsonValue s && s.GetValueKind() == JsonValueKind.String ? s.GetValue<string>() : null;

		/// <summary>A whole number, as JavaScript's safe integers: <c>12</c>, and <c>12.0</c>, not 1.5, a string or 2^53.</summary>
		static long? WholeOf(JsonNode? v) =>
			v is JsonValue j && j.GetValueKind() == JsonValueKind.Number && j.TryGetValue(out double d) && double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) <= 9007199254740991
				? (long)d : null;
	}

	/// <summary>
	/// Reads the other PC's stream through Manor, and asks again <see cref="LoanTiming.Retry"/> after it drops, from the
	/// line after the last one read, until it's over or stopped. A 410 is over: the other side isn't in this loan any more.
	/// </summary>
	sealed class Puller {
		public bool Linked { get; private set; }
		/// <summary>Since when no stream has been open (Environment.TickCount64), or null while one is.</summary>
		public long? DownSince { get; private set; } = Environment.TickCount64;
		/// <summary>Streams that dropped once linked, and streams linked (the first, and each after a drop).</summary>
		public int Drops { get; private set; }
		public int Joins { get; private set; }
		/// <summary>The other side's outbox, and its last line read.</summary>
		readonly LineReader reader = new();
		volatile bool stopped;
		CancellationTokenSource? current;
		readonly SemaphoreSlim wake = new(0);
		readonly LoanTiming t;
		readonly string path;
		readonly Action<JsonObject, long, LoanAnswers?> onLine;
		readonly Action<JsonObject>? onHead;
		readonly Action<LoanOver> onOver;
		readonly Action? onChanged;

		public Puller(LoanTiming t, string path, Action<JsonObject, long, LoanAnswers?> onLine, Action<LoanOver> onOver,
			Action<JsonObject>? onHead = null, Action? onChanged = null) {
			this.t = t;
			this.path = path;
			this.onLine = onLine;
			this.onOver = onOver;
			this.onHead = onHead;
			this.onChanged = onChanged;
		}

		/// <summary>Starts pulling: once whoever keeps this puller has it, so its callbacks know it as theirs.</summary>
		public Puller Start() {
			_ = Task.Run(RunAsync);
			return this;
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
			using HttpResponseMessage r = await EstateLink.AskAsync(NpuLoan.PullPath(path, reader.Box, reader.Last), cts.Token);
			int status = (int)r.StatusCode;
			PullAnswer answer = NpuLoan.PullAnswerOf(status, status is not (204 or 205) && r.Content.Headers.ContentLength != 0);
			if (answer == PullAnswer.Gone) return new LoanOver("gone");
			if (answer != PullAnswer.Read) {
				// The other PC answered, and said no: asking again changes nothing. Anything else (its Manor, the link) may pass.
				if (answer != PullAnswer.Refused) return null;
				string? error = null, code = null;
				try {
					using JsonDocument said = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
					if (said.RootElement.ValueKind == JsonValueKind.Object) {
						error = EstateLink.Str(said.RootElement, "error");
						code = EstateLink.Str(said.RootElement, "code");
					}
				}
				catch (Exception e) when (e is JsonException or InvalidOperationException or HttpRequestException or OperationCanceledException or IOException) { }
				return new LoanOver(null, status, error, code);
			}
			await using Stream body = await r.Content.ReadAsStreamAsync(cts.Token);
			reader.Restart();
			byte[] buffer = new byte[64 * 1024];
			while (true) {
				int got = await body.ReadAsync(buffer, cts.Token);
				if (got == 0) return null;
				cts.CancelAfter(t.Dead); // heard from: alive
				if (!Linked) {
					Linked = true;
					Joins++;
					DownSince = null;
					onChanged?.Invoke();
				}
				foreach (Heard h in reader.Feed(buffer.AsSpan(0, got))) {
					switch (h) {
						case HeardEnd end:
							return new LoanOver(end.End);
						case HeardHead head:
							onHead?.Invoke(head.Head);
							break;
						case HeardLine line:
							onLine(line.Line, line.N, line.Answers);
							break;
					}
				}
			}
		}
	}

	/// <summary>How a loan is going, for a page: durations only, each measured on one PC's own clock.</summary>
	/// <param name="Pc">The other PC: the lender, or the borrower.</param>
	/// <param name="Linked">Whether the other PC's lines are coming now.</param>
	/// <param name="WorkMs">The lender's own time per piece of work, the last 10 averaged (ms); null before one.</param>
	/// <param name="TripMs">The borrower's: from handing a piece out to reading its result, less the lender's time (ms); null on the lender, and before one.</param>
	/// <param name="SentPerSec">Lines a second to the other PC, over the last 5 s.</param>
	/// <param name="ReceivedPerSec">Lines a second from it.</param>
	/// <param name="Drops">Streams that dropped once linked.</param>
	/// <param name="Resumes">Streams linked after the first.</param>
	sealed record LoanStats(Pc Pc, bool Linked, long? WorkMs, long? TripMs, double SentPerSec, double ReceivedPerSec, int Drops, int Resumes);

	/// <summary>The last few durations, averaged (ms).</summary>
	sealed class Durations {
		readonly List<double> last = new();

		public void Add(double ms) {
			lock (last) {
				last.Add(ms);
				if (last.Count > NpuLoan.DurationsKept) last.RemoveAt(0);
			}
		}

		public long? Mean {
			get { lock (last) return NpuLoan.MeanOf(last); }
		}
	}

	/// <summary>Lines counted as they pass: how many a second, over the last 5 s.</summary>
	sealed class PerSecond {
		readonly Queue<long> at = new();

		public void Tick() {
			lock (at) {
				at.Enqueue(Environment.TickCount64);
				Trim();
			}
		}

		public double Rate {
			get {
				lock (at) {
					Trim();
					return NpuLoan.RateOf(at, Environment.TickCount64);
				}
			}
		}

		void Trim() {
			long now = Environment.TickCount64;
			while (at.Count > 0 && now - at.Peek() > NpuLoan.RateWindowMs) at.Dequeue();
		}
	}

	/// <summary>A loan's numbers, kept by each side.</summary>
	sealed class Tally {
		public readonly Durations Work = new(), Trip = new();
		public readonly PerSecond Sent = new(), Received = new();

		public LoanStats Stats(Pc pc, Puller? puller) =>
			new(pc, puller?.Linked ?? false, Work.Mean, Trip.Mean, Sent.Rate, Received.Rate, puller?.Drops ?? 0, NpuLoan.ResumesOf(puller?.Joins ?? 0));
	}

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
		Tally tally = new();

		/// <param name="route">Under /api/peer: "scan" serves /api/peer/scan, /api/peer/scan/out and /api/peer/scan/in. One that can't name a loan's routes throws.</param>
		/// <param name="onLine">A result from the lender.</param>
		/// <param name="onEnd">The loan is over from the lender's side: it ended it, refused it, or was gone too long. In words, naming it.</param>
		public Borrowing(string route, Action<JsonObject> onLine, Action<string> onEnd, Action? onChange = null, LoanTiming? timing = null) {
			this.route = NpuLoan.Route(route);
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

		/// <summary>How the loan is going: the lender's time per piece of work, the way there and back, lines each way, drops.</summary>
		public LoanStats? Stats {
			get {
				lock (gate) return Lender == null ? null : tally.Stats(Lender, puller);
			}
		}

		/// <summary>Borrows <paramref name="lender"/>'s NPU: its results come to onLine, and it pulls the work offered from /in.</summary>
		public void Start(Pc lender) {
			Stop();
			lock (gate) {
				Lender = lender;
				var box = new Outbox(t);
				outbox = box;
				var counts = new Tally();
				tally = counts;
				Puller? p = null;
				// The loan is named by this outbox: the lender pulls its work by that name, and a new loan is a new name.
				p = new Puller(t, NpuLoan.OutPath(EstateLink.Agent, route, lender.Id, box.Id),
					onLine: (l, _, a) => {
						counts.Received.Tick();
						if (a is { } answers) {
							counts.Work.Add(answers.Ms);
							// From handing the piece out to reading its result, both on this PC's clock, less the lender's own time.
							if (box.SentAt(answers.Answers) is double at) counts.Trip.Add(NpuLoan.TripOf(at, NpuLoan.NowMs(), answers.Ms));
						}
						onLine(l);
					},
					onOver: o => {
						if (!ReferenceEquals(puller, p)) return;
						Stop();
						onEnd(NpuLoan.OverWords(EstateLink.Who, lender, o));
						onChange?.Invoke();
					},
					onChanged: () => {
						if (!ReferenceEquals(puller, p)) return;
						WatchGone();
						onChange?.Invoke();
					});
				puller = p;
				p.Start();
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
		public void Offer(JsonObject line, bool head = false) {
			Outbox? box;
			Tally counts;
			lock (gate) {
				box = outbox;
				counts = tally;
			}
			if (box == null) return;
			box.Push(line, head);
			counts.Sent.Tick();
		}

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
				await NpuLoan.Json(403, new { error = NpuLoan.NotOverTheLink }).ExecuteAsync(ctx);
				return;
			}
			Outbox? box = outbox;
			// A lender asking again names the loan twice (box=<loan>&box=<loan>): the first is the loan's.
			if (box == null || Lender?.Id != asker.Id || NpuLoan.First(ctx.Request.Query, "box") != box.Id) {
				await NpuLoan.Json(410, new { error = NpuLoan.NotBorrowing }).ExecuteAsync(ctx);
				return;
			}
			await NpuLoan.WriteAsync(ctx, box.Stream(box.Id, NpuLoan.AfterOf(ctx.Request.Query)));
		}
	}

	/// <summary>A loan of this PC's NPU, as the staff member's work sees it.</summary>
	interface ILoan {
		Pc From { get; }
		/// <summary>A result for the borrower; <paramref name="answers"/>: the number of the piece of work it's for (<see cref="ILoanWork.Line"/>'s), so both sides can time it.</summary>
		void Send(JsonObject line, long? answers = null);
		/// <summary>Ends the loan from this side (the NPU is needed here, or the work failed): the borrower hears why.</summary>
		void End(string why);
	}

	/// <summary>The staff member's work for a loan: each piece of work as it comes (and the head again after a drop), and its end.</summary>
	interface ILoanWork {
		/// <summary><paramref name="n"/>: the piece's number, for a result that answers it; null for the head.</summary>
		void Line(JsonObject line, long? n);
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
			/// <summary>The borrower's name for the loan: its outbox, whose work this side pulls.</summary>
			public required string Loan { get; init; }
			public required DateTime Since { get; init; }
			public required Outbox Outbox { get; init; }
			public Puller? Puller;
			public ILoanWork? Work;
			public Timer? Gone;
			public readonly Tally Tally = new();
			/// <summary>When each piece of work lately was read (<see cref="NpuLoan.NowMs"/>), by its number, to time its result.</summary>
			public readonly Dictionary<long, double> ReadAt = new();
			public Lending Owner = null!;

			public void Send(JsonObject line, long? answers = null) => Owner.Send(this, line, answers);
			public void End(string why) => Owner.EndFromHere(this, why);
		}

		/// <param name="route">Under /api/peer: "scan" serves /api/peer/scan and /api/peer/scan/out. One that can't name a loan's routes throws.</param>
		/// <param name="whyNot">Why this PC can't lend its NPU now, in words for the other PC, with no full stop; null when it can.</param>
		/// <param name="npu">This PC's NPU as a person knows it ("Snapdragon X2 Elite"), for the borrower's page; null when not known.</param>
		/// <param name="begin">Starts the work for a loan; a throw is why it couldn't, in its message, said to the borrower.</param>
		public Lending(string route, Func<string?> whyNot, Func<string?> npu, Func<ILoan, ILoanWork> begin, Action? onChange = null, LoanTiming? timing = null) {
			this.route = NpuLoan.Route(route);
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

		/// <summary>How the loan is going: this PC's time per piece of work, lines each way, drops.</summary>
		public LoanStats? Stats {
			get {
				Lent? n = now;
				return n?.Tally.Stats(n.From, n.Puller);
			}
		}

		/// <summary>
		/// The answer to <c>GET /api/peer/&lt;route&gt;</c> from <paramref name="asker"/>: whether this PC can lend its NPU now,
		/// with <paramref name="more"/>'s keys beside (Heiward's <c>v</c> and <c>build</c>).
		/// </summary>
		public object Offer(Pc asker, object? more = null) {
			Lent? n = now;
			string? why = n != null ? (n.From.Id == asker.Id ? null : NpuLoan.Busy) : whyNot();
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

		/// <summary>GET /api/peer/&lt;route&gt;: whether this PC can lend its NPU now (<see cref="Offer"/>), only over the link.</summary>
		public async Task OfferRouteAsync(HttpContext ctx, object? more = null) {
			if (EstateLink.AskerOf(ctx.Request) is not { } asker) {
				await NpuLoan.Json(403, new { error = NpuLoan.NotOverTheLink }).ExecuteAsync(ctx);
				return;
			}
			await NpuLoan.Json(200, Offer(asker, more)).ExecuteAsync(ctx);
		}

		/// <summary>GET /api/peer/&lt;route&gt;/out?loan=…: the borrower asks for the results, which starts the loan (or goes on with it, after a drop).</summary>
		public async Task OutRouteAsync(HttpContext ctx) {
			Pc? asker = EstateLink.AskerOf(ctx.Request);
			if (asker == null) {
				await NpuLoan.Json(403, new { error = NpuLoan.NotOverTheLink }).ExecuteAsync(ctx);
				return;
			}
			string loan = NpuLoan.First(ctx.Request.Query, "loan") ?? "";
			if (!NpuLoan.LoanId().IsMatch(loan)) {
				await NpuLoan.Json(400, new { error = "Which loan?" }).ExecuteAsync(ctx);
				return;
			}
			Outbox.Sink? stream = null;
			IResult? refused = null;
			lock (gate) {
				if (now != null && now.From.Id != asker.Id)
					refused = NpuLoan.Json(409, new { error = NpuLoan.Busy, code = "busy" });
				else {
					// The same PC asking for a new loan (its staff member restarted): the old one is over.
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
			Lent n = now!;
			n.Gone?.Dispose();
			n.Gone = null;
			return n.Outbox.Stream(NpuLoan.First(ctx.Request.Query, "box"), NpuLoan.AfterOf(ctx.Request.Query), () => {
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
			n.Puller = new Puller(t, NpuLoan.InPath(EstateLink.Agent, route, from.Id, loan),
				onLine: (l, number, _) => {
					lock (gate) {
						n.Tally.Received.Tick();
						n.ReadAt[number] = NpuLoan.NowMs();
						// Numbered in order: the oldest first out.
						if (n.ReadAt.Count > t.Keep) n.ReadAt.Remove(n.ReadAt.Keys.Min());
					}
					work.Line(l, number);
				},
				onHead: l => work.Line(l, null),
				// The borrower ended it, or isn't borrowing from this PC any more.
				onOver: _ => {
					lock (gate) Finish(n);
				},
				onChanged: onChange).Start();
			onChange?.Invoke();
			return null;
		}

		void Send(Lent n, JsonObject line, long? answers) {
			lock (gate) {
				if (now != n) return;
				LoanAnswers? a = answers is long number && n.ReadAt.TryGetValue(number, out double at) ? new LoanAnswers(number, Math.Floor(NpuLoan.NowMs() - at)) : null;
				if (a is { } timed) n.Tally.Work.Add(timed.Ms);
				n.Outbox.Push(line, answers: a);
				n.Tally.Sent.Tick();
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

		/// <summary>Lets a loan's streams and its pull go; <paramref name="why"/> is said to the borrower.</summary>
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

		/// <summary>Heiward is stopping here: a loan ends, the borrower told why (by default the kit's <c>&lt;Who&gt; stopped there</c>).</summary>
		public void Stop(string why = EstateLink.Who + " stopped there") {
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
