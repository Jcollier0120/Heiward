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
using System.Net;
using System.Text.Json;
using HEI.Agent.Estate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace HEI.Agent.Tests;

/// <summary>
/// Another PC's listing in pages: the PC scanned writes it to disk once and answers it a page at a time (LentListing), the
/// PC scanning asks for the pages one after another (ListingPages), over a made-up link here. A library of any size goes
/// over in answers far under what one may be, a listing that changes as its pages come is asked for again, and the
/// whole listing in one answer is still version 1's, byte for byte.
/// </summary>
[Collection(AgentHomeCollection.Name)] // EstateLink.SendOverride is process-wide, and so is HEIWARD_HOME
public sealed class ListingPagesTests : IDisposable {
	readonly string dir = Directory.CreateTempSubdirectory("hei-pages-").FullName;
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	readonly ITestOutputHelper output;
	readonly List<LentListing> lent = new();

	public ListingPagesTests(ITestOutputHelper output) {
		this.output = output;
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		EstateLink.SendOverride = null;
		foreach (LentListing l in lent) l.Dispose();
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch (IOException) { }
	}

	const string Build = "1.11.1+abc";
	const string Pc = "4a90f04b-a29e-8af3-aae6-5b5a465d36d8";
	static readonly DateTime When = new(2026, 9, 21, 14, 13, 20, 123, DateTimeKind.Utc);

	/// <summary>What a PC keeps for its own scans: <paramref name="files"/> photos on C:, a few videos on D:, nothing on E:, the roots in order.</summary>
	static List<(string Root, List<(string Path, long Size, DateTime ModifiedUtc)> Files)> Kept(int files, int extra = 0) {
		var c = new List<(string, long, DateTime)>(files + extra);
		for (int i = 0; i < files + extra; i++)
			c.Add(($"C:\\Users\\Sam\\Pictures\\{(i % 7 == 0 ? "Été à Nice" : "Trips")}\\{i / 500:D4}\\IMG_{i:D7}.jpg", 1_000 + i, When.AddSeconds(i)));
		c.Add(("C:\\users\\sam\\pictures\\trips\\0000\\img_0000001.JPG", 5, When)); // the same file in another case: once
		var d = new List<(string, long, DateTime)> { ("D:\\Videos\\clip.mp4", 50_000_000, When), ("D:\\Videos\\clip (2).mp4", 50_000_000, When) };
		return [("C:\\", c), ("D:\\", d), ("E:\\", [])];
	}

	LentListing Lend(IEnumerable<(string Root, List<(string Path, long Size, DateTime ModifiedUtc)> Files)> kept, int pageFiles = LentListing.PageFiles,
		int pageBytes = LentListing.PageBytes) {
		LentListing l = LentListing.Write(kept, Build, pageFiles, pageBytes);
		lent.Add(l);
		return l;
	}

	/// <summary>The made-up link: each ask for <c>scan/pages</c> answered by the pages route from <paramref name="now"/>'s listing.</summary>
	static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Link(Func<LentListing> now, List<string>? asked = null) => async (request, ct) => {
		string pathAndQuery = request.RequestUri!.OriginalString;
		asked?.Add(pathAndQuery);
		int q = pathAndQuery.IndexOf('?');
		var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
		ctx.Request.Method = "GET";
		ctx.Request.QueryString = new QueryString(q < 0 ? "" : pathAndQuery[q..]);
		var body = new MemoryStream();
		ctx.Response.Body = body;
		if (!pathAndQuery[..(q < 0 ? pathAndQuery.Length : q)].EndsWith("/scan/pages", StringComparison.Ordinal)) ctx.Response.StatusCode = 404;
		else await ScanForOthers.PageAsync(ctx, now());
		return new HttpResponseMessage((HttpStatusCode)ctx.Response.StatusCode) { Content = new ByteArrayContent(body.ToArray()) };
	};

	static void SameListing(RemoteListing want, RemoteListing? got) {
		Assert.NotNull(got);
		Assert.Equal(want.Stamp, got!.Stamp);
		Assert.Equal(want.Roots.Select(r => (r.Root, r.Stamp, r.Files.Count)), got.Roots.Select(r => (r.Root, r.Stamp, r.Files.Count)));
		Assert.Equal(want.Roots.SelectMany(r => r.Files), got.Roots.SelectMany(r => r.Files));
	}

