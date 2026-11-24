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
using HEI.Agent.Estate;
using HEI.Core.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HEI.Agent.Tests;

/// <summary>
/// Whole scans for another PC (<c>hei remote-scan</c>'s <see cref="RemoteScanner.RunAsync"/>) over a made-up link between
/// the two PCs: "the other PC" (this PC's own files, served as its Heiward serves them, with its records of its scans,
/// <see cref="ScanRecords"/>) and the PC scanning it (its homes of scans for other PCs, as its page answers for them). The
/// sets are found; once the other PC has taken its records back, nothing at all is left on the PC that scanned; a scan
/// again starts from the other PC's records and fetches only what's new; a scan stopped part way loses nothing.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME, EstateLink.SendOverride and the engine's database are process-wide
public sealed class RemoteScanRunTests : IDisposable {
	const string Target = "pc1", Scanner = "scanner-pc";
	readonly string dir = Directory.CreateTempSubdirectory("hei-remote-run-").FullName;
	readonly string src, remote, home, store;
	readonly string? homeWas = Environment.GetEnvironmentVariable("HEIWARD_HOME"), npuWas = Environment.GetEnvironmentVariable("NPU_AGENT_HOME"), pathWas = Environment.GetEnvironmentVariable("PATH");
	readonly Func<string?> gameWas = ScanForOthers.Game;
	readonly List<LentListing> lent = new();
	/// <summary>The other PC's records' files, as its page serves them to the PC scanning it.</summary>
	readonly PeerFiles records = new(() => null) { BytesPerSec = null };
	long fetched, recordBytes;
	int asks;
	/// <summary>Asked when the scan asks for a photo's bytes (its path there), before they're given: a test's say in the scan's course.</summary>
	Func<string, CancellationToken, Task>? onBytes;

