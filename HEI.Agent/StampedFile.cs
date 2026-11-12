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

using System.Security.Cryptography;
using System.Text;

namespace HEI.Agent {
	/// <summary>
	/// A file's contents, parsed once and kept while the file is the one they came from: the same path, length and last-write
	/// time. The review page reads report.json (megabytes on a big library), decisions.json and index.json for each folder it
	/// lists, and used to parse all three again every time, one folder after another.
	/// <list type="bullet">
	/// <item>Another process (a scan) may write the file at any time. It writes it whole (<see cref="AgentPaths.WriteAtomic"/>):
	/// a new file under the name, with a new last-write time and nearly always a new length.</item>
	/// <item>Windows moves a file's last-write time on in steps, though: on the owner's PC, 804 of 1,000 back-to-back rewrites
	/// kept the time of the one before. So a file rewritten within one step, to the same length, would look unchanged.
	/// A file read within <see cref="Settle"/> of its last write is checked again on each read, by a hash of its bytes, until
	/// that much time has passed (as git does with files changed in the second it looks). Hashing a file costs a small part
	/// of parsing it.</item>
	/// <item>Each caller gets a copy of its own (<c>copy</c>), never the kept one, so what a caller changes doesn't reach the next.</item>
	/// <item>It's kept only while the review page is in use (<see cref="KeptFiles"/>): the page's process stays up all day,
	/// and a big library's report takes some 15 MB once parsed (9 MB of JSON). Otherwise each read parses the file, as before.</item>
	/// </list>
	/// </summary>
	sealed class StampedFile<T> : KeptFiles.IKept where T : class {
		/// <summary>Longer than any Windows file system's step in last-write times: NTFS's is the clock's tick, FAT's two seconds.</summary>
		internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

		static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

		public delegate T? Parser(ReadOnlySpan<byte> utf8Json);

		readonly Parser parse;
		readonly Func<T, T> copy;
		readonly object gate = new();
		Held? held;

		/// <param name="Hash">The bytes' SHA-256 while the file may still be rewritten within its last-write time's step; null once it has settled.</param>
		sealed record Held(string Path, long Length, DateTime WrittenUtc, T? Value, byte[]? Hash);

		/// <param name="parse">The file's bytes (UTF-8, without a byte order mark) to its contents.</param>
		/// <param name="copy">A copy for one caller, sharing nothing it could change with the one kept.</param>
		public StampedFile(Parser parse, Func<T, T> copy) {
			this.parse = parse;
			this.copy = copy;
			KeptFiles.Add(this);
		}

		/// <summary>How many times the file was parsed: the tests count them.</summary>
		internal int Parses { get; private set; }

		/// <summary>Whether anything is kept: the tests look.</summary>
		internal bool Holds {
			get { lock (gate) return held != null; }
		}

		/// <summary>The file's contents, a copy of the kept ones when the file hasn't changed since; null when there's no file.</summary>
		/// <exception cref="IOException">The file can't be read. Nothing is kept, and the next read tries again.</exception>
		/// <exception cref="System.Text.Json.JsonException">The file isn't what <c>parse</c> reads, as above.</exception>
		public T? Read(string path) {
			// Before the file is looked at: the time it settles by is then never later than it really is.
			DateTime now = DateTime.UtcNow;
			if (!KeptFiles.InUse(now)) {
				// Nobody is looking at the page: read as it was before anything was kept, and keep nothing.
				lock (gate) held = null;
				if (!File.Exists(path)) return null;
				T? fresh = Parse(File.ReadAllBytes(path));
				lock (gate) Parses++;
				return fresh;
			}
			T? value;
			lock (gate) value = Current(path, now);
			return value == null ? null : copy(value);
		}

		/// <summary>Lets go of what's kept; the next read parses the file again. True if anything was.</summary>
		public bool Drop() {
			lock (gate) {
				bool had = held != null;
				held = null;
				return had;
			}
		}

		T? Parse(ReadOnlySpan<byte> bytes) => parse(bytes.StartsWith(Bom) ? bytes[Bom.Length..] : bytes);

		T? Current(string path, DateTime now) {
			var file = new FileInfo(path);
			if (!file.Exists) {
				held = null;
				return null;
			}
			long length = file.Length;
			DateTime written = file.LastWriteTimeUtc;
			bool settled = now - written >= Settle;
			if (held is { } h && h.Path == path && h.Length == length && h.WrittenUtc == written) {
				if (h.Hash == null) return h.Value;
				byte[] again = File.ReadAllBytes(path);
				if (SHA256.HashData(again).AsSpan().SequenceEqual(h.Hash)) {
					// Read once it had settled, and still the same: nothing written after it can have its time.
					if (settled) held = h with { Hash = null };
					return h.Value;
				}
				return Keep(path, again, length, written, settled);
			}
			// Read after its length and time were: a newer file read here has a newer time, which the next read sees.
			return Keep(path, File.ReadAllBytes(path), length, written, settled);
		}