	// Written to disk a root at a time, the listing is version 1's: the same files, in the same order, with the same stamps,
	// and its whole answer the same bytes, so a scanner that asks for it whole reads it as it did.
	[Fact]
	public async Task The_lent_listing_is_version_1s_listing_and_its_whole_answer_the_same_bytes() {
		var kept = Kept(1_200);
		RemoteListing v1 = RemoteListing.FromKept(kept, Build);
		LentListing listing = Lend(kept, pageFiles: 100, pageBytes: 8 * 1024);
		Assert.Equal(v1.Stamp, listing.Stamp);
		Assert.Equal(v1.Roots.Select(r => (r.Root, r.Stamp, r.Files.Count)), listing.Roots.Select(r => (r.Path, r.Stamp, r.Files)));
		Assert.Equal(1_202, listing.Files);
		Assert.True(listing.Pages.Count > 12, $"{listing.Pages.Count} pages");

		var whole = new MemoryStream();
		await listing.WriteWholeAsync(whole, CancellationToken.None);
		Assert.Equal(v1.ToJson(), whole.ToArray());
		using JsonDocument doc = JsonDocument.Parse(whole.ToArray());
		SameListing(v1, RemoteListing.Parse(doc.RootElement));
	}

	// Each page an answer of its own, far under the most one may be (here a small one, 64 KB, where the whole listing is
	// several times that): the scan has all of it, as listed, and hears how much has come as it comes.
	[Fact]
	public async Task A_listing_bigger_than_one_answer_may_be_comes_whole_in_pages() {
		var kept = Kept(5_000);
		LentListing listing = Lend(kept, pageFiles: 400, pageBytes: 16 * 1024);
		var whole = new MemoryStream();
		await listing.WriteWholeAsync(whole, CancellationToken.None);
		Assert.True(whole.Length > 4 * 64 * 1024, $"the whole answer is {whole.Length:N0} bytes");

		var asked = new List<string>();
		EstateLink.SendOverride = Link(() => listing, asked);
		var heard = new List<(int Done, int Total)>();
		ListingPages.Fetched got = await ListingPages.FetchAsync(Pc, "GAMING-010", null, (d, t) => heard.Add((d, t)), CancellationToken.None, limit: 64 * 1024);
		Assert.Null(got.Problem);
		SameListing(RemoteListing.FromKept(kept, Build), got.Listing);
		Assert.Equal(listing.Pages.Count, asked.Count);
		Assert.All(asked.Skip(1), a => Assert.Contains("&stamp=" + listing.Stamp, a));
		Assert.Equal((5_002, 5_002), heard[^1]);
		Assert.True(heard.Count == listing.Pages.Count && heard.Select(h => h.Done).SequenceEqual(heard.Select(h => h.Done).Order()));
	}

	// Asked with the stamp kept from the last scan, as it still is: one small answer, and nothing read.
	[Fact]
	public async Task A_listing_as_it_was_at_the_last_scan_isnt_read_at_all() {
		LentListing listing = Lend(Kept(500), pageFiles: 50);
		var asked = new List<string>();
		EstateLink.SendOverride = Link(() => listing, asked);
		ListingPages.Fetched got = await ListingPages.FetchAsync(Pc, "GAMING-010", listing.Stamp, (_, _) => { }, CancellationToken.None);
		Assert.True(got.Same);
		Assert.Null(got.Listing);
		Assert.Single(asked);
	}

	// The PC scanned listed its files again while their pages came (a new listing, a new stamp): the next page is refused
	// as of another listing, and the scan asks for the new one from its first page, whole and as it is now.
	[Fact]
	public async Task A_listing_that_changes_as_its_pages_come_is_asked_for_again_from_its_first_page() {
		LentListing before = Lend(Kept(1_000), pageFiles: 100);
		var keptAfter = Kept(1_000, extra: 7);
		LentListing after = Lend(keptAfter, pageFiles: 100);
		Assert.NotEqual(before.Stamp, after.Stamp);
		var asked = new List<string>();
		EstateLink.SendOverride = Link(() => asked.Count <= 3 ? before : after, asked);
		ListingPages.Fetched got = await ListingPages.FetchAsync(Pc, "GAMING-010", null, (_, _) => { }, CancellationToken.None);
		Assert.Null(got.Problem);
		SameListing(RemoteListing.FromKept(keptAfter, Build), got.Listing);
		Assert.Equal(2, asked.Count(a => a.EndsWith("?n=0", StringComparison.Ordinal)));
		Assert.Contains(asked, a => a.Contains("&stamp=" + before.Stamp));
	}

