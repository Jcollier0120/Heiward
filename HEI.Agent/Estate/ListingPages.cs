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

using System.Text.Json;

namespace HEI.Agent.Estate {
	/// <summary>
	/// Another PC's listing, version 2, as this PC's scan of it asks for it: in pages (<c>GET /api/peer/scan/pages</c>,
	/// <see cref="LentListing.WritePage"/>), one at a time, so however big that PC's library is, no answer is more than a
	/// few megabytes and neither PC holds the listing as one string. Every page carries the listing's stamp: a listing
	/// that changes while its pages come is asked for again from its first page.
	/// <list type="bullet">
	/// <item><c>?n=0&amp;have=&lt;stamp&gt;</c>: the first page, or <c>{"v":2,"same":true,"stamp":"…"}</c> when that PC's
	/// files are as they were at the stamp this PC kept: then nothing is read at all.</item>
	/// <item><c>?n=&lt;page&gt;&amp;stamp=&lt;stamp&gt;</c>: each page after, or 409 <c>{"code":"changed"}</c> once the listing
	/// isn't that one any more.</item>
	/// </list>
	/// And the listing as the scans for that PC keep it here (<see cref="Save"/>), read a line at a time too.
	/// </summary>
	static class ListingPages {
		public const int Version = 2;

		/// <summary>The most one page's answer may be: a page is about 2 MB, and its paths' letters are as they are.</summary>
		public const int PageLimit = 32 * 1024 * 1024;

		static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(2);

		/// <summary>How many times a listing that changed as it came is asked for again, before the scan says so and stops.</summary>
		const int Rounds = 3;

		/// <summary>What asking for that PC's listing came to: the listing, or word it's as it was, or why not, in words for its page.</summary>
		internal sealed record Fetched(RemoteListing? Listing, bool Same, string? Problem, int LeftOut = 0);

		/// <summary>One page, read: the listing's own facts, the roots, and the page's files (<see cref="LeftOut"/>: those no stand-in can be made for).</summary>
		internal sealed record PageRead(string Build, string Stamp, int Files, int Pages, int Page, List<(string Root, string Stamp, int Files)> Roots,
			int Of, List<ListedFile> List, int LeftOut);

		/// <summary>
		/// That PC's listing, page after page; <paramref name="have"/>, the stamp kept from the last scan, gets word that
		/// it's as it was instead. <paramref name="progress"/> hears how many files have come of how many.
		/// </summary>
		internal static async Task<Fetched> FetchAsync(string pc, string name, string? have, Action<int, int> progress, CancellationToken ct, int limit = PageLimit) {
			for (int round = 0; round < Rounds; round++) {
				if (round > 0) AgentPaths.AppendLog($"scan for {name}: its list of files changed as it came, so it's asked for again");
				string first = "scan/pages?n=0" + (have != null ? "&have=" + Uri.EscapeDataString(have) : "");
				var (status, json) = await EstateLink.GetJsonAsync(EstateLink.PeerPath(pc, first), PageTimeout, ct, limit);
				PageRead page;
				using (json) {
					JsonElement? root = json?.RootElement;
					if (status == 404 && Code(root) == null)
						return Problem($"Heiward on this PC is older than on {Me}: update Heiward here");
					if (status != 200 || root is not { ValueKind: JsonValueKind.Object } answer) return Problem(Refused(status));
					if (answer.TryGetProperty("same", out JsonElement same) && same.ValueKind == JsonValueKind.True) return new Fetched(null, true, null);
					if (Read(answer) is not { Page: 0 } read) return Problem(Unreadable);
					page = read;
				}
				string stamp = page.Stamp;
				int pages = page.Pages, total = page.Files;
				var roots = page.Roots.Select(r => new ListedRoot(r.Root, r.Stamp, new List<ListedFile>(Math.Min(r.Files, 1_000_000)))).ToList();
				int[] leftOutOf = new int[roots.Count];
				int got = 0, leftOut = 0;
				bool changed = false;
				for (int n = 0; ; n++) {
					if (n > 0) {
						(status, json) = await EstateLink.GetJsonAsync(EstateLink.PeerPath(pc, $"scan/pages?n={n}&stamp={Uri.EscapeDataString(stamp)}"), PageTimeout, ct, limit);
						using (json) {
							JsonElement? root = json?.RootElement;
							if (status == 409 && Code(root) == "changed") {
								changed = true;
								break;
							}
							if (status != 200 || root is not { ValueKind: JsonValueKind.Object } answer) return Problem(Refused(status));
							if (Read(answer) is not { } read) return Problem(Unreadable);
							if (read.Stamp != stamp) {
								changed = true;
								break;
							}
							if (read.Page != n || read.Pages != pages || read.Files != total || !SameRoots(read.Roots, page.Roots)) return Problem(Unreadable);
							page = read;
						}
					}
					if (page.List.Count + page.LeftOut > 0) {
						if (page.Of < 0 || page.Of >= roots.Count) return Problem(Unreadable);
						roots[page.Of].Files.AddRange(page.List);
						leftOutOf[page.Of] += page.LeftOut;
					}
					got += page.List.Count + page.LeftOut;
					leftOut += page.LeftOut;
					progress(got, total);
					if (n == pages - 1) break;
				}
				if (changed) continue;
				// Every file each root lists came, once (a root's files come in order, page after page).
				for (int i = 0; i < roots.Count; i++)
					if (roots[i].Files.Count + leftOutOf[i] != page.Roots[i].Files) return Problem(Unreadable);
				if (got != total) return Problem(Unreadable);
				if (leftOut > 0) AgentPaths.AppendLog($"scan for {name}: {leftOut:N0} of its files were left out: their paths can't stand in this PC's mirror");
				return new Fetched(new RemoteListing(Version, page.Build, stamp, roots), false, null, leftOut);
			}
			return Problem($"The files on this PC kept changing while {Me} listed them. Try again in a while");
		}