		T? Keep(string path, byte[] bytes, long length, DateTime written, bool settled) {
			T? value = Parse(bytes);
			Parses++;
			held = new Held(path, length, written, value, settled ? null : SHA256.HashData(bytes));
			return value;
		}

		/// <summary>
		/// What this process has just written to <paramref name="path"/> (<paramref name="contents"/>, saying <paramref name="value"/>),
		/// kept as if it had been read: the change shows at once, without parsing the file again. It's checked against the file by
		/// hash until the file settles, like any file just written, so another process's write straight after it is still seen.
		/// Nothing is kept while the page isn't in use (<see cref="KeptFiles"/>).
		/// </summary>
		public void Wrote(string path, T value, string contents) {
			bool inUse = KeptFiles.InUse(DateTime.UtcNow);
			lock (gate) {
				var file = new FileInfo(path);
				held = inUse && file.Exists ? new Held(path, file.Length, file.LastWriteTimeUtc, copy(value), SHA256.HashData(Encoding.UTF8.GetBytes(contents))) : null;
			}
		}
	}

	/// <summary>
	/// When <see cref="StampedFile{T}"/>s keep what they read: while the review page is in use, that is within <see cref="Idle"/>
	/// of the page's last request (<see cref="PageUsed"/>). The page asks for its state every 15 seconds while it's open, and
	/// at least once a minute from a tab in the background. Manor's tiles and the other agents' requests don't count: Manor
	/// reads the disk glance every minute all day, and the report kept for it would stay in memory all day.
	/// Once the page has gone unused that long, <see cref="RunAsync"/> lets go of what's kept, and the reads after that
	/// keep nothing until the page is used again.
	/// </summary>
	static class KeptFiles {
		internal static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

		/// <summary>One of the files kept.</summary>
		internal interface IKept {
			bool Drop();
		}

		static readonly List<IKept> all = [];
		static long lastUse = long.MinValue; // the page's last request, UTC ticks; MinValue: none yet

		internal static void Add(IKept kept) {
			lock (all) all.Add(kept);
		}

		/// <summary>The review page asked for something, at <paramref name="now"/>.</summary>
		internal static void PageUsed(DateTime now) => Interlocked.Exchange(ref lastUse, now.Ticks);

		/// <summary>Whether the page was used within <see cref="Idle"/> of <paramref name="now"/>.</summary>
		internal static bool InUse(DateTime now) {
			long last = Interlocked.Read(ref lastUse);
			return last != long.MinValue && now.Ticks - last < Idle.Ticks;
		}

		/// <summary>A request the review page makes: not one of Manor's (its tiles, its ping), the installer's or another agent's.</summary>
		internal static bool FromThePage(PathString path) =>
			path.StartsWithSegments("/api") && !NotThePages.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

		static readonly string[] NotThePages = ["/api/ping", "/api/glance", "/api/disk", "/api/pictures", "/api/quit"];

		/// <summary>Lets go of every file kept, when the page isn't in use at <paramref name="now"/>. True if anything was kept.</summary>
		internal static bool DropIfIdle(DateTime now) {
			if (InUse(now)) return false;
			IKept[] files;
			lock (all) files = [.. all];
			bool dropped = false;
			foreach (IKept f in files) dropped |= f.Drop();
			return dropped;
		}

		/// <summary>
		/// In the page's process: once a minute, lets go of the files kept once the page has gone unused, and has the memory
		/// they took handed back to Windows (an idle process would otherwise hold it until it next allocates a lot).
		/// </summary>
		internal static async Task RunAsync(CancellationToken ct) {
			try {
				while (!ct.IsCancellationRequested) {
					await Task.Delay(TimeSpan.FromMinutes(1), ct);
					if (!DropIfIdle(DateTime.UtcNow)) continue;
					long before = GC.GetTotalMemory(false);
					GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
					AgentPaths.AppendLog($"the review page unused for {Idle.TotalMinutes:0} minutes: the report, decisions and index it read are let go ({before / 1048576} MB in use before, {GC.GetTotalMemory(false) / 1048576} MB after)");
				}
			}
			catch (OperationCanceledException) { }
		}
	}
}