	public RemoteScanRunTests() {
		src = Path.Combine(dir, "other pc");
		remote = Path.Combine(dir, "scanning pc", "remote"); // the scanning PC's homes of scans for other PCs
		home = Path.Combine(remote, Target); // as its page starts the scan: that PC's home there
		store = Path.Combine(dir, "other pc's heiward", "scanned-by", Scanner); // the other PC's records of its scans by it
		Directory.CreateDirectory(src);
		Directory.CreateDirectory(home);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		Environment.SetEnvironmentVariable("NPU_AGENT_HOME", Path.Combine(dir, "no npu")); // no NPU record to join
		AiComponents.TestOverrideModelPath = Path.Combine(dir, "no model.onnx"); // AI matching off: the photos' own looks
		ScanForOthers.Game = () => null;
		// The engine won't start without FFmpeg's programs, which a test PC may not have. Its photos are read by Windows'
		// decoder and, without FFmpeg's libraries here, its videos are left out: the programs are only looked for, never run.
		string tools = Path.Combine(dir, "ffmpeg");
		Directory.CreateDirectory(tools);
		File.WriteAllBytes(Path.Combine(tools, "ffmpeg.exe"), []);
		File.WriteAllBytes(Path.Combine(tools, "ffprobe.exe"), []);
		Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + pathWas);
	}

	public void Dispose() {
		EstateLink.SendOverride = null;
		RemoteMirror.StopServing();
		ScanForOthers.Game = gameWas;
		AiComponents.TestOverrideModelPath = null;
		Power.SetPace(fullSpeed: true, cpuCap: 0);
		foreach (LentListing l in lent) l.Dispose();
		Environment.SetEnvironmentVariable("HEIWARD_HOME", homeWas);
		Environment.SetEnvironmentVariable("NPU_AGENT_HOME", npuWas);
		Environment.SetEnvironmentVariable("PATH", pathWas);
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>A 24-bit BMP of <paramref name="w"/> × <paramref name="h"/>, each pixel's colour as <paramref name="at"/> says.</summary>
	string Photo(string name, int w, int h, Func<int, int, (byte R, byte G, byte B)> at) {
		int row = (w * 3 + 3) / 4 * 4, size = 54 + row * h;
		var b = new byte[size];
		b[0] = (byte)'B'; b[1] = (byte)'M';
		BitConverter.GetBytes(size).CopyTo(b, 2);
		BitConverter.GetBytes(54).CopyTo(b, 10);
		BitConverter.GetBytes(40).CopyTo(b, 14);
		BitConverter.GetBytes(w).CopyTo(b, 18);
		BitConverter.GetBytes(h).CopyTo(b, 22);
		BitConverter.GetBytes((short)1).CopyTo(b, 26);
		BitConverter.GetBytes((short)24).CopyTo(b, 28);
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++) {
				var (r, g, bl) = at(x, y);
				int i = 54 + y * row + x * 3;
				b[i] = bl; b[i + 1] = g; b[i + 2] = r;
			}
		string path = Path.Combine(src, name);
		File.WriteAllBytes(path, b);
		File.SetLastWriteTimeUtc(path, new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddMinutes(name.Length));
		return path;
	}

	string Copy(string of, string name) {
		string path = Path.Combine(src, name);
		File.Copy(of, path);
		File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(of));
		return path;
	}

	/// <summary>The other PC's listing of its files as they are now, lent as its Heiward lends it.</summary>
	LentListing Lend() {
		var files = Directory.GetFiles(src).Select(p => { var f = new FileInfo(p); return (p, f.Length, f.LastWriteTimeUtc); }).ToList();
		// In its own Heiward folder, as it lends it, not the PC scanning's.
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.GetDirectoryName(Path.GetDirectoryName(store)));
		LentListing l;
		try { l = LentListing.Write([(src + "\\", files)], "test"); }
		finally { Environment.SetEnvironmentVariable("HEIWARD_HOME", home); }
		lent.Add(l);
		return l;
	}

	/// <summary>
	/// The made-up link, as Castellan carries it: each ask goes to the PC it names, which hears who asks. The other PC
	/// answers the pages of its listing, its files' bytes (counted) and its records; the PC scanning answers what it holds
	/// of the other PC's scans. Nothing else (its timings: an older Heiward's answer).
	/// </summary>
	Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Link(Func<LentListing> now) => async (request, ct) => {
		string pathAndQuery = request.RequestUri!.OriginalString;
		int q = pathAndQuery.IndexOf('?');
		string path = pathAndQuery[..(q < 0 ? pathAndQuery.Length : q)], query = q < 0 ? "" : pathAndQuery[q..];
		string to = Uri.UnescapeDataString(path.Split('/')[3]); // /api/peers/<pc>/agents/heiward/<route>
		string route = path[(path.IndexOf("/agents/heiward/", StringComparison.Ordinal) + "/agents/heiward/".Length)..];
		if (to == Target && route == "scan/bytes") {
			string file = Uri.UnescapeDataString(query.TrimStart('?').Split('&').Single(p => p.StartsWith("path=", StringComparison.Ordinal))["path=".Length..]);
			if (onBytes is { } said) await said(file, ct);
			var f = new FileInfo(file);
			RangeItemHeaderValue range = request.Headers.Range!.Ranges.Single();
			long from = range.From!.Value, last = Math.Min(range.To ?? f.Length - 1, f.Length - 1);
			byte[] body = new byte[last - from + 1];
			using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) {
				fs.Position = from;
				fs.ReadExactly(body);
			}
			Interlocked.Add(ref fetched, body.Length);
			Interlocked.Increment(ref asks);
			var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
			r.Content.Headers.ContentLength = body.Length;
			r.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, last, f.Length);
			r.Headers.ETag = new EntityTagHeaderValue(PeerFiles.Tag(f.Length, PeerFiles.MsOf(f.LastWriteTimeUtc)));
			return r;
		}
		var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
		ctx.Request.Method = "GET";
		ctx.Request.QueryString = new QueryString(query);
		if (request.Headers.Range is { } asked) ctx.Request.Headers.Range = asked.ToString();
		// Who asks: the PC scanning asks the other one, and the other PC asks it.
		ctx.Request.Headers[EstateLink.PeerHeader] = to == Target ? Scanner : Target;
		ctx.Request.Headers["x-castellan-peer-name"] = to == Target ? "SCANNING-PC" : "OTHER-PC";
		var answer = new MemoryStream();
		ctx.Response.Body = answer;
		switch (to, route) {
			case (Target, "scan/pages"): await ScanForOthers.PageAsync(ctx, now()); break;
			case (Target, "scan/records"): await ScanRecords.OfferAsync(ctx, store, ScanRecords.Of(store), records); break;
			case (Target, "scan/records/bytes"): await records.ServeAsync(ctx); break;
			case (Scanner, "scan/kept"): await ScanForOthers.KeptAsync(ctx, remote); break;
			case (Scanner, "scan/kept/bytes"): await ScanForOthers.KeptBytesRouteAsync(ctx); break;
			default: ctx.Response.StatusCode = 404; break;
		}
		if (to == Target && route == "scan/records/bytes") Interlocked.Add(ref recordBytes, answer.Length);
		var reply = new HttpResponseMessage((HttpStatusCode)ctx.Response.StatusCode) { Content = new ByteArrayContent(answer.ToArray()) };
		if (ctx.Response.Headers.ETag.Count > 0) reply.Headers.TryAddWithoutValidation("ETag", ctx.Response.Headers.ETag.ToString());
		if (ctx.Response.Headers.ContentRange.Count > 0) reply.Content.Headers.TryAddWithoutValidation("Content-Range", ctx.Response.Headers.ContentRange.ToString());
		return reply;
	};

	/// <summary>A scan, which must end as <paramref name="expected"/> says; its log and the engine's say why when it doesn't.</summary>
	async Task Scan(int expected = 0) {
		Directory.CreateDirectory(home); // as the scanning PC's page makes it, for the scan's start
		var engine = new System.Collections.Concurrent.ConcurrentQueue<string>();
		void Heard(HEI.Core.Utils.LogEntry e) => engine.Enqueue(e.Message);
		HEI.Core.Utils.Logger.Instance.LogEntryAdded += Heard;
		int code;
		try { code = await RemoteScanner.RunAsync(Target, "OTHER-PC", CancellationToken.None); }
		finally { HEI.Core.Utils.Logger.Instance.LogEntryAdded -= Heard; }
		string log = Path.Combine(home, "heiward.log");
		Assert.True(code == expected, $"ended {code}: " + (File.Exists(log) ? File.ReadAllText(log) : "no log") + "\nThe engine: " + string.Join("\n", engine));
	}

	/// <summary>The other PC takes back what the PC scanning holds of its scan, as its page does as the scan ends.</summary>
	async Task TakeBack() {
		ScanRecords.Took took = await ScanRecords.TakeAsync(Scanner, store, CancellationToken.None);
		Assert.True(took.Any, took.Problem ?? "nothing was taken back");
		Assert.False(Directory.Exists(home)); // let go there
	}

	/// <summary>Everything on the PC that scanned, once its page has cleared what it let go: nothing.</summary>
	string[] LeftOnScanner() {
		RemoteHomes.Sweep(remote, _ => false, CancellationToken.None, offer: _ => RemoteHomes.Offered.Away);
		return Directory.Exists(remote) ? Directory.GetFileSystemEntries(remote, "*", SearchOption.AllDirectories) : [];
	}

	RemoteFindings Found() => RemoteFindings.Load(Path.Combine(store, "findings.json")) ?? throw new Xunit.Sdk.XunitException("no findings among the other PC's records");

	static string Sets(RemoteFindings f) => string.Join(" | ", f.Groups.Select(g => string.Join(", ", g.Items.Select(i => Path.GetFileName(i.Path)).Order())).Order());

	[Fact]
	public async Task NothingStaysOnThePcThatScanned_TheRecordsAreTheOtherPcs_AndAScanAgainFetchesOnlyWhatsNew() {
		(byte, byte, byte) Gradient(int x, int y) => ((byte)(x * 255 / 160), (byte)(y * 255 / 120), 60);
		(byte, byte, byte) Checks(int x, int y) => (x / 20 + y / 20) % 2 == 0 ? ((byte)230, (byte)230, (byte)230) : ((byte)20, (byte)40, (byte)200);
		(byte, byte, byte) Rings(int x, int y) => ((byte)((x - 80) * (x - 80) + (y - 60) * (y - 60) < 1600 ? 250 : 10), 120, (byte)(255 - y * 2));
		string a = Photo("a.bmp", 160, 120, Gradient), b = Photo("b.bmp", 160, 120, Checks), c = Photo("c.bmp", 160, 120, Rings);
		string[] first = [a, Copy(a, "a copy.bmp"), b, Copy(b, "b copy.bmp"), c];
		long all = first.Sum(p => new FileInfo(p).Length);

		// What 1.11.5 left on the PC scanning: a sparse stand-in of each of its files, and its marker once they were emptied.
		string mirror = Path.Combine(home, "mirror");
		foreach (string p in first) {
			string stand = RemotePath.Mirrored(mirror, p);
			Directory.CreateDirectory(Path.GetDirectoryName(stand)!);
			using var fs = new FileStream(stand, FileMode.Create);
			fs.SetLength(new FileInfo(p).Length);
		}
		File.WriteAllText(mirror + ".empty", "");
		LentListing listing = Lend();
		EstateLink.SendOverride = Link(() => listing);

		// The first scan: each photo fetched once, and the sets found. The other PC takes its records back, and nothing at
		// all is left on the PC that scanned: no stand-in, no listing, no database, no findings, no log.
		await Scan();
		Assert.Equal(all, fetched);
		Assert.Equal(0, recordBytes); // the other PC had none yet
		await TakeBack();
		string sets = Sets(Found());
		Assert.Equal("a copy.bmp, a.bmp | b copy.bmp, b.bmp", sets);
		foreach (string kept in new[] { "listing.tsv", "report.json", "findings.json", ScanRecords.Log, Path.Combine("db", "ScannedFiles.db") })
			Assert.True(File.Exists(Path.Combine(store, kept)), kept + " isn't among the other PC's records");
		Assert.DoesNotContain(Directory.GetFiles(store, "*", SearchOption.AllDirectories), p => p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));
		Assert.Empty(LeftOnScanner());
		(long Bytes, int Asks) firstScan = (fetched, asks);

		// Nothing new there: its records say what it last listed, its listing says it's as it was, and nothing is read:
		// none of its files, none of its records.
		await Scan();
		Assert.Equal(firstScan, (fetched, asks));
		Assert.Equal(0, recordBytes);
		await TakeBack(); // the scan's log alone
		Assert.Equal(sets, Sets(Found()));
		Assert.Empty(LeftOnScanner());

		// What it found isn't among its records any more, so the listing comes whole again, and its records with it: every
		// file is known from them as listed, by the engine and by the sorting of the sets. None of its files is fetched.
		File.Delete(Path.Combine(store, "findings.json"));
		await Scan();
		Assert.Equal(firstScan, (fetched, asks));
		Assert.True(recordBytes > 0);
		await TakeBack();
		Assert.Equal(sets, Sets(Found()));
		Assert.Empty(LeftOnScanner());

		// A photo new there: that one alone is fetched.
		string d = Photo("d.bmp", 150, 110, (x, y) => ((byte)((x + y) % 40 < 20 ? 200 : 30), (byte)(x * 255 / 150), 90));
		listing = Lend();
		await Scan();
		Assert.Equal(firstScan.Bytes + new FileInfo(d).Length, fetched);
		await TakeBack();
		Assert.Equal(sets, Sets(Found()));
		Assert.Empty(LeftOnScanner());

		// A scan stopped part way (the page here stops it as a new photo's bytes are asked for): what it has goes back to
		// the other PC all the same, its earlier records standing, and nothing stays on the PC that scanned. The next scan
		// starts from them: only the photos new since are fetched.
		string e = Photo("e.bmp", 140, 100, (x, y) => ((byte)(x * 3), (byte)(y * 2), (byte)((x ^ y) & 0xFF)));
		Copy(e, "e copy.bmp");
		listing = Lend();
		onBytes = async (path, ct) => {
			if (!path.EndsWith("\\e.bmp", StringComparison.OrdinalIgnoreCase)) return;
			File.WriteAllText(RemoteScanner.StopFile(home), "");
			// Its bytes don't come before the scan has stopped: the stop ends this ask (at most 20 s).
			using var most = CancellationTokenSource.CreateLinkedTokenSource(ct);
			most.CancelAfter(TimeSpan.FromSeconds(20));
			try { await Task.Delay(Timeout.Infinite, most.Token); }
			catch (OperationCanceledException) { }
			ct.ThrowIfCancellationRequested();
			throw new IOException("the scan didn't stop within 20 s");
		};
		await Scan(expected: 130);
		onBytes = null;
		await TakeBack();
		Assert.Equal(sets, Sets(Found()));
		Assert.Empty(LeftOnScanner());
		long beforeResume = fetched;
		await Scan();
		Assert.InRange(fetched - beforeResume, 1, 2 * new FileInfo(e).Length); // e and its copy, no more: none checked before
		await TakeBack();
		Assert.Equal("a copy.bmp, a.bmp | b copy.bmp, b.bmp | e copy.bmp, e.bmp", Sets(Found()));
		Assert.Empty(LeftOnScanner());
	}
}
