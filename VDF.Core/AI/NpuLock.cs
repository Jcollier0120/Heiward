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
using System.Text.Json;
using VDF.Core.Utils;

namespace VDF.Core.AI {
	/// <summary>
	/// A machine-wide NPU lock shared with other NPU tools on the PC, so their work takes turns on the
	/// Hexagon instead of stacking up (an oversized concurrent load has bugchecked a Snapdragon driver).
	/// Wire-compatible with npu-agent's lock (src/lock.ts, npu-embed/npu_lock.py): an atomic mkdir of
	/// <c>%USERPROFILE%\.npu-agent\locks\npu</c> holding owner.json <c>{"pid", "since"}</c>; a holder that
	/// died or held it over 10 minutes is evicted. Active only when that tool is present (its folder
	/// exists) or NPU_AGENT_NPU_LOCK names a lock; otherwise every call is a no-op.
	/// </summary>
	public static class NpuLock {
		static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

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

		/// <summary>Holds the lock until disposed. Waits up to <paramref name="wait"/> (default 5 min), then throws <see cref="TimeoutException"/>.</summary>
		public static IDisposable Acquire(TimeSpan? wait = null) {
			string? dir = LockDirectory;
			if (dir == null) return NoLock.Instance;
			Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
			var deadline = Stopwatch.StartNew();
			TimeSpan limit = wait ?? TimeSpan.FromMinutes(5);
			bool logged = false;
			while (true) {
				try {
					// Win32 CreateDirectory fails when the folder exists: the atomic test-and-set the
					// lock rests on (.NET's Directory.CreateDirectory succeeds silently instead).
					if (CreateDirectoryW(dir, 0)) {
						var me = new Owner(Environment.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
						File.WriteAllText(Path.Combine(dir, "owner.json"), JsonSerializer.Serialize(me, OwnerJson.Default.Owner));
						return new Held(dir, me);
					}
					if (Marshal.GetLastPInvokeError() != ErrorAlreadyExists)
						throw new IOException($"Cannot create the NPU lock {dir} (Win32 error {Marshal.GetLastPInvokeError()}).");
					if (IsStale(dir)) {
						Logger.Instance.Info($"NPU lock {dir} was held by a process that died or overstayed; taking it over.");
						Directory.Delete(dir, recursive: true);
						continue;
					}
				}
				catch (IOException) { /* lost a race with another taker: wait and retry */ }
				catch (UnauthorizedAccessException) { }
				if (deadline.Elapsed > limit)
					throw new TimeoutException($"Timed out after {limit.TotalSeconds:N0} s waiting for the NPU lock {dir}.");
				if (!logged) {
					Logger.Instance.Info($"Waiting for another NPU tool to finish (lock {dir}).");
					logged = true;
				}
				Thread.Sleep(150);
			}
		}

		static bool IsStale(string dir) {
			Owner? owner = ReadOwner(Path.Combine(dir, "owner.json"));
			if (owner == null) {
				// Not written yet (give the holder a moment) or lost in a crash.
				try { return DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromSeconds(10); }
				catch { return false; }
			}
			if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - owner.Since > Stale.TotalMilliseconds)
				return true;
			try {
				using var p = Process.GetProcessById(owner.Pid);
				return p.HasExited;
			}
			catch (ArgumentException) { return true; } // no such process
			catch { return false; }
		}

		static Owner? ReadOwner(string path) {
			try { return JsonSerializer.Deserialize(File.ReadAllText(path), OwnerJson.Default.Owner); }
			catch { return null; }
		}

		internal sealed record Owner(int Pid, long Since);

		sealed class Held(string dir, Owner me) : IDisposable {
			int released;
			public void Dispose() {
				if (Interlocked.Exchange(ref released, 1) != 0) return;
				try {
					// Only remove it while it is still ours (an overstayed holder may have been evicted).
					if (ReadOwner(Path.Combine(dir, "owner.json")) is { } o && o.Pid == me.Pid && o.Since == me.Since)
						Directory.Delete(dir, recursive: true);
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
