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
	/// </list>
	/// </summary>
	sealed class StampedFile<T> where T : class {
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
		}

		/// <summary>How many times the file was parsed: the tests count them.</summary>
		internal int Parses { get; private set; }

		/// <summary>The file's contents, a copy of the kept ones when the file hasn't changed since; null when there's no file.</summary>
		/// <exception cref="IOException">The file can't be read. Nothing is kept, and the next read tries again.</exception>
		/// <exception cref="System.Text.Json.JsonException">The file isn't what <c>parse</c> reads, as above.</exception>
		public T? Read(string path) {
			T? value;
			lock (gate) value = Current(path);
			return value == null ? null : copy(value);
		}

		T? Current(string path) {
			// Before the file is looked at: the time it settles by is then never later than it really is.
			DateTime now = DateTime.UtcNow;
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
			ReadOnlySpan<byte> json = bytes;
			if (json.StartsWith(Bom)) json = json[Bom.Length..];
			T? value = parse(json);
			Parses++;
			held = new Held(path, length, written, value, settled ? null : SHA256.HashData(bytes));
			return value;
		}

		/// <summary>
		/// What this process has just written to <paramref name="path"/> (<paramref name="contents"/>, saying <paramref name="value"/>),
		/// kept as if it had been read: the change shows at once, without parsing the file again. It's checked against the file by
		/// hash until the file settles, like any file just written, so another process's write straight after it is still seen.
		/// </summary>
		public void Wrote(string path, T value, string contents) {
			lock (gate) {
				var file = new FileInfo(path);
				held = file.Exists ? new Held(path, file.Length, file.LastWriteTimeUtc, copy(value), SHA256.HashData(Encoding.UTF8.GetBytes(contents))) : null;
			}
		}
	}
}
