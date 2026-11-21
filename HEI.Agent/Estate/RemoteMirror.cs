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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using HEI.Core;
using HEI.Core.Utils;
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent.Estate {
	/// <summary>A read of another PC's file that couldn't be done; <see cref="Code"/> as its Heiward said why (changed, cloud, unlisted, over), or null.</summary>
	sealed class RemoteReadFailed(string message, string? code = null, Exception? inner = null) : IOException(message, inner) {
		public string? Code { get; } = code;
	}

	/// <summary>Where another PC's bytes come from: its Heiward over the Estate (<see cref="EstateBytes"/>), or, in tests, this PC's own files.</summary>
	interface IRemoteBytes {
		/// <summary>
		/// Reads <paramref name="count"/> bytes of <paramref name="file"/> (as that PC listed it) from <paramref name="offset"/>
		/// into <paramref name="buffer"/>: all of them, fewer only where the file ends. Throws <see cref="RemoteReadFailed"/>,
		/// with code "changed" when the file there isn't as it was listed.
		/// </summary>
		int Read(ListedFile file, long offset, byte[] buffer, int count, CancellationToken ct);
	}

	/// <summary>
	/// Another PC's bytes over the Estate: <c>GET /api/peer/scan/bytes?path=…</c> on that PC's Heiward (the kit's PeerFiles,
	/// <see cref="PeerFiles"/>), one range at a time, as the spec's asking side does:
	/// <list type="bullet">
	/// <item>Its ETag must be the listing's (size and time): a file changed since it was listed isn't read, and waits for the next scan.</item>
	/// <item>While a game is played there, it's told 503 and waits <c>retryMs</c>; a reply cut short (a game started) is asked
	/// again from the byte after the last that came.</item>
	/// <item>A link that drops, or a Manor that can't reach the other PC just now, is tried again, three times in a row.</item>
	/// </list>
	/// </summary>
	sealed class EstateBytes(string pc, Action<string?>? waiting = null) : IRemoteBytes {
		long fetched;
		int asks;

		/// <summary>Bytes fetched so far.</summary>
		public long Fetched => Interlocked.Read(ref fetched);
		/// <summary>Ranges asked for so far.</summary>
		public int Asks => Volatile.Read(ref asks);

		public int Read(ListedFile file, long offset, byte[] buffer, int count, CancellationToken ct) {
			if (count <= 0) return 0;
			string route = EstateLink.PeerPath(pc, "scan/bytes?path=" + Uri.EscapeDataString(file.Path));
			string tag = PeerFiles.Tag(file.Size, PeerFiles.MsOf(file.ModifiedUtc));
			int got = 0, tries = 0;
			while (got < count) {
				ct.ThrowIfCancellationRequested();
				try {
					long from = offset + got;
					using HttpResponseMessage r = EstateLink.Ask(route, ct, new RangeHeaderValue(from, offset + count - 1));
					int status = (int)r.StatusCode;
					if (status is 200 or 206) {
						waiting?.Invoke(null);
						Interlocked.Increment(ref asks);
						if (r.Headers.ETag?.Tag is string etag && etag != tag) throw new RemoteReadFailed("it changed since it was listed", "changed");
						long expected = r.Content.Headers.ContentLength ?? -1;
						using Stream body = r.Content.ReadAsStream(ct);
						if (status == 200 && from > 0) Skip(body, from); // the whole file, from its start
						int n = 0;
						bool cut = false;
						try {
							while (got + n < count) {
								int k = body.Read(buffer, got + n, count - got - n);
								if (k <= 0) break;
								n += k;
							}
						}
						catch (IOException) when (!ct.IsCancellationRequested) {
							cut = true; // the reply ended short of its Content-Length
						}
						got += n;
						Interlocked.Add(ref fetched, n);
						if (n > 0) tries = 0;
						if (!cut && status == 206 && expected >= 0 && n >= expected && got < count) return got; // the file ends there
						if (!cut && got >= count) return got;
						if (!cut && status == 200) return got;
						if (++tries >= 6) throw new RemoteReadFailed("its replies kept ending short", null);
						continue; // asked again from the byte after the last that came
					}
					string? code = CodeOf(r, ct, out long? retryMs);
					if (status == 503 && code == "game") {
						waiting?.Invoke("game");
						TimeSpan wait = TimeSpan.FromMilliseconds(Math.Clamp(retryMs ?? PeerFiles.GameKeepMs, 5_000, 120_000));
						ct.WaitHandle.WaitOne(wait);
						continue;
					}
					if (status == 416) return got; // past its end
					if (status == 404) throw new RemoteReadFailed("it isn't there any more", "gone");
					if (status is 403 or 410) throw new RemoteReadFailed("that PC isn't offering its files to this one now", "over");
					if (status >= 500 && ++tries < 4) { // Manor couldn't reach it just now
						ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Pow(3, tries - 1)));
						continue;
					}
					throw new RemoteReadFailed($"the other PC answered {status}", code);
				}
				catch (EstateMissing e) {
					throw new RemoteReadFailed(e.Message, "over", e);
				}
				catch (Exception e) when ((e is HttpRequestException || e is TaskCanceledException || e is IOException && e is not RemoteReadFailed) && !ct.IsCancellationRequested) {
					if (++tries >= 4) throw new RemoteReadFailed("the link to the other PC dropped", null, e);
					ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Pow(3, tries - 1)));
				}
			}
			return got;
		}

		static void Skip(Stream body, long bytes) {
			byte[] scratch = new byte[64 * 1024];
			while (bytes > 0) {
				int n = body.Read(scratch, 0, (int)Math.Min(scratch.Length, bytes));
				if (n <= 0) return;
				bytes -= n;
			}
		}

		/// <summary>The <c>code</c> of a refusal's JSON (<c>{"error":…,"code":…}</c>), or null; and a game's <c>retryMs</c>.</summary>
		static string? CodeOf(HttpResponseMessage r, CancellationToken ct, out long? retryMs) {
			retryMs = null;
			try {
				using Stream body = r.Content.ReadAsStream(ct);
				byte[] head = new byte[4096];
				int n = body.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
				using JsonDocument doc = JsonDocument.Parse(head.AsMemory(0, n));
				if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
				if (doc.RootElement.TryGetProperty("retryMs", out JsonElement ms) && ms.TryGetInt64(out long wait)) retryMs = wait;
				return EstateLink.Str(doc.RootElement, "code");
			}
			catch (Exception e) when (e is JsonException or IOException or HttpRequestException) {
				return null;
			}
		}
	}

	/// <summary>
	/// Another PC's photos and videos as this PC's scan of them sees them: under <see cref="Folder"/>, a stand-in for each
	/// (<c>…\mirror\C\Photos\a.jpg</c> for its <c>C:\Photos\a.jpg</c>), a sparse file of its size and dates holding none of
	/// its bytes. Only what a read needs is fetched, and nothing of it stays:
	/// <list type="bullet">
	/// <item>A photo is fetched whole as it's read (<see cref="Before"/>; a photo can't be decoded from part of it), and
	/// emptied once read (<see cref="After"/>).</item>
	/// <item>A video is read through <see cref="Open"/>, a stream that fetches the parts its decoder asks for (its header
	/// and the frame sampled), kept in memory and never written here; its first and last 64 KB, the scan's fingerprint of
	/// it, go into its stand-in.</item>
	/// <item>Every reader that opens a file by its path asks <see cref="Fetch"/> first (<see cref="RemoteFiles"/>), so a
	/// photo the report looks at again is fetched again rather than read as zeros.</item>
	/// <item>At most <see cref="Cap"/> of fetched bytes are held at a time: past it, those read longest ago are emptied.</item>
	/// </list>
	/// </summary>
	sealed partial class RemoteMirror : IDisposable {
		/// <summary>How much of the other PC's files may be held here at once: a few hundred photos.</summary>
		public const long DefaultCap = 1L << 30;
		/// <summary>The most fetched whole: a photo, or a small video read by its path. A bigger file is read only through <see cref="Open"/>.</summary>
		public const long MaxWhole = 256L << 20;
		const int Align = 64 * 1024;
		const int MaxPiece = 4 * 1024 * 1024;

		/// <summary>A file read this recently isn't emptied to make room: its reader may not be done with it.</summary>
		internal TimeSpan Recent { get; init; } = TimeSpan.FromSeconds(30);

		readonly IRemoteBytes source;
		readonly ConcurrentDictionary<string, Stand> stands = new(StringComparer.OrdinalIgnoreCase);
		readonly ConcurrentDictionary<Stand, byte> holding = new();
		readonly object trimming = new();
		long held, mostHeld;

		public RemoteMirror(string folder, IRemoteBytes source, long cap = DefaultCap) {
			Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
			this.source = source;
			Cap = cap;
		}

		/// <summary>Where the stand-ins are.</summary>
		public string Folder { get; }
		public long Cap { get; }
		/// <summary>The scan stops: a fetch under way gives up.</summary>
		public CancellationToken Stopping { get; set; }
		/// <summary>Bytes of the other PC's files held here now, and the most held at once.</summary>
		public long Held => Interlocked.Read(ref held);
		public long MostHeld => Interlocked.Read(ref mostHeld);
		/// <summary>Stand-ins whose bytes couldn't be fetched, and why: the scan leaves them for its next round.</summary>
		public ConcurrentDictionary<string, string> Failed { get; } = new(StringComparer.OrdinalIgnoreCase);
		/// <summary>The other PC ended its scan (or Castellan stopped here): nothing more can be fetched.</summary>
		public bool Over { get; private set; }

		/// <summary>A file of the other PC's, as this PC holds it.</summary>
		sealed class Stand(ListedFile file, string path) {
			public readonly ListedFile File = file;
			public readonly string Path = path;
			public readonly bool IsPhoto = FileUtils.IsImageFile(file.Path);
			public readonly object Gate = new();
			/// <summary>The ranges fetched into the stand-in, [From, To), in order and apart.</summary>
			public readonly List<(long From, long To)> Filled = new();
			public long Bytes;
			public long Used;
			public int Readers;
		}

		/// <summary>Whether <paramref name="path"/> is one of the stand-ins (or under their folder).</summary>
		public bool IsRemote(string path) => path.StartsWith(Folder + "\\", StringComparison.OrdinalIgnoreCase);

		/// <summary>The other PC's path of a stand-in.</summary>
		public string? Original(string path) => stands.TryGetValue(path, out Stand? s) ? s.File.Path : RemotePath.Original(Folder, path);

		/// <summary>NTFS and ReFS can hold sparse files; FAT and exFAT can't, and a stand-in there would take its full size.</summary>
		public static bool CanHold(string folder) {
			try {
				string? root = Path.GetPathRoot(Path.GetFullPath(folder));
				return root != null && new DriveInfo(root).DriveFormat is "NTFS" or "ReFS";
			}
			catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) {
				return false;
			}
		}

		string CleanMark => Folder + ".empty";

		/// <summary>
		/// Makes the stand-ins the listing's: one for each file listed, of its size and dates; one whose file changed is made
		/// again; one no longer listed goes. Every stand-in is empty after (a scan stopped short may have left some held).
		/// </summary>
		public (int Added, int Changed, int Gone) Sync(RemoteListing listing) {
			Directory.CreateDirectory(Folder);
			try { new DirectoryInfo(Folder).Attributes |= FileAttributes.NotContentIndexed; }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			bool clean = File.Exists(CleanMark);
			TryDelete(CleanMark);
			stands.Clear();
			holding.Clear();
			Interlocked.Exchange(ref held, 0);
			var want = new Dictionary<string, ListedFile>(StringComparer.OrdinalIgnoreCase);
			foreach (ListedRoot root in listing.Roots)
				foreach (ListedFile f in root.Files)
					want[RemotePath.Mirrored(Folder, f.Path)] = f;
			int added = 0, changed = 0, gone = 0;
			foreach (string p in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
				if (!want.ContainsKey(p) && TryDelete(p)) gone++;
			foreach (var (p, f) in want) {
				var info = new FileInfo(p);
				if (info.Exists && info.Length == f.Size && info.LastWriteTimeUtc.Ticks == f.ModifiedTicks) {
					if (!clean) Zero(p, f);
				}
				else {
					if (info.Exists) changed++;
					else added++;
					Create(p, f);
				}
				stands[p] = new Stand(f, p);
			}
			RemoveEmptyFolders(Folder);
			return (added, changed, gone);
		}

		/// <summary>
		/// <see cref="Core.ScanEngine.BeforeRead"/>: a photo the scan is to decode is fetched whole (one it knows already, and
		/// won't decode, isn't); a video is fetched as its decoder reads it. False: it couldn't be, and it waits for the next scan.
		/// </summary>
		public bool Before(FileEntry entry) {
			if (!stands.TryGetValue(entry.Path, out Stand? s)) return !IsRemote(entry.Path);
			Interlocked.Increment(ref s.Readers);
			bool ok = !s.IsPhoto || !ScanEngine.ImageNeedsDecode(entry) || Fetch(entry.Path, 0, -1);
			if (!ok) Interlocked.Decrement(ref s.Readers);
			return ok;
		}

		/// <summary><see cref="Core.ScanEngine.AfterRead"/>: the scan is done with the file, so what was fetched of it goes.</summary>
		public void After(FileEntry entry) {
			if (!stands.TryGetValue(entry.Path, out Stand? s)) return;
			if (Interlocked.Decrement(ref s.Readers) > 0) return;
			if (s.IsPhoto && WhileHeld is { } take && IsFilled(s, 0, s.File.Size)) {
				try { take(s.Path); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			}
			Empty(s);
		}

		/// <summary>
		/// Called with a photo's stand-in while all of it is here, just before it's emptied: what a later step would fetch it
		/// again for is taken now (<see cref="RemoteScanner"/>: its hash, and whether it has a capture date, for the sets).
		/// </summary>
		public Action<string>? WhileHeld { get; set; }

		/// <summary>
		/// <see cref="RemoteFiles.Fetch"/>: bytes <paramref name="offset"/> to <paramref name="offset"/> + <paramref name="count"/>
		/// of a stand-in (-1: all of it) fetched into it, if they aren't there. True for a file that isn't one; false when
		/// they couldn't be fetched, or it's too big to fetch whole.
		/// </summary>
		public bool Fetch(string path, long offset, long count) {
			if (!stands.TryGetValue(path, out Stand? s)) return !IsRemote(path);
			long size = s.File.Size;
			if (count < 0) {
				if (size > MaxWhole) {
					Failed.TryAdd(path, "too big to fetch whole");
					return false;
				}
				offset = 0;
				count = size;
			}
			long from = Math.Max(0, offset), to = Math.Min(size, offset + count);
			if (to <= from) return true;
			try {
				lock (s.Gate) {
					s.Used = Stopwatch.GetTimestamp();
					foreach (var (a, b) in Missing(s, from / Align * Align, Math.Min(size, (to + Align - 1) / Align * Align)))
						FillRange(s, a, b);
				}
				Trim();
				return true;
			}
			catch (RemoteReadFailed e) {
				Failed[path] = e.Code ?? e.Message;
				if (e.Code == "over") Over = true;
				return false;
			}
			catch (OperationCanceledException) {
				return false;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Failed[path] = "this PC couldn't hold it: " + e.Message;
				return false;
			}
		}

		/// <summary>
		/// <see cref="Core.FFTools.FFmpegNative.RemoteMedia"/>'s stream of a stand-in's bytes: a photo fetched whole and read
		/// from its stand-in; a video's parts fetched as they're read, held in memory only. Null for a file that isn't one.
		/// </summary>
		public Stream? Open(string path) => stands.TryGetValue(path, out Stand? s) ? new RemoteStream(this, s) : null;

		/// <summary>Every stand-in emptied (the scan is over), and marked so the next scan needn't check.</summary>
		public void EmptyAll() {
			foreach (Stand s in holding.Keys) Empty(s);
			try { File.WriteAllText(CleanMark, ""); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		public void Dispose() => EmptyAll();

		/// <summary>The parts of [a, b) not fetched yet.</summary>
		static List<(long, long)> Missing(Stand s, long a, long b) {
			var gaps = new List<(long, long)>();
			long at = a;
			foreach (var (from, to) in s.Filled) {
				if (to <= at) continue;
				if (from >= b) break;
				if (from > at) gaps.Add((at, Math.Min(from, b)));
				at = Math.Max(at, to);
				if (at >= b) break;
			}
			if (at < b) gaps.Add((at, b));
			return gaps;
		}

		bool IsFilled(Stand s, long a, long b) {
			lock (s.Gate) return Missing(s, a, b).Count == 0;
		}

		void FillRange(Stand s, long a, long b) {
			byte[] buffer = new byte[(int)Math.Min(MaxPiece, b - a)];
			using (var fs = new FileStream(s.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1)) {
				for (long at = a; at < b;) {
					int want = (int)Math.Min(buffer.Length, b - at);
					int n = source.Read(s.File, at, buffer, want, Stopping);
					if (n < want) throw new RemoteReadFailed("it ended early", "changed");
					fs.Position = at;
					fs.Write(buffer, 0, n);
					Mark(s, at, at + n);
					at += n;
				}
			}
			Stamp(s.Path, s.File); // writing moved its date: back to the other PC's, which the scan compares
		}

		/// <summary>[a, b) is fetched: added to the stand's ranges, merged with its neighbours.</summary>
		void Mark(Stand s, long a, long b) {
			long before = s.Bytes;
			var ranges = s.Filled;
			ranges.Add((a, b));
			ranges.Sort((x, y) => x.From.CompareTo(y.From));
			var merged = new List<(long From, long To)>(ranges.Count);
			foreach (var r in ranges) {
				if (merged.Count > 0 && r.From <= merged[^1].To) merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, r.To));
				else merged.Add(r);
			}
			ranges.Clear();
			ranges.AddRange(merged);
			s.Bytes = merged.Sum(r => r.To - r.From);
			long now = Interlocked.Add(ref held, s.Bytes - before);
			holding[s] = 0;
			long most;
			while (now > (most = Interlocked.Read(ref mostHeld)) && Interlocked.CompareExchange(ref mostHeld, now, most) != most) { }
		}

		void Empty(Stand s) {
			lock (s.Gate) {
				if (s.Bytes == 0) return;
				try { Zero(s.Path, s.File); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return; // open elsewhere just now: emptied by the next trim, or at the end
				}
				Interlocked.Add(ref held, -s.Bytes);
				s.Bytes = 0;
				s.Filled.Clear();
				holding.TryRemove(s, out _);
			}
		}

		/// <summary>Past <see cref="Cap"/>: the files read longest ago, and not being read, are emptied, down to three quarters of it.</summary>
		void Trim() {
			if (Held <= Cap) return;
			lock (trimming) {
				long now = Stopwatch.GetTimestamp();
				foreach (Stand s in holding.Keys.Where(s => Volatile.Read(ref s.Readers) == 0 && Stopwatch.GetElapsedTime(s.Used, now) > Recent).OrderBy(s => s.Used).ToList()) {
					if (Held <= Cap / 4 * 3) break;
					Empty(s);
				}
			}
		}

		static void Create(string path, ListedFile f) {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1)) {
				SetSparse(fs.SafeFileHandle);
				fs.SetLength(f.Size);
				ZeroAll(fs); // NTFS keeps the cluster the file now ends in: given back too
			}
			Stamp(path, f);
		}

		/// <summary>All of a stand-in's bytes zeroed and their space given back (it stays sparse), its dates the other PC's.</summary>
		static void Zero(string path, ListedFile f) {
			using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1))
				ZeroAll(fs);
			Stamp(path, f);
		}

		static void ZeroAll(FileStream fs) {
			var zero = new ZeroData { FileOffset = 0, BeyondFinalZero = fs.Length };
			if (zero.BeyondFinalZero > 0 && !DeviceIoControl(fs.SafeFileHandle, FsctlSetZeroData, ref zero, Marshal.SizeOf<ZeroData>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
				throw new IOException($"couldn't empty it (error {Marshal.GetLastPInvokeError()})");
		}

		static void Stamp(string path, ListedFile f) {
			File.SetLastWriteTimeUtc(path, f.ModifiedUtc);
			File.SetCreationTimeUtc(path, f.ModifiedUtc);
		}

		static void SetSparse(SafeFileHandle handle) {
			if (!DeviceIoControlNoInput(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
				throw new IOException($"this drive can't hold sparse files (error {Marshal.GetLastPInvokeError()})");
		}

		static bool TryDelete(string path) {
			try {
				if (!File.Exists(path)) return false;
				File.Delete(path);
				return true;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return false;
			}
		}

		static void RemoveEmptyFolders(string folder) {
			try {
				foreach (string sub in Directory.EnumerateDirectories(folder)) {
					RemoveEmptyFolders(sub);
					if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		const uint FsctlSetSparse = 0x000900C4, FsctlSetZeroData = 0x000980C8;

		[StructLayout(LayoutKind.Sequential)]
		struct ZeroData {
			public long FileOffset, BeyondFinalZero;
		}

		[LibraryImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool DeviceIoControl(SafeFileHandle device, uint code, ref ZeroData input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

		[LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool DeviceIoControlNoInput(SafeFileHandle device, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

		/// <summary>
		/// A stand-in's bytes for a decoder or a hash: a photo's fetched whole into its stand-in and read from there (held
		/// while the stream is open); a video's fetched in 512 KB pieces as they're read, the last eight kept in memory.
		/// A part that can't be fetched fails the read (an IOException), never reads as zeros.
		/// </summary>
		sealed class RemoteStream : Stream {
			const int Piece = 512 * 1024, Keep = 8;
			readonly RemoteMirror mirror;
			readonly Stand stand;
			readonly bool whole;
			readonly LinkedList<(long At, byte[] Data)> pieces = new();
			FileStream? file;
			long position;
			bool disposed;

			public RemoteStream(RemoteMirror mirror, Stand stand) {
				this.mirror = mirror;
				this.stand = stand;
				Interlocked.Increment(ref stand.Readers);
				whole = stand.IsPhoto && stand.File.Size <= MaxWhole && mirror.Fetch(stand.Path, 0, -1);
			}

			public override bool CanRead => !disposed;
			public override bool CanSeek => !disposed;
			public override bool CanWrite => false;
			public override long Length => stand.File.Size;
			public override long Position { get => position; set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }

			public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

			public override int Read(Span<byte> into) {
				ObjectDisposedException.ThrowIf(disposed, this);
				long size = stand.File.Size;
				if (position >= size || into.Length == 0) return 0;
				int want = (int)Math.Min(into.Length, size - position);
				if (whole || mirror.IsFilled(stand, position, position + want)) {
					file ??= new FileStream(stand.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
					file.Position = position;
					int read = file.Read(into[..want]);
					position += read;
					return read;
				}
				long at = position / Piece * Piece;
				byte[] data = PieceAt(at);
				int n = (int)Math.Min(want, data.Length - (position - at));
				if (n <= 0) return 0;
				data.AsSpan((int)(position - at), n).CopyTo(into);
				position += n;
				return n;
			}

			byte[] PieceAt(long at) {
				for (var node = pieces.First; node != null; node = node.Next)
					if (node.Value.At == at) {
						pieces.Remove(node);
						pieces.AddFirst(node);
						return node.Value.Data;
					}
				int length = (int)Math.Min(Piece, stand.File.Size - at);
				byte[] data = new byte[length];
				try {
					int got = mirror.source.Read(stand.File, at, data, length, mirror.Stopping);
					if (got < length) throw new RemoteReadFailed("it ended early", "changed");
				}
				catch (RemoteReadFailed e) {
					mirror.Failed[stand.Path] = e.Code ?? e.Message;
					if (e.Code == "over") mirror.Over = true;
					throw;
				}
				pieces.AddFirst((at, data));
				if (pieces.Count > Keep) pieces.RemoveLast();
				return data;
			}

			public override long Seek(long offset, SeekOrigin origin) =>
				Position = origin switch {
					SeekOrigin.Begin => offset,
					SeekOrigin.Current => position + offset,
					_ => stand.File.Size + offset,
				};

			public override void Flush() { }
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

			protected override void Dispose(bool disposing) {
				if (!disposed) {
					disposed = true;
					file?.Dispose();
					pieces.Clear();
					Interlocked.Decrement(ref stand.Readers);
					mirror.Trim();
				}
				base.Dispose(disposing);
			}
		}
	}
}
