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
using System.Text.Json.Serialization;

namespace HEI.Agent.Estate {
	/// <summary>One file of a listing sent to another PC: its path on this PC, its size, and its last-modified time (UTC ticks).</summary>
	sealed record ListedFile(string Path, long Size, long ModifiedTicks) {
		public DateTime ModifiedUtc => new(ModifiedTicks, DateTimeKind.Utc);
	}

	/// <summary>One scanned folder's listing: its root ("C:\"), a stamp that changes only when its files do, and the files.</summary>
	sealed record ListedRoot(string Root, string Stamp, List<ListedFile> Files);

	/// <summary>
	/// The photos and videos a PC lists, for another PC that scans them (<c>GET /api/peer/scan/listing</c>), version 1:
	/// <code>{"v":1,"build":"…","stamp":"…","roots":[{"root":"C:\\","stamp":"…","files":[["C:\\Photos\\a.jpg",2048000,638000000000000000],…]}]}</code>
	/// Each file is an array (path, size, ticks), about 80 bytes. <c>?have=&lt;stamp&gt;</c>, the stamp the asker kept, is
	/// answered <c>{"v":1,"same":true,"stamp":"…"}</c> when nothing changed since: a PC with nothing new isn't read at all.
	/// Only what this PC's own scans see: its own settings decide its drives, folders and what's left out.
	/// </summary>
	sealed record RemoteListing(int V, string Build, string Stamp, List<ListedRoot> Roots) {
		public const int Version = 1;

		/// <summary>The listing a PC keeps for its own scans, made into one to send (<see cref="ListingPlan.Kept"/>).</summary>
		public static RemoteListing FromKept(IEnumerable<(string Root, List<(string Path, long Size, DateTime ModifiedUtc)> Files)> kept, string build) {
			var roots = new List<ListedRoot>();
			foreach (var (root, files) in kept) {
				var listed = files.Select(f => new ListedFile(f.Path, f.Size, f.ModifiedUtc.Ticks))
					.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
					.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
				roots.Add(new ListedRoot(root, StampOf(listed), listed));
			}
			roots.Sort((a, b) => string.CompareOrdinal(a.Root, b.Root));
			return new RemoteListing(Version, build, Hash(string.Join("\n", roots.Select(r => r.Root + "\t" + r.Stamp))), roots);
		}

		/// <summary>A root's stamp: its files' paths, sizes and times, hashed (the first 32 hex digits of a SHA-256).</summary>
		internal static string StampOf(IEnumerable<ListedFile> files) {
			var text = new StringBuilder();
			foreach (ListedFile f in files) text.Append(f.Path).Append('\t').Append(f.Size).Append('\t').Append(f.ModifiedTicks).Append('\n');
			return Hash(text.ToString());
		}

		static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();

		/// <summary>The listing's JSON, as the route sends it (each file an array).</summary>
		public byte[] ToJson() {
			using var ms = new MemoryStream();
			using (var w = new Utf8JsonWriter(ms)) {
				w.WriteStartObject();
				w.WriteNumber("v", V);
				w.WriteString("build", Build);
				w.WriteString("stamp", Stamp);
				w.WriteStartArray("roots");
				foreach (ListedRoot r in Roots) {
					w.WriteStartObject();
					w.WriteString("root", r.Root);
					w.WriteString("stamp", r.Stamp);
					w.WriteStartArray("files");
					foreach (ListedFile f in r.Files) {
						w.WriteStartArray();
						w.WriteStringValue(f.Path);
						w.WriteNumberValue(f.Size);
						w.WriteNumberValue(f.ModifiedTicks);
						w.WriteEndArray();
					}
					w.WriteEndArray();
					w.WriteEndObject();
				}
				w.WriteEndArray();
				w.WriteEndObject();
			}
			return ms.ToArray();
		}

		/// <summary>A listing as another PC sent it; null when it isn't one this build reads (another version, or not JSON).</summary>
		public static RemoteListing? Parse(JsonElement root) {
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out JsonElement v) || !v.TryGetInt32(out int version) || version != Version) return null;
			if (EstateLink.Str(root, "stamp") is not string stamp || !root.TryGetProperty("roots", out JsonElement roots) || roots.ValueKind != JsonValueKind.Array) return null;
			var list = new List<ListedRoot>();
			foreach (JsonElement r in roots.EnumerateArray()) {
				if (r.ValueKind != JsonValueKind.Object || EstateLink.Str(r, "root") is not string rootPath || EstateLink.Str(r, "stamp") is not string rootStamp) return null;
				if (!r.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array) return null;
				var listed = new List<ListedFile>(files.GetArrayLength());
				foreach (JsonElement f in files.EnumerateArray()) {
					if (f.ValueKind != JsonValueKind.Array || f.GetArrayLength() != 3) return null;
					if (f[0].GetString() is not string path || !f[1].TryGetInt64(out long size) || !f[2].TryGetInt64(out long ticks)) return null;
					if (size < 0 || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !RemotePath.Valid(path, rootPath)) return null;
					listed.Add(new ListedFile(path, size, ticks));
				}
				list.Add(new ListedRoot(rootPath, rootStamp, listed));
			}
			return new RemoteListing(version, EstateLink.Str(root, "build") ?? "", stamp, list);
		}
	}

	/// <summary>Where another PC's files stand on this one, for its scan: its paths kept as they are there, checked.</summary>
	static class RemotePath {
		/// <summary>
		/// A path another PC may name: a full path on one of its drives ("C:\…"), under the root it's listed in, with no
		/// "." or ".." part and nothing a Windows file name can't hold.
		/// </summary>
		public static bool Valid(string path, string root) {
			if (!Drive(path) || !Drive(root) || path.Length > 1024) return false;
			string under = Path.TrimEndingDirectorySeparator(root);
			if (!under.EndsWith('\\')) under += "\\";
			if (!path.StartsWith(under, StringComparison.OrdinalIgnoreCase) || path.Length == under.Length) return false;
			string[] parts = path[3..].Split('\\');
			foreach (string part in parts)
				if (part.Length == 0 || part is "." or ".." || part.IndexOfAny(Bad) >= 0 || part.EndsWith(' ') || part.EndsWith('.')) return false;
			return true;
		}

		static readonly char[] Bad = ['/', ':', '*', '?', '"', '<', '>', '|', '\0'];

		static bool Drive(string p) => p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';

		/// <summary>Another PC's file, as this PC keeps it for that PC's scan: <c>&lt;mirror&gt;\C\Photos\a.jpg</c> for <c>C:\Photos\a.jpg</c>.</summary>
		public static string Mirrored(string mirror, string path) => Path.Combine(mirror, char.ToUpperInvariant(path[0]).ToString(), path[3..]);

		/// <summary>The other PC's path of a file in the mirror; null for one that isn't in it.</summary>
		public static string? Original(string mirror, string mirrored) {
			string prefix = Path.TrimEndingDirectorySeparator(mirror) + "\\";
			if (!mirrored.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
			string rest = mirrored[prefix.Length..];
			if (rest.Length < 1 || !char.IsAsciiLetter(rest[0])) return null;
			return rest.Length == 1 ? $"{rest[0]}:\\" : rest[1] == '\\' ? $"{rest[0]}:\\{rest[2..]}" : null;
		}
	}
}
