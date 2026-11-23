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

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HEI.Agent.Estate;

namespace HEI.Agent.Tests;

/// <summary>
/// A scan of another PC, around the engine: the listing that PC sends, its paths as this PC keeps them, the findings
/// mapped back to that PC's own paths, what that PC takes of them, and the asking side of its bytes (the kit's PeerFiles).
/// </summary>
[Collection(AgentHomeCollection.Name)] // EstateLink.SendOverride is process-wide
public sealed class RemoteScanTests : IDisposable {
	public void Dispose() => EstateLink.SendOverride = null;

	static readonly DateTime When = new(2026, 9, 21, 14, 13, 20, 123, DateTimeKind.Utc);

	static RemoteListing Listing() => RemoteListing.FromKept([
		("C:\\", [("C:\\Photos\\b.jpg", 2000L, When), ("C:\\Photos\\a.jpg", 1000L, When), ("C:\\photos\\A.JPG", 1000L, When)]),
		("D:\\", [("D:\\Videos\\clip.mp4", 50_000_000L, When)]),
	], "1.10.24+abc");

	[Fact]
	public void A_listing_goes_over_the_link_and_back_as_it_was() {
		RemoteListing sent = Listing();
		Assert.Equal(["C:\\Photos\\a.jpg", "C:\\Photos\\b.jpg"], sent.Roots[0].Files.Select(f => f.Path)); // one of each, in order
		using JsonDocument doc = JsonDocument.Parse(sent.ToJson());
		RemoteListing? got = RemoteListing.Parse(doc.RootElement);
		Assert.NotNull(got);
		Assert.Equal(sent.Stamp, got!.Stamp);
		Assert.Equal("1.10.24+abc", got.Build);
		Assert.Equal(sent.Roots.SelectMany(r => r.Files), got.Roots.SelectMany(r => r.Files));
		Assert.Equal(When, got.Roots[0].Files[0].ModifiedUtc);
	}

	// A stopped scan's sets aren't sorted, so it can't tell which of its photos' hashes are needed: it keeps those it had
	// and those it took, and its capture dates, so the next scan doesn't fetch those photos again for them.
	[Fact]
	public void A_stopped_scan_keeps_the_hashes_it_had_and_those_it_took_and_its_dates() {
		string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");
		string dir = Directory.CreateTempSubdirectory("hei-remote-kept-").FullName;
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
		try {
			string a = Path.Combine(dir, "a.jpg"), b = Path.Combine(dir, "b.jpg");
			File.WriteAllBytes(a, new byte[100]);
			File.WriteAllBytes(b, new byte[200]);
			void Take(string path, string hash) {
				var h = ReportBuilder.ContentHashes.Load();
				var f = new FileInfo(path);
				h.Keep(path, f.Length, f.LastWriteTimeUtc, hash);
				h.SaveTaken();
			}
			Take(a, "AA"); // the first scan, stopped
			Take(b, "BB"); // the next, stopped too
			var kept = ReportBuilder.ContentHashes.Load();
			Assert.Equal("AA", kept.Get(a)); // as kept, not hashed again
			Assert.Equal("BB", kept.Get(b));

			string dates = Path.Combine(dir, "dates.json");
			File.WriteAllText(dates, JsonSerializer.Serialize(new Dictionary<string, RemoteScanner.DateKept> { [a] = new(100, When.Ticks, true) }, AgentConfig.Json));
			Assert.Equal(new RemoteScanner.DateKept(100, When.Ticks, true), RemoteScanner.ReadDates(dates)[a]);
			File.WriteAllText(dates, "not json");
			Assert.Empty(RemoteScanner.ReadDates(dates));
		}
		finally {
			Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
			try { Directory.Delete(dir, true); } catch (IOException) { }
		}
	}

	[Fact]
	public void The_stamp_changes_only_when_the_files_do() {
		Assert.Equal(Listing().Stamp, Listing().Stamp);
		RemoteListing changed = RemoteListing.FromKept([("C:\\", [("C:\\Photos\\a.jpg", 1001L, When)])], "1.10.24+abc");
		RemoteListing same = RemoteListing.FromKept([("C:\\", [("C:\\Photos\\a.jpg", 1000L, When)])], "1.10.24+abc");
		Assert.NotEqual(changed.Stamp, same.Stamp);
		Assert.NotEqual(changed.Roots[0].Stamp, same.Roots[0].Stamp);
	}

