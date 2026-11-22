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

using System.Runtime.InteropServices;
using HEI.Agent.Estate;
using HEI.Core.Utils;

namespace HEI.Agent.Tests;

/// <summary>
/// The stand-ins a scan of another PC reads: sparse files of the other PC's sizes and dates, holding only what a read
/// fetched and emptied after, so nothing of the other PC's files stays here; a part that can't be fetched fails the read,
/// never reads as zeros.
/// </summary>
[Collection(AgentHomeCollection.Name)] // RemoteFiles.Fetch is process-wide
public sealed partial class RemoteMirrorTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-mirror-" + Guid.NewGuid().ToString("N")[..8]);
	readonly string src, mirrorDir;

	public RemoteMirrorTests() {
		src = Path.Combine(dir, "src");
		mirrorDir = Path.Combine(dir, "m");
		Directory.CreateDirectory(src);
	}

	public void Dispose() {
		RemoteFiles.Fetch = null;
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>"The other PC": this PC's own files, read as its Heiward would hand them over, counted; or refused.</summary>
	sealed class LocalBytes : IRemoteBytes {
		public long Bytes;
		public int Asks;
		public string? Refuse;

		public int Read(ListedFile file, long offset, byte[] buffer, int count, CancellationToken ct) {
			if (Refuse != null) throw new RemoteReadFailed("refused", Refuse);
			using var fs = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			fs.Position = offset;
			int n = fs.ReadAtLeast(buffer.AsSpan(0, count), count, throwOnEndOfStream: false);
			Interlocked.Add(ref Bytes, n);
			Interlocked.Increment(ref Asks);
			return n;
		}
	}

	string Make(string name, int size, int seed = 1) {
		string path = Path.Combine(src, name);
		var data = new byte[size];
		new Random(seed).NextBytes(data);
		File.WriteAllBytes(path, data);
		File.SetLastWriteTimeUtc(path, new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc).AddSeconds(seed));
		return path;
	}

	RemoteListing Listing(params string[] paths) =>
		RemoteListing.FromKept([(src + "\\", paths.Select(p => { var f = new FileInfo(p); return (p, f.Length, f.LastWriteTimeUtc); }).ToList())], "test");

	(RemoteMirror Mirror, LocalBytes Bytes) Mirror(long cap = RemoteMirror.DefaultCap, params string[] paths) {
		var bytes = new LocalBytes();
		var mirror = new RemoteMirror(mirrorDir, bytes, cap) { Recent = TimeSpan.Zero };
		mirror.Sync(Listing(paths));
		return (mirror, bytes);
	}

	[Fact]
	public void StandIns_HaveTheOtherPcsSizesAndDates_AndTakeNoSpace() {
		string a = Make("a.jpg", 300_000), v = Make("v.mp4", 3_000_000, 2);
		var (mirror, bytes) = Mirror(paths: [a, v]);
		using (mirror) {
			foreach (string p in new[] { a, v }) {
				var stand = new FileInfo(RemotePath.Mirrored(mirror.Folder, p));
				Assert.True(stand.Exists);
				Assert.Equal(new FileInfo(p).Length, stand.Length);
				Assert.Equal(new FileInfo(p).LastWriteTimeUtc, stand.LastWriteTimeUtc);
				Assert.Equal(0, Allocated(stand.FullName));
			}
			Assert.Equal(0, bytes.Bytes);
		}
	}

	[Fact]
	public void APhoto_IsFetchedWholeToBeRead_AndEmptiedAfter_ItsDateKept() {
		string a = Make("a.jpg", 300_000);
		var (mirror, bytes) = Mirror(paths: [a]);
		using (mirror) {
			string stand = RemotePath.Mirrored(mirror.Folder, a);
			var entry = new Core.FileEntry(stand);
			Assert.True(mirror.Before(entry));
			Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(stand));
			Assert.Equal(300_000, mirror.Held);
			Assert.Equal(new FileInfo(a).LastWriteTimeUtc, new FileInfo(stand).LastWriteTimeUtc);

			mirror.After(entry);
			Assert.Equal(0, mirror.Held);
			Assert.Equal(0, Allocated(stand));
			Assert.All(File.ReadAllBytes(stand), b => Assert.Equal(0, b));
			Assert.Equal(new FileInfo(a).LastWriteTimeUtc, new FileInfo(stand).LastWriteTimeUtc);
			Assert.Equal(300_000, bytes.Bytes);
		}
	}

	[Fact]
	public void WhatSortingTheSetsReadsOfAPhoto_IsTakenWhileItsHere_SoItIsntFetchedAgain() {
		string a = Make("a.jpg", 300_000);
		var (mirror, bytes) = Mirror(paths: [a]);
		using (mirror) {
			string stand = RemotePath.Mirrored(mirror.Folder, a);
			var hashes = new ReportBuilder.ContentHashes();
			mirror.WhileHeld = path => {
				var file = new FileInfo(path);
				hashes.Keep(path, file.Length, file.LastWriteTimeUtc, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
			};
			var entry = new Core.FileEntry(stand);
			Assert.True(mirror.Before(entry));
			mirror.After(entry);
			Assert.Equal(0, mirror.Held); // emptied, as before

			// The set's check for plain copies: the photo's own hash, from the stand-in that holds none of it now.
			Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(a))), hashes.Get(stand));
			Assert.Equal(300_000, bytes.Bytes);
			Assert.Equal(1, bytes.Asks);
		}
	}

	[Fact]
	public void AVideo_IsReadThroughTheStream_OnlyThePartsAsked_NothingWrittenHere() {
		string v = Make("v.mp4", 6_000_000, 3);
		byte[] original = File.ReadAllBytes(v);
		var (mirror, bytes) = Mirror(paths: [v]);
		using (mirror) {
			string stand = RemotePath.Mirrored(mirror.Folder, v);
			using (Stream s = mirror.Open(stand)!) {
				Assert.Equal(original.Length, s.Length);
				var head = new byte[100_000];
				s.ReadExactly(head);
				Assert.Equal(original.AsSpan(0, 100_000).ToArray(), head);
				s.Seek(-50_000, SeekOrigin.End);
				var tail = new byte[50_000];
				s.ReadExactly(tail);
				Assert.Equal(original.AsSpan(original.Length - 50_000).ToArray(), tail);
			}
			Assert.InRange(bytes.Bytes, 150_000, 2 * 512 * 1024); // two pieces, not the file
			Assert.Equal(0, mirror.Held);
			Assert.Equal(0, Allocated(stand));
		}
	}

	[Fact]
	public void TheFingerprintsEnds_AreFetchedAlone_AndMatchTheOriginals() {
		string v = Make("v.mp4", 5_000_000, 4);
		var (mirror, bytes) = Mirror(paths: [v]);
		using (mirror) {
			RemoteFiles.Fetch = mirror.Fetch;
			string stand = RemotePath.Mirrored(mirror.Folder, v);
			Assert.Equal(OsHashUtils.TryCompute(v), OsHashUtils.TryCompute(stand));
			Assert.InRange(bytes.Bytes, 2 * 64 * 1024, 3 * 64 * 1024); // its ends, in 64 KB steps
		}
	}

	[Fact]
	public void APhotoEmptiedSince_IsFetchedAgainForTheNextRead_NeverReadAsZeros() {
		string p = Path.Combine(src, "pic.bmp");
		File.WriteAllBytes(p, Bmp(40, 30));
		var (mirror, bytes) = Mirror(paths: [p]);
		using (mirror) {
			RemoteFiles.Fetch = mirror.Fetch;
			string stand = RemotePath.Mirrored(mirror.Folder, p);
			var entry = new Core.FileEntry(stand);
			Assert.True(mirror.Before(entry));
			mirror.After(entry);
			Assert.Equal(0, mirror.Held);
			// The report looks at it again: fetched again by its reader's ask.
			Assert.True(WicImageDecoder.TryGetSize(stand, out int w, out int h));
			Assert.Equal((40, 30), (w, h));
			Assert.Equal(2 * new FileInfo(p).Length, bytes.Bytes);
		}
	}

	[Fact]
	public void PastTheCap_ThoseReadLongestAgoAreEmptied() {
		string[] photos = Enumerable.Range(1, 6).Select(i => Make($"p{i}.jpg", 100_000, i)).ToArray();
		var (mirror, _) = Mirror(cap: 250_000, photos);
		using (mirror) {
			foreach (string p in photos) Assert.True(mirror.Fetch(RemotePath.Mirrored(mirror.Folder, p), 0, -1));
			Assert.InRange(mirror.Held, 100_000, 250_000);
			Assert.True(mirror.MostHeld <= 300_000);
			Assert.Equal(0, Allocated(RemotePath.Mirrored(mirror.Folder, photos[0])));
			Assert.Equal(File.ReadAllBytes(photos[^1]), File.ReadAllBytes(RemotePath.Mirrored(mirror.Folder, photos[^1])));
		}
	}

	[Fact]
	public void AFileThatCantBeFetched_FailsItsRead_AndIsNoted() {
		string a = Make("a.jpg", 200_000), v = Make("v.mov", 2_000_000, 2), b = Make("b.jpg", 100_000, 3);
		var (mirror, bytes) = Mirror(paths: [a, v, b]);
		using (mirror) {
			bytes.Refuse = "changed";
			string standA = RemotePath.Mirrored(mirror.Folder, a), standV = RemotePath.Mirrored(mirror.Folder, v);
			Assert.False(mirror.Before(new Core.FileEntry(standA)));
			Assert.Equal("changed", mirror.Failed[standA]);
			using Stream s = mirror.Open(standV)!;
			Assert.ThrowsAny<IOException>(() => s.ReadExactly(new byte[1000]));
			Assert.Equal("changed", mirror.Failed[standV]);

			// One that changed isn't asked for again this scan; another tells that the loan is over.
			bytes.Refuse = null;
			Assert.False(mirror.Fetch(standA, 0, -1));
			bytes.Refuse = "over";
			Assert.False(mirror.Fetch(RemotePath.Mirrored(mirror.Folder, b), 0, -1));
			Assert.True(mirror.Over);
		}
	}

	[Fact]
	public void ThisPcsOwnFiles_AreReadAsTheyAre() {
		string mine = Make("mine.jpg", 1000);
		var (mirror, bytes) = Mirror(paths: []);
		using (mirror) {
			Assert.True(mirror.Fetch(mine, 0, -1));
			Assert.Null(mirror.Open(mine));
			Assert.False(mirror.IsRemote(mine));
			Assert.Equal(0, bytes.Asks);
		}
	}

	[Fact]
	public void Sync_RemakesChangedStandIns_AndDropsThoseNoLongerListed() {
		string a = Make("a.jpg", 1000), b = Make("b.jpg", 2000, 2);
		var (mirror, _) = Mirror(paths: [a, b]);
		mirror.EmptyAll();
		File.WriteAllBytes(a, new byte[1500]);
		File.Delete(b);
		string c = Make("c.jpg", 3000, 3);
		var again = new RemoteMirror(mirrorDir, new LocalBytes());
		var (added, changed, gone) = again.Sync(Listing(a, c));
		Assert.Equal((1, 1, 1), (added, changed, gone));
		Assert.Equal(1500, new FileInfo(RemotePath.Mirrored(again.Folder, a)).Length);
		Assert.False(File.Exists(RemotePath.Mirrored(again.Folder, b)));
		Assert.Equal(a, again.Original(RemotePath.Mirrored(again.Folder, a)));
	}

	/// <summary>A small 24-bit BMP of a gradient.</summary>
	static byte[] Bmp(int w, int h) {
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
				int at = 54 + y * row + x * 3;
				b[at] = (byte)(x * 6); b[at + 1] = (byte)(y * 8); b[at + 2] = 128;
			}
		return b;
	}

	/// <summary>The disk space a file takes (a sparse one: only what's written).</summary>
	static long Allocated(string path) {
		uint low = GetCompressedFileSizeW(path, out uint high);
		return ((long)high << 32) | low;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	static extern uint GetCompressedFileSizeW(string path, out uint high);
}
