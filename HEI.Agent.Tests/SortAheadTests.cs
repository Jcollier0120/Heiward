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
using System.Text.Json;
using HEI.Agent.Estate;
using HEI.Core;
using HEI.Core.FFTools.FFmpegNative;
using HEI.Core.Utils;
using HEI.Core.ViewModels;
using Xunit.Abstractions;

namespace HEI.Agent.Tests;

/// <summary>
/// Sorting another PC's sets: the photos they look at again (up close, for their words, lined up) are found by a first pass
/// that fetches nothing, then fetched just ahead of the sorting, several at once, each once. Asked one at a time, a scan of
/// another PC sorted 26.6 GB of them at 3.3 MB/s, slower than its checking. A scan with nothing new fetches nothing, not even
/// for an answer that couldn't be told before.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME, RemoteFiles.Fetch and RemoteMedia are process-wide
public sealed class SortAheadTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-sortahead-" + Guid.NewGuid().ToString("N")[..8]);
	readonly string src, mirrorDir, homeDir;
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	readonly ITestOutputHelper output;

	public SortAheadTests(ITestOutputHelper output) {
		this.output = output;
		src = Path.Combine(dir, "src");
		mirrorDir = Path.Combine(dir, "m");
		homeDir = Path.Combine(dir, "home");
		Directory.CreateDirectory(src);
		Directory.CreateDirectory(homeDir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", homeDir);
	}

	public void Dispose() {
		RemoteFiles.Fetch = null;
		RemoteMedia.Stop();
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>
	/// "The other PC": this PC's own files, each ask taking <paramref name="latency"/> and all of them together at most
	/// <paramref name="bytesPerSecond"/> (the other PC's pace); counted, and how many were under way at once.
	/// </summary>
	sealed class SlowBytes(TimeSpan latency = default, long bytesPerSecond = 0) : IRemoteBytes {
		public long Bytes;
		public int Asks, Now, MostAtOnce;
		public string? RefusePath;
		readonly object pace = new();
		long paceFree = Stopwatch.GetTimestamp();

		public int Read(ListedFile file, long offset, byte[] buffer, int count, CancellationToken ct) {
			if (RefusePath != null && string.Equals(file.Path, RefusePath, StringComparison.OrdinalIgnoreCase)) throw new RemoteReadFailed("refused", "gone");
			int now = Interlocked.Increment(ref Now);
			for (int most; now > (most = Volatile.Read(ref MostAtOnce)) && Interlocked.CompareExchange(ref MostAtOnce, now, most) != most;) { }
			try {
				if (latency > TimeSpan.Zero) Thread.Sleep(latency);
				using var fs = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				fs.Position = offset;
				int n = fs.ReadAtLeast(buffer.AsSpan(0, count), count, throwOnEndOfStream: false);
				if (bytesPerSecond > 0) {
					long until;
					lock (pace) {
						long start = Math.Max(paceFree, Stopwatch.GetTimestamp());
						paceFree = until = start + n * Stopwatch.Frequency / bytesPerSecond;
					}
					TimeSpan wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), until);
					if (wait > TimeSpan.Zero) Thread.Sleep(wait);
				}
				Interlocked.Add(ref Bytes, n);
				Interlocked.Increment(ref Asks);
				return n;
			}
			finally { Interlocked.Decrement(ref Now); }
		}
	}

	/// <summary>
	/// The looks, as the scan's would make them of another PC's photos: each reads the stand-in by its path (fetched first,
	/// <see cref="RemoteFiles.Ready(string)"/>), and fails as for a file that can't be decoded when its bytes aren't there.
	/// Never reads zeros: a stand-in whose bytes aren't the original's is counted.
	/// </summary>
	sealed class RemoteLooks(RemoteMirror mirror, TimeSpan work = default) : IFingerprints {
		public readonly Dictionary<string, (float Gray, float? Ai)> ToKeeper = new(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase);
		public readonly HashSet<string> TooEvenToLineUp = new(StringComparer.OrdinalIgnoreCase);
		public int Looks, ZeroReads;
		/// <summary>After each look (a scan stopped partway).</summary>
		public Action? AfterLook;

		/// <summary>The time the looks waited for bytes, and spent on their own work (the probe).</summary>
		public long WaitTicks, WorkTicks;

		bool Here(string path) {
			long asked = Stopwatch.GetTimestamp();
			if (!RemoteFiles.Ready(path)) return false;
			long came = Stopwatch.GetTimestamp();
			Interlocked.Add(ref WaitTicks, came - asked);
			Interlocked.Increment(ref Looks);
			if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(mirror.Original(path)!))) Interlocked.Increment(ref ZeroReads);
			if (work > TimeSpan.Zero) Thread.Sleep(work); // decoding it, reading it
			Interlocked.Add(ref WorkTicks, Stopwatch.GetTimestamp() - came);
			AfterLook?.Invoke();
			return true;
		}

		public float? GrayPercent(string a, string b) => ToKeeper.TryGetValue(a, out var v) ? v.Gray : null;
		public float? AiPercent(string a, string b) => ToKeeper.TryGetValue(a, out var v) ? v.Ai : null;
		public float? AudioPercent(string a, string b) => null;
		public int? MaxCellDifference(string a, string b) => 0;
		public bool? SameUpClose(string a, string b) => Here(a) && Here(b) ? true : null;
		public bool? SameColors(string a, string b) => Here(a) && Here(b) ? true : null;
		public bool? SameDetail(string a, string b) => Here(a) && Here(b) ? true : null;
		public bool? LinesUp(string keep, string other) =>
			!Here(keep) || !Here(other) || TooEvenToLineUp.Contains(keep) || TooEvenToLineUp.Contains(other) ? null : true;
		public (int W, int H)? PictureSize(string path) => Here(path) && Words.ContainsKey(path) ? (1080, 2340) : null;
		public bool ReadsWords => true;
		public string? ReadWords(string path, (int W, int H) size) => Here(path) && Words.TryGetValue(path, out string? w) ? w : null;
	}

	/// <summary>The sorting's fetches, as the scan of another PC makes them, and what its first pass found.</summary>
	sealed class Fetching(RemoteMirror mirror) : ISortFetch {
		public SortNeeds? Needs;
		public int Planned;
		public SortNeeds Refuse() => mirror.Refuse();
		public void Ahead(SortNeeds needs) {
			Needs = needs;
			if (needs.Asks > 0) Planned = mirror.SortAhead(needs.Paths).Photos;
		}
	}

	readonly Guid group = Guid.NewGuid();
	static readonly DateTime When = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

	string Make(string name, int size, int seed) {
		string path = Path.Combine(src, name);
		var data = new byte[size];
		new Random(seed).NextBytes(data);
		File.WriteAllBytes(path, data);
		File.SetLastWriteTimeUtc(path, When.AddSeconds(seed));
		return path;
	}

	string Copy(string of, string name) {
		string path = Path.Combine(src, name);
		File.Copy(of, path);
		File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(of));
		return path;
	}

	(RemoteMirror Mirror, SlowBytes Bytes) Mirror(string[] paths, SlowBytes? bytes = null) {
		bytes ??= new SlowBytes(TimeSpan.FromMilliseconds(5));
		var mirror = new RemoteMirror(mirrorDir, bytes) { Recent = TimeSpan.Zero };
		mirror.Sync(RemoteListing.FromKept([(src + "\\", paths.Select(p => { var f = new FileInfo(p); return (p, f.Length, f.LastWriteTimeUtc); }).ToList())], "test"));
		RemoteFiles.Fetch = mirror.Fetch;
		RemoteMedia.Use(mirror.IsRemote, mirror.Open);
		return (mirror, bytes);
	}

	DuplicateItem Item(RemoteMirror mirror, string original, int w, int h, Guid inGroup) {
		var f = new FileInfo(original);
		string stand = RemotePath.Mirrored(mirror.Folder, original);
		return new DuplicateItem {
			Path = stand, Folder = Path.GetDirectoryName(stand)!, SizeLong = f.Length, IsImage = true, FrameSize = $"{w}x{h}", FrameSizeInt = w + h,
			Similarity = 90, GroupId = inGroup, DateModified = f.LastWriteTimeUtc.ToLocalTime(),
		};
	}

	/// <summary>
	/// Four sets of another PC's photos: a photo and its smaller copy (looked at up close, their words read), two receipts
	/// with other totals (read, lined up), the same bytes twice (hashed), and a look-alike whose words can't be read and
	/// that's too even to line up (can't be told, twice).
	/// </summary>
	(List<DuplicateItem> Items, RemoteLooks Looks, RemoteMirror Mirror, SlowBytes Bytes) Sets(SlowBytes? bytes = null) {
		string alpha = Make("alpha.jpg", 300_000, 1), alphaSmall = Make("alpha small.jpg", 100_000, 2);
		string bravo = Make("bravo.jpg", 250_000, 3), bravoOther = Make("bravo other.jpg", 240_000, 4);
		string charlie = Make("charlie.jpg", 200_000, 5), charlieCopy = Copy(charlie, "charlie copy.jpg");
		string delta = Make("delta.jpg", 220_000, 6), deltaAgain = Make("delta again.jpg", 210_000, 7);
		var (mirror, b) = Mirror([alpha, alphaSmall, bravo, bravoOther, charlie, charlieCopy, delta, deltaAgain], bytes);
		var looks = new RemoteLooks(mirror);
		string S(string p) => RemotePath.Mirrored(mirror.Folder, p);
		Guid g1 = Guid.NewGuid(), g2 = Guid.NewGuid(), g3 = Guid.NewGuid(), g4 = Guid.NewGuid();
		var items = new List<DuplicateItem> {
			Item(mirror, alpha, 4032, 3024, g1), Item(mirror, alphaSmall, 1280, 960, g1),
			Item(mirror, bravo, 2000, 1000, g2), Item(mirror, bravoOther, 2000, 1000, g2),
			Item(mirror, charlie, 1600, 1200, g3), Item(mirror, charlieCopy, 1600, 1200, g3),
			Item(mirror, delta, 3000, 2000, g4), Item(mirror, deltaAgain, 3000, 2000, g4),
		};
		looks.ToKeeper[S(alpha)] = (100, null);
		looks.ToKeeper[S(alphaSmall)] = (99.9f, null);
		looks.ToKeeper[S(bravo)] = (100, null);
		looks.ToKeeper[S(bravoOther)] = (90, 95);
		looks.ToKeeper[S(charlie)] = (100, null);
		looks.ToKeeper[S(charlieCopy)] = (100, null);
		looks.ToKeeper[S(delta)] = (100, null);
		looks.ToKeeper[S(deltaAgain)] = (92, 96);
		looks.Words[S(alpha)] = looks.Words[S(alphaSmall)] = "holiday at the lake with everyone";
		looks.Words[S(bravo)] = "total due 10 dollars thank you for shopping";
		looks.Words[S(bravoOther)] = "total due 12 dollars thank you for shopping";
		looks.Words[S(delta)] = "a sign by the road";
		looks.TooEvenToLineUp.Add(S(deltaAgain)); // and its words can't be read: none set
		return (items, looks, mirror, b);
	}

	static string Json(List<ReportGroup> groups) => JsonSerializer.Serialize(groups, AgentConfig.Json);

	[Fact]
	public void ThePhotosTheSetsLookAt_AreFetchedOnceAndAhead_AndTheSetsAreTheSameAsFetchedOneAtATime() {
		string expected;
		{
			// As before: each photo fetched as the sorting gets to it.
			var (items, looks, mirror, _) = Sets();
			using (mirror) expected = Json(ReportBuilder.Build(items, looks, new ReportBuilder.ContentHashes(), datesKnown: new Dictionary<string, bool>()));
			Assert.Equal(0, looks.ZeroReads);
			RemoteFiles.Fetch = null;
			Directory.Delete(mirrorDir, true);
			foreach (string f in Directory.GetFiles(src)) File.Delete(f);
		}
		var (again, remote, m, bytes) = Sets(new SlowBytes(TimeSpan.FromMilliseconds(30)));
		using (m) {
			var fetching = new Fetching(m);
			string got = Json(ReportBuilder.Build(again, remote, new ReportBuilder.ContentHashes(), datesKnown: new Dictionary<string, bool>(), fetching: fetching));
			Assert.Equal(expected, got);
			Assert.Equal(0, remote.ZeroReads);
			Assert.NotNull(fetching.Needs);
			Assert.Equal(8, fetching.Planned); // all eight photos, found without fetching any
			Assert.Equal(8, bytes.Asks); // each fetched once, whole
			Assert.True(bytes.MostAtOnce > 1, $"one ask at a time ({bytes.MostAtOnce})");
		}
	}

	[Fact]
	public void AScanWithNothingNew_FetchesNothingForItsSets_NotEvenForWhatCouldntBeTold() {
		var (items, looks, mirror, bytes) = Sets();
		using (mirror) {
			var dates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			var hashes = ReportBuilder.ContentHashes.Load();
			string first = Json(ReportBuilder.Build(items, looks, hashes, datesKnown: dates, fetching: new Fetching(mirror)));
			hashes.Save();
			int asks = bytes.Asks, looked = looks.Looks;
			Assert.True(asks > 0);

			var fetching = new Fetching(mirror);
			string next = Json(ReportBuilder.Build(items, looks, ReportBuilder.ContentHashes.Load(), datesKnown: dates, fetching: fetching));
			Assert.Equal(first, next);
			foreach (string p in fetching.Needs!.Paths) output.WriteLine("asked: " + p);
			Assert.Equal(0, fetching.Needs!.Asks); // the first pass asked for nothing: its sets are the ones sorted
			Assert.Equal(asks, bytes.Asks);
			Assert.Equal(looked, looks.Looks); // nothing looked at again, the words that can't be read and the pair too even to line up included
		}
	}

	// A scan stopped while it sorts its sets (Stop on the page, an update after its day's hold): what it looked at is kept
	// (SaveTaken, as RemoteScanner's Stopped keeps it), so the scan asked again picks up where it stopped.
	[Fact]
	public void AScanCutWhileItSortsItsSets_KeepsWhatItLookedAt_SoTheNextFetchesOnlyTheRest() {
		var (items, looks, mirror, bytes) = Sets();
		using (mirror) {
			var dates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			using var stop = new CancellationTokenSource();
			looks.AfterLook = () => { if (looks.Looks >= 4) stop.Cancel(); };
			var hashes = ReportBuilder.ContentHashes.Load();
			Assert.ThrowsAny<OperationCanceledException>(() =>
				ReportBuilder.Build(items, looks, hashes, ct: stop.Token, datesKnown: dates, fetching: new Fetching(mirror)));
			hashes.SaveTaken();
			looks.AfterLook = null;

			var fetching = new Fetching(mirror);
			string resumed = Json(ReportBuilder.Build(items, looks, ReportBuilder.ContentHashes.Load(), datesKnown: dates, fetching: fetching));
			Assert.InRange(fetching.Planned, 1, 7); // not the eight a first scan fetches: what it looked at before it stopped isn't fetched again
			string whole = Json(ReportBuilder.Build(items, looks, new ReportBuilder.ContentHashes(), datesKnown: new Dictionary<string, bool>(dates, StringComparer.OrdinalIgnoreCase)));
			Assert.Equal(whole, resumed); // the same sets as a scan never stopped
			Assert.Equal(0, looks.ZeroReads);
		}
	}

	[Fact]
	public void WhatCouldntBeTold_OfAPhotoWhoseBytesDidntCome_IsAskedAgainNextScan() {
		var (items, looks, mirror, bytes) = Sets();
		using (mirror) {
			string deltaAgain = items.Single(i => i.Path.EndsWith("delta again.jpg", StringComparison.Ordinal)).Path;
			bytes.RefusePath = mirror.Original(deltaAgain);
			var hashes = ReportBuilder.ContentHashes.Load();
			hashes.NotFetched = mirror.Failed.ContainsKey;
			ReportBuilder.Build(items, looks, hashes, datesKnown: new Dictionary<string, bool>(), fetching: new Fetching(mirror));
			Assert.True(mirror.Failed.ContainsKey(deltaAgain));
			hashes.Save();
			string looksJson = File.ReadAllText(ReportBuilder.ContentHashes.LooksPath), textsJson = File.ReadAllText(ReportBuilder.ContentHashes.TextsPath);
			Assert.DoesNotContain("delta again", looksJson, StringComparison.Ordinal); // nothing kept of it as unknown
			Assert.DoesNotContain("delta again", textsJson, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void WhatCouldntBeTold_IsKeptUnderKeysAnOlderHeiwardNeverAsksFor_AndItsOwnFilesAreReadAsBefore() {
		var (items, looks, mirror, _) = Sets();
		using (mirror) {
			var dates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // kept between scans, as dates.json is
			var hashes = ReportBuilder.ContentHashes.Load();
			ReportBuilder.Build(items, looks, hashes, datesKnown: dates, fetching: new Fetching(mirror));
			hashes.Save();
			// An older Heiward reads looks.json and texts.json as maps of what it keeps: every entry still reads as one.
			var keptLooks = JsonSerializer.Deserialize<Dictionary<string, ReportBuilder.ContentHashes.Look>>(File.ReadAllText(ReportBuilder.ContentHashes.LooksPath), AgentConfig.Json)!;
			var keptWords = JsonSerializer.Deserialize<Dictionary<string, ReportBuilder.ContentHashes.Words>>(File.ReadAllText(ReportBuilder.ContentHashes.TextsPath), AgentConfig.Json)!;
			// The pair too even to line up, and the words that can't be read: under keys no path or pair of paths is.
			Assert.Contains(keptLooks.Keys, k => k.StartsWith(ReportBuilder.ContentHashes.UnknownKey, StringComparison.Ordinal) && k.Contains("delta again", StringComparison.Ordinal));
			Assert.Contains(keptWords.Keys, k => k.StartsWith(ReportBuilder.ContentHashes.UnreadKey, StringComparison.Ordinal) && k.EndsWith("delta again.jpg", StringComparison.Ordinal));
			Assert.All(keptWords.Keys.Where(k => !k.StartsWith(ReportBuilder.ContentHashes.UnreadKey, StringComparison.Ordinal)), k => Assert.True(Path.IsPathFullyQualified(k), k));
			Assert.All(keptLooks.Where(kv => !kv.Key.StartsWith(ReportBuilder.ContentHashes.UnknownKey, StringComparison.Ordinal)), kv => Assert.True(kv.Value.Same, kv.Key));

			// What an older Heiward kept (no such keys) is read as before: its close looks and words, nothing asked again.
			foreach (string key in keptLooks.Keys.Where(k => k.StartsWith(ReportBuilder.ContentHashes.UnknownKey, StringComparison.Ordinal)).ToList()) keptLooks.Remove(key);
			foreach (string key in keptWords.Keys.Where(k => k.StartsWith(ReportBuilder.ContentHashes.UnreadKey, StringComparison.Ordinal)).ToList()) keptWords.Remove(key);
			File.WriteAllText(ReportBuilder.ContentHashes.LooksPath, JsonSerializer.Serialize(keptLooks, AgentConfig.Json));
			File.WriteAllText(ReportBuilder.ContentHashes.TextsPath, JsonSerializer.Serialize(keptWords, AgentConfig.Json));
			var fetching = new Fetching(mirror);
			ReportBuilder.Build(items, looks, ReportBuilder.ContentHashes.Load(), datesKnown: dates, fetching: fetching);
			// Only the two that couldn't be told are fetched again: the pair too even to line up, and the words not read.
			Assert.Equal(2, fetching.Planned);
		}
	}

	/// <summary>
	/// The sorting's rate over a link like two PCs': each ask 70 ms, 20 MB/s in all, and 10 ms of the sorting's own work for
	/// each look at a photo (about 40 ms a photo in all, as one PC's sorting spent). <c>HEI_SORT_PROBE</c> set: how many sets of two photos of 1 MB (none: nothing runs).
	/// </summary>
	[Fact]
	public void Probe_TheSortingsRate() {
		if (!int.TryParse(Environment.GetEnvironmentVariable("HEI_SORT_PROBE"), out int sets) || sets <= 0) return;
		foreach (bool ahead in new[] { false, true }) {
			if (Directory.Exists(mirrorDir)) Directory.Delete(mirrorDir, true);
			foreach (string f in Directory.GetFiles(src)) File.Delete(f);
			var paths = new List<string>();
			for (int s = 0; s < sets; s++) {
				paths.Add(Make($"set {Letters(s)}.jpg", 1_000_000 + 2 * s, 2 * s + 1)); // sizes of their own: none hashed to gather split copies
				paths.Add(Make($"set {Letters(s)} small.jpg", 1_000_000 + 2 * s + 1, 2 * s + 2));
			}
			var bytes = new SlowBytes(TimeSpan.FromMilliseconds(70), 20L << 20);
			var (mirror, _) = Mirror([.. paths], bytes);
			using (mirror) {
				var looks = new RemoteLooks(mirror, TimeSpan.FromMilliseconds(10)); // each look: about 40 ms of work a photo in all
				var items = new List<DuplicateItem>();
				for (int s = 0; s < sets; s++) {
					Guid g = Guid.NewGuid();
					items.Add(Item(mirror, paths[2 * s], 4000, 3000, g));
					items.Add(Item(mirror, paths[2 * s + 1], 2000, 1500, g));
					looks.ToKeeper[items[^2].Path] = (100, null);
					looks.ToKeeper[items[^1].Path] = (99.9f, null);
				}
				var timer = Stopwatch.StartNew();
				ReportBuilder.Build(items, looks, new ReportBuilder.ContentHashes(), datesKnown: new Dictionary<string, bool>(), fetching: ahead ? new Fetching(mirror) : null);
				double seconds = timer.Elapsed.TotalSeconds;
				output.WriteLine($"{(ahead ? "ahead" : "one at a time")}: {sets * 2} photos, {bytes.Bytes / 1e6:N1} MB in {bytes.Asks} asks, {seconds:N1} s, " +
					$"{bytes.Bytes / 1e6 / seconds:N2} MB/s, at most {bytes.MostAtOnce} asks at once; its looks waited {looks.WaitTicks / (double)Stopwatch.Frequency:N1} s " +
					$"for bytes and worked {looks.WorkTicks / (double)Stopwatch.Frequency:N1} s");
			}
			RemoteFiles.Fetch = null;
		}
	}

	static string Letters(int n) {
		string s = "";
		do { s = (char)('a' + n % 26) + s; n /= 26; } while (n > 0);
		return s;
	}
}