	[Theory]
	[InlineData("{\"v\":2,\"stamp\":\"x\",\"roots\":[]}")] // another version
	[InlineData("[1,2]")]
	[InlineData("{\"v\":1,\"stamp\":\"x\",\"roots\":[{\"root\":\"C:\\\\\",\"stamp\":\"y\",\"files\":[[\"D:\\\\a.jpg\",1,1]]}]}")] // outside its root
	[InlineData("{\"v\":1,\"stamp\":\"x\",\"roots\":[{\"root\":\"C:\\\\Photos\",\"stamp\":\"y\",\"files\":[[\"C:\\\\Photos\\\\..\\\\Windows\\\\a.jpg\",1,1]]}]}")] // climbs out
	[InlineData("{\"v\":1,\"stamp\":\"x\",\"roots\":[{\"root\":\"C:\\\\\",\"stamp\":\"y\",\"files\":[[\"C:\\\\a.jpg\",-1,1]]}]}")] // a size below 0
	[InlineData("{\"v\":1,\"stamp\":\"x\",\"roots\":[{\"root\":\"C:\\\\\",\"stamp\":\"y\",\"files\":[[\"C:\\\\a.jpg:hidden\",1,1]]}]}")] // a stream
	[InlineData("{\"v\":1,\"stamp\":\"x\",\"roots\":[{\"root\":\"\\\\\\\\NAS\\\\share\",\"stamp\":\"y\",\"files\":[[\"\\\\\\\\NAS\\\\share\\\\a.jpg\",1,1]]}]}")] // a share
	public void A_listing_this_build_cant_trust_is_refused_whole(string json) {
		using JsonDocument doc = JsonDocument.Parse(json);
		Assert.Null(RemoteListing.Parse(doc.RootElement));
	}

	[Fact]
	public void Another_PCs_paths_stand_in_the_mirror_and_come_back_as_they_were() {
		string mirror = Path.Combine(Path.GetTempPath(), "m");
		string kept = RemotePath.Mirrored(mirror, "c:\\Photos\\a.jpg");
		Assert.Equal(Path.Combine(mirror, "C", "Photos", "a.jpg"), kept);
		Assert.Equal("C:\\Photos\\a.jpg", RemotePath.Original(mirror, kept));
		Assert.Null(RemotePath.Original(mirror, "C:\\Photos\\a.jpg")); // this PC's own
		Assert.Null(RemotePath.Original(mirror, Path.Combine(mirror + "x", "C", "a.jpg")));
	}

	static ReportItem Item(string path, long size) =>
		new(path, Path.GetFileName(path), Path.GetDirectoryName(path) ?? "", size, 100, 100, "jpeg", 0, 0, 0, When, 1, false, "identical", false, false);

	static ReportGroup Set(params ReportItem[] items) =>
		new(ReportBuilder.GroupKey(items.Select(i => i.Path)), "identical", "image", items[0].Path, "oldest", items[0].Size, 1, [.. items]);

	[Fact]
	public void Findings_go_back_with_the_other_PCs_own_paths_and_keys() {
		string mirror = Path.Combine(Path.GetTempPath(), "m");
		ReportGroup inMirror = Set(Item(RemotePath.Mirrored(mirror, "C:\\Photos\\a.jpg"), 1000), Item(RemotePath.Mirrored(mirror, "D:\\Old\\a.jpg"), 1000));
		ReportGroup outside = Set(Item(RemotePath.Mirrored(mirror, "C:\\Photos\\b.jpg"), 2000), Item("C:\\Users\\me\\b.jpg", 2000));
		List<ReportGroup> mapped = RemoteScanner.Mapped([inMirror, outside], mirror);
		ReportGroup g = Assert.Single(mapped);
		Assert.Equal(["C:\\Photos\\a.jpg", "D:\\Old\\a.jpg"], g.Items.Select(i => i.Path));
		Assert.Equal("C:\\Photos\\a.jpg", g.KeepPath);
		Assert.Equal("D:\\Old", g.Items[1].Folder);
		Assert.Equal(ReportBuilder.GroupKey(["C:\\Photos\\a.jpg", "D:\\Old\\a.jpg"]), g.Key);
	}

