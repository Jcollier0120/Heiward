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

using HEI.Agent.Estate;
using HEI.Core;
using HEI.Core.Utils;

namespace HEI.Agent.Tests;

/// <summary>
/// A scan of another PC's files over a link with a round trip of tens of ms: the files just ahead of the engine are fetched
/// before it gets to them, several asks at once, within bounds; a video's decoder reading straight on gets bigger asks.
/// </summary>
public sealed partial class RemoteMirrorTests {
	List<FileEntry> Entries(RemoteMirror mirror, params string[] paths) => paths.Select(p => new FileEntry(RemotePath.Mirrored(mirror.Folder, p))).ToList();

	(RemoteMirror Mirror, LocalBytes Bytes) MirrorWith(long aheadBytes, params string[] paths) {
		var bytes = new LocalBytes();
		var mirror = new RemoteMirror(mirrorDir, bytes) { Recent = TimeSpan.Zero, AheadBytes = aheadBytes };
		mirror.Sync(Listing(paths));
		return (mirror, bytes);
	}

	[Fact]
	public void TheFilesJustAhead_AreFetchedBeforeTheEngineGetsToThem_SoItDoesntWaitForThem() {
		string[] photos = Enumerable.Range(0, 6).Select(i => Make($"p{i}.jpg", 300_000 + i, 10 + i)).ToArray();
		string video = Make("v.mp4", 3_000_000, 20);
		var (mirror, bytes) = Mirror(paths: [.. photos, video]);
		using (mirror) {
			List<FileEntry> entries = Entries(mirror, [.. photos, video]);
			mirror.Plan([entries]);
			long expected = photos.Sum(p => new FileInfo(p).Length) + 2L * RemoteMirror.EndBytes; // the photos whole, the video's ends
			Assert.True(SpinWait.SpinUntil(() => mirror.Held >= expected, 10_000), $"held {mirror.Held} of {expected}");
			int asks = bytes.Asks;
			for (int i = 0; i < photos.Length; i++) {
				Assert.True(mirror.Before(entries[i]));
				Assert.Equal(File.ReadAllBytes(photos[i]), File.ReadAllBytes(entries[i].Path));
				mirror.After(entries[i]);
			}
			RemoteFiles.Fetch = mirror.Fetch;
			Assert.True(mirror.Before(entries[^1]));
			Assert.Equal(OsHashUtils.TryCompute(video), OsHashUtils.TryCompute(entries[^1].Path)); // its fingerprint's ends are there
			mirror.After(entries[^1]);
			Assert.Equal(asks, bytes.Asks); // nothing more was asked
			Assert.Equal(0, mirror.Held);
		}
	}

	[Fact]
	public void NoMoreIsFetchedAheadThanItsBound_AndMoreOnceTheEngineTakesSome() {
		string[] photos = Enumerable.Range(0, 10).Select(i => Make($"p{i}.jpg", 50_000, 30 + i)).ToArray();
		var (mirror, _) = MirrorWith(aheadBytes: 120_000, photos);
		using (mirror) {
			List<FileEntry> entries = Entries(mirror, photos);
			mirror.Plan([entries]);
			Assert.True(SpinWait.SpinUntil(() => mirror.Held >= 150_000, 10_000));
			Thread.Sleep(300);
			Assert.Equal(150_000, mirror.Held); // three: the third took it past 120,000
			Assert.True(mirror.Before(entries[0]));
			Assert.True(SpinWait.SpinUntil(() => mirror.Held >= 200_000, 10_000), $"held {mirror.Held}");
			mirror.After(entries[0]);
		}
	}

	[Fact]
	public void AFileFetchedAhead_ThatTheEngineGoesPast_IsEmptied() {
		string[] photos = Enumerable.Range(0, 40).Select(i => Make($"p{i:D2}.jpg", 50_000, 100 + i)).ToArray();
		var (mirror, _) = Mirror(paths: photos);
		using (mirror) {
			List<FileEntry> entries = Entries(mirror, photos);
			mirror.Plan([entries]);
			Assert.True(SpinWait.SpinUntil(() => mirror.Held >= 40 * 50_000L, 10_000), $"held {mirror.Held}");
			// The engine begins the last: the seven more than 32 places before it, never begun, were passed (left out, or known).
			Assert.True(mirror.Before(entries[^1]));
			Assert.True(SpinWait.SpinUntil(() => mirror.Held <= 33 * 50_000L, 10_000), $"held {mirror.Held}");
			Assert.Equal(0, Allocated(entries[0].Path));
			mirror.After(entries[^1]);
		}
	}

