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

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HEI.Agent.Estate {
	/// <summary>A PC on the license: its device id, and its name.</summary>
	sealed record Pc(string Id, string Name);

	/// <summary>
	/// The Estate: Manor's link between the PCs that hold the same Castellan license (Other PCs, under The estate in
	/// Castellan; Manor's peers.ts). Only on a license for more than one PC: free Heiward, with no Manor, never sees it.
	/// <list type="bullet">
	/// <item>Heiward asks its own Manor, <c>GET http://127.0.0.1:&lt;manor&gt;/api/peers/&lt;pc&gt;/agents/heiward/&lt;route&gt;</c>,
	/// with Manor's token and the X-Castellan-Peer header (no web page can add it).</item>
	/// <item>The other PC's Manor hands it to that PC's Heiward page as <c>GET /api/peer/&lt;route&gt;</c>, naming the asking PC
	/// in X-Castellan-Peer (its device id) and X-Castellan-Peer-Name.</item>
	/// <item>Only GET passes: every flow is a pull by the PC that wants the data.</item>
	/// </list>
	/// </summary>
	static partial class EstateLink {
		public const string PeerHeader = "x-castellan-peer";
		const string PeerNameHeader = "x-castellan-peer-name";
		public const string Agent = "heiward";

		/// <summary>
		/// One client for every ask: Manor answers on this PC, and a stream may stay open as long as a scan runs, so no
		/// timeout of its own (each ask brings its own).
		/// </summary>
		static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false, PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) }) {
			Timeout = Timeout.InfiniteTimeSpan,
		};

		/// <summary>Tests: another way to ask Manor (a fake Estate).</summary>
		internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendOverride;

		/// <summary>Manor on this PC: its port and token, from the server.json it writes in its folder; null when it isn't running here.</summary>
		internal static (int Port, string Token)? ManorServer() {
			if (Manor.Folder is not string folder) return null;
			try {
				string file = Path.Combine(folder, "server.json");
				if (!File.Exists(file)) return null;
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
				JsonElement root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object) return null;
				int port = root.TryGetProperty("port", out JsonElement p) && p.TryGetInt32(out int n) && n is > 0 and < 65536 ? n : 0;
				string? token = root.TryGetProperty("token", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
				return port > 0 && token is { Length: >= 16 } && TokenShape().IsMatch(token) ? (port, token) : null;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return null;
			}
		}

		[GeneratedRegex("^[0-9A-Za-z_-]{16,200}$")]
		private static partial Regex TokenShape();

		[GeneratedRegex("^[0-9A-Za-z-]{1,64}$")]
		private static partial Regex PcId();

		/// <summary>The PC asking, as the other PC's Manor names it; null when a request didn't come over the link.</summary>
		public static Pc? AskerOf(HttpRequest request) {
			string? id = request.Headers[PeerHeader];
			if (id == null || !PcId().IsMatch(id)) return null;
			string name = "";
			try { name = Uri.UnescapeDataString(request.Headers[PeerNameHeader].ToString()).Trim(); }
			catch (UriFormatException) { }
			if (name.Length > 80) name = name[..80];
			return new Pc(id, name.Length > 0 ? name : "Another of your PCs");
		}

		/// <summary>The path on this PC's Manor for <paramref name="route"/> of Heiward on <paramref name="pc"/>.</summary>
		public static string PeerPath(string pc, string route) => $"/api/peers/{Uri.EscapeDataString(pc)}/agents/{Agent}/{route.TrimStart('/')}";

		/// <summary>
		/// A GET on this PC's Manor, as the staff ask it: Manor's token read fresh (a Manor started again has a new one),
		/// and the X-Castellan-Peer header. Its head only: the caller reads the body, and disposes it.
		/// </summary>
		public static async Task<HttpResponseMessage> AskAsync(string pathAndQuery, CancellationToken ct, RangeHeaderValue? range = null) {
			var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
			request.Headers.TryAddWithoutValidation(PeerHeader, Agent);
			if (range != null) request.Headers.Range = range;
			if (SendOverride is { } fake) return await fake(request, ct);
			if (ManorServer() is not var (port, token)) throw new EstateMissing("Castellan isn't running on this PC");
			request.RequestUri = new Uri($"http://127.0.0.1:{port}{pathAndQuery}");
			request.Headers.TryAddWithoutValidation("x-token", token);
			return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
		}

		/// <summary>
		/// <see cref="AskAsync"/> for a reader that can't wait asynchronously (the scan's file readers, on their own
		/// threads): the same ask, sent synchronously, so no thread pool thread is held waiting on another.
		/// </summary>
		public static HttpResponseMessage Ask(string pathAndQuery, CancellationToken ct, RangeHeaderValue? range = null) {
			var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
			request.Headers.TryAddWithoutValidation(PeerHeader, Agent);
			if (range != null) request.Headers.Range = range;
			if (SendOverride is { } fake) return fake(request, ct).GetAwaiter().GetResult();
			if (ManorServer() is not var (port, token)) throw new EstateMissing("Castellan isn't running on this PC");
			request.RequestUri = new Uri($"http://127.0.0.1:{port}{pathAndQuery}");
			request.Headers.TryAddWithoutValidation("x-token", token);
			return Client.Send(request, HttpCompletionOption.ResponseHeadersRead, ct);
		}

		/// <summary>A GET's answer read whole as JSON (at most <paramref name="limit"/> bytes), with its status.</summary>
		public static async Task<(int Status, JsonDocument? Json)> GetJsonAsync(string pathAndQuery, TimeSpan timeout, CancellationToken ct, int limit = 64 * 1024 * 1024) {
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(timeout);
			using HttpResponseMessage r = await AskAsync(pathAndQuery, cts.Token);
			byte[] body = await ReadLimitedAsync(r.Content, limit, cts.Token);
			JsonDocument? json = null;
			try { json = body.Length > 0 ? JsonDocument.Parse(body) : null; }
			catch (JsonException) { }
			return ((int)r.StatusCode, json);
		}

		static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken ct) {
			await using Stream s = await content.ReadAsStreamAsync(ct);
			using var ms = new MemoryStream();
			byte[] buffer = new byte[81920];
			int n;
			while ((n = await s.ReadAsync(buffer, ct)) > 0) {
				if (ms.Length + n > limit) throw new InvalidDataException("its answer was too long");
				ms.Write(buffer, 0, n);
			}
			return ms.ToArray();
		}

		/// <summary>Another PC on the Estate: heard lately, and whether Heiward works there (null: not known yet).</summary>
		public sealed record EstatePc(string Id, string Name, bool Here, bool? HasHeiward);

		/// <summary>The Estate as Manor here sees it: on or not (null: Castellan couldn't say), its PCs, and why none can be reached.</summary>
		public sealed record EstateView(bool? On, List<EstatePc> Pcs, string? Problem);

		/// <summary>Manor's GET /api/peers: the PCs Heiward could ask. Null when Castellan isn't on this PC (free Heiward).</summary>
		public static async Task<EstateView?> ReadEstateAsync(CancellationToken ct) {
			if (SendOverride == null && ManorServer() == null) return null;
			try {
				var (status, json) = await GetJsonAsync("/api/peers", TimeSpan.FromSeconds(5), ct, limit: 1024 * 1024);
				using (json) {
					if (status == 404) return new EstateView(null, new(), "This Castellan is too old for the Estate: it updates itself soon.");
					if (status != 200 || json?.RootElement is not { ValueKind: JsonValueKind.Object } root) return new EstateView(null, new(), "Castellan didn't answer.");
					var pcs = new List<EstatePc>();
					if (root.TryGetProperty("peers", out JsonElement peers) && peers.ValueKind == JsonValueKind.Array)
						foreach (JsonElement p in peers.EnumerateArray()) {
							if (p.ValueKind != JsonValueKind.Object || Str(p, "id") is not string id || Str(p, "name") is not string name) continue;
							bool here = p.TryGetProperty("here", out JsonElement h) && h.ValueKind == JsonValueKind.True;
							bool? has = Str(p, "version") == null ? null
								: p.TryGetProperty("agents", out JsonElement agents) && agents.ValueKind == JsonValueKind.Array && agents.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.Object && Str(a, "id") == Agent);
							pcs.Add(new EstatePc(id, name.Length > 80 ? name[..80] : name, here, has));
						}
					bool on = root.TryGetProperty("on", out JsonElement o) ? o.ValueKind == JsonValueKind.True : pcs.Count > 0 || root.TryGetProperty("self", out _);
					string? problem = Str(root, "problem");
					if (!on) return new EstateView(false, pcs, "The Estate is off on this PC: turn on Other PCs under The estate in Castellan.");
					return new EstateView(true, pcs, problem is { Length: > 0 } ? "The Estate isn't working on this PC: " + problem : null);
				}
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or EstateMissing && !ct.IsCancellationRequested) {
				return new EstateView(null, new(), "Castellan didn't answer.");
			}
		}

		internal static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
	}

	/// <summary>Castellan isn't on this PC, or not running: no Estate to ask.</summary>
	sealed class EstateMissing(string message) : Exception(message);
}