	// It keeps changing: after a few rounds the scan says so, in words, and stops.
	[Fact]
	public async Task A_listing_that_keeps_changing_stops_the_scan_with_words_for_its_page() {
		int n = 0;
		var listings = Enumerable.Range(0, 4).Select(i => Lend(Kept(300, extra: i), pageFiles: 100)).ToList();
		EstateLink.SendOverride = Link(() => listings[Math.Min(3, n++ / 2)]);
		ListingPages.Fetched got = await ListingPages.FetchAsync(Pc, "GAMING-010", null, (_, _) => { }, CancellationToken.None);
		Assert.Null(got.Listing);
		Assert.Equal($"The files on this PC kept changing while {Environment.MachineName} listed them. Try again in a while", got.Problem);
	}

	// ---- Versions

	// A PC scanned that runs an older Heiward, before pages: its page has no such route (404). The scan says which PC to update.
	[Fact]
	public async Task A_PC_scanned_without_pages_is_told_to_update_Heiward() {
		EstateLink.SendOverride = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
		ListingPages.Fetched got = await ListingPages.FetchAsync(Pc, "GAMING-010", null, (_, _) => { }, CancellationToken.None);
		Assert.Equal($"Heiward on this PC is older than on {Environment.MachineName}: update Heiward here", got.Problem);
	}

	// Both PCs run the same build for a scan; when they don't, each page says which is older, and that's the one to update.
	[Fact]
	public void Versions_name_the_PC_to_update() {
		Assert.True(ScanForOthers.Newer("1.12.0+abc", "1.11.0+def"));
		Assert.False(ScanForOthers.Newer("1.11.0+abc", "1.11.0+def"));
		Assert.False(ScanForOthers.Newer("dev", "1.11.0"));
		Assert.Equal("Heiward on WEASELDEV-01 is newer (1.12.0; this PC has 1.11.0): update Heiward on this PC.",
			ScanForOthers.Versions("WEASELDEV-01", "1.12.0+abc", "1.11.0+def"));
		Assert.Equal("Heiward on GAMING-010 is older (1.11.0; this PC has 1.12.0): update Heiward there.",
			ScanForOthers.Versions("GAMING-010", "1.11.0+def", "1.12.0+abc"));
		Assert.EndsWith("update Heiward on both PCs.", ScanForOthers.Versions("GAMING-010", "1.11.0+def", "1.11.0+abc"));
	}

	// What goes wrong shows on the page of the PC scanned: in words, never an exception's own.
	[Fact]
	public void A_failed_scan_says_what_happened_in_words() {
		string me = Environment.MachineName;
		Assert.Equal($"This PC sent something Heiward on {me} doesn't read: update Heiward on both PCs",
			RemoteScanner.Plain(new InvalidDataException("its answer was too long")));
		Assert.Equal($"The link between {me} and this PC broke off. Try again; Castellan must be running on both PCs",
			RemoteScanner.Plain(new HttpRequestException("An error occurred while sending the request.")));
		Assert.DoesNotContain("NullReference", RemoteScanner.Plain(new NullReferenceException()));
	}

	// ---- What each PC keeps

	// The PC scanned looks each asked-for file up in the listing on disk (16 bytes a file in memory), whatever the case it's
	// asked in, and finds it as it listed it; the listing goes when the loan ends.
	[Fact]
	public void A_file_is_looked_up_on_disk_as_it_was_listed() {
		LentListing listing = LentListing.Write(Kept(2_000), Build);
		try {
			string? key = PeerFiles.KeyOf("c:/users/SAM/pictures/trips/0000/./img_0000001.JPG");
			Assert.Equal("C:\\Users\\Sam\\Pictures\\Trips\\0000\\IMG_0000001.jpg", listing.PathOf(key!));
			Assert.Equal(new ListedFile("C:\\Users\\Sam\\Pictures\\Trips\\0000\\IMG_0000001.jpg", 1_001, When.AddSeconds(1).Ticks),
				listing.Find("C:\\USERS\\SAM\\PICTURES\\TRIPS\\0000\\IMG_0000001.JPG"));
			Assert.Equal("C:\\Users\\Sam\\Pictures\\Été à Nice\\0000\\IMG_0000007.jpg", listing.Find("c:\\users\\sam\\pictures\\été à nice\\0000\\img_0000007.jpg")?.Path);
			Assert.Null(listing.Find("C:\\Windows\\win.ini"));
			Assert.Null(listing.PathOf(PeerFiles.KeyOf("C:\\Users\\Sam\\Pictures\\Trips\\0000\\IMG_9999999.jpg")!));
			Assert.Single(Directory.GetFiles(LentListing.Folder, "*.tsv"));
		}
		finally {
			listing.Dispose();
		}
		Assert.Empty(Directory.GetFiles(LentListing.Folder, "*.tsv"));
	}