	[Fact]
	public void AVideosDecoderReadingStraightOn_GetsBiggerAsks() {
		string v = Make("v.mp4", 6_000_000, 40);
		byte[] original = File.ReadAllBytes(v);
		var (mirror, bytes) = Mirror(paths: [v]);
		using (mirror) {
			using Stream s = mirror.Open(RemotePath.Mirrored(mirror.Folder, v))!;
			var read = new byte[4 * 1024 * 1024];
			for (int at = 0; at < read.Length; at += 64 * 1024) s.ReadExactly(read, at, 64 * 1024); // as FFmpeg's I/O reads
			Assert.Equal(original.AsSpan(0, read.Length).ToArray(), read);
			Assert.Equal(3, bytes.Asks); // 512 KB after the seek to its start, then 2 MB at a time: not eight of 512 KB
		}
	}

	[Fact]
	public void MakingRoom_CountsTheStandInsAsTheyreMade() {
		string[] photos = Enumerable.Range(0, 2_500).Select(i => Path.Combine(src, $"p{i:D4}.jpg")).ToArray();
		foreach (string p in photos) File.WriteAllBytes(p, [1, 2, 3]);
		var calls = new List<(int Done, int Total)>();
		using var mirror = new RemoteMirror(mirrorDir, new LocalBytes());
		mirror.Sync(Listing(photos), (d, t) => calls.Add((d, t)));
		Assert.Equal((0, 2_500), calls[0]);
		Assert.Contains((1_000, 2_500), calls);
		Assert.Contains((2_000, 2_500), calls);
		Assert.Equal((2_500, 2_500), calls[^1]);
	}

	[Fact]
	public void TheTimings_SayWhereTheTimeWent() {
		var t = new RemoteTimings();
		long ms = System.Diagnostics.Stopwatch.Frequency / 1000;
		t.Ask(200 * ms, 300 * ms, 4_000_000);
		t.Ask(100 * ms, 100 * ms, 1_000_000);
		t.Waited(RemoteTimings.For.Whole, 400 * ms);
		t.File(photo: true, 600 * ms, 400 * ms, failed: false);
		t.File(photo: false, 2_000 * ms, 1_500 * ms, failed: true);
		string line = t.Summary("GAMING-010", 0, 1_000, 671_751, "photos decoded 1 × 20 ms", new RemoteTimings.Target(2, 5_000_000, 0, 30, 400, 10));
		Assert.Contains("files 0–1,000 of 671,751", line);
		Assert.Contains("1 photos fetched whole, 1 videos read in parts, 998 with nothing to fetch", line);
		Assert.Contains("Per photo: 600 ms in the engine, 400 ms of it waiting for its bytes", line);
		Assert.Contains("asked → first byte 150 ms, first byte → done 200 ms", line);
		Assert.Contains("On GAMING-010: 2 asks served, per ask opening 0 ms, reading 15 ms, pacing 200 ms, sending 5 ms", line);
		Assert.Contains("Engine steps: photos decoded 1 × 20 ms", line);
		Assert.Contains("No asks", t.Summary("X", 1_000, 1_000, 1_000, null, null)); // a new window each summary
	}

	[Fact]
	public void TheProcessorsCap_IsEightCoresOfTheWholePc() {
		Assert.InRange(Power.MachineProcessors, Environment.ProcessorCount, 4096);
		Assert.Equal(44.4, Math.Round(RemoteScanner.CpuCap(18), 1));
		Assert.Equal(50, RemoteScanner.CpuCap(16));
		Assert.Equal(100, RemoteScanner.CpuCap(8));
	}
}
