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
	/// What another of the person's PCs found when it scanned this one's files for it: <c>remote-findings.json</c> in this
	/// PC's home, a file older builds don't know and leave alone. Its sets are this PC's own files, each marked with the PC
	/// that found it (<see cref="ReportGroup.FoundOn"/>), and the page shows them with this PC's own sets and cleans them up
	/// with the same buttons and checks (<see cref="Recycler"/>). Automatic cleanup leaves them alone: only the person
	/// removes what another PC found.
	/// <para>Only files this PC listed to that PC, as it listed them, make it in: a set naming anything else is left out.
	/// Another build's findings are set aside, as another build's report is (<see cref="Report.Load"/>).</para>
	/// </summary>
	sealed record ForeignFindings(int V, string FromId, string FromName, string Build, DateTime ScannedAtUtc, List<ReportGroup> Groups) {
		public const int Version = 1;

		public static string FilePath => Path.Combine(AgentPaths.Home, "remote-findings.json");

		/// <summary>The findings, when this build can show them; null otherwise (none, another build's, unreadable).</summary>
		public static ForeignFindings? Load() => LoadAny() is { } f && f.Build == AppBuild.Current ? f : null;

		/// <summary>The file as last parsed, parsed again only once it has changed: the page reads it with the report, as often.</summary>
		static readonly StampedFile<ForeignFindings> Saved = new(json => JsonSerializer.Deserialize<ForeignFindings>(json, AgentConfig.Json),
			f => f with { Groups = f.Groups?.ConvertAll(g => g with { Items = [.. g.Items] }) ?? [] });

		/// <summary>The findings whichever build made them: for telling the page another build's are set aside.</summary>
		public static ForeignFindings? LoadAny() {
			try {
				return Saved.Read(FilePath) is { V: Version } f ? f : null;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return null;
			}
		}

		/// <summary>The page's Clear: what that PC found here is let go (its sets' answers in the History stay).</summary>
		public static void Clear() {
			try { File.Delete(FilePath); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		public void Save() => AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(this, AgentConfig.Json));

		/// <summary>
		/// <paramref name="found"/>'s sets as this PC keeps them: only those whose every file is one it listed to
		/// <paramref name="from"/> (<paramref name="listed"/>), as listed, each marked with that PC's name, whether it's in a
		/// cloud-synced folder looked at here.
		/// </summary>
		public static ForeignFindings From(Pc from, RemoteFindings found, IReadOnlyDictionary<string, ListedFile> listed) {
			var groups = new List<ReportGroup>();
			foreach (ReportGroup g in found.Groups ?? []) {
				if (g.Items is not { Count: >= 2 } items || !listed.ContainsKey(g.KeepPath)) continue;
				var mine = new List<ReportItem>(items.Count);
				foreach (ReportItem i in items) {
					if (!listed.TryGetValue(i.Path, out ListedFile? f) || f.Size != i.Size) break;
					mine.Add(i with { Name = Path.GetFileName(f.Path), Folder = Path.GetDirectoryName(f.Path) ?? "", Synced = CloudFiles.IsSynced(f.Path) });
				}
				if (mine.Count != items.Count) continue;
				groups.Add(g with { Key = ReportBuilder.GroupKey(mine.Select(i => i.Path)), Items = mine, FoundOn = from.Name });
			}
			return new ForeignFindings(Version, from.Id, from.Name, found.Build, found.ScannedAtUtc, groups);
		}
	}

	/// <summary>The sets the page shows and acts on: this PC's own report's, then those another PC found here.</summary>
	static class PageSets {
		public static List<ReportGroup> Groups() => With(Report.Load()?.Groups);

		/// <summary><paramref name="own"/> (this PC's report's sets, already read), then those another PC found here that it hasn't.</summary>
		public static List<ReportGroup> With(List<ReportGroup>? own) {
			var groups = own ?? new();
			if (ForeignFindings.Load() is not { Groups.Count: > 0 } foreign) return groups;
			var keys = groups.Select(g => g.Key).ToHashSet();
			return [.. groups, .. foreign.Groups.Where(g => !keys.Contains(g.Key))];
		}
	}
}
