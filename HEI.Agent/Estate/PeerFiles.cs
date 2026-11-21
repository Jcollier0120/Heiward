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
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent.Estate {
	/// <summary>
	/// A file's bytes for another of the person's PCs, over the Estate: the C# twin of the Steward kit's PeerFiles
	/// (<c>kit/spec/PEER-FILES.md</c>, its vectors <c>peer-files-vectors.json</c>, which the tests run unchanged).
	/// <c>GET /api/peer/&lt;route&gt;?path=…</c>, with Range and If-None-Match, for the files on <see cref="Replace">the list</see>
	/// only, gently: read at very low disk priority, on threads in Windows' efficiency mode, at <see cref="BytesPerSec"/> in
	/// all, and never while a game is played (a reply under way is cut short when one starts; the other PC asks again from
	/// where it got to). A file in the cloud and not on this PC (OneDrive's online-only) is "not there": reading it would
	/// download it. The list lives in memory only, or, a long one, where the staff member keeps it while it lends
	/// (<see cref="ReplaceWith"/>: Heiward's listing for another PC's scan, on disk until the loan ends).
	/// </summary>
	sealed partial class PeerFiles {
		public const long DefaultBytesPerSec = 20L * 1024 * 1024;
		public const int ChunkBytes = 64 * 1024;
		public const double BurstMs = 250;
		public const int GameKeepMs = 30_000;

		/// <summary>A file as the route sees it: a folder, or a file of a size and modification time (ms since the Unix epoch).</summary>
		internal sealed record FileFacts(bool Folder, long Size, double MtimeMs);

		/// <summary>Where the route stats and reads files: this PC's disks, or (tests) made-up files.</summary>
		internal interface IFiles {
			FileFacts? Stat(string path);
			Stream Open(string path);
		}

		readonly ConcurrentDictionary<string, string> list = new(StringComparer.Ordinal);
		readonly Func<string?> game;
		readonly IFiles files;
		readonly Pace pace = new();
		long served, cpuTicks;

		/// <param name="game">The game check's answer now (the program played), or null: the kit's <c>gamePlayed</c>, kept by the caller.</param>
		internal PeerFiles(Func<string?> game, IFiles? files = null) {
			this.game = game;
			this.files = files ?? new Disks();
		}

		/// <summary>Whether the route serves its files to that PC (by default, every PC on the license).</summary>
		public Func<Pc, bool> May { get; set; } = _ => true;

		/// <summary>The pace of all the route's reads together; null or 0 as fast as the drive goes. Read at each chunk.</summary>
		public long? BytesPerSec { get; set; } = DefaultBytesPerSec;

		/// <summary>Bytes handed over so far, and the processor time their reads took.</summary>
		public long Served => Interlocked.Read(ref served);
		public TimeSpan Cpu => TimeSpan.FromTicks(Interlocked.Read(ref cpuTicks));

		/// <summary>The list kept elsewhere (<see cref="ReplaceWith"/>): a key's file as it was given, or null.</summary>
		volatile Func<string, string?>? kept;
		int keptCount;

		/// <summary>The list, all of it at once: only these files are served, as they're given here.</summary>
		public void Replace(IEnumerable<string> paths) {
			kept = null;
			list.Clear();
			foreach (string p in paths) Add(p);
		}

		/// <summary>
		/// The list, all of it at once, kept elsewhere: a long one, on disk rather than in memory (<see cref="LentListing.PathOf"/>).
		/// <paramref name="pathOf"/> gives a key's file as it's listed (one whose key is that one), or null for a key not on it.
		/// </summary>
		public void ReplaceWith(Func<string, string?> pathOf, int count) {
			list.Clear();
			keptCount = count;
			kept = pathOf;
		}

		public void Add(string path) {
			if (KeyOf(path) is string key) list[key] = path;
		}

		public void Clear() {
			kept = null;
			keptCount = 0;
			list.Clear();
		}

		public int Count => kept != null ? keptCount : list.Count;

		/// <summary>The file on the list by that key, as it was given; null for one not on it.</summary>
		string? Listed(string key) => kept is { } pathOf ? pathOf(key) : list.TryGetValue(key, out string? path) ? path : null;

		// ---- The readings (PEER-FILES.md: paths, tags, ranges, noneMatch, contentRanges)

		/// <summary>
		/// A path's key: slashes as backslashes; a drive's full path (<c>C:\</c>), not a relative, drive-relative, share or
		/// device path; empty and <c>.</c> parts gone, <c>..</c> taking the part before it (never above the drive); no part
		/// holding <c>&lt; &gt; : " | ? *</c> or a control character; not the drive alone; in lower case. Null when it's none.
		/// </summary>
		public static string? KeyOf(string? path) {
			if (string.IsNullOrEmpty(path)) return null;
			string p = path.Replace('/', '\\');
			if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\') return null;
			var parts = new List<string>();
			foreach (string part in p[3..].Split('\\')) {
				if (part.Length == 0 || part == ".") continue;
				if (part == "..") {
					if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
					continue;
				}
				foreach (char c in part)
					if (c < ' ' || c is '<' or '>' or ':' or '"' or '|' or '?' or '*') return null;
				parts.Add(part);
			}
			if (parts.Count == 0) return null;
			return (p[0] + ":\\" + string.Join('\\', parts)).ToLowerInvariant();
		}

		/// <summary>The ETag: <c>"&lt;size&gt;-&lt;ms&gt;"</c>, the modification time in whole ms since the Unix epoch, rounded down.</summary>
		public static string Tag(long size, double mtimeMs) => $"\"{size}-{(long)Math.Floor(mtimeMs)}\"";

		/// <summary>The modification time as an HTTP date (<c>Mon, 21 Sep 2026 14:13:20 GMT</c>).</summary>
		public static string HttpDate(double mtimeMs) =>
			DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(mtimeMs)).UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

		/// <summary>A file's modification time in ms since the Unix epoch, as the route tags it.</summary>
		public static double MsOf(DateTime utc) => (utc - DateTime.UnixEpoch).TotalMilliseconds;

		/// <summary>If-None-Match names <paramref name="tag"/>: one of its comma-separated tags is it, a <c>W/</c> before it let go, or it's <c>*</c>.</summary>
		public static bool NoneMatch(string? header, string tag) {
			if (string.IsNullOrWhiteSpace(header)) return false;
			if (header.Trim() == "*") return true;
			foreach (string one in header.Split(',')) {
				string t = one.Trim();
				if (t.StartsWith("W/", StringComparison.Ordinal)) t = t[2..];
				if (t == tag) return true;
			}
			return false;
		}

		/// <summary>What a Range header asks of a file of <paramref name="size"/>: the whole (200), one range of it (206), or none it holds (416).</summary>
		public readonly record struct RangeAsk(int Status, long Start, long End);

		public static RangeAsk RangeOf(string? header, long size) {
			string h = header?.Trim() ?? "";
			if (h.Length == 0) return new(200, 0, size - 1);
			var bad = new RangeAsk(416, 0, -1);
			if (size <= 0) return bad;
			Match m = RangeShape().Match(h);
			if (!m.Success) return bad;
			string a = m.Groups[1].Value, b = m.Groups[2].Value;
			if (a.Length == 0 && b.Length == 0) return bad;
			if (!Number(a, out long first) || !Number(b, out long last)) return bad;
			if (a.Length == 0) {
				if (last == 0) return bad;
				return new(206, Math.Max(0, size - last), size - 1);
			}
			if (first >= size) return bad;
			long end = b.Length == 0 ? size - 1 : Math.Min(last, size - 1);
			if (b.Length > 0 && last < first) return bad;
			return new(206, first, end);
		}

		[GeneratedRegex("^bytes=([0-9]*)-([0-9]*)$")]
		private static partial Regex RangeShape();

		const long MaxExact = 1L << 53;

		static bool Number(string digits, out long value) {
			value = 0;
			if (digits.Length == 0) return true;
			return digits.Length <= 16 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= MaxExact;
		}

		/// <summary>A Content-Range, for the asking side: <c>bytes a-b/size</c> (a 206's) or <c>bytes */size</c> (a 416's); null when it's neither.</summary>
		public static (long? Start, long? End, long Size)? ContentRangeOf(string? header) {
			if (header == null) return null;
			Match m = ContentRangeShape().Match(header.Trim());
			if (!m.Success || !long.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out long size)) return null;
			if (m.Groups[1].Value == "*") return (null, null, size);
			string[] ab = m.Groups[1].Value.Split('-');
			return long.TryParse(ab[0], CultureInfo.InvariantCulture, out long s) && long.TryParse(ab[1], CultureInfo.InvariantCulture, out long e) ? (s, e, size) : null;
		}

		[GeneratedRegex(@"^bytes (\*|[0-9]+-[0-9]+)/([0-9]+)$")]
		private static partial Regex ContentRangeShape();

		/// <summary>
		/// The pace (PEER-FILES.md, Gently): <c>next</c>, when the next chunk may go, minus infinity at first. For a chunk of
		/// <c>bytes</c> ready at <c>at</c> (ms): <c>next = max(next, at - burstMs)</c>; <c>wait = max(0, ceil(next - at))</c>;
		/// <c>next += bytes * 1000 / bytesPerSec</c>. No pace (null or 0): no wait.
		/// </summary>
		internal sealed class Pace {
			readonly object gate = new();
			double next = double.NegativeInfinity;

			public long Wait(double at, long bytes, long? bytesPerSec, double burstMs = BurstMs) {
				if (bytesPerSec is not > 0) return 0;
				lock (gate) {
					next = Math.Max(next, at - burstMs);
					long wait = (long)Math.Max(0, Math.Ceiling(next - at));
					next += bytes * 1000.0 / bytesPerSec!.Value;
					return wait;
				}
			}
		}

		// ---- The route (PEER-FILES.md: answers)

		static IResult Refuse(int status, string error, string code) => Results.Json(new { error, code }, statusCode: status);

		/// <summary>GET /api/peer/&lt;route&gt;?path=…: the checks in the spec's order, the first that applies answering; then the bytes.</summary>
		public async Task ServeAsync(HttpContext ctx) {
			ctx.Response.Headers.CacheControl = "no-store";
			Pc? asker = EstateLink.AskerOf(ctx.Request);
			if (asker == null) {
				await Refuse(403, "Only another of your PCs, through Castellan, can ask.", "peer").ExecuteAsync(ctx);
				return;
			}
			if (!May(asker)) {
				await Refuse(403, "This PC doesn't serve its files to that PC.", "not-allowed").ExecuteAsync(ctx);
				return;
			}
			if (KeyOf(ctx.Request.Query["path"]) is not string key) {
				await Refuse(400, "Ask for a file by its full path (path).", "path").ExecuteAsync(ctx);
				return;
			}
			if (Listed(key) is not string path) {
				await Refuse(403, "That file isn't offered to other PCs.", "not-offered").ExecuteAsync(ctx);
				return;
			}
			// During a game the file isn't looked at: no stat, no read.
			if (game() is string playing) {
				await Results.Json(new { error = $"A game is being played there ({playing}).", code = "game", game = playing, retryMs = GameKeepMs }, statusCode: 503).ExecuteAsync(ctx);
				return;
			}
			if (files.Stat(path) is not { Folder: false } file) {
				await Refuse(404, "That file isn't there any more.", "gone").ExecuteAsync(ctx);
				return;
			}
			string tag = Tag(file.Size, file.MtimeMs);
			ctx.Response.Headers.ETag = tag;
			ctx.Response.Headers.LastModified = HttpDate(file.MtimeMs);
			if (NoneMatch(ctx.Request.Headers.IfNoneMatch, tag)) {
				ctx.Response.StatusCode = 304;
				return;
			}
			RangeAsk range = RangeOf(ctx.Request.Headers.Range, file.Size);
			if (range.Status == 416) {
				ctx.Response.Headers.ETag = default;
				ctx.Response.Headers.LastModified = default;
				ctx.Response.Headers.ContentRange = $"bytes */{file.Size}";
				await Results.Json(new { error = "That range isn't in the file.", code = "range", size = file.Size }, statusCode: 416).ExecuteAsync(ctx);
				return;
			}
			long length = Math.Max(0, range.End - range.Start + 1);
			ctx.Response.StatusCode = range.Status;
			ctx.Response.ContentType = "application/octet-stream";
			ctx.Response.Headers.AcceptRanges = "bytes";
			ctx.Response.ContentLength = length;
			if (range.Status == 206) ctx.Response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{file.Size}";
			if (length == 0) return;
			await ReadAsync(path, range.Start, length, ctx);
		}

		/// <summary>
		/// The range in 64 KiB chunks, each in its turn of the pace; before each, the game check's last answer: once a game
		/// is played, the reply is cut short of its Content-Length.
		/// </summary>
		async Task ReadAsync(string path, long from, long length, HttpContext ctx) {
			byte[] buffer = new byte[ChunkBytes];
			await using Stream stream = files.Open(path);
			if (from > 0) stream.Seek(from, SeekOrigin.Begin);
			long left = length;
			while (left > 0) {
				if (game() != null) {
					ctx.Abort();
					return;
				}
				int want = (int)Math.Min(buffer.Length, left);
				long wait = pace.Wait(Now(), want, BytesPerSec);
				if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ctx.RequestAborted);
				int n = stream is FileStream fs ? await Task.Run(() => Gentle.Read(fs, buffer, want, ref cpuTicks), ctx.RequestAborted) : await stream.ReadAsync(buffer.AsMemory(0, want), ctx.RequestAborted);
				if (n <= 0) {
					ctx.Abort(); // it ended early: the other PC sees a short reply, and asks again
					return;
				}
				await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
				Interlocked.Add(ref served, n);
				left -= n;
			}
		}

		static double Now() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

		/// <summary>This PC's disks: each file opened shared, at very low disk priority; an online-only cloud file isn't there.</summary>
		sealed class Disks : IFiles {
			public FileFacts? Stat(string path) {
				try {
					var info = new FileInfo(path);
					if (!info.Exists) return Directory.Exists(path) ? new FileFacts(true, 0, 0) : null;
					// OneDrive's online-only files: reading one would download it.
					const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
					if ((info.Attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0) return null;
					return new FileFacts(false, info.Length, MsOf(info.LastWriteTimeUtc));
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return null;
				}
			}

			public Stream Open(string path) {
				var fs = new FileStream(path, new FileStreamOptions {
					Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.SequentialScan, BufferSize = 0,
				});
				Gentle.LowDiskPriority(fs.SafeFileHandle);
				return fs;
			}
		}
	}

	/// <summary>Windows' gentlest ways to read a file: very low disk priority, and efficiency mode for the thread reading.</summary>
	static partial class Gentle {
		/// <summary>The handle's reads go at very low I/O priority (FileIoPriorityHintInfo, IoPriorityHintVeryLow): whatever else uses the drive goes first.</summary>
		public static void LowDiskPriority(SafeFileHandle handle) {
			const int FileIoPriorityHintInfo = 12, IoPriorityHintVeryLow = 0;
			int hint = IoPriorityHintVeryLow;
			try { SetFileInformationByHandle(handle, FileIoPriorityHintInfo, ref hint, sizeof(int)); }
			catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { }
		}

		/// <summary>One read, on this thread in efficiency mode (EcoQoS) for its time; its processor time added to <paramref name="cpuTicks"/>.</summary>
		public static int Read(Stream stream, byte[] buffer, int count, ref long cpuTicks) {
			IntPtr thread = GetCurrentThread();
			bool eco = SetEco(thread, true);
			long before = ThreadCpu(thread);
			try { return stream.Read(buffer, 0, count); }
			finally {
				Interlocked.Add(ref cpuTicks, Math.Max(0, ThreadCpu(thread) - before));
				if (eco) SetEco(thread, false);
			}
		}

		static long ThreadCpu(IntPtr thread) =>
			GetThreadTimes(thread, out _, out _, out long kernel, out long user) ? kernel + user : 0;

		static bool SetEco(IntPtr thread, bool on) {
			const int ThreadPowerThrottling = 3;
			const uint ExecutionSpeed = 0x1;
			var state = new ThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = on ? ExecutionSpeed : 0 };
			try { return SetThreadInformation(thread, ThreadPowerThrottling, ref state, Marshal.SizeOf<ThrottlingState>()); }
			catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return false; }
		}

		[StructLayout(LayoutKind.Sequential)]
		struct ThrottlingState {
			public uint Version, ControlMask, StateMask;
		}

		[LibraryImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref int info, int size);

		[LibraryImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool SetThreadInformation(IntPtr thread, int infoClass, ref ThrottlingState info, int size);

		[LibraryImport("kernel32.dll")]
		private static partial IntPtr GetCurrentThread();

		[LibraryImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
	}
}
