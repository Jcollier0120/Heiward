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
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>
	/// A machine-wide NPU lock shared with other NPU tools on this PC that use the same lock, so their
	/// work takes turns on the Hexagon instead of stacking up (an oversized concurrent load has bugchecked
	/// a Snapdragon driver). Every such tool follows one protocol: an atomic mkdir of
	/// <c>%USERPROFILE%\.npu-agent\locks\npu</c> holding owner.json <c>{"pid", "since"}</c>; a holder that
	/// died or held it over 10 minutes is evicted. The folder name is historical; it and NPU_AGENT_NPU_LOCK
	/// stay as they are, or the tools would stop taking turns. Active only while
	/// <c>%USERPROFILE%\.npu-agent</c> exists or NPU_AGENT_NPU_LOCK names a lock; otherwise every call is a no-op.
	/// <para>
	/// Turns are first come, first served through the NPU queue those tools share: a waiter
	/// drops a ticket into <c>&lt;lock&gt;.queue</c>, keeps it fresh, and tries the lock only when its
	/// ticket heads the line. Heiward's work is background: a request a person is waiting on goes
	/// ahead of it, but never ahead of a scan that has waited two minutes.
	/// </para>
	/// <para>
	/// The same lock and line, unchanged, serve every accelerator (the Steward's kit: kit\spec\ACCELERATORS.md): a graphics
	/// card's first slot is the folder <c>&lt;locks&gt;\gpu-&lt;name&gt;</c> beside the NPU's (<see cref="LockDirectoryFor"/>),
	/// its line <c>gpu-&lt;name&gt;.queue</c>. Other tools may serve a card with more slots (<c>&lt;id&gt;.2</c>, …);
	/// Heiward knows nothing of them and only ever takes the first, through the same line, which they honour.
	/// </para>
	/// </summary>
	public static partial class NpuLock {
		static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

		// Shared with every implementation of the NPU queue.
		internal static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(2);
		internal static readonly TimeSpan Late = TimeSpan.FromSeconds(5);
		internal static readonly TimeSpan Dead = TimeSpan.FromSeconds(15);
		internal static readonly TimeSpan Age = TimeSpan.FromSeconds(120);
		const int HeadPollMs = 50, PollMs = 100;

		const int ErrorAlreadyExists = 183;

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		static extern bool CreateDirectoryW(string path, nint securityAttributes);

		/// <summary>The lock directory, or null when no other NPU tool uses one on this PC (and off Windows, where there is no Hexagon NPU).</summary>
		public static string? LockDirectory {
			get {
				if (!OperatingSystem.IsWindows()) return null;
				string? explicitLock = Environment.GetEnvironmentVariable("NPU_AGENT_NPU_LOCK");
				if (!string.IsNullOrWhiteSpace(explicitLock)) return explicitLock;
				string home = Environment.GetEnvironmentVariable("NPU_AGENT_HOME") is { Length: > 0 } h
					? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npu-agent");
				return Directory.Exists(home) ? Path.Combine(home, "locks", "npu") : null;
			}
		}

		/// <summary>
		/// An accelerator's lock folder (its first slot): the NPU's is <see cref="LockDirectory"/>; any other's, the
		/// folder named by its id beside it. Null when the locks aren't in use on this PC (see <see cref="LockDirectory"/>).
		/// </summary>
		/// <param name="accelerator">An accelerator id: <c>npu</c>, <c>cpu</c> or <c>gpu-…</c> (<see cref="Accelerators"/>).</param>
		public static string? LockDirectoryFor(string accelerator) {
			if (!Accelerators.IsId(accelerator))
				throw new ArgumentException($"Not an accelerator id: \"{accelerator}\".", nameof(accelerator));
			string? npu = LockDirectory;
			if (npu == null || accelerator == Accelerators.Npu) return npu;
			return Path.GetDirectoryName(npu) is { Length: > 0 } locks ? Path.Combine(locks, accelerator) : null;
		}

		/// <summary>The NPU queue: the folder of tickets next to the lock.</summary>
		internal static string QueueDirectoryFor(string lockDir) => lockDir + ".queue";

		/// <summary>
		/// Holds the NPU lock until disposed, after waiting its turn in the NPU queue. Waits up to
		/// <paramref name="wait"/> (default 5 min), then throws <see cref="TimeoutException"/>.
		/// </summary>
		public static IDisposable Acquire(TimeSpan? wait = null) => Acquire(Accelerators.Npu, wait, interactive: false);

		/// <summary>
		/// Holds an accelerator's lock (its first slot) until disposed, after waiting its turn in its line, as
		/// <see cref="Acquire(TimeSpan?)"/> does for the NPU.
		/// </summary>
		public static IDisposable Acquire(string accelerator, TimeSpan? wait = null) => Acquire(accelerator, wait, interactive: false);

		internal static IDisposable Acquire(TimeSpan? wait, bool interactive) => Acquire(Accelerators.Npu, wait, interactive);

		internal static IDisposable Acquire(string accelerator, TimeSpan? wait, bool interactive) {
			string? dir = LockDirectoryFor(accelerator);
			if (dir == null) return NoLock.Instance;
			return AcquireAt(dir, accelerator == Accelerators.Npu ? "NPU" : accelerator, wait, interactive, Stale);
		}

		/// <summary>
		/// The manor's first-round line (the Steward's kit 2.43.0, kit\spec\ROUND.md's "Pace"): the folder
		/// <c>&lt;locks&gt;\first-rounds</c> beside the NPU's, one slot, the same line. Agents new to the manor take it for
		/// their heavy first round, one at a time. Null when the locks aren't in use on this PC (<see cref="LockDirectory"/>).
		/// </summary>
		public static string? FirstRoundDirectory => LockDirectory is string npu && Path.GetDirectoryName(npu) is { Length: > 0 } locks ? Path.Combine(locks, "first-rounds") : null;

		/// <summary>
		/// How long a first round may hold its turn before a waiter takes it over: a first scan of every drive takes
		/// hours, and a holder whose process has gone gives the turn up at once anyway. The kit's agents allow as long.
		/// </summary>
		public static readonly TimeSpan FirstRoundHold = TimeSpan.FromHours(12);

		/// <summary>
		/// Holds the manor's first-round turn until disposed, after waiting in its line (background) up to
		/// <paramref name="wait"/>, then throws <see cref="TimeoutException"/>. A no-op where the locks aren't in use.
		/// </summary>
		public static IDisposable AcquireFirstRound(TimeSpan wait) =>
			FirstRoundDirectory is string dir ? AcquireAt(dir, "first-round", wait, interactive: false, FirstRoundHold) : NoLock.Instance;

		/// <summary>A lock folder and its line (any of them: an accelerator's, the first-round line), as above; <paramref name="stale"/> evicts an overstayed holder.</summary>
		internal static IDisposable AcquireAt(string dir, string what, TimeSpan? wait, bool interactive, TimeSpan stale) {
			string queueDir = QueueDirectoryFor(dir);
			Directory.CreateDirectory(queueDir);
			var deadline = Stopwatch.StartNew();
			TimeSpan limit = wait ?? TimeSpan.FromMinutes(5);

			long us = NowUs();
			string name = $"{(interactive ? 0 : 1)}-{us:D17}-{Environment.ProcessId}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}.ticket";
			string ticket = Path.Combine(queueDir, name);
			string body = $$"""{"pid":{{Environment.ProcessId}},"since":{{us / 1000}},"lane":"{{(interactive ? "interactive" : "background")}}","who":"heiward"}""";
			File.WriteAllText(ticket, body);

			var beat = Stopwatch.StartNew();
			bool logged = false;
			try {
				while (true) {
					if (beat.Elapsed >= Heartbeat) {
						try { File.SetLastWriteTimeUtc(ticket, DateTime.UtcNow); }
						catch (IOException) { WriteTicket(ticket, body); } // taken for dead (a long pause): back in, same place
						beat.Restart();
					}
					List<string> line = ReadLine(queueDir, keep: name);
					if (!line.Contains(name)) {
						WriteTicket(ticket, body);
						beat.Restart();
						continue;
					}
					bool head = line[0] == name;
					if (head && TryLock(dir, what, stale) is { } held)
						return held;
					if (deadline.Elapsed > limit)
						throw new TimeoutException($"Timed out after {limit.TotalSeconds:N0} s waiting for the {what} lock {dir} ({line.Count} in line).");
					if (!logged) {
						Logger.Instance.Info($"Waiting for the {what}: {line.IndexOf(name)} ahead in line (lock {dir}).");
						logged = true;
					}
					Thread.Sleep(head ? HeadPollMs : PollMs);
				}
			}
			finally {
				try { File.Delete(ticket); } catch { }
			}
		}

		/// <summary>One try at the lock folder: ours, or null. Evicts a holder that died or overstayed.</summary>
		static Held? TryLock(string dir, string what, TimeSpan stale) {
			for (int attempt = 0; attempt < 2; attempt++) {
				try {
					// Win32 CreateDirectory fails when the folder exists: the atomic test-and-set the
					// lock rests on (.NET's Directory.CreateDirectory succeeds silently instead).
					if (CreateDirectoryW(dir, 0)) {
						var me = new Owner(Environment.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
						File.WriteAllText(Path.Combine(dir, "owner.json"), JsonSerializer.Serialize(me, OwnerJson.Default.Owner));
						return new Held(dir, me);
					}
					if (Marshal.GetLastPInvokeError() != ErrorAlreadyExists)
						throw new IOException($"Cannot create the {what} lock {dir} (Win32 error {Marshal.GetLastPInvokeError()}).");
					if (!IsStale(dir, stale)) return null;
					Logger.Instance.Info($"{what} lock {dir} was held by a process that died or overstayed; taking it over.");
					Directory.Delete(dir, recursive: true);
				}
				catch (IOException) { return null; } // lost a race with another taker: try again next poll
				catch (UnauthorizedAccessException) { return null; }
			}
			return null;
		}

		// ------------------------------------------------------------------------------------- the line

		internal readonly record struct Ticket(string Name, int Lane, long TimeUs, int Pid, string Nonce);

		[GeneratedRegex(@"^([01])-(\d{17})-(\d+)-([0-9a-z]+)\.ticket$", RegexOptions.CultureInvariant)]
		private static partial Regex TicketName();

		internal static Ticket? ParseTicket(string name) {
			Match m = TicketName().Match(name);
			if (!m.Success || !int.TryParse(m.Groups[3].ValueSpan, out int pid)) return null;
			return new Ticket(name, m.Groups[1].ValueSpan[0] - '0', long.Parse(m.Groups[2].ValueSpan), pid, m.Groups[4].Value);
		}

		/// <summary>Line order: interactive first, then by arrival; background that has waited <see cref="Age"/> counts as interactive.</summary>
		internal static int CompareTickets(Ticket a, Ticket b, long nowUs) {
			int Lane(Ticket t) => t.Lane == 0 || nowUs - t.TimeUs >= (long)Age.TotalMicroseconds ? 0 : 1;
			int c = Lane(a).CompareTo(Lane(b));
			if (c == 0) c = a.TimeUs.CompareTo(b.TimeUs);
			if (c == 0) c = a.Pid.CompareTo(b.Pid);
			if (c == 0) c = string.CompareOrdinal(a.Nonce, b.Nonce);
			return c;
		}

		/// <summary>A waiter is gone: no heartbeat for <see cref="Dead"/>, or a late heartbeat and no such process.</summary>
		internal static bool IsDeadTicket(TimeSpan age, Func<bool> pidAlive) => age > Dead || (age > Late && !pidAlive());

		/// <summary>Live ticket names in line order. Dead tickets are removed on the way (anyone in line may), except <paramref name="keep"/>.</summary>
		internal static List<string> ReadLine(string queueDir, string? keep = null) {
			var live = new List<Ticket>();
			IEnumerable<string> files;
			try { files = new List<string>(Directory.EnumerateFiles(queueDir, "*.ticket")); }
			catch { return []; }
			DateTime now = DateTime.UtcNow;
			foreach (string path in files) {
				string name = Path.GetFileName(path);
				if (ParseTicket(name) is not { } t) continue;
				DateTime touched;
				try {
					var info = new FileInfo(path);
					if (!info.Exists) continue; // left the line just now
					touched = info.LastWriteTimeUtc;
				}
				catch { continue; }
				if (name != keep && IsDeadTicket(now - touched, () => PidAlive(t.Pid))) {
					try { File.Delete(path); } catch { }
					continue;
				}
				live.Add(t);
			}
			long nowUs = NowUs();
			live.Sort((a, b) => CompareTickets(a, b, nowUs));
			return live.ConvertAll(t => t.Name);
		}

		static long lastUs;

		/// <summary>Wall-clock microseconds since the Unix epoch, strictly increasing within this process.</summary>
		static long NowUs() {
			long t = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
			while (true) {
				long last = Interlocked.Read(ref lastUs);
				long next = t > last ? t : last + 1;
				if (Interlocked.CompareExchange(ref lastUs, next, last) == last) return next;
			}
		}

		static bool PidAlive(int pid) {
			try {
				using var p = Process.GetProcessById(pid);
				return !p.HasExited;
			}
			catch (ArgumentException) { return false; } // no such process
			catch { return true; }
		}

		// ------------------------------------------------------------------------------------- the lock

		static bool IsStale(string dir, TimeSpan stale) {
			Owner? owner = ReadOwner(Path.Combine(dir, "owner.json"));
			if (owner == null) {
				// Not written yet (give the holder a moment) or lost in a crash.
				try { return DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromSeconds(10); }
				catch { return false; }
			}
			if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - owner.Since > stale.TotalMilliseconds)
				return true;
			return !PidAlive(owner.Pid);
		}

		/// <summary>
		/// Reads owner.json letting others write and delete it meanwhile. File.ReadAllText doesn't share
		/// delete, so a waiter checking the holder at the moment it let go made its release fail, and the
		/// lock stayed taken until it went stale.
		/// </summary>
		static Owner? ReadOwner(string path) {
			try {
				using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				return JsonSerializer.Deserialize(file, OwnerJson.Default.Owner);
			}
			catch { return null; }
		}

		/// <summary>Writes a ticket, putting the queue folder back if it was removed.</summary>
		static void WriteTicket(string ticket, string body) {
			try { File.WriteAllText(ticket, body); }
			catch (DirectoryNotFoundException) {
				Directory.CreateDirectory(Path.GetDirectoryName(ticket)!);
				File.WriteAllText(ticket, body);
			}
		}

		internal sealed record Owner(int Pid, long Since);

		sealed class Held(string dir, Owner me) : IDisposable {
			int released;
			public void Dispose() {
				if (Interlocked.Exchange(ref released, 1) != 0) return;
				try {
					// Only remove it while it is still ours (an overstayed holder may have been evicted).
					if (ReadOwner(Path.Combine(dir, "owner.json")) is not { } o || o.Pid != me.Pid || o.Since != me.Since) return;
					// A waiter may have a file in it open for a moment (another tool's reader may not share
					// delete): try again briefly rather than leave the lock taken until it goes stale.
					for (int attempt = 0; ; attempt++) {
						try {
							Directory.Delete(dir, recursive: true);
							return;
						}
						catch (DirectoryNotFoundException) { return; }
						catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 40) {
							Thread.Sleep(25);
						}
					}
				}
				catch { /* the next taker's stale check cleans up */ }
			}
		}

		sealed class NoLock : IDisposable {
			public static readonly NoLock Instance = new();
			public void Dispose() { }
		}
	}

	[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
	[System.Text.Json.Serialization.JsonSerializable(typeof(NpuLock.Owner))]
	internal partial class OwnerJson : System.Text.Json.Serialization.JsonSerializerContext { }
}