	[Fact]
	public void A_PC_takes_only_sets_of_the_files_it_listed_as_it_listed_them() {
		var listed = Listing().Roots.SelectMany(r => r.Files).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
		listed["C:\\Photos\\c.jpg"] = new ListedFile("C:\\Photos\\c.jpg", 1000, When.Ticks);
		var found = new RemoteFindings(RemoteFindings.Version, "1.10.24+abc", "s", When, 10, 4, "NPU", [
			Set(Item("C:\\Photos\\a.jpg", 1000), Item("C:\\Photos\\c.jpg", 1000)), // both listed: kept
			Set(Item("C:\\Photos\\a.jpg", 1000), Item("C:\\Windows\\a.jpg", 1000)), // one never listed
			Set(Item("C:\\Photos\\b.jpg", 2000), Item("C:\\Photos\\c.jpg", 2000)), // a size that isn't the listed one
			Set(Item("C:\\Photos\\a.jpg", 1000)), // one file isn't a set
		], null);
		ForeignFindings taken = ForeignFindings.From(new Pc("0d4a8e61-3f2b-4c97-b5e0-91a6c7d2f384", "Laptop"), found, listed);
		ReportGroup g = Assert.Single(taken.Groups);
		Assert.Equal("Laptop", g.FoundOn);
		Assert.Equal("Laptop", taken.FromName);
		Assert.Equal(["C:\\Photos\\a.jpg", "C:\\Photos\\c.jpg"], g.Items.Select(i => i.Path));
	}

	// ---- When the findings are from: the scan's end, not its start (a scan of a whole PC takes hours)

	static readonly Pc Laptop = new("0d4a8e61-3f2b-4c97-b5e0-91a6c7d2f384", "Laptop");

	static Dictionary<string, ListedFile> Listed() {
		var listed = Listing().Roots.SelectMany(r => r.Files).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
		listed["C:\\Photos\\c.jpg"] = new ListedFile("C:\\Photos\\c.jpg", 1000, When.Ticks);
		return listed;
	}

	static List<ReportGroup> OneSet() => [Set(Item("C:\\Photos\\a.jpg", 1000), Item("C:\\Photos\\c.jpg", 1000))];

	[Fact]
	public void Findings_are_dated_by_the_scans_end() {
		DateTime ended = When.AddHours(4.5);
		var found = new RemoteFindings(RemoteFindings.Version, "1.10.24+abc", "s", When, 16_167, 4, "NPU", OneSet(), null, ended);
		ForeignFindings kept = ForeignFindings.From(Laptop, found, Listed());
		Assert.Equal(ended, kept.FinishedUtc);
		Assert.Equal(ended, kept.FoundUtc());
		Assert.Equal(When, kept.ScannedAtUtc); // which scan they're from stays its start
	}

	[Fact]
	public void An_older_scanners_findings_are_dated_by_when_it_began_and_how_long_it_took() {
		// As a Heiward before the scan's end was sent writes them: no finishedUtc.
		string json = JsonSerializer.Serialize(new RemoteFindings(RemoteFindings.Version, "1.10.24+abc", "s", When, 7_200, 4, "NPU", OneSet()), AgentConfig.Json);
		using (JsonDocument doc = JsonDocument.Parse(json)) Assert.False(doc.RootElement.TryGetProperty("finishedUtc", out JsonElement f) && f.ValueKind != JsonValueKind.Null);
		RemoteFindings older = JsonSerializer.Deserialize<RemoteFindings>(json.Replace(",\"finishedUtc\":null", ""), AgentConfig.Json)!;
		Assert.Null(older.FinishedUtc);
		Assert.Equal(When.AddHours(2), older.EndedUtc);
		Assert.Equal(When.AddHours(2), ForeignFindings.From(Laptop, older, Listed()).FoundUtc());
	}

	/// <summary>The findings as a Heiward before the scan's end was kept reads them: the same, without it.</summary>
	sealed record OlderRemoteFindings(int V, string Build, string Stamp, DateTime ScannedAtUtc, double DurationSec, int FilesScanned, string Device,
		List<ReportGroup> Groups, RemoteCost? Cost = null);

	sealed record OlderForeignFindings(int V, string FromId, string FromName, string Build, DateTime ScannedAtUtc, List<ReportGroup> Groups);

	[Fact]
	public void An_older_PC_reads_the_newer_findings_as_it_did() {
		var found = new RemoteFindings(RemoteFindings.Version, "1.10.24+abc", "s", When, 60, 4, "NPU", OneSet(), new RemoteCost(1, 1, 1, 60, 0), When.AddMinutes(1));
		OlderRemoteFindings older = JsonSerializer.Deserialize<OlderRemoteFindings>(JsonSerializer.Serialize(found, AgentConfig.Json), AgentConfig.Json)!;
		Assert.Equal((found.V, found.ScannedAtUtc, found.Groups.Count, found.Stamp), (older.V, older.ScannedAtUtc, older.Groups.Count, older.Stamp));

		ForeignFindings kept = ForeignFindings.From(Laptop, found, Listed());
		OlderForeignFindings olderKept = JsonSerializer.Deserialize<OlderForeignFindings>(JsonSerializer.Serialize(kept, AgentConfig.Json), AgentConfig.Json)!;
		Assert.Equal((kept.V, kept.FromId, kept.ScannedAtUtc, kept.Groups.Count), (olderKept.V, olderKept.FromId, olderKept.ScannedAtUtc, olderKept.Groups.Count));
	}