	// The PC scanning keeps the listing for its next scan a line a file, written and read without holding it as one string;
	// one cut short isn't read (the next scan asks for all of it).
	[Fact]
	public void The_listing_kept_for_the_next_scan_is_read_back_as_it_was() {
		RemoteListing listing = RemoteListing.FromKept(Kept(1_500), Build);
		string path = Path.Combine(dir, "listing.tsv");
		ListingPages.Save(listing, path);
		SameListing(listing, ListingPages.Load(path));
		byte[] bytes = File.ReadAllBytes(path);
		File.WriteAllBytes(path, bytes[..^10]);
		Assert.Null(ListingPages.Load(path));
		File.WriteAllText(path, "{\"v\":1}\n");
		Assert.Null(ListingPages.Load(path));
	}

	// ---- A big library (HEI_BIG_LISTING=1): what listing 700,000 files costs each PC, in time and memory.
	[Fact]
	public async Task A_library_of_700000_files_goes_over_in_pages() {
		if (Environment.GetEnvironmentVariable("HEI_BIG_LISTING") != "1") return;
		const int Files = 700_000;
		long Managed() {
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
			GC.WaitForPendingFinalizers();
			return GC.GetTotalMemory(true);
		}
		long baseline = Managed();
		var timer = Stopwatch.StartNew();
		LentListing listing = LendBig(Files);
		double writeSec = timer.Elapsed.TotalSeconds;
		long held = Managed() - baseline; // the kept files read for it are gone: only the listing's own
		long onDisk = new FileInfo(Directory.GetFiles(LentListing.Folder, "*.tsv").Single()).Length;
		var whole = new CountingStream();
		await listing.WriteWholeAsync(whole, CancellationToken.None);

		var sizes = new List<long>();
		EstateLink.SendOverride = async (request, ct) => {
			HttpResponseMessage r = await Link(() => listing)(request, ct);
			sizes.Add((await r.Content.ReadAsByteArrayAsync(ct)).Length);
			return r;
		};
		long before = Managed();
		timer.Restart();
		ListingPages.Fetched got = await ListingPages.FetchAsync(Pc, "GAMING-010", null, (_, _) => { }, CancellationToken.None);
		double fetchSec = timer.Elapsed.TotalSeconds;
		Assert.Null(got.Problem);
		Assert.Equal(Files + 2, got.Listing!.Roots.Sum(r => r.Files.Count));
		long scannerHeld = Managed() - before;
		timer.Restart();
		string kept = Path.Combine(dir, "listing.tsv");
		ListingPages.Save(got.Listing, kept);
		Assert.NotNull(ListingPages.Load(kept));
		double keptSec = timer.Elapsed.TotalSeconds;
		output.WriteLine($"{listing.Files:N0} files: written in {writeSec:N1} s, {onDisk / 1e6:N1} MB on disk, {held / 1e6:N1} MB held in memory while lent " +
			$"({(double)held / listing.Files:N1} bytes a file); version 1's one answer would be {whole.Length / 1e6:N1} MB");
		output.WriteLine($"{listing.Pages.Count:N0} pages, the biggest {sizes.Max() / 1e6:N2} MB; all of them read in {fetchSec:N1} s; " +
			$"the scanning PC holds the listing as {scannerHeld / 1e6:N1} MB of objects; kept and read back in {keptSec:N1} s");
		Assert.True(sizes.Max() < ListingPages.PageLimit / 8);
		Assert.True(held < 40L * 1024 * 1024, $"{held:N0} bytes held");
	}

	/// <summary>The listing of a big library, made apart so what it was made from is let go when it returns (a Debug build keeps a method's own until it ends).</summary>
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	LentListing LendBig(int files) => Lend(Kept(files));

	sealed class CountingStream : Stream {
		long length;
		public override long Length => length;
		public override void Write(byte[] buffer, int offset, int count) => length += count;
		public override void Write(ReadOnlySpan<byte> buffer) => length += buffer.Length;
		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) {
			length += buffer.Length;
			return ValueTask.CompletedTask;
		}
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Position { get => length; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
	}
}
