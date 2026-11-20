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
