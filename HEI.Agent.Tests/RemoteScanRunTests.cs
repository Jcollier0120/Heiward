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
/// A whole scan for another PC (<c>hei remote-scan</c>'s <see cref="RemoteScanner.RunAsync"/>), over a made-up link to "the
/// other PC" (this PC's own files, served as its Heiward serves them): it finds the sets, keeps no file of the other PC's
/// here once it's over (Heiward 1.11.0 to 1.11.5 kept one of each), and a scan again fetches only what's new there.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME, EstateLink.SendOverride and the engine's database are process-wide
public sealed class RemoteScanRunTests : IDisposable {
	readonly string dir = Directory.CreateTempSubdirectory("hei-remote-run-").FullName;
	readonly string src, home;
	readonly string? homeWas = Environment.GetEnvironmentVariable("HEIWARD_HOME"), npuWas = Environment.GetEnvironmentVariable("NPU_AGENT_HOME"), pathWas = Environment.GetEnvironmentVariable("PATH");
	readonly List<LentListing> lent = new();
	long fetched;
	int asks;

	public RemoteScanRunTests() {
		src = Path.Combine(dir, "other pc");
		home = Path.Combine(dir, "heiward", "remote", "pc1"); // as the page starts it: that PC's home here
		Directory.CreateDirectory(src);
		Directory.CreateDirectory(home);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		Environment.SetEnvironmentVariable("NPU_AGENT_HOME", Path.Combine(dir, "no npu")); // no NPU record to join
		AiComponents.TestOverrideModelPath = Path.Combine(dir, "no model.onnx"); // AI matching off: the photos' own looks
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
		LentListing l = LentListing.Write([(src + "\\", files)], "test");
		lent.Add(l);
		return l;
	}

