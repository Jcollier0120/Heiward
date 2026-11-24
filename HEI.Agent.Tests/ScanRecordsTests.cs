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
using System.Text.Json;
using HEI.Agent.Estate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HEI.Agent.Tests;

/// <summary>
/// A scan's records going between the two PCs (<see cref="ScanRecords"/>): only a scan's own files, never its stand-ins or
/// this PC's settings; a transfer the link broke off picks up where it stopped, file by file and within a file; records
/// that changed as they came aren't taken.
/// </summary>
[Collection(AgentHomeCollection.Name)] // EstateLink.SendOverride and ScanRecords.Piece are process-wide
public sealed class ScanRecordsTests : IDisposable {
	readonly string dir = Directory.CreateTempSubdirectory("hei-records-").FullName;
	readonly string from, into;
	readonly long piece = ScanRecords.Piece;
	readonly PeerFiles files = new(() => null) { BytesPerSec = null };
	long served;
	/// <summary>Asks for bytes past this many in all are refused, as a link that's down: null, none are.</summary>
	long? downAfter;

	public ScanRecordsTests() {
		from = Path.Combine(dir, "home");
		into = Path.Combine(dir, "coming");
		ScanRecords.Piece = 64 * 1024;
		EstateLink.SendOverride = async (request, ct) => {
			string pathAndQuery = request.RequestUri!.OriginalString;
			int q = pathAndQuery.IndexOf('?');
			if (downAfter is long most && Interlocked.Read(ref served) >= most) throw new HttpRequestException("the link is down");
			var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
			ctx.Request.Method = "GET";
			ctx.Request.QueryString = new QueryString(q < 0 ? "" : pathAndQuery[q..]);
			if (request.Headers.Range is { } range) ctx.Request.Headers.Range = range.ToString();
			ctx.Request.Headers[EstateLink.PeerHeader] = "pc1";
			var answer = new MemoryStream();
			ctx.Response.Body = answer;
			await files.ServeAsync(ctx);
			Interlocked.Add(ref served, answer.Length);
			var reply = new HttpResponseMessage((HttpStatusCode)ctx.Response.StatusCode) { Content = new ByteArrayContent(answer.ToArray()) };
			if (ctx.Response.Headers.ETag.Count > 0) reply.Headers.TryAddWithoutValidation("ETag", ctx.Response.Headers.ETag.ToString());
			return reply;
		};
	}

	public void Dispose() {
		EstateLink.SendOverride = null;
		ScanRecords.Piece = piece;
		try { Directory.Delete(dir, true); } catch { }
	}

	string Write(string name, int size, int seed) {
		string path = Path.Combine(from, name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var bytes = new byte[size];
		new Random(seed).NextBytes(bytes);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	/// <summary>The records' list as the PC holding them answers it, and its file route set to serve them.</summary>
	ScanRecords.Offered Offer(ScanRecords.Manifest m) {
		var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
		var answer = new MemoryStream();
		ctx.Response.Body = answer;
		ScanRecords.OfferAsync(ctx, from, m, files).GetAwaiter().GetResult();
		return JsonSerializer.Deserialize<ScanRecords.Offered>(answer.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
	}

	[Fact]
	public void TheRecords_AreTheScansOwnFiles_NeverItsStandInsOrThisPcsSettings() {
		Write("listing.tsv", 10, 1);
		Write(Path.Combine("db", "ScannedFiles.db"), 10, 2);
		Write("heiward.log", 10, 3);
		Write(Path.Combine("mirror", "C", "a.jpg"), 10, 4);
		Write(Path.Combine("mirror.old-20261124", "C", "b.jpg"), 10, 5);
		Write("settings.json", 10, 6);
		Write("remote-status.json", 10, 7);
		Write("target.json", 10, 8);
		Write(Path.Combine(ScanRecords.Coming, "listing.tsv"), 10, 9);
		Write(Path.Combine("db", "ScannedFiles.db.4242.tmp"), 10, 10);
		ScanRecords.Manifest m = ScanRecords.Of(from);
		Assert.True(m.Whole);
		Assert.Equal(["db/ScannedFiles.db", "heiward.log", "listing.tsv"], m.Files.Select(f => f.Name));
		Assert.False(m.Holds); // no report, no findings

		File.Delete(Path.Combine(from, "listing.tsv"));
		Directory.Delete(Path.Combine(from, "db"), true);
		ScanRecords.Manifest log = ScanRecords.Of(from);
		Assert.False(log.Whole); // a scan that read nothing: its log alone
		Assert.Equal(["heiward.log"], log.Files.Select(f => f.Name));
		Assert.NotEqual(m.Tag, log.Tag);
	}

	[Fact]
	public void ATransferTheLinkBrokeOff_PicksUpWhereItStopped() {
		string small = Write("listing.tsv", 100_000, 1), big = Write(Path.Combine("db", "ScannedFiles.db"), 1_000_000, 2);
		ScanRecords.Offered offered = Offer(ScanRecords.Of(from));
		long all = offered.Bytes;

		// The link goes down part way through the database: what came stays.
		downAfter = 400_000;
		Assert.ThrowsAny<IOException>(() => ScanRecords.Fetch("pc1", "scan/kept/bytes", offered, into, CancellationToken.None));
		long first = served;
		Assert.InRange(first, 400_000, 400_000 + ScanRecords.Piece);
		Assert.InRange(Directory.GetFiles(into, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length), 400_000 - ScanRecords.Piece, first); // whole files and a piece

		// Back up: only the rest comes, and every byte is as it was there.
		downAfter = null;
		ScanRecords.Fetch("pc1", "scan/kept/bytes", offered, into, CancellationToken.None);
		Assert.Equal(all, served);
		Assert.Equal(File.ReadAllBytes(small), File.ReadAllBytes(Path.Combine(into, "listing.tsv")));
		Assert.Equal(File.ReadAllBytes(big), File.ReadAllBytes(Path.Combine(into, "db", "ScannedFiles.db")));
		Assert.Empty(Directory.GetFiles(into, "*.piece", SearchOption.AllDirectories));

		// Fetched again: all of it is there already.
		ScanRecords.Fetch("pc1", "scan/kept/bytes", offered, into, CancellationToken.None);
		Assert.Equal(all, served);
	}

	[Fact]
	public void RecordsThatChangedAsTheyCame_AreNotTaken() {
		string db = Write(Path.Combine("db", "ScannedFiles.db"), 300_000, 2);
		ScanRecords.Offered offered = Offer(ScanRecords.Of(from));
		File.SetLastWriteTimeUtc(db, DateTime.UtcNow.AddMinutes(5)); // a scan wrote it again since its list was given
		RemoteReadFailed failed = Assert.Throws<RemoteReadFailed>(() => ScanRecords.Fetch("pc1", "scan/kept/bytes", offered, into, CancellationToken.None));
		Assert.Equal("changed", failed.Code);
		Assert.False(File.Exists(Path.Combine(into, "db", "ScannedFiles.db")));
	}
}
