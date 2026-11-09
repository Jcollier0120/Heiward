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

using System.Text;
using System.Text.RegularExpressions;

namespace HEI.StoreSubmit;

/// <summary>
/// The Store listing's "What's new in this version" (the submission's listings.en-us.baseListing.releaseNotes), from
/// CHANGELOG.md: every version since the one the Store has published, up to the one sent, newest first, in plain
/// text. Each version's bold line and its What's new and What changed; not Before you update (the Store updates
/// Heiward by itself), nor the kit's lines, which are about the manor's staff, not Heiward. Cut at a whole line to
/// fit the field's 1500 characters.
/// </summary>
public static partial class ReleaseNotes {
	/// <summary>Partner Center's limit for the field (Store listings, "What's new in this version").</summary>
	public const int Limit = 1500;

	/// <summary>When none of the versions brought anything but the kit.</summary>
	public const string Fallback = "Small fixes and improvements.";

	public sealed record Item(int Depth, string Text);

	public sealed record Entry(Version Version, string? Headline, IReadOnlyList<Item> Items);

	/// <summary>The changelog's versions, each with its bold line (null for the kit's) and its user-facing lines.</summary>
	public static List<Entry> Parse(string changelog) {
		var entries = new List<Entry>();
		Version? version = null;
		string? headline = null;
		var items = new List<(int Depth, StringBuilder Text)>();
		bool bold = false, inSection = false, userSection = false, dropping = false;
		(int Depth, StringBuilder Text)? last = null;

		void Flush() {
			if (version != null) entries.Add(new Entry(version, headline, items.Select(i => new Item(i.Depth, i.Text.ToString())).ToList()));
			version = null;
			headline = null;
			items = [];
			bold = inSection = userSection = dropping = false;
			last = null;
		}

		foreach (string raw in changelog.ReplaceLineEndings("\n").Split('\n')) {
			string line = raw.TrimEnd();
			if (line.StartsWith("## ", StringComparison.Ordinal)) {
				Flush();
				// "## 1.9.137"; any other heading ("## Unreleased") starts nothing.
				version = Versions.Parse(line[3..].Trim().Split(' ')[0]);
				continue;
			}
			if (version == null) continue;
			if (line.StartsWith("### ", StringComparison.Ordinal)) {
				string section = line[4..].Trim().Replace('’', '\'');
				inSection = true;
				userSection = section.Equals("What's new", StringComparison.OrdinalIgnoreCase) || section.Equals("What changed", StringComparison.OrdinalIgnoreCase);
				dropping = false;
				last = null;
				continue;
			}
			if (line.StartsWith('#')) continue;
			if (!inSection) {
				if (!bold && line.Length > 4 && line.StartsWith("**", StringComparison.Ordinal) && line.EndsWith("**", StringComparison.Ordinal)) {
					bold = true;
					string text = Plain(line[2..^2]);
					if (!KitHeadline().IsMatch(text) && !Combined().IsMatch(text)) headline = text;
				}
				continue;
			}
			if (!userSection) continue;
			if (line.Trim().Length == 0) {
				last = null;
				continue;
			}
			Match bullet = Bullet().Match(line);
			if (bullet.Success) {
				int indent = bullet.Groups[1].Value.Replace("\t", "    ").Length;
				int depth = Math.Min(indent / 2, 2);
				string text = Plain(bullet.Groups[2].Value);
				if (depth == 0) dropping = KitLine().IsMatch(text);
				if (dropping || text.Length == 0) {
					last = null;
					continue;
				}
				items.Add((depth, new StringBuilder(text)));
				last = items[^1];
			}
			else if (last is { } item && !dropping) item.Text.Append(' ').Append(Plain(line));
			else {
				// A paragraph of its own.
				string text = Plain(line);
				dropping = KitLine().IsMatch(text);
				if (dropping) continue;
				items.Add((0, new StringBuilder(text)));
				last = items[^1];
			}
		}
		Flush();
		return entries;
	}

	/// <summary>The text for a submission of <paramref name="upTo"/>, when the Store has published <paramref name="after"/>
	/// (null: every version up to it, as many as fit).</summary>
	public static string For(string changelog, Version? after, Version upTo, int limit = Limit) {
		var lines = new List<string>();
		foreach (Entry e in Parse(changelog).Where(e => (after == null || e.Version > after) && e.Version <= upTo).OrderByDescending(e => e.Version)) {
			if (e.Items.Count == 0) continue;
			if (lines.Count > 0) lines.Add("");
			string version = Versions.Show(e.Version);
			lines.Add(e.Headline != null ? version + ": " + e.Headline : version);
			foreach (Item i in e.Items) lines.Add(new string(' ', 2 * i.Depth) + (i.Depth == 0 ? "• " : "– ") + i.Text);
		}
		return lines.Count == 0 ? Fallback : Fit(lines, limit);
	}

	/// <summary>The field's length as Partner Center may count it: each line break as two characters (CR LF).</summary>
	public static int Cost(string text) => text.Length + text.Count(c => c == '\n');

	static string Fit(List<string> lines, int limit) {
		while (lines.Count > 1 && Cost(string.Join('\n', lines)) > limit) lines.RemoveAt(lines.Count - 1);
		while (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
		string text = string.Join('\n', lines);
		if (Cost(text) <= limit) return text;
		// A single line longer than the whole field: cut at a word.
		int end = text.LastIndexOf(' ', Math.Max(0, limit - 1));
		return (end > 0 ? text[..end] : text[..(limit - 1)]).TrimEnd() + "…";
	}

	/// <summary>Markdown to plain text: links to their words, no bold, italics or code marks, one space between words.</summary>
	public static string Plain(string markdown) {
		string s = Link().Replace(markdown, "$1");
		s = Code().Replace(s, "$1");
		s = Bold().Replace(s, "$1");
		s = Italic().Replace(s, "$1");
		s = Escaped().Replace(s, "$1");
		return Spaces().Replace(s, " ").Trim();
	}

	[GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
	private static partial Regex Bullet();

	/// <summary>A line about the Steward's kit, the parts the manor's staff share: "Kit 2.152.4: ...".</summary>
	[GeneratedRegex(@"^(The Steward's kit|Kit) \d+\.\d+", RegexOptions.IgnoreCase)]
	private static partial Regex KitLine();

	[GeneratedRegex(@"^It carries (the Steward's )?kit \d", RegexOptions.IgnoreCase)]
	private static partial Regex KitHeadline();

	/// <summary>"Brings 6 changes: ...; ...": the bold line of versions merged by the build, which only repeats their lines.</summary>
	[GeneratedRegex(@"^Brings \d+ changes:")]
	private static partial Regex Combined();

	[GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
	private static partial Regex Link();

	[GeneratedRegex(@"`+([^`]+?)`+")]
	private static partial Regex Code();

	[GeneratedRegex(@"\*\*(.+?)\*\*")]
	private static partial Regex Bold();

	[GeneratedRegex(@"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])")]
	private static partial Regex Italic();

	[GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!])")]
	private static partial Regex Escaped();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Spaces();
}