		static Fetched Problem(string why) => new(null, false, why);

		static string? Code(JsonElement? root) => root is { ValueKind: JsonValueKind.Object } o ? EstateLink.Str(o, "code") : null;

		/// <summary>The PC scanning, by name: these words show on the page of the PC scanned, which is "this PC" there.</summary>
		static string Me => Environment.MachineName;

		static string Unreadable => $"{Me} can't read this PC's list of files: update Heiward on both PCs";

		/// <summary>An answer that isn't a page, in words for the page of the PC scanned.</summary>
		static string Refused(int status) => status switch {
			410 => $"This PC isn't asking {Me} to scan it any more",
			_ => $"This PC's list of files didn't reach {Me}. Try again; Castellan must be running on both PCs",
		};

		static bool SameRoots(List<(string Root, string Stamp, int Files)> a, List<(string Root, string Stamp, int Files)> b) => a.SequenceEqual(b);

		/// <summary>One page's answer read; null when it isn't one this build reads. Files no stand-in can be made for are left out, and counted.</summary>
		internal static PageRead? Read(JsonElement page) {
			if (!page.TryGetProperty("v", out JsonElement v) || !v.TryGetInt32(out int version) || version != Version) return null;
			if (EstateLink.Str(page, "stamp") is not string stamp || !Int(page, "files", out int files) || !Int(page, "pages", out int pages) || pages < 1 ||
				!Int(page, "page", out int n) || n >= pages) return null;
			if (!page.TryGetProperty("roots", out JsonElement rs) || rs.ValueKind != JsonValueKind.Array) return null;
			var roots = new List<(string, string, int)>();
			foreach (JsonElement r in rs.EnumerateArray()) {
				if (r.ValueKind != JsonValueKind.Object || EstateLink.Str(r, "root") is not string rootPath || EstateLink.Str(r, "stamp") is not string rootStamp ||
					!Int(r, "files", out int count)) return null;
				roots.Add((rootPath, rootStamp, count));
			}
			int of = page.TryGetProperty("of", out JsonElement o) && o.TryGetInt32(out int at) ? at : -1;
			if (!page.TryGetProperty("list", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return null;
			var listed = new List<ListedFile>(list.GetArrayLength());
			int leftOut = 0;
			string? under = of >= 0 && of < roots.Count ? roots[of].Item1 : null;
			foreach (JsonElement f in list.EnumerateArray()) {
				if (f.ValueKind != JsonValueKind.Array || f.GetArrayLength() != 3 || f[0].ValueKind != JsonValueKind.String) return null;
				if (!f[1].TryGetInt64(out long size) || !f[2].TryGetInt64(out long ticks)) return null;
				string path = f[0].GetString()!;
				if (under == null) return null;
				if (size < 0 || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !ListingLine.Holds(path) || !RemotePath.Valid(path, under)) {
					leftOut++;
					continue;
				}
				listed.Add(new ListedFile(path, size, ticks));
			}
			return new PageRead(EstateLink.Str(page, "build") ?? "", stamp, files, pages, n, roots, of, listed, leftOut);
		}

		static bool Int(JsonElement o, string name, out int value) {
			value = 0;
			return o.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out value) && value >= 0;
		}

		// ================= The listing kept here =================

		/// <summary>
		/// The listing as the scans for that PC keep it (<c>listing.tsv</c> in its home), for the next scan: a header line
		/// (<c>{"v":2,"build":"…","stamp":"…","roots":[{"root":"C:\\","stamp":"…","files":500000},…]}</c>), then a line a file
		/// (<see cref="ListingLine"/>), root after root. Written a file at a time, and in whole (a temporary file, then moved).
		/// </summary>
		internal static void Save(RemoteListing listing, string path) {
			string tmp = path + $".{Environment.ProcessId}.tmp";
			using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) {
				using (var w = new Utf8JsonWriter(output)) {
					w.WriteStartObject();
					w.WriteNumber("v", Version);
					w.WriteString("build", listing.Build);
					w.WriteString("stamp", listing.Stamp);
					w.WriteStartArray("roots");
					foreach (ListedRoot r in listing.Roots) {
						w.WriteStartObject();
						w.WriteString("root", r.Root);
						w.WriteString("stamp", r.Stamp);
						w.WriteNumber("files", r.Files.Count);
						w.WriteEndObject();
					}
					w.WriteEndArray();
					w.WriteEndObject();
				}
				output.WriteByte((byte)'\n');
				byte[] buffer = new byte[4096];
				foreach (ListedRoot r in listing.Roots)
					foreach (ListedFile f in r.Files)
						if (ListingLine.Holds(f.Path)) output.Write(buffer, 0, ListingLine.Encode(f.Path, f.Size, f.ModifiedTicks, ref buffer));
			}
			File.Move(tmp, path, overwrite: true);
		}