	[Fact]
	public void Findings_an_older_Heiward_kept_here_are_dated_by_when_they_came() {
		// remote-findings.json as an older Heiward wrote it: no finishedUtc.
		string json = JsonSerializer.Serialize(new OlderForeignFindings(ForeignFindings.Version, Laptop.Id, Laptop.Name, "1.10.24+abc", When, OneSet()), AgentConfig.Json);
		ForeignFindings old = JsonSerializer.Deserialize<ForeignFindings>(json, AgentConfig.Json)!;
		Assert.Null(old.FinishedUtc);
		Assert.Equal(When.AddHours(3), old.FoundUtc(written: When.AddHours(3))); // the file's time, as they came
		Assert.Equal(When, old.FoundUtc()); // nothing else to go by: the scan's start, as before
		Assert.Equal(When, old.FoundUtc(written: When.AddDays(-1))); // never before the scan began (a clock set back, a copied file)
	}

	[Fact]
	public void A_scan_that_found_nothing_new_is_dated_by_its_own_end_its_sets_as_they_were() {
		var found = new RemoteFindings(RemoteFindings.Version, "1.10.24+abc", "s", When, 60, 4, "NPU", OneSet(), null, When.AddMinutes(1));
		DateTime now = When.AddDays(1);
		ForeignFindings kept = ForeignFindings.From(Laptop, found, Listed(), finished: now);
		Assert.Equal(now, kept.FoundUtc());
		Assert.Equal(When, kept.ScannedAtUtc); // still the scan its sets are from
		Assert.Single(kept.Groups);
	}

	[Fact]
	public void A_set_from_this_PCs_own_scan_carries_no_found_on() {
		string json = JsonSerializer.Serialize(Set(Item("C:\\a.jpg", 1), Item("C:\\b.jpg", 1)), AgentConfig.Json);
		Assert.DoesNotContain("foundOn", json, StringComparison.OrdinalIgnoreCase);
	}

	// ---- The asking side of a file's bytes (PEER-FILES.md)

	static readonly ListedFile File1 = new("C:\\Photos\\a.jpg", 300_000, When.Ticks);
	static string TagOf(ListedFile f) => PeerFiles.Tag(f.Size, PeerFiles.MsOf(f.ModifiedUtc));
	static byte ByteAt(long i) => (byte)(i % 251);

