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
using HEI.Core.FFTools.FFmpegNative;
using HEI.Core.Utils;
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent.Estate {
	/// <summary>A read of another PC's file that couldn't be done; <see cref="Code"/> as its Heiward said why (changed, cloud, unlisted, over), or null.</summary>
	sealed class RemoteReadFailed(string message, string? code = null, Exception? inner = null) : IOException(message, inner) {
		public string? Code { get; } = code;
	}

	/// <summary>A piece of a file as it comes: where in the file it starts, and its bytes.</summary>
	delegate void BytesCame(long at, ReadOnlySpan<byte> bytes);

	/// <summary>Where another PC's bytes come from: its Heiward over the Estate (<see cref="EstateBytes"/>), or, in tests, this PC's own files.</summary>
	interface IRemoteBytes {
		/// <summary>
		/// Reads <paramref name="count"/> bytes of <paramref name="file"/> (as that PC listed it) from <paramref name="offset"/>
		/// into <paramref name="buffer"/>: all of them, fewer only where the file ends. Throws <see cref="RemoteReadFailed"/>,
		/// with code "changed" when the file there isn't as it was listed.
		/// </summary>
		int Read(ListedFile file, long offset, byte[] buffer, int count, CancellationToken ct);

		/// <summary>
		/// <paramref name="count"/> bytes of <paramref name="file"/> from <paramref name="offset"/>, handed to
		/// <paramref name="came"/> in order as they come, in one ask where the source can (a photo whole, not a piece at a
		/// time): how many came, fewer only where the file ends. Throws as <see cref="Read"/> does.
		/// </summary>
		long Stream(ListedFile file, long offset, long count, BytesCame came, CancellationToken ct) {
			byte[] buffer = new byte[(int)Math.Min(4 * 1024 * 1024, Math.Max(1, count))];
			long got = 0;
			while (got < count) {
				int want = (int)Math.Min(buffer.Length, count - got);
				int n = Read(file, offset + got, buffer, want, ct);
				if (n > 0) came(offset + got, buffer.AsSpan(0, n));
				got += n;
				if (n < want) break;
			}
			return got;
		}
	}

	/// <summary>
	/// Another PC's bytes over the Estate: <c>GET /api/peer/scan/bytes?path=…</c> on that PC's Heiward (the kit's PeerFiles,
	/// <see cref="PeerFiles"/>), a range in one ask, as the spec's asking side does:
	/// <list type="bullet">
	/// <item>Its ETag must be the listing's (size and time): a file changed since it was listed isn't read, and waits for the next scan.</item>
	/// <item>While a game is played there, it's told 503 and waits <c>retryMs</c>; a reply cut short (a game started) is asked
	/// again from the byte after the last that came.</item>
	/// <item>A link that drops, or a Manor that can't reach the other PC just now, is tried again, three times in a row.</item>
	/// </list>
	/// Each ask is timed (<see cref="RemoteTimings"/>): asked → its head → its last byte. <paramref name="route"/>: the route
	/// asked, <c>scan/bytes</c> for the files listed, another for a scan's records (<see cref="ScanRecords"/>).
	/// </summary>
	sealed class EstateBytes(string pc, Action<string?>? waiting = null, RemoteTimings? timings = null, string route = "scan/bytes") : IRemoteBytes {
		long fetched;
		int asks;

		/// <summary>Bytes fetched so far.</summary>
		public long Fetched => Interlocked.Read(ref fetched);
		/// <summary>Ranges asked for so far.</summary>
		public int Asks => Volatile.Read(ref asks);

		public int Read(ListedFile file, long offset, byte[] buffer, int count, CancellationToken ct) =>
			count <= 0 ? 0 : (int)Stream(file, offset, count, (at, bytes) => bytes.CopyTo(buffer.AsSpan((int)(at - offset))), ct);

		public long Stream(ListedFile file, long offset, long count, BytesCame came, CancellationToken ct) {
			if (count <= 0) return 0;
			string ask = EstateLink.PeerPath(pc, route + "?path=" + Uri.EscapeDataString(file.Path));
			string tag = PeerFiles.Tag(file.Size, PeerFiles.MsOf(file.ModifiedUtc));
			long got = 0;
			int tries = 0;
			byte[]? buffer = null;
			try {
				while (got < count) {
					ct.ThrowIfCancellationRequested();
					try {
						long from = offset + got;
						long asked = Stopwatch.GetTimestamp();
						using HttpResponseMessage r = EstateLink.Ask(ask, ct, new RangeHeaderValue(from, offset + count - 1));
						long head = Stopwatch.GetTimestamp();
						int status = (int)r.StatusCode;
						if (status is 200 or 206) {
							waiting?.Invoke(null);
							Interlocked.Increment(ref asks);
							if (r.Headers.ETag?.Tag is string etag && etag != tag) throw new RemoteReadFailed("it changed since it was listed", "changed");
							long expected = r.Content.Headers.ContentLength ?? -1;
							using Stream body = r.Content.ReadAsStream(ct);
							if (status == 200 && from > 0) Skip(body, from); // the whole file, from its start
							buffer ??= System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Min(256 * 1024, count));
							long n = 0;
							bool cut = false;
							try {
								while (got + n < count) {
									int k = body.Read(buffer, 0, (int)Math.Min(buffer.Length, count - got - n));
									if (k <= 0) break;
									came(from + n, buffer.AsSpan(0, k));
									n += k;
								}
							}
							catch (IOException) when (!ct.IsCancellationRequested) {
								cut = true; // the reply ended short of its Content-Length
							}
							got += n;
							Interlocked.Add(ref fetched, n);
							timings?.Ask(head - asked, Stopwatch.GetTimestamp() - head, n);
							if (n > 0) tries = 0;
							if (!cut && status == 206 && expected >= 0 && n >= expected && got < count) return got; // the file ends there
							if (!cut && got >= count) return got;
							if (!cut && status == 200) return got;
							if (++tries >= 6) throw new RemoteReadFailed("its replies kept ending short", null);
							timings?.Retried();
							continue; // asked again from the byte after the last that came
						}
						timings?.Refused();
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
						timings?.Retried();
						ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Pow(3, tries - 1)));
					}
				}
				return got;
			}
			finally {
				if (buffer != null) System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
			}
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
	/// Another PC's photos and videos as this PC's scan of them sees them: under <see cref="Folder"/>, a path for each, its
	/// stand-in (<c>…\mirror\C\Photos\a.jpg</c> for its <c>C:\Photos\a.jpg</c>), of the size and dates it was listed with
	/// (<see cref="About"/>, for <see cref="RemoteFiles.About"/>). A stand-in is a file here only while bytes of it are: made
	/// when they're fetched, deleted once they're let go, so the folder holds no more files than the scan holds at once
	/// (<see cref="Cap"/>), and none once it's over (<see cref="EmptyAll"/>). Only what a read needs is fetched:
	/// <list type="bullet">
	/// <item>A photo is fetched whole as it's read (<see cref="Before"/>; a photo can't be decoded from part of it), and
	/// let go once read (<see cref="After"/>).</item>
	/// <item>A video is read through <see cref="Open"/>, a stream that fetches the parts its decoder asks for (its header
	/// and the frame sampled), kept in memory and never written here; its first and last 64 KB, the scan's fingerprint of
	/// it, go into its stand-in, a sparse file of its size holding only those.</item>
	/// <item>Every reader that opens a file by its path asks <see cref="Fetch"/> first (<see cref="RemoteFiles"/>), so a
	/// photo the report looks at again is fetched again rather than found missing.</item>
	/// <item>At most <see cref="Cap"/> of fetched bytes are held at a time: past it, those read longest ago are let go.</item>
	/// </list>
	/// Heiward 1.11.0 to 1.11.5 kept a sparse stand-in of every file listed, and never removed them: 670,000 files on one
	/// PC. What an earlier scan left in the folder is moved aside whole as the scan begins (<see cref="SetAside"/>), and
	/// deleted in the background (<see cref="RemoteHomes"/>).
	/// </summary>
	sealed partial class RemoteMirror : IDisposable {
		/// <summary>How much of the other PC's files may be held here at once: a few hundred photos.</summary>
		public const long DefaultCap = 1L << 30;
		/// <summary>The most fetched whole: a photo, or a small video read by its path. A bigger file is read only through <see cref="Open"/>.</summary>
		public const long MaxWhole = 256L << 20;
		const int Align = 64 * 1024;

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
			/// <summary>Its file is here: made with the first bytes fetched into it, deleted when they're let go (<see cref="Empty"/>).</summary>
			public bool OnDisk;
			public long Bytes;
			public long Used;
			public int Readers;
			/// <summary>For the timings: when its first reader began, and how long its reads waited for bytes since.</summary>
			public long Began, Waited;
			/// <summary>Where it is in the files fetched ahead (<see cref="Plan"/>): its drive's lane and place, -1 for none; what it costs ahead; whether the engine has begun it.</summary>
			public int Lane, PlanAt = -1;
			public long AheadCost;
			public bool Begun;
		}

		/// <summary>The fetching ahead's own threads, whose waits aren't the engine's.</summary>
		[ThreadStatic] static bool aheadThread;

		/// <summary>A reason a file's bytes didn't come that asking again won't change (this scan): it went, it changed, the loan is over, it's too big.</summary>
		static bool Final(string why) => why is "gone" or "changed" or "over" or "not-offered" or "too big to fetch whole";

		/// <summary>Where the scan's time goes, for its log (null: not kept).</summary>
		public RemoteTimings? Timings { get; init; }

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

		/// <summary>Added to the folder's name when what's in it is moved aside (<see cref="SetAside"/>), before the time it was.</summary>
		internal const string OldSuffix = ".old-";

		/// <summary>What Heiward 1.11.0 to 1.11.5 wrote beside the folder once a scan had emptied its stand-ins.</summary>
		internal static string EmptyMark(string folder) => folder + ".empty";

		/// <summary>
		/// What an earlier scan left in <paramref name="folder"/> moved aside whole, an instant however much it holds: an older
		/// Heiward's stand-in of every file, or what a scan cut short held. It goes to <c>mirror.old-&lt;time&gt;</c> beside it,
		/// which <see cref="RemoteHomes"/> deletes in the background; an empty folder just goes. False when something is there
		/// and couldn't be moved (a file in it is open just now): it's left as it is, to be moved another time.
		/// </summary>
		internal static bool SetAside(string folder) {
			TryDelete(EmptyMark(folder));
			if (!Directory.Exists(folder)) return true;
			try {
				Directory.Delete(folder); // empty: nothing to set aside
				return true;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			try {
				Directory.Move(folder, folder + OldSuffix + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture));
				return true;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return false;
			}
		}

		/// <summary>The folder holds only what this scan made (what was there was moved aside): it goes whole when the scan is over.</summary>
		bool ownsFolder;

		/// <summary>
		/// The stand-ins the listing's: one for each file listed, of its size and dates, none of them a file here yet. What an
		/// earlier scan left in the folder is moved aside (<see cref="SetAside"/>). How many there are.
		/// </summary>
		public int Sync(RemoteListing listing) {
			stands.Clear();
			holding.Clear();
			Interlocked.Exchange(ref held, 0);
			lock (fillGate) {
				closed = false;
				if (closing.IsCancellationRequested) {
					closing = new CancellationTokenSource();
					fetchStop = null;
				}
			}
			ownsFolder = SetAside(Folder);
			foreach (ListedRoot root in listing.Roots)
				foreach (ListedFile f in root.Files) {
					string p = RemotePath.Mirrored(Folder, f.Path);
					stands[p] = new Stand(f, p);
				}
			return stands.Count;
		}

		/// <summary>
		/// <see cref="RemoteFiles.About"/>: a stand-in as the other PC listed its file (its creation time its last write, as
		/// Heiward's stand-ins always had it, so the database's entries match); a path under <see cref="Folder"/> that isn't
		/// listed isn't there; null for any other path, which the disk is asked about.
		/// </summary>
		public RemoteFiles.FileFacts? About(string path) =>
			stands.TryGetValue(path, out Stand? s) ? new RemoteFiles.FileFacts(true, s.File.Size, s.File.ModifiedUtc, s.File.ModifiedUtc)
			: IsRemote(path) ? default(RemoteFiles.FileFacts)
			: null;

		/// <summary>From here on, every reader reads the stand-ins through this mirror: <see cref="RemoteFiles"/> and <see cref="RemoteMedia"/>.</summary>
		public void Serve() {
			RemoteFiles.Fetch = Fetch;
			RemoteFiles.About = About;
			RemoteMedia.Use(IsRemote, Open);
		}

		/// <summary>Back to reading every file from the disk.</summary>
		public static void StopServing() {
			RemoteFiles.Fetch = null;
			RemoteFiles.About = null;
			RemoteMedia.Stop();
		}

		/// <summary>
		/// <see cref="Core.ScanEngine.BeforeRead"/>: a photo the scan is to decode is fetched whole (one it knows already, and
		/// won't decode, isn't); a video is fetched as its decoder reads it. False: it couldn't be, and it waits for the next scan.
		/// </summary>
		public bool Before(FileEntry entry) {
			if (!stands.TryGetValue(entry.Path, out Stand? s)) return !IsRemote(entry.Path);
			if (Interlocked.Increment(ref s.Readers) == 1) Begin(s);
			Reached(s); // after its reader is counted: begun and no reader is a file the engine is done with

			// Fetched ahead already, as a rule (a photo whole, a video's ends); the engine waits here only when it caught up.
			bool ok = !(Failed.TryGetValue(s.Path, out string? why) && Final(why)) && (!s.IsPhoto || !ScanEngine.ImageNeedsDecode(entry) || Fetch(entry.Path, 0, -1));
			if (!ok && Interlocked.Decrement(ref s.Readers) == 0) Finish(s, failed: true);
			return ok;
		}

		/// <summary><see cref="Core.ScanEngine.AfterRead"/>: the scan is done with the file, so what was fetched of it goes.</summary>
		public void After(FileEntry entry) {
			if (!stands.TryGetValue(entry.Path, out Stand? s)) return;
			if (Interlocked.Decrement(ref s.Readers) > 0) return;
			Finish(s, Failed.ContainsKey(s.Path));
			if (s.IsPhoto && WhileHeld is { } take && IsFilled(s, 0, s.File.Size)) {
				try { take(s.Path); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			}
			Empty(s);
		}

		void Begin(Stand s) {
			lock (s.Gate) {
				s.Began = Stopwatch.GetTimestamp();
				s.Waited = 0;
			}
		}

		/// <summary>The engine is done with a file: its time, for the timings.</summary>
		void Finish(Stand s, bool failed) {
			if (Timings is not { } t) return;
			long began, waited;
			lock (s.Gate) {
				(began, waited) = (s.Began, s.Waited);
				s.Began = 0;
			}
			if (began != 0) t.File(s.IsPhoto, Stopwatch.GetTimestamp() - began, waited, failed);
		}

		/// <summary>One fetch of the other PC's bytes, timed for <paramref name="what"/> they were (whoever fetched them).</summary>
		T Timed<T>(RemoteTimings.For what, Func<T> read) {
			long t0 = Stopwatch.GetTimestamp();
			try { return read(); }
			finally { Timings?.Waited(what, Stopwatch.GetTimestamp() - t0); }
		}

		/// <summary>The engine waited <paramref name="since"/> till now for <paramref name="s"/>'s bytes (not the fetching ahead's own waits).</summary>
		static void EngineWaited(Stand s, long since) {
			if (!aheadThread) Interlocked.Add(ref s.Waited, Stopwatch.GetTimestamp() - since);
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
			if (sorting && !aheadThread) Reached(s); // the sets got to it: the photos after it are fetched ahead
			long size = s.File.Size;
			bool whole = count < 0 || offset <= 0 && count >= size;
			if (count < 0) {
				if (size > MaxWhole) {
					Failed.TryAdd(path, "too big to fetch whole");
					return false;
				}
				offset = 0;
				count = size;
			}
			long from = Math.Max(0, offset), to = Math.Min(size, offset + count);
			if (to <= from && size > 0) return true; // nothing asked
			if (Failed.TryGetValue(path, out string? why) && Final(why)) return false;
			long t0 = Stopwatch.GetTimestamp();
			lock (fillGate) {
				if (closed) return false; // the scan is over
				filling++;
			}
			try {
				lock (s.Gate) {
					s.Used = Stopwatch.GetTimestamp();
					if (to <= from) { // an empty file: one to open, as there
						if (!s.OnDisk) MakeFile(s, sparse: false);
						return true;
					}
					foreach (var (a, b) in Missing(s, from / Align * Align, Math.Min(size, (to + Align - 1) / Align * Align)))
						FillRange(s, a, b, whole ? RemoteTimings.For.Whole : RemoteTimings.For.Ends);
				}
				EngineWaited(s, t0);
				Trim();
				return true;
			}
			catch (RemoteReadFailed e) when (!closing.IsCancellationRequested) {
				Failed[path] = e.Code ?? e.Message;
				if (e.Code == "over") Over = true;
				return false;
			}
			catch (OperationCanceledException) {
				return false;
			}
			catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && !closing.IsCancellationRequested) {
				Failed[path] = "this PC couldn't hold it: " + e.Message;
				return false;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return false; // the scan was over before it came: nothing to note
			}
			finally {
				lock (fillGate)
					if (--filling == 0) Monitor.PulseAll(fillGate);
			}
		}

		/// <summary>Fetches under way (<see cref="Fetch"/>), and whether the scan is over (<see cref="EmptyAll"/>): none starts after.</summary>
		readonly object fillGate = new();
		int filling;
		bool closed;
		/// <summary>Cancelled once the scan is over: a fetch still under way gives up, rather than leave a file behind.</summary>
		CancellationTokenSource closing = new();
		CancellationTokenSource? fetchStop;
		/// <summary>How long the end of a scan waits for the fetches under way to give up.</summary>
		static readonly TimeSpan FillsEndWait = TimeSpan.FromSeconds(30);

		/// <summary>What a fetch is cancelled by: the scan stopping (<see cref="Stopping"/>), or being over (<see cref="EmptyAll"/>).</summary>
		CancellationToken FetchToken {
			get {
				if (fetchStop == null) {
					var made = CancellationTokenSource.CreateLinkedTokenSource(Stopping, closing.Token);
					if (Interlocked.CompareExchange(ref fetchStop, made, null) != null) made.Dispose();
				}
				return fetchStop.Token;
			}
		}

		/// <summary>
		/// <see cref="Core.FFTools.FFmpegNative.RemoteMedia"/>'s stream of a stand-in's bytes: a photo fetched whole and read
		/// from its stand-in; a video's parts fetched as they're read, held in memory only. Null for a file that isn't one.
		/// </summary>
		public Stream? Open(string path) => stands.TryGetValue(path, out Stand? s) ? new RemoteStream(this, s) : null;

		/// <summary>
		/// For <see cref="ISortFetch.Refuse"/>: until it's disposed, every read of a stand-in fails as one whose bytes couldn't be
		/// fetched, and nothing is asked of the other PC; the stand-ins asked for are kept, in order. After it, reads fetch
		/// (<see cref="Fetch"/>, <see cref="Open"/>) as before.
		/// </summary>
		public SortNeeds Refuse() {
			var needs = new SortNeeds(Serve);
			RemoteFiles.Fetch = (path, _, _) => {
				if (!stands.ContainsKey(path)) return !IsRemote(path);
				needs.Asked(path);
				return false;
			};
			RemoteMedia.Use(IsRemote, path => {
				if (stands.ContainsKey(path)) needs.Asked(path);
				return null;
			});
			return needs;
		}

		/// <summary>
		/// The scan is over: the fetching stops (what's under way gives up, and nothing more is fetched), every stand-in held is
		/// deleted, and the folder goes with them, unless what an earlier scan left there couldn't be moved aside.
		/// </summary>
		public void EmptyAll() {
			closing.Cancel();
			StopAhead();
			lock (fillGate) {
				closed = true;
				var waited = Stopwatch.StartNew();
				while (filling > 0 && FillsEndWait - waited.Elapsed is var left && left > TimeSpan.Zero) Monitor.Wait(fillGate, left);
			}
			foreach (Stand s in holding.Keys) Empty(s);
			if (ownsFolder && Directory.Exists(Folder)) {
				try { Directory.Delete(Folder, recursive: true); } // the folders the stand-ins were in, and any file a reader still had open
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // moved aside by the next scan, or RemoteHomes
			}
		}

		public void Dispose() {
			EmptyAll();
			fetchStop?.Dispose();
		}

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

		/// <summary>
		/// [a, b) fetched into the stand-in in one ask (a photo whole, not 4 MB at a time), written as it comes; its file made
		/// first, if it isn't here: of its size, sparse unless all of it is coming now. Under its <see cref="Stand.Gate"/>.
		/// </summary>
		void FillRange(Stand s, long a, long b, RemoteTimings.For what) {
			bool made = !s.OnDisk;
			if (made) MakeFile(s, sparse: a > 0 || b < s.File.Size);
			try {
				using (var fs = new FileStream(s.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1)) {
					long got = Timed(what, () => source.Stream(s.File, a, b - a, (at, bytes) => {
						fs.Position = at;
						fs.Write(bytes);
						Mark(s, at, at + bytes.Length);
					}, FetchToken));
					if (got < b - a) throw new RemoteReadFailed("it ended early", "changed");
				}
			}
			catch when (made) {
				Empty(s); // a file made for bytes that didn't come doesn't stay (a photo's takes its whole size)
				throw;
			}
			Stamp(s.Path, s.File); // writing moved its date: back to the other PC's, for any reader that asks the file
		}

		/// <summary>The stand-in's file made, held from now on (<see cref="holding"/>) so it's deleted with the rest. Under its <see cref="Stand.Gate"/>.</summary>
		void MakeFile(Stand s, bool sparse) {
			Create(s.Path, s.File, sparse);
			s.OnDisk = true;
			holding[s] = 0;
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

		/// <summary>What was fetched of a stand-in let go: its file deleted.</summary>
		void Empty(Stand s) {
			lock (s.Gate) {
				if (!s.OnDisk) return;
				try { File.Delete(s.Path); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return; // open elsewhere just now: let go of by the next trim, or at the end
				}
				Interlocked.Add(ref held, -s.Bytes);
				s.Bytes = 0;
				s.Filled.Clear();
				s.OnDisk = false;
				holding.TryRemove(s, out _);
			}
		}

		/// <summary>Past <see cref="Cap"/>: the files read longest ago, and not being read, are emptied, down to three quarters of it.</summary>
		void Trim() {
			if (Held <= Cap) return;
			lock (trimming) {
				long now = Stopwatch.GetTimestamp();
				foreach (Stand s in holding.Keys.Where(s => Volatile.Read(ref s.Readers) == 0 && Stopwatch.GetElapsedTime(s.Used, now) > Recent && !IsAhead(s))
						.OrderBy(s => s.Used).ToList()) {
					if (Held <= Cap / 4 * 3) break;
					Empty(s);
				}
			}
		}

		/// <summary>A stand-in's file of the other PC's size: sparse, taking no room till its bytes come; or not, for one all of whose bytes are coming now.</summary>
		static void Create(string path, ListedFile f, bool sparse) {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1);
			if (sparse) SetSparse(fs.SafeFileHandle);
			fs.SetLength(f.Size);
			if (sparse) ZeroAll(fs); // NTFS keeps the cluster the file now ends in: given back too
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

		static void TryDelete(string path) {
			try { File.Delete(path); }
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
		/// while the stream is open); a video's read from its stand-in where it holds them (its ends, fetched ahead), else
		/// fetched as they're read: 512 KB after a seek, 2 MB at a time while the decoder reads straight on (a frame's group of
		/// pictures), the last 8 MB kept in memory. A part that can't be fetched fails the read (an IOException), never reads as zeros.
		/// </summary>
		sealed class RemoteStream : Stream {
			const int Piece = 512 * 1024, Straight = 2 * 1024 * 1024, KeepBytes = 8 * 1024 * 1024;
			readonly RemoteMirror mirror;
			readonly Stand stand;
			readonly bool whole;
			readonly LinkedList<(long At, byte[] Data)> pieces = new();
			long kept;
			FileStream? file;
			long position, lastEnd = -1;
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
				bool straight = position == lastEnd;
				if (whole || mirror.IsFilled(stand, position, position + want)) {
					int read = FromStandIn(position, into[..want]);
					position += read;
					lastEnd = position;
					return read;
				}
				var (at, data) = PieceFor(position, straight);
				int n = (int)Math.Min(want, at + data.Length - position);
				if (n <= 0) return 0;
				data.AsSpan((int)(position - at), n).CopyTo(into);
				position += n;
				lastEnd = position;
				return n;
			}

			int FromStandIn(long at, Span<byte> into) {
				file ??= new FileStream(stand.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
				file.Position = at;
				return file.Read(into);
			}

			/// <summary>The piece holding <paramref name="at"/>: kept, read from the stand-in where it holds it all, or fetched (bigger <paramref name="straight"/> on).</summary>
			(long At, byte[] Data) PieceFor(long at, bool straight) {
				for (var node = pieces.First; node != null; node = node.Next)
					if (node.Value.At <= at && at < node.Value.At + node.Value.Data.Length) {
						pieces.Remove(node);
						pieces.AddFirst(node);
						return node.Value;
					}
				long from = straight ? at / Align * Align : at / Piece * Piece;
				int length = (int)Math.Min(straight ? Straight : Piece, stand.File.Size - from);
				byte[] data = new byte[length];
				if (mirror.IsFilled(stand, from, from + length)) {
					if (FromStandIn(from, data) < length) throw new IOException("its stand-in ended early");
				}
				else {
					long t0 = Stopwatch.GetTimestamp();
					try {
						int got = mirror.Timed(RemoteTimings.For.Parts, () => mirror.source.Read(stand.File, from, data, length, mirror.FetchToken));
						if (got < length) throw new RemoteReadFailed("it ended early", "changed");
					}
					catch (RemoteReadFailed e) {
						mirror.Failed[stand.Path] = e.Code ?? e.Message;
						if (e.Code == "over") mirror.Over = true;
						throw;
					}
					finally { EngineWaited(stand, t0); }
				}
				pieces.AddFirst((from, data));
				kept += length;
				while (kept > KeepBytes && pieces.Count > 1) {
					kept -= pieces.Last!.Value.Data.Length;
					pieces.RemoveLast();
				}
				return (from, data);
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