		/// <summary>The listing kept by <see cref="Save"/>, read a line at a time; null when there's none, or it isn't whole.</summary>
		internal static RemoteListing? Load(string path) {
			try {
				if (!File.Exists(path)) return null;
				using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
				List<ListedRoot>? roots = null;
				List<int>? counts = null;
				string? build = null, stamp = null;
				int root = 0;
				bool whole = true;
				ListingLine.ForEach(input, line => {
					if (!whole) return;
					if (roots == null) {
						try {
							using JsonDocument doc = JsonDocument.Parse(line.ToArray());
							JsonElement h = doc.RootElement;
							if (!h.TryGetProperty("v", out JsonElement v) || !v.TryGetInt32(out int version) || version != Version ||
								EstateLink.Str(h, "stamp") is not string s || !h.TryGetProperty("roots", out JsonElement rs) || rs.ValueKind != JsonValueKind.Array) {
								whole = false;
								return;
							}
							build = EstateLink.Str(h, "build") ?? "";
							stamp = s;
							roots = new List<ListedRoot>();
							counts = new List<int>();
							foreach (JsonElement r in rs.EnumerateArray()) {
								if (EstateLink.Str(r, "root") is not string rp || EstateLink.Str(r, "stamp") is not string st || !Int(r, "files", out int c)) {
									whole = false;
									return;
								}
								roots.Add(new ListedRoot(rp, st, new List<ListedFile>(Math.Min(c, 1_000_000))));
								counts.Add(c);
							}
						}
						catch (JsonException) {
							whole = false;
						}
						return;
					}
					while (root < roots.Count && roots[root].Files.Count == counts![root]) root++;
					if (root == roots.Count || ListingLine.Parse(line) is not { } f) {
						whole = false;
						return;
					}
					roots[root].Files.Add(f);
				});
				if (!whole || roots == null || stamp == null) return null;
				for (int i = 0; i < roots.Count; i++)
					if (roots[i].Files.Count != counts![i]) return null;
				return new RemoteListing(Version, build!, stamp, roots);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return null;
			}
		}

		/// <summary>The stamp of the listing kept at <paramref name="path"/>, from its header alone; null when there's none.</summary>
		internal static string? StampOf(string path) {
			try {
				if (!File.Exists(path)) return null;
				using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
				if (reader.ReadLine() is not { Length: > 0 } head) return null;
				using JsonDocument doc = JsonDocument.Parse(head);
				return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("v", out JsonElement v) && v.TryGetInt32(out int version) &&
					version == Version ? EstateLink.Str(doc.RootElement, "stamp") : null;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return null;
			}
		}

		/// <summary>The files of the listing kept at <paramref name="path"/> that <paramref name="wanted"/> names, read a line at a time.</summary>
		internal static Dictionary<string, ListedFile> Find(string path, IReadOnlySet<string> wanted) {
			var found = new Dictionary<string, ListedFile>(StringComparer.OrdinalIgnoreCase);
			try {
				if (wanted.Count == 0 || !File.Exists(path)) return found;
				using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
				bool head = true;
				ListingLine.ForEach(input, line => {
					if (head) {
						head = false;
						return;
					}
					if (ListingLine.Parse(line) is { } f && wanted.Contains(f.Path)) found[f.Path] = f;
				});
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			return found;
		}
	}
}
