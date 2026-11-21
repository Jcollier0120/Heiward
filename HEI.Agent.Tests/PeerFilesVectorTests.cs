using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HEI.Agent.Estate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HEI.Agent.Tests;

/// <summary>
/// The kit's shared cases for a file's bytes served to another PC (kit/spec/peer-files-vectors.json, PEER-FILES.md),
/// run unchanged against Heiward's twin, <see cref="PeerFiles"/>: every reading, and every answer the route gives.
/// The file comes from the kit tools\kit.ps1 fills, at the version kit.json pins.
/// </summary>
public class PeerFilesVectorTests {
	static readonly Lazy<JsonObject> Vectors = new(() => JsonNode.Parse(File.ReadAllText(KitFile("peer-files-vectors.json")))!.AsObject());

	/// <summary>kit\spec\&lt;name&gt;, found from the tests' folder up.</summary>
	internal static string KitFile(string name) {
		for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent) {
			string path = Path.Combine(d.FullName, "kit", "spec", name);
			if (File.Exists(path)) return path;
		}
		throw new FileNotFoundException($"kit\\spec\\{name} isn't there: run tools\\kit.ps1 to fill the kit at the version kit.json pins.");
	}

	static JsonArray Cases(string section) => Vectors.Value[section]!.AsArray();

	static string Case(JsonNode c) => (string?)c["case"] ?? "?";

	[Fact]
	public void The_pace_and_its_numbers_are_the_kits() {
		JsonNode pace = Vectors.Value["pace"]!;
		Assert.Equal((long)pace["bytesPerSec"]!, PeerFiles.DefaultBytesPerSec);
		Assert.Equal((int)pace["chunkBytes"]!, PeerFiles.ChunkBytes);
		Assert.Equal((double)pace["burstMs"]!, PeerFiles.BurstMs);
		Assert.Equal((int)pace["gameKeepMs"]!, PeerFiles.GameKeepMs);
	}

	[Fact]
	public void Paths() {
		foreach (JsonNode c in Cases("paths"))
			Assert.True((string?)c["expected"] == PeerFiles.KeyOf((string?)c["path"]), $"{Case(c)}: {PeerFiles.KeyOf((string?)c["path"]) ?? "null"}");
	}

	[Fact]
	public void Tags() {
		foreach (JsonNode c in Cases("tags"))
			Assert.True((string)c["expected"]! == PeerFiles.Tag((long)c["size"]!, (double)c["mtimeMs"]!), Case(c));
	}

	[Fact]
	public void Ranges() {
		foreach (JsonNode c in Cases("ranges")) {
			PeerFiles.RangeAsk got = PeerFiles.RangeOf((string?)c["header"], (long)c["size"]!);
			JsonNode want = c["expected"]!;
			Assert.True((int)want["status"]! == got.Status, $"{Case(c)}: {got.Status}");
			if (got.Status == 206)
				Assert.True((long)want["start"]! == got.Start && (long)want["end"]! == got.End, $"{Case(c)}: {got.Start}-{got.End}");
		}
	}

	[Fact]
	public void None_match() {
		foreach (JsonNode c in Cases("noneMatch"))
			Assert.True((bool)c["expected"]! == PeerFiles.NoneMatch((string?)c["header"], (string)c["tag"]!), Case(c));
	}

	[Fact]
	public void Content_ranges() {
		foreach (JsonNode c in Cases("contentRanges")) {
			var got = PeerFiles.ContentRangeOf((string?)c["header"]);
			JsonNode? want = c["expected"];
			if (want == null) {
				Assert.True(got == null, Case(c));
				continue;
			}
			Assert.True(got != null, Case(c));
			Assert.True((long)want["size"]! == got!.Value.Size, Case(c));
			Assert.True((long?)want["start"] == got.Value.Start && (long?)want["end"] == got.Value.End, Case(c));
		}
	}

	[Fact]
	public void Paces() {
		foreach (JsonNode c in Cases("paces")) {
			var pace = new PeerFiles.Pace();
			long? rate = (long?)c["bytesPerSec"];
			double burst = (double)c["burstMs"]!;
			long[] got = c["chunks"]!.AsArray().Select(k => pace.Wait((double)k!["at"]!, (long)k["bytes"]!, rate, burst)).ToArray();
			long[] want = c["expected"]!.AsArray().Select(w => (long)w!).ToArray();
			Assert.True(want.SequenceEqual(got), $"{Case(c)}: [{string.Join(',', got)}]");
		}
	}

	[Fact]
	public async Task Answers() {
		JsonObject defaults = Vectors.Value["defaults"]!.AsObject();
		var failed = new List<string>();
		foreach (JsonNode c in Cases("answers")) {
			string? why = await AnswerDiffers(c.AsObject(), defaults);
			if (why != null) failed.Add($"{Case(c)}: {why}");
		}
		Assert.True(failed.Count == 0, string.Join('\n', failed));
	}

	/// <summary>What's wrong with the route's answer to one case, or null when it answers exactly as listed.</summary>
	static async Task<string?> AnswerDiffers(JsonObject c, JsonObject defaults) {
		JsonNode? Given(string key) => c.ContainsKey(key) ? c[key] : defaults[key];

		var files = new MadeUpFiles(Given("files")!.AsObject());
		string? game = (string?)Given("game");
		bool may = (bool)Given("may")!;
		var route = new PeerFiles(() => game, files) { May = _ => may };
		route.Replace(Given("offered")!.AsArray().Select(p => (string)p!));

		var ctx = new DefaultHttpContext {
			RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
		};
		ctx.Request.Method = "GET";
		ctx.Request.Path = "/api/peer/scan/bytes";
		var body = new MemoryStream();
		ctx.Response.Body = body;
		if ((string?)Given("from") is string from) {
			JsonNode pc = Vectors.Value["pcs"]![from]!;
			ctx.Request.Headers[EstateLink.PeerHeader] = (string)pc["id"]!;
			ctx.Request.Headers["x-castellan-peer-name"] = Uri.EscapeDataString((string)pc["name"]!);
		}
		JsonNode ask = c["ask"]!;
		if ((string?)ask["path"] is string path) ctx.Request.QueryString = new QueryString("?path=" + Uri.EscapeDataString(path));
		if ((string?)ask["range"] is string range) ctx.Request.Headers.Range = range;
		if ((string?)ask["ifNoneMatch"] is string tag) ctx.Request.Headers.IfNoneMatch = tag;

		await route.ServeAsync(ctx);

		JsonNode want = c["expected"]!;
		if (ctx.Response.StatusCode != (int)want["status"]!) return $"status {ctx.Response.StatusCode}, not {want["status"]}";
		if (want["headers"] is JsonObject headers)
			foreach (var (name, value) in headers) {
				string got = ctx.Response.Headers[name].ToString();
				if (got != (string?)value) return $"{name} is '{got}', not '{value}'";
			}
		if (!ctx.Response.Headers.CacheControl.ToString().Contains("no-store", StringComparison.Ordinal)) return "no Cache-Control: no-store";
		byte[] sent = body.ToArray();
		if (want["json"] is JsonNode json) {
			JsonNode? got;
			try { got = JsonNode.Parse(sent); }
			catch (JsonException) { return $"not JSON: {Encoding.UTF8.GetString(sent)}"; }
			if (!JsonNode.DeepEquals(got, json)) return $"JSON {got?.ToJsonString()}, not {json.ToJsonString()}";
		}
		else if (want["bytes"] is JsonArray span) {
			long a = (long)span[0]!, b = (long)span[1]!;
			if (sent.Length != b - a + 1) return $"{sent.Length} bytes, not {b - a + 1}";
			for (int i = 0; i < sent.Length; i++)
				if (sent[i] != (byte)((a + i) % 256)) return $"byte {a + i} is {sent[i]}";
		}
		else if (want.AsObject().ContainsKey("bytes") && sent.Length > 0) return $"{sent.Length} bytes, not none";
		return null;
	}

	/// <summary>The cases' made-up files: each of the size and time given, its byte at offset i being i mod 256.</summary>
	sealed class MadeUpFiles(JsonObject files) : PeerFiles.IFiles {
		public PeerFiles.FileFacts? Stat(string path) =>
			files[path] is JsonNode f ? new PeerFiles.FileFacts((bool?)f["folder"] ?? false, (long)f["size"]!, (double)f["mtimeMs"]!) : null;

		public Stream Open(string path) => new Pattern((long)files[path]!["size"]!);
	}

	sealed class Pattern(long length) : Stream {
		long at;
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => length;
		public override long Position { get => at; set => at = value; }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) {
			int n = (int)Math.Max(0, Math.Min(count, length - at));
			for (int i = 0; i < n; i++) buffer[offset + i] = (byte)((at + i) % 256);
			at += n;
			return n;
		}
		public override long Seek(long offset, SeekOrigin origin) =>
			at = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => at + offset, _ => length + offset };
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
