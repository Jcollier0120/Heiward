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
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace HEI.Agent.Estate {
	/// <summary>
	/// The records of a PC's scans by another: the scan's database, the listing it scanned, its photos' hashes, looks, words
	/// and dates, its report, what it found, and its log. They're the scanned PC's, kept in its own Heiward folder
	/// (<see cref="StoreFor"/>). The PC scanning holds them only while it scans, in that PC's home there
	/// (<see cref="RemoteScanner.HomeFor"/>): it fetches them from the scanned PC as the scan starts (<c>scan/records</c>),
	/// and the scanned PC takes them back as it ends, done or not (<c>scan/kept</c>, <see cref="TakeAsync"/>). Nothing of
	/// them stays on the PC that scanned.
	/// <para>As on every route between the PCs, the PC that gets them asks for them: their list (<see cref="Offered"/>), then
	/// each file through the kit's gentle file route (<see cref="PeerFiles"/>, read by <see cref="EstateBytes"/>), a piece at
	/// a time, a file broken off picked up where it stopped. They come into a folder of their own (<see cref="Coming"/>) and
	/// take the old ones' place only once all have come.</para>
	/// </summary>
	static class ScanRecords {
		public const int Version = 1;
		/// <summary>The scans' log, kept with their records: added to, where a scan brings only its own lines.</summary>
		public const string Log = "heiward.log";
		/// <summary>In a home, where records being fetched wait until all have come: a home never holds part of them.</summary>
		public const string Coming = "records.part";
		/// <summary>In the scanned PC's records, the list it last took back: one taken again (its let-go lost) isn't added twice.</summary>
		const string Taken = "taken.json";
		/// <summary>The most of a file one ask brings.</summary>
		internal static long Piece { get; set; } = 16L * 1024 * 1024;

		/// <summary>A home's files and folders that aren't records: its stand-ins, this PC's settings, how the scan is getting on, its stop and lock.</summary>
		static readonly HashSet<string> NotRecords = new(StringComparer.OrdinalIgnoreCase) {
			"settings.json", "remote-status.json", "stop", "scan.lock", "target.json", Taken, Coming, "lent",
		};

		/// <summary>The records of this PC's scans by <paramref name="lender"/>, in this PC's Heiward folder.</summary>
		public static string StoreFor(string lender) => Path.Combine(AgentPaths.Home, "scanned-by", lender);

		/// <summary>One file of the records: its path under their folder (<c>db/ScannedFiles.db</c>), its size and last write (UTC ticks).</summary>
		internal sealed record Entry(string Name, long Size, long Ticks);

		/// <summary>
		/// The records in a folder: <see cref="Whole"/> when they're a scan's (its listing or its database), standing for those
		/// kept before, else only its log; the stamp of the listing they're of, and whether they hold the scan's report and
		/// findings (a scan that finds that listing again needs no more of them).
		/// </summary>
		internal sealed record Manifest(bool Whole, string? Stamp, bool Holds, List<Entry> Files) {
			/// <summary>What they are, for telling them from another time's: their files, sizes and times.</summary>
			public string Tag {
				get {
					var text = new StringBuilder(Whole ? "whole" : "log");
					foreach (Entry f in Files) text.Append('\n').Append(f.Name).Append('|').Append(f.Size).Append('|').Append(f.Ticks);
					return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32].ToLowerInvariant();
				}
			}
		}

		/// <summary>A file of the records as they're offered: as <see cref="Entry"/>, and its path on the PC offering them, for its file route.</summary>
		internal sealed record OfferedFile(string Name, string Path, long Size, long Ticks);

		/// <summary>The records' list, as a PC offers them (<c>GET …/scan/records</c>, <c>GET …/scan/kept</c>).</summary>
		internal sealed record Offered(int V, bool Whole, string? Stamp, bool Holds, string Tag, List<OfferedFile> Files) {
			public long Bytes => Files.Sum(f => f.Size);
		}

		/// <summary>Whether <paramref name="folder"/> holds a scan's records (its listing or its database), not only a log.</summary>
		internal static bool Whole(string folder) => File.Exists(Path.Combine(folder, "listing.tsv")) || Directory.Exists(Path.Combine(folder, "db"));

		/// <summary>The records in <paramref name="folder"/>; none when it isn't there. A stand-ins' folder isn't looked into.</summary>
		internal static Manifest Of(string folder) {
			var files = new List<Entry>();
			void Walk(DirectoryInfo dir, string under) {
				foreach (FileSystemInfo e in dir.EnumerateFileSystemInfos()) {
					if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
					string name = under.Length == 0 ? e.Name : under + "/" + e.Name;
					if (under.Length == 0 && (NotRecords.Contains(e.Name) || e.Name.StartsWith("mirror", StringComparison.OrdinalIgnoreCase))) continue;
					if (e is DirectoryInfo sub) Walk(sub, name);
					else if (!e.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith(".piece", StringComparison.OrdinalIgnoreCase))
						files.Add(new Entry(name, ((FileInfo)e).Length, e.LastWriteTimeUtc.Ticks));
				}
			}
			try {
				if (Directory.Exists(folder)) Walk(new DirectoryInfo(folder), "");
			}
			catch (Exception e) when (e is DirectoryNotFoundException) {
				files.Clear();
			}
			files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
			string listing = Path.Combine(folder, "listing.tsv");
			// The findings stand for a scan that finds the same files only when this build judged them (another's rules judge sets otherwise).
			bool holds = File.Exists(listing) && File.Exists(Path.Combine(folder, "report.json")) && BuildOf(Path.Combine(folder, "findings.json")) == AppBuild.Current;
			return new Manifest(Whole(folder), ListingPages.StampOf(listing), holds, files);
		}

		/// <summary>The build that judged the findings at <paramref name="path"/>, read from the start of the file (it's written first); null when it can't be told.</summary>
		internal static string? BuildOf(string path) {
			try {
				if (!File.Exists(path)) return null;
				using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
				byte[] head = new byte[4096];
				int n = input.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
				var reader = new Utf8JsonReader(head.AsSpan(0, n), isFinalBlock: n < head.Length, default);
				int depth = 0;
				while (reader.Read()) {
					if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
					else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
					else if (depth == 1 && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("build") && reader.Read())
						return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
			return null;
		}

		/// <summary>A record's path under <paramref name="folder"/>; null for a name that would lead out of it.</summary>
		static string? PathIn(string folder, string name) {
			string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
			string path = Path.GetFullPath(Path.Combine(root, name.Replace('/', '\\')));
			return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path.Length > root.Length ? path : null;
		}

		/// <summary>The records' list answered, and <paramref name="files"/> (the route their bytes come by) set to serve them alone.</summary>
		internal static async Task OfferAsync(HttpContext ctx, string folder, Manifest m, PeerFiles files) {
			var offered = new Offered(Version, m.Whole, m.Stamp, m.Holds, m.Tag, m.Files.Select(f => new OfferedFile(f.Name, PathIn(folder, f.Name)!, f.Size, f.Ticks)).ToList());
			files.Replace(offered.Files.Select(f => f.Path));
			ctx.Response.Headers.CacheControl = "no-store";
			await NpuLoan.Json(200, offered).ExecuteAsync(ctx);
		}

		/// <summary>
		/// The records <paramref name="pc"/> offers this PC at <paramref name="route"/>; null when it has none for it now (404:
		/// none, or an older Heiward; 409: a scan uses them; 410: it isn't asking now). Throws when the link breaks.
		/// </summary>
		internal static async Task<Offered?> AskAsync(string pc, string route, CancellationToken ct) {
			var (status, json) = await EstateLink.GetJsonAsync(EstateLink.PeerPath(pc, route), TimeSpan.FromSeconds(60), ct, limit: 16 * 1024 * 1024);
			using (json) {
				if (status is 404 or 409 or 410) return null;
				if (status != 200 || json == null) throw new HttpRequestException($"its records' list came as {status}");
				Offered? offered = json.RootElement.Deserialize<Offered>(AgentConfig.Json);
				return offered is { V: Version, Files: not null, Tag: not null } ? offered : throw new InvalidDataException("its records' list isn't one this Heiward reads");
			}
		}

		/// <summary>
		/// The records offered fetched into <paramref name="into"/> by <paramref name="pc"/>'s file route
		/// <paramref name="bytesRoute"/>: a file already there whole isn't fetched again, one broken off goes on from where it
		/// stopped; anything else there goes. Every byte is the file's as offered (its tag on every piece): one changed since
		/// throws, as a link that won't come back does. <paramref name="came"/>: bytes as they come.
		/// </summary>
		internal static void Fetch(string pc, string bytesRoute, Offered offered, string into, CancellationToken ct, Action<long>? came = null) {
			Directory.CreateDirectory(into);
			var wanted = new Dictionary<string, (OfferedFile File, string Path)>(StringComparer.OrdinalIgnoreCase);
			foreach (OfferedFile f in offered.Files)
				if (PathIn(into, f.Name) is string path) wanted[path] = (f, path);
				else throw new InvalidDataException($"its records name a file outside their folder ({f.Name})");
			// What an earlier try left that isn't one of these (another time's records, their pieces) goes.
			foreach (string found in Directory.EnumerateFiles(into, "*", SearchOption.AllDirectories)) {
				string left = Path.GetFullPath(found);
				int dot = left.EndsWith(".piece", StringComparison.OrdinalIgnoreCase) ? left.LastIndexOf('.', left.Length - 7) : -1;
				string whole = dot > 0 ? left[..dot] : left;
				bool mine = wanted.TryGetValue(whole, out var w) && (whole == left || left == PieceOf(w.Path, w.File));
				if (!mine) File.Delete(left);
			}
			var bytes = new EstateBytes(pc, route: bytesRoute);
			foreach (var (f, path) in wanted.Values) {
				ct.ThrowIfCancellationRequested();
				var done = new FileInfo(path);
				if (done.Exists && done.Length == f.Size && done.LastWriteTimeUtc.Ticks == f.Ticks) continue;
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				string piece = PieceOf(path, f);
				using (var fs = new FileStream(piece, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 16)) {
					long at = fs.Seek(0, SeekOrigin.End);
					if (at > f.Size) {
						fs.SetLength(0);
						at = 0;
					}
					var file = new ListedFile(f.Path, f.Size, f.Ticks);
					while (at < f.Size) {
						long got = bytes.Stream(file, at, Math.Min(Piece, f.Size - at), (_, b) => fs.Write(b), ct);
						if (got <= 0) throw new RemoteReadFailed("it ended before its size", null);
						at += got;
						came?.Invoke(got);
					}
				}
				File.Move(piece, path, overwrite: true);
				File.SetLastWriteTimeUtc(path, new DateTime(f.Ticks, DateTimeKind.Utc));
			}
		}

		static string PieceOf(string path, OfferedFile f) => $"{path}.{f.Size}-{f.Ticks}.piece";

		/// <summary>The outcome of taking a scan's records back: whether any came (or were here already), and why not, in words, when the link broke.</summary>
		internal readonly record struct Took(bool Any, string? Problem);

		/// <summary>
		/// What <paramref name="lender"/> holds of its scan of this PC, taken back into <paramref name="store"/>, then let go
		/// there (<c>scan/kept?got=</c>): a scan's whole records stand for those kept here, one that read nothing brings only
		/// its log's lines. None when it holds none now (or a scan of this PC runs there); a link that breaks says so, and
		/// they're taken the next time, picking up where this stopped.
		/// </summary>
		internal static async Task<Took> TakeAsync(string lender, string store, CancellationToken ct) {
			try {
				Settle(store);
				if (await AskAsync(lender, "scan/kept", ct) is not { } kept) return new(false, null);
				if (TakenTag(store) != kept.Tag) {
					string coming = store + ".part";
					await Task.Run(() => Fetch(lender, "scan/kept/bytes", kept, coming, ct), ct);
					Apply(store, coming, kept.Whole);
					AgentPaths.WriteAtomic(Path.Combine(store, Taken), JsonSerializer.Serialize(new { tag = kept.Tag }));
				}
				// Let go there: nothing of them stays on that PC.
				var (_, json) = await EstateLink.GetJsonAsync(EstateLink.PeerPath(lender, "scan/kept?got=" + Uri.EscapeDataString(kept.Tag)), TimeSpan.FromSeconds(30), ct, limit: 64 * 1024);
				json?.Dispose();
				return new(true, null);
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException or EstateMissing or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested) {
				return new(false, e is EstateMissing ? "Castellan isn't running on this PC" : e.Message.TrimEnd('.'));
			}
		}

		/// <summary>The tag of the records last taken into <paramref name="store"/>, or null.</summary>
		static string? TakenTag(string store) {
			try {
				string path = Path.Combine(store, Taken);
				if (!File.Exists(path)) return null;
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				return EstateLink.Str(doc.RootElement, "tag");
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return null;
			}
		}

		/// <summary>
		/// The records come (<paramref name="coming"/>) put in <paramref name="store"/>: whole ones in place of those kept (the
		/// old ones moved aside first, an instant, then deleted), else their log's lines added to the kept log.
		/// </summary>
		static void Apply(string store, string coming, bool whole) {
			Directory.CreateDirectory(Path.GetDirectoryName(store)!);
			if (whole) {
				string old = store + ".old";
				DeleteTree(old);
				if (Directory.Exists(store)) Directory.Move(store, old);
				Directory.Move(coming, store);
				DeleteTree(old);
				return;
			}
			Directory.CreateDirectory(store);
			string log = Path.Combine(coming, Log);
			if (File.Exists(log)) {
				using var to = new FileStream(Path.Combine(store, Log), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
				using var from = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				from.CopyTo(to);
			}
			DeleteTree(coming);
		}

		/// <summary>Records cut off as they were put in place (Heiward stopped between the two moves) put back.</summary>
		internal static void Settle(string store) {
			string old = store + ".old";
			if (Directory.Exists(store) || !Directory.Exists(old)) return;
			try { Directory.Move(old, store); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		static void DeleteTree(string path) {
			if (!Directory.Exists(path)) return;
			foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
			Directory.Delete(path, recursive: true);
		}
	}
}
