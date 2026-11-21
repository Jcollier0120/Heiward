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

using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent.Estate {
	/// <summary>
	/// The listing this PC lends to another PC's scan of it (<see cref="ScanForOthers"/>), kept on disk rather than in
	/// memory: a library of any size goes over in pages (<c>scan/pages</c>, <see cref="WritePage"/>), and this PC holds
	/// little of it while the other PC scans, which can take hours. Written once, as the scan is asked for: a line a file
	/// (<see cref="ListingLine"/>), root after root, each root's files once (whatever their case), in ordinal order, as
	/// version 1's listing had them (<see cref="RemoteListing.FromKept"/>), so the stamps are the same. In memory: the
	/// roots, where each page starts, and where each file's line is by its path's key, 16 bytes a file. Deleted when the
	/// loan ends.
	/// <para>A page is one root's files, at most <see cref="PageFiles"/> of them and about <see cref="PageBytes"/> of
	/// lines, its JSON about as big: far under what one answer over the link may be, however big the library.</para>
	/// </summary>
	sealed class LentListing : IDisposable {
		public const int PageFiles = 20_000;
		public const int PageBytes = 2 * 1024 * 1024;

		/// <summary>One scanned folder: its root, its stamp, how many files it lists, and where its lines are in the file.</summary>
		public sealed record Root(string Path, string Stamp, int Files, long Offset, long Length);

		/// <summary>One page: its root (its index; -1 for the one page of a listing with no files), and its lines in the file.</summary>
		public readonly record struct Page(int Root, int Files, long Offset, int Length);

		public string Build { get; }
		public string Stamp { get; }
		public int Files { get; }
		public IReadOnlyList<Root> Roots { get; }
		public IReadOnlyList<Page> Pages { get; }

		readonly string file;
		readonly SafeFileHandle handle;
		/// <summary>Each file's key's hash (<see cref="KeyHash"/>), in order, and where its line starts.</summary>
		readonly ulong[] hashes;
		readonly long[] lines;

		LentListing(string file, string build, string stamp, int files, List<Root> roots, List<Page> pages, ulong[] hashes, long[] lines) {
			(this.file, Build, Stamp, Files, Roots, Pages, this.hashes, this.lines) = (file, build, stamp, files, roots, pages, hashes, lines);
			handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
		}

		/// <summary>Where the lent listings are written: one at a time, and any left by a Heiward that ended without its loan's end cleared.</summary>
		public static string Folder => Path.Combine(AgentPaths.Home, "lent");

		/// <summary>
		/// The listing written from what this PC keeps for its own scans (<see cref="ListingPlan.Kept"/>), a root at a time,
		/// the roots in ordinal order: one root's files are in memory while they're written, and none after.
		/// </summary>
		public static LentListing Write(IEnumerable<(string Root, List<(string Path, long Size, DateTime ModifiedUtc)> Files)> kept, string build,
			int pageFiles = PageFiles, int pageBytes = PageBytes) {
			Directory.CreateDirectory(Folder);
			string file = Path.Combine(Folder, $"listing-{Guid.NewGuid():N}.tsv");
			foreach (string old in Directory.EnumerateFiles(Folder, "listing-*.tsv"))
				if (old != file) TryDelete(old); // a file still read meanwhile goes once it's let go (FileShare.Delete)
			var roots = new List<Root>();
			var pages = new List<Page>();
			var hashes = new List<ulong>();
			var lines = new List<long>();
			long at = 0;
			try {
				using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16)) {
					byte[] buffer = new byte[4096];
					foreach (var (root, files) in kept) {
						if (roots.Count > 0 && string.CompareOrdinal(roots[^1].Path, root) > 0)
							throw new ArgumentException("The roots must come in ordinal order.", nameof(kept));
						List<(string Path, long Size, long Ticks)> listed = OnceInOrder(root, files);
						using var stamp = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
						long rootAt = at;
						int pageFilesNow = 0;
						long pageAt = at;
						foreach (var (path, size, ticks) in listed) {
							int n = ListingLine.Encode(path, size, ticks, ref buffer);
							if (pageFilesNow > 0 && (pageFilesNow >= pageFiles || at - pageAt + n > pageBytes)) {
								pages.Add(new Page(roots.Count, pageFilesNow, pageAt, checked((int)(at - pageAt))));
								(pageFilesNow, pageAt) = (0, at);
							}
							output.Write(buffer, 0, n);
							stamp.AppendData(buffer, 0, n); // the line is version 1's stamp's text for the file
							hashes.Add(KeyHash(KeyFor(path)));
							lines.Add(at);
							at += n;
							pageFilesNow++;
						}
						if (pageFilesNow > 0) pages.Add(new Page(roots.Count, pageFilesNow, pageAt, checked((int)(at - pageAt))));
						roots.Add(new Root(root, RemoteListing.Hex(stamp.GetHashAndReset()), listed.Count, rootAt, at - rootAt));
					}
				}
				if (pages.Count == 0) pages.Add(new Page(-1, 0, 0, 0));
				ulong[] byHash = [.. hashes];
				long[] whereLine = [.. lines];
				Array.Sort(byHash, whereLine);
				string all = RemoteListing.Hash(string.Join("\n", roots.Select(r => r.Path + "\t" + r.Stamp)));
				return new LentListing(file, build, all, whereLine.Length, roots, pages, byHash, whereLine);
			}
			catch {
				TryDelete(file);
				throw;
			}
		}

		/// <summary>A root's files once each, ignoring case (the first as listed), that another PC can stand in for, in ordinal order.</summary>
		static List<(string Path, long Size, long Ticks)> OnceInOrder(string root, List<(string Path, long Size, DateTime ModifiedUtc)> files) {
			var seen = new HashSet<string>(files.Count, StringComparer.OrdinalIgnoreCase);
			var listed = new List<(string Path, long Size, long Ticks)>(files.Count);
			foreach (var (path, size, modified) in files)
				if (seen.Add(path) && ListingLine.Holds(path) && RemotePath.Valid(path, root)) listed.Add((path, size, modified.Ticks));
			// Distinct ignoring case is distinct as ordinal, so the order is version 1's (a stable sort's) too.
			listed.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
			return listed;
		}

		/// <summary>A path's key, as the kit's PeerFiles knows a file (<see cref="PeerFiles.KeyOf"/>); lower case for any other path.</summary>
		static string KeyFor(string path) => PeerFiles.KeyOf(path) ?? path.ToLowerInvariant();

		/// <summary>FNV-1a, 64 bits: where a key's line is looked for. Two keys may share one; each line found is checked.</summary>
		static ulong KeyHash(string key) {
			ulong h = 14695981039346656037UL;
			foreach (char c in key) {
				h ^= c;
				h *= 1099511628211UL;
			}
			return h;
		}

		// ================= Looking a file up =================

		/// <summary>The file listed by that path (whatever its case), as it's listed; null for one that isn't.</summary>
		public ListedFile? Find(string path) => FindKey(KeyFor(path));

		/// <summary>The kit's PeerFiles' list (<see cref="PeerFiles.ReplaceWith"/>): the path as listed of the file with that key, or null.</summary>
		public string? PathOf(string key) => FindKey(key) is { } f && PeerFiles.KeyOf(f.Path) == key ? f.Path : null;

		ListedFile? FindKey(string key) {
			ulong h = KeyHash(key);
			int i = Array.BinarySearch(hashes, h);
			if (i < 0) return null;
			while (i > 0 && hashes[i - 1] == h) i--;
			for (; i < hashes.Length && hashes[i] == h; i++)
				if (LineAt(lines[i]) is { } f && KeyFor(f.Path) == key) return f;
			return null;
		}

		/// <summary>The line that starts at <paramref name="offset"/>: a path is at most a few thousand bytes, so 8 KB hold it.</summary>
		ListedFile? LineAt(long offset) {
			byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
			try {
				int n = RandomAccess.Read(handle, buffer.AsSpan(0, 8192), offset);
				int end = Array.IndexOf(buffer, (byte)'\n', 0, n);
				return end < 0 ? null : ListingLine.Parse(buffer.AsSpan(0, end));
			}
			finally {
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}

		void ReadExactly(long offset, Span<byte> into) {
			while (into.Length > 0) {
				int n = RandomAccess.Read(handle, into, offset);
				if (n == 0) throw new EndOfStreamException("the lent listing ended early");
				into = into[n..];
				offset += n;
			}
		}

		// ================= Its answers =================

		/// <summary>Pages' JSON: their paths' letters as they are (UTF-8), not escaped as for a web page.</summary>
		static readonly JsonWriterOptions PageJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

		/// <summary>
		/// Page <paramref name="n"/> (<c>GET /api/peer/scan/pages?n=…</c>, <see cref="ListingPages"/>), as JSON:
		/// <code>{"v":2,"build":"…","stamp":"…","files":671761,"pages":34,"page":0,"roots":[{"root":"C:\\","stamp":"…","files":500000},…],"of":0,"list":[["C:\\Photos\\a.jpg",2048000,638000000000000000],…]}</code>
		/// Every page names all the roots, with how many files each lists; <c>of</c> is the root its files are in.
		/// </summary>
		public void WritePage(int n, IBufferWriter<byte> to) {
			Page page = Pages[n];
			using var w = new Utf8JsonWriter(to, PageJson);
			w.WriteStartObject();
			w.WriteNumber("v", ListingPages.Version);
			w.WriteString("build", Build);
			w.WriteString("stamp", Stamp);
			w.WriteNumber("files", Files);
			w.WriteNumber("pages", Pages.Count);
			w.WriteNumber("page", n);
			w.WriteStartArray("roots");
			foreach (Root r in Roots) {
				w.WriteStartObject();
				w.WriteString("root", r.Path);
				w.WriteString("stamp", r.Stamp);
				w.WriteNumber("files", r.Files);
				w.WriteEndObject();
			}
			w.WriteEndArray();
			if (page.Root >= 0) w.WriteNumber("of", page.Root);
			w.WriteStartArray("list");
			byte[] bytes = ArrayPool<byte>.Shared.Rent(Math.Max(1, page.Length));
			try {
				ReadExactly(page.Offset, bytes.AsSpan(0, page.Length));
				WriteFiles(w, bytes.AsSpan(0, page.Length));
			}
			finally {
				ArrayPool<byte>.Shared.Return(bytes);
			}
			w.WriteEndArray();
			w.WriteEndObject();
		}

		/// <summary>
		/// The whole listing as version 1 answered it (<c>GET /api/peer/scan/listing</c>, <see cref="RemoteListing"/>):
		/// the same bytes, written as they're read, a megabyte at a time.
		/// </summary>
		public async Task WriteWholeAsync(Stream to, CancellationToken ct) {
			var buffer = new ArrayBufferWriter<byte>(1 << 20);
			await using var w = new Utf8JsonWriter(buffer);
			async Task Send() {
				await w.FlushAsync(ct);
				await to.WriteAsync(buffer.WrittenMemory, ct);
				buffer.ResetWrittenCount();
			}
			w.WriteStartObject();
			w.WriteNumber("v", RemoteListing.Version);
			w.WriteString("build", Build);
			w.WriteString("stamp", Stamp);
			w.WriteStartArray("roots");
			byte[] chunk = ArrayPool<byte>.Shared.Rent(1 << 20);
			try {
				foreach (Root r in Roots) {
					w.WriteStartObject();
					w.WriteString("root", r.Path);
					w.WriteString("stamp", r.Stamp);
					w.WriteStartArray("files");
					for (long at = r.Offset, end = r.Offset + r.Length; at < end;) {
						int n = (int)Math.Min(1 << 20, end - at);
						ReadExactly(at, chunk.AsSpan(0, n));
						int whole = Array.LastIndexOf(chunk, (byte)'\n', n - 1) + 1; // the lines read whole
						if (whole == 0) throw new InvalidDataException("a line of the lent listing is too long");
						WriteFiles(w, chunk.AsSpan(0, whole));
						at += whole;
						await Send();
					}
					w.WriteEndArray();
					w.WriteEndObject();
				}
			}
			finally {
				ArrayPool<byte>.Shared.Return(chunk);
			}
			w.WriteEndArray();
			w.WriteEndObject();
			await Send();
		}

		/// <summary>Each line of <paramref name="lines"/> (whole lines, each ending in a line break) as a file's array.</summary>
		static void WriteFiles(Utf8JsonWriter w, ReadOnlySpan<byte> lines) {
			while (lines.Length > 0) {
				int end = lines.IndexOf((byte)'\n');
				if (end < 0) throw new InvalidDataException("the lent listing ended within a line");
				if (ListingLine.Parse(lines[..end]) is not { } f) throw new InvalidDataException("a line of the lent listing isn't a file's");
				w.WriteStartArray();
				w.WriteStringValue(f.Path);
				w.WriteNumberValue(f.Size);
				w.WriteNumberValue(f.ModifiedTicks);
				w.WriteEndArray();
				lines = lines[(end + 1)..];
			}
		}

		/// <summary>The loan is over: the file goes (once nothing reads it).</summary>
		public void Dispose() {
			handle.Dispose();
			TryDelete(file);
		}

		static void TryDelete(string path) {
			try { File.Delete(path); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}
	}

	/// <summary>
	/// A file of a listing on disk, a line: <c>path\tsize\tticks\n</c> in UTF-8, as version 1's stamps hash it
	/// (<see cref="RemoteListing.StampOf"/>). No Windows file name holds a tab or a line break, and a path that does isn't listed.
	/// </summary>
	static class ListingLine {
		/// <summary>Whether a path can be a line: no control character (a tab or a line break), no half of a surrogate pair alone.</summary>
		public static bool Holds(string path) {
			for (int i = 0; i < path.Length; i++) {
				char c = path[i];
				if (c < ' ') return false;
				if (char.IsHighSurrogate(c) && i + 1 < path.Length && char.IsLowSurrogate(path[i + 1])) i++;
				else if (char.IsSurrogate(c)) return false;
			}
			return path.Length > 0;
		}

		/// <summary>The line into <paramref name="buffer"/> (made bigger when it must be); its length.</summary>
		public static int Encode(string path, long size, long ticks, ref byte[] buffer) {
			int most = Encoding.UTF8.GetMaxByteCount(path.Length) + 48;
			if (buffer.Length < most) buffer = new byte[most];
			int n = Encoding.UTF8.GetBytes(path, buffer);
			buffer[n++] = (byte)'\t';
			Utf8Formatter.TryFormat(size, buffer.AsSpan(n), out int w);
			n += w;
			buffer[n++] = (byte)'\t';
			Utf8Formatter.TryFormat(ticks, buffer.AsSpan(n), out w);
			n += w;
			buffer[n++] = (byte)'\n';
			return n;
		}

		/// <summary>A line (without its line break) read back; null when it isn't one.</summary>
		public static ListedFile? Parse(ReadOnlySpan<byte> line) {
			int last = line.LastIndexOf((byte)'\t');
			if (last <= 0) return null;
			int middle = line[..last].LastIndexOf((byte)'\t');
			if (middle <= 0) return null;
			if (!Utf8Parser.TryParse(line[(middle + 1)..last], out long size, out int a) || a != last - middle - 1 || size < 0) return null;
			if (!Utf8Parser.TryParse(line[(last + 1)..], out long ticks, out int b) || b != line.Length - last - 1) return null;
			if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;
			return new ListedFile(Encoding.UTF8.GetString(line[..middle]), size, ticks);
		}

		public delegate void Each(ReadOnlySpan<byte> line);

		/// <summary>Each whole line of <paramref name="input"/>, a megabyte read at a time; a last line with no line break is left out (cut short).</summary>
		public static void ForEach(Stream input, Each each) {
			byte[] buffer = new byte[1 << 20];
			int held = 0;
			while (true) {
				int n = input.Read(buffer, held, buffer.Length - held);
				if (n == 0) return;
				held += n;
				int start = 0;
				for (int end; (end = Array.IndexOf(buffer, (byte)'\n', start, held - start)) >= 0; start = end + 1)
					each(buffer.AsSpan(start, end - start));
				if (start == 0 && held == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2); // a line longer than the buffer
				Buffer.BlockCopy(buffer, start, buffer, 0, held - start);
				held -= start;
			}
		}
	}
}