	/// <summary>The made-up link: the pages of its listing as its route answers them, its files' bytes by range, counted; nothing else.</summary>
	Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Link(Func<LentListing> now) => async (request, ct) => {
		string pathAndQuery = request.RequestUri!.OriginalString;
		int q = pathAndQuery.IndexOf('?');
		string route = pathAndQuery[..(q < 0 ? pathAndQuery.Length : q)], query = q < 0 ? "" : pathAndQuery[q..];
		if (route.EndsWith("/scan/bytes", StringComparison.Ordinal)) {
			string path = Uri.UnescapeDataString(query.Split('&').Single(p => p.TrimStart('?').StartsWith("path=", StringComparison.Ordinal)).TrimStart('?')["path=".Length..]);
			var f = new FileInfo(path);
			RangeItemHeaderValue range = request.Headers.Range!.Ranges.Single();
			long from = range.From!.Value, to = Math.Min(range.To ?? f.Length - 1, f.Length - 1);
			byte[] body = new byte[to - from + 1];
			using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
				fs.Position = from;
				fs.ReadExactly(body);
			}
			Interlocked.Add(ref fetched, body.Length);
			Interlocked.Increment(ref asks);
			var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
			r.Content.Headers.ContentLength = body.Length;
			r.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, f.Length);
			r.Headers.ETag = new EntityTagHeaderValue(PeerFiles.Tag(f.Length, PeerFiles.MsOf(f.LastWriteTimeUtc)));
			return r;
		}
		var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
		ctx.Request.Method = "GET";
		ctx.Request.QueryString = new QueryString(query);
		var answer = new MemoryStream();
		ctx.Response.Body = answer;
		if (route.EndsWith("/scan/pages", StringComparison.Ordinal)) await ScanForOthers.PageAsync(ctx, now());
		else ctx.Response.StatusCode = 404; // its timings: an older Heiward's answer
		return new HttpResponseMessage((HttpStatusCode)ctx.Response.StatusCode) { Content = new ByteArrayContent(answer.ToArray()) };
	};

	/// <summary>A scan, which must end well; its log says why when it doesn't.</summary>
	async Task Scan() {
		var engine = new System.Collections.Concurrent.ConcurrentQueue<string>();
		void Heard(HEI.Core.Utils.LogEntry e) => engine.Enqueue(e.Message);
		HEI.Core.Utils.Logger.Instance.LogEntryAdded += Heard;
		int code;
		try { code = await RemoteScanner.RunAsync("pc1", "OTHER-PC", CancellationToken.None); }
		finally { HEI.Core.Utils.Logger.Instance.LogEntryAdded -= Heard; }
		string log = Path.Combine(home, "heiward.log");
		Assert.True(code == 0, $"ended {code}: " + (File.Exists(log) ? File.ReadAllText(log) : "no log") + "\nThe engine: " + string.Join("\n", engine));
	}

	RemoteFindings Found() => RemoteFindings.Load(Path.Combine(home, "findings.json")) ?? throw new Xunit.Sdk.XunitException("no findings");

	static string Sets(RemoteFindings f) => string.Join(" | ", f.Groups.Select(g => string.Join(", ", g.Items.Select(i => Path.GetFileName(i.Path)).Order())).Order());

	/// <summary>The files in this PC's home of the scans for that PC, but for what's moved aside for <see cref="OldMirrors"/>.</summary>
	string[] FilesKept() => Directory.GetFiles(home, "*", SearchOption.AllDirectories)
		.Where(p => !Path.GetRelativePath(home, p).StartsWith("mirror" + RemoteMirror.OldSuffix, StringComparison.OrdinalIgnoreCase)).ToArray();

	[Fact]
	public async Task AScan_KeepsNoFileOfTheOtherPCsHere_AndAScanAgainFetchesOnlyWhatsNew() {
		(byte, byte, byte) Gradient(int x, int y) => ((byte)(x * 255 / 160), (byte)(y * 255 / 120), 60);
		(byte, byte, byte) Checks(int x, int y) => (x / 20 + y / 20) % 2 == 0 ? ((byte)230, (byte)230, (byte)230) : ((byte)20, (byte)40, (byte)200);
		(byte, byte, byte) Rings(int x, int y) => ((byte)((x - 80) * (x - 80) + (y - 60) * (y - 60) < 1600 ? 250 : 10), 120, (byte)(255 - y * 2));
		string a = Photo("a.bmp", 160, 120, Gradient), b = Photo("b.bmp", 160, 120, Checks), c = Photo("c.bmp", 160, 120, Rings);
		string[] first = [a, Copy(a, "a copy.bmp"), b, Copy(b, "b copy.bmp"), c];
		long all = first.Sum(p => new FileInfo(p).Length);

		// What 1.11.5 left here: a sparse stand-in of each of its files, and its marker once they were emptied.
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

		// The first scan: each photo fetched once, the sets found, and nothing of the other PC's files here after.
		await Scan();
		string sets = Sets(Found());
		Assert.Equal("a copy.bmp, a.bmp | b copy.bmp, b.bmp", sets);
		Assert.Equal(all, fetched);
		Assert.False(Directory.Exists(mirror));
		Assert.False(File.Exists(mirror + ".empty"));
		Assert.Single(Directory.GetDirectories(home, "mirror" + RemoteMirror.OldSuffix + "*")); // the old ones, set aside whole
		Assert.DoesNotContain(FilesKept(), p => p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));
		Assert.InRange(FilesKept().Length, 1, 20); // its database, listing, what it found: a handful, not one a file
		(long Bytes, int Asks) firstScan = (fetched, asks);

		// Nothing new there: that PC says so, and nothing is read.
		await Scan();
		Assert.Equal(firstScan, (fetched, asks));

		// What it found isn't here any more, so the listing comes whole again: every file is known as listed, by the engine
		// and by the sorting of the sets, with no file of it here to ask. Nothing is fetched.
		File.Delete(Path.Combine(home, "findings.json"));
		await Scan();
		Assert.Equal(firstScan, (fetched, asks));
		Assert.Equal(sets, Sets(Found()));
		Assert.False(Directory.Exists(mirror));

		// A photo new there: that one alone is fetched.
		string d = Photo("d.bmp", 150, 110, (x, y) => ((byte)((x + y) % 40 < 20 ? 200 : 30), (byte)(x * 255 / 150), 90));
		listing = Lend();
		await Scan();
		Assert.Equal(firstScan.Bytes + new FileInfo(d).Length, fetched);
		Assert.Equal(sets, Sets(Found()));
		Assert.False(Directory.Exists(mirror));
		Assert.DoesNotContain(FilesKept(), p => p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));

		// The old stand-ins go in the background, and nothing else of that PC's home.
		OldMirrors.Swept swept = OldMirrors.Sweep(Path.GetDirectoryName(home)!, _ => false, CancellationToken.None);
		Assert.Equal(first.Length, swept.Files);
		Assert.Empty(Directory.GetDirectories(home, "mirror*"));
		Assert.True(File.Exists(Path.Combine(home, "listing.tsv")));
		Assert.True(File.Exists(Path.Combine(home, "findings.json")));
		Assert.NotEmpty(Directory.GetFiles(Path.Combine(home, "db")));
	}
}