	/// <summary>A 206 of the file's bytes <paramref name="from"/>..<paramref name="to"/>, whose body breaks after <paramref name="cutAfter"/> bytes.</summary>
	static HttpResponseMessage Bytes(ListedFile f, long from, long to, string? tag = null, long cutAfter = long.MaxValue) {
		to = Math.Min(to, f.Size - 1);
		byte[] body = new byte[to - from + 1];
		for (long i = 0; i < body.Length; i++) body[i] = ByteAt(from + i);
		var r = new HttpResponseMessage(HttpStatusCode.PartialContent) {
			Content = new StreamContent(new Breaking(body, cutAfter)),
		};
		r.Content.Headers.ContentLength = body.Length;
		r.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, f.Size);
		r.Headers.ETag = new EntityTagHeaderValue(tag ?? TagOf(f));
		return r;
	}

	static HttpResponseMessage Refused(HttpStatusCode status, string json) =>
		new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

	[Fact]
	public void Bytes_come_one_range_at_a_time_and_a_reply_cut_short_is_asked_again_from_where_it_got_to() {
		var asked = new List<(long From, long To)>();
		EstateLink.SendOverride = (request, _) => {
			Assert.Contains("/scan/bytes?path=" + Uri.EscapeDataString(File1.Path), request.RequestUri!.OriginalString);
			RangeItemHeaderValue range = request.Headers.Range!.Ranges.Single();
			asked.Add((range.From!.Value, range.To!.Value));
			return Task.FromResult(Bytes(File1, range.From.Value, range.To.Value, cutAfter: asked.Count == 1 ? 70_000 : long.MaxValue));
		};
		var bytes = new EstateBytes("pc1");
		byte[] buffer = new byte[200_000];
		int n = bytes.Read(File1, 50_000, buffer, buffer.Length, CancellationToken.None);
		Assert.Equal(200_000, n);
		Assert.Equal([(50_000L, 249_999L), (120_000L, 249_999L)], asked);
		for (int i = 0; i < n; i++) Assert.True(buffer[i] == ByteAt(50_000 + i), $"byte {50_000 + i}");
		Assert.Equal(200_000, bytes.Fetched);
	}

	[Fact]
	public void The_end_of_the_file_ends_a_read() {
		EstateLink.SendOverride = (request, _) => {
			RangeItemHeaderValue range = request.Headers.Range!.Ranges.Single();
			return Task.FromResult(Bytes(File1, range.From!.Value, range.To!.Value));
		};
		byte[] buffer = new byte[100_000];
		Assert.Equal(10_000, new EstateBytes("pc1").Read(File1, 290_000, buffer, buffer.Length, CancellationToken.None));
	}

	// A photo whole is one ask, its bytes handed on as they come, not 4 MB at a time; each ask is timed.
	[Fact]
	public void A_whole_file_comes_in_one_ask_as_it_comes_and_is_timed() {
		var asked = new List<(long From, long To)>();
		EstateLink.SendOverride = (request, _) => {
			RangeItemHeaderValue range = request.Headers.Range!.Ranges.Single();
			asked.Add((range.From!.Value, range.To!.Value));
			return Task.FromResult(Bytes(File1, range.From.Value, range.To.Value));
		};
		var timings = new RemoteTimings();
		var bytes = new EstateBytes("pc1", timings: timings);
		long next = 0;
		long got = bytes.Stream(File1, 0, File1.Size, (at, piece) => {
			Assert.Equal(next, at);
			for (int i = 0; i < piece.Length; i++) Assert.True(piece[i] == ByteAt(at + i), $"byte {at + i}");
			next += piece.Length;
		}, CancellationToken.None);
		Assert.Equal(File1.Size, got);
		Assert.Equal(File1.Size, next);
		Assert.Equal([(0L, File1.Size - 1)], asked);
		Assert.Contains("Asks: 1 ", timings.Summary("pc1", 0, 1, 1, null, null));
	}

	[Fact]
	public void A_file_changed_since_it_was_listed_isnt_read() {
		EstateLink.SendOverride = (request, _) => Task.FromResult(Bytes(File1, 0, 99, tag: "\"300000-1\""));
		var e = Assert.Throws<RemoteReadFailed>(() => new EstateBytes("pc1").Read(File1, 0, new byte[100], 100, CancellationToken.None));
		Assert.Equal("changed", e.Code);
	}

	[Theory]
	[InlineData(HttpStatusCode.NotFound, "{\"error\":\"That file isn't there any more.\",\"code\":\"gone\"}", "gone")]
	[InlineData(HttpStatusCode.Forbidden, "{\"error\":\"That file isn't offered to other PCs.\",\"code\":\"not-offered\"}", "over")]
	[InlineData(HttpStatusCode.Forbidden, "{\"error\":\"This PC doesn't serve its files to that PC.\",\"code\":\"not-allowed\"}", "over")]
	[InlineData(HttpStatusCode.Gone, "{\"error\":\"This PC isn't borrowing that PC's NPU now.\"}", "over")]
	public void A_refusal_fails_the_read_with_its_reason(HttpStatusCode status, string json, string code) {
		EstateLink.SendOverride = (_, _) => Task.FromResult(Refused(status, json));
		var e = Assert.Throws<RemoteReadFailed>(() => new EstateBytes("pc1").Read(File1, 0, new byte[100], 100, CancellationToken.None));
		Assert.Equal(code, e.Code);
	}

	[Fact]
	public void During_a_game_there_it_waits_and_says_so() {
		using var stop = new CancellationTokenSource();
		var said = new List<string?>();
		EstateLink.SendOverride = (_, _) => Task.FromResult(Refused(HttpStatusCode.ServiceUnavailable,
			"{\"error\":\"A game is being played there (Game.exe).\",\"code\":\"game\",\"game\":\"Game.exe\",\"retryMs\":30000}"));
		var bytes = new EstateBytes("pc1", why => {
			said.Add(why);
			stop.Cancel(); // the wait ends with the scan
		});
		Assert.ThrowsAny<OperationCanceledException>(() => bytes.Read(File1, 0, new byte[100], 100, stop.Token));
		Assert.Equal(["game"], said);
	}

	/// <summary>A body that breaks (as a reply cut short does) after so many bytes.</summary>
	sealed class Breaking(byte[] data, long cutAfter) : Stream {
		long at;
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => data.Length;
		public override long Position { get => at; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) {
			if (at >= cutAfter) throw new IOException("The response ended prematurely.");
			int n = (int)Math.Min(Math.Min(count, data.Length - at), cutAfter - at);
			Array.Copy(data, at, buffer, offset, n);
			at += n;
			return n;
		}
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
