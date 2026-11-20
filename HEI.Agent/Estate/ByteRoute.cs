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
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent.Estate {
	/// <summary>
	/// The bytes of this PC's photos and videos, for the PC scanning them (<c>GET /api/peer/scan/bytes?path=…</c>, with a
	/// Range header, which Manor passes on): gently, since this PC stays in charge of its own drive and its games.
	/// <list type="bullet">
	/// <item>Only for the PC this one asked to scan it, while it does, and only files in the listing sent to it, as they
	/// were listed: one changed since is refused (409, <c>changed</c>), and the other PC leaves it for its next scan.</item>
	/// <item>Never while a game is played (or any program full screen): 503 with Retry-After, and the other PC waits.</item>
	/// <item>Read at very low disk priority (as the search indexer reads), on threads in Windows' efficiency mode, at about
	/// <see cref="BytesPerSecond"/> in all: a drive a game loads from isn't kept busy.</item>
	/// <item>A cloud file not on this PC (OneDrive's online-only) is never read, so nothing is downloaded for it.</item>
	/// </list>
	/// </summary>
	sealed class ByteRoute {
		/// <summary>How fast this PC hands its files over, in all: about 20 MB/s, a third of a hard disk's, under Wi-Fi's.</summary>
		public const long BytesPerSecond = 20L * 1000 * 1000;
		/// <summary>The most one request reads: the other PC asks in pieces.</summary>
		public const long MaxRange = 16L * 1024 * 1024;
		const int Chunk = 256 * 1024;

		readonly Pace pace;
		readonly Func<string?> gameNow;

		/// <param name="gameNow">What's being played now ("a full-screen game"), or null.</param>
		public ByteRoute(Func<string?> gameNow, long bytesPerSecond = BytesPerSecond) {
			this.gameNow = gameNow;
			pace = new Pace(bytesPerSecond);
		}

		/// <summary>Bytes handed over since this started, for the page and the log.</summary>
		public long Served => Interlocked.Read(ref served);
		long served;

		/// <summary>Processor time this PC spent handing them over (its threads', reading and sending).</summary>
		public TimeSpan Cpu => TimeSpan.FromTicks(Interlocked.Read(ref cpuTicks));
		long cpuTicks;

		/// <summary>
		/// Answers one request for <paramref name="path"/>'s bytes: <paramref name="listed"/>, the file as it was listed to
		/// the asker (null: not in its listing).
		/// </summary>
		public async Task ServeAsync(HttpContext ctx, string? path, ListedFile? listed) {
			if (path == null || listed == null) {
				await NpuLoan.Json(404, new { error = "That file isn't in the listing sent to that PC.", code = "unlisted" }).ExecuteAsync(ctx);
				return;
			}
			if (gameNow() is string game) {
				ctx.Response.Headers.RetryAfter = "30";
				await NpuLoan.Json(503, new { error = $"{game} is running on this PC: its files wait", code = "game" }).ExecuteAsync(ctx);
				return;
			}
			var file = new FileInfo(path);
			if (!file.Exists || file.Length != listed.Size || file.LastWriteTimeUtc.Ticks != listed.ModifiedTicks) {
				await NpuLoan.Json(409, new { error = "It changed since it was listed.", code = "changed" }).ExecuteAsync(ctx);
				return;
			}
			// OneDrive's online-only files: reading one would download it.
			const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000, Offline = FileAttributes.Offline;
			if ((file.Attributes & (RecallOnDataAccess | Offline)) != 0) {
				await NpuLoan.Json(409, new { error = "It's in the cloud, not on this PC.", code = "cloud" }).ExecuteAsync(ctx);
				return;
			}
			long size = file.Length;
			long from = 0, to = size - 1;
			bool ranged = false;
			string? asked = ctx.Request.Headers.Range;
			if (!string.IsNullOrEmpty(asked)) {
				if (!RangeHeaderValue.TryParse(asked, out RangeHeaderValue? range) || range.Unit != "bytes" || range.Ranges.Count != 1) {
					await NpuLoan.Json(416, new { error = "One range of bytes, please." }).ExecuteAsync(ctx);
					return;
				}
				RangeItemHeaderValue r = range.Ranges.First();
				if (r.From is long f) {
					from = f;
					to = r.To is long t ? Math.Min(t, size - 1) : size - 1;
				}
				else if (r.To is long suffix) {
					from = Math.Max(0, size - suffix);
					to = size - 1;
				}
				if (from < 0 || from > to || from >= size) {
					ctx.Response.Headers.ContentRange = $"bytes */{size}";
					await NpuLoan.Json(416, new { error = "That range isn't in the file." }).ExecuteAsync(ctx);
					return;
				}
				ranged = true;
			}
			to = Math.Min(to, from + MaxRange - 1);
			long length = to - from + 1;
			ctx.Response.StatusCode = ranged ? 206 : 200;
			ctx.Response.ContentType = "application/octet-stream";
			ctx.Response.ContentLength = length;
			ctx.Response.Headers.AcceptRanges = "bytes";
			ctx.Response.Headers.CacheControl = "no-store";
			if (ranged) ctx.Response.Headers.ContentRange = $"bytes {from}-{to}/{size}";
			if (size == 0) return;
			await ReadGentlyAsync(path, from, length, ctx.Response.Body, ctx.RequestAborted);
		}

		/// <summary>
		/// Reads the range in pieces at very low disk priority, each on a thread in efficiency mode for its read, and paced:
		/// the next piece waits its turn among all the requests' pieces.
		/// </summary>
		async Task ReadGentlyAsync(string path, long from, long length, Stream to, CancellationToken ct) {
			byte[] buffer = new byte[Chunk];
			using var stream = new FileStream(path, new FileStreamOptions {
				Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.SequentialScan, BufferSize = 0,
			});
			Gentle.LowDiskPriority(stream.SafeFileHandle);
			stream.Seek(from, SeekOrigin.Begin);
			long left = length;
			while (left > 0) {
				int want = (int)Math.Min(buffer.Length, left);
				await pace.WaitAsync(want, ct);
				int n = await Task.Run(() => Gentle.Read(stream, buffer, want, ref cpuTicks), ct);
				if (n <= 0) throw new IOException("the file ended early");
				await to.WriteAsync(buffer.AsMemory(0, n), ct);
				Interlocked.Add(ref served, n);
				left -= n;
			}
		}

		/// <summary>A token bucket: about <c>rate</c> bytes a second in all, with a second's worth at most saved up.</summary>
		internal sealed class Pace(long rate) {
			readonly object gate = new();
			double tokens = rate;
			long at = Stopwatch.GetTimestamp();

			public async Task WaitAsync(long bytes, CancellationToken ct) {
				while (true) {
					TimeSpan wait;
					lock (gate) {
						long now = Stopwatch.GetTimestamp();
						tokens = Math.Min(rate, tokens + Stopwatch.GetElapsedTime(at, now).TotalSeconds * rate);
						at = now;
						if (tokens >= bytes || tokens >= rate) {
							tokens -= bytes;
							return;
						}
						wait = TimeSpan.FromSeconds((bytes - tokens) / rate);
					}
					await Task.Delay(wait < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : wait, ct);
				}
			}
		}
	}

	/// <summary>Windows' gentlest ways to read a file: very low disk priority, and efficiency mode for the thread reading.</summary>
	static partial class Gentle {
		/// <summary>The handle's reads go at very low I/O priority (FileIoPriorityHintInfo): whatever else uses the drive goes first.</summary>
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

		/// <summary>The thread's processor time so far, in 100 ns ticks.</summary>
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
