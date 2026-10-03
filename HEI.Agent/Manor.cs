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
using System.Text.Json;

namespace HEI.Agent {
	/// <summary>
	/// The manor Heiward works at, when Manor is installed on this PC. Its page then has "Back to &lt;manor&gt;" first in
	/// the title bar, with Manor's icon (<see cref="ManorIcon"/>), as every agent's page has (the Steward's kit 2.3.0:
	/// node/manor.ts); and Manor chooses the theme for every page in the manor, Heiward's too: the page is served with
	/// Manor's theme on its &lt;html&gt; (<see cref="Stamp"/>), so it's right from the first paint, and its Theme menu
	/// says Manor chooses it, with a link there. Without Manor, the page is as it always was: no link, and Heiward's own
	/// Theme menu picks the theme, kept in the browser.
	/// </summary>
	/// <param name="Name">What Manor is called on this PC: its settings.json's "name", "Manor" when it has none.</param>
	/// <param name="Port">Manor's page's port: its settings.json's "port", 18585 when it has none.</param>
	/// <param name="Theme">One of <see cref="Themes"/>; null for Match Windows.</param>
	sealed record Manor(string Name, int Port, string? Theme) {
		/// <summary>The themes Manor can choose besides Match Windows ("system"): the same as theme.js and app.css have.</summary>
		public static readonly IReadOnlyList<string> Themes = ["light", "dark", "arcade", "onyx", "carbon", "tinsel", "rosegold", "quest"];

		public const string DefaultName = "Manor";
		public const int DefaultPort = 18585, MaxNameLength = 60;

		/// <summary>Manor's page.</summary>
		public string Url => $"http://manor.localhost:{Port}/";

		/// <summary>Manor's folder: MANOR_HOME as a full path, or %USERPROFILE%\.manor. Null when MANOR_HOME isn't a path.</summary>
		public static string? Folder {
			get {
				string? overridden = Environment.GetEnvironmentVariable("MANOR_HOME");
				if (string.IsNullOrWhiteSpace(overridden))
					return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".manor");
				try { return Path.GetFullPath(overridden); }
				catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return null; }
			}
		}

		/// <summary>The manor, read fresh from Manor's settings.json; null when Manor isn't installed on this PC.</summary>
		public static Manor? Load() => Folder is string folder ? Load(folder) : null;

		/// <summary>
		/// The manor from Manor's folder. Manor is installed when the folder has both settings.json and app\; a
		/// settings.json that can't be read, or isn't a JSON object, leaves every value at its default.
		/// </summary>
		public static Manor? Load(string folder) {
			string file = Path.Combine(folder, "settings.json");
			if (!File.Exists(file) || !Directory.Exists(Path.Combine(folder, "app"))) return null;
			string? json = null;
			try { json = File.ReadAllText(file); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
			return FromJson(json);
		}

		/// <summary>Manor's settings.json (a byte-order mark is fine) as the manor's pages take it: each value checked, a wrong one its default.</summary>
		internal static Manor FromJson(string? json) {
			string name = DefaultName;
			int port = DefaultPort;
			string? theme = null;
			try {
				using JsonDocument doc = JsonDocument.Parse((json ?? "").TrimStart('\uFEFF'));
				JsonElement root = doc.RootElement;
				if (root.ValueKind == JsonValueKind.Object) {
					if (root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String && n.GetString()!.Trim() is { Length: > 0 } trimmed)
						name = Cut(trimmed, MaxNameLength);
					// A whole number, as JavaScript's Number.isInteger has it (18585.0 too).
					if (root.TryGetProperty("port", out JsonElement p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double d)
						&& d == Math.Floor(d) && d is >= 1024 and <= 65535)
						port = (int)d;
					if (root.TryGetProperty("theme", out JsonElement t) && t.ValueKind == JsonValueKind.String && t.GetString() is string chosen && Themes.Contains(chosen))
						theme = chosen;
				}
			}
			catch (JsonException) { /* not JSON: Manor's defaults */ }
			return new Manor(name, port, theme);
		}

		/// <summary>At most <paramref name="max"/> characters of <paramref name="text"/>, never half of an emoji's pair.</summary>
		static string Cut(string text, int max) {
			if (text.Length <= max) return text;
			return char.IsHighSurrogate(text[max - 1]) ? text[..(max - 1)] : text[..max];
		}

		/// <summary>index.html's own tags, which <see cref="Stamp"/> adds to.</summary>
		internal const string HtmlTag = "<html lang=\"en\">", TitleBarTag = "<header class=\"titlebar\">";

		/// <summary>
		/// The page as Heiward serves it at the manor: Manor's theme on its &lt;html&gt; (data-theme, unless it's Match
		/// Windows, with data-manor and data-manor-url, which tell theme.js and the Theme menu the theme is Manor's),
		/// and "Back to &lt;manor&gt;" first in the title bar, as the kit's pages have it. Without Manor, the page as it is.
		/// </summary>
		public static string Stamp(string html, Manor? manor) {
			if (manor == null) return html;
			string name = Attribute(manor.Name), url = Attribute(manor.Url);
			var tag = new StringBuilder("<html lang=\"en\"");
			if (manor.Theme != null) tag.Append(" data-theme=\"").Append(Attribute(manor.Theme)).Append('"');
			tag.Append(" data-manor=\"").Append(name).Append("\" data-manor-url=\"").Append(url).Append("\">");
			html = ReplaceFirst(html, HtmlTag, tag.ToString());
			return ReplaceFirst(html, TitleBarTag, TitleBarTag +
				$"\n    <a class=\"manor-back\" href=\"{url}\" title=\"Back to {name}\"><img src=\"/manor-icon.svg\" alt=\"\" width=\"22\" height=\"22\"><span>Back to {name}</span></a>" +
				"<span class=\"manor-sep\" aria-hidden=\"true\"></span>");
		}

		static string ReplaceFirst(string text, string find, string with) {
			int at = text.IndexOf(find, StringComparison.Ordinal);
			return at < 0 ? text : string.Concat(text.AsSpan(0, at), with, text.AsSpan(at + find.Length));
		}

		/// <summary>Text for an HTML attribute in double quotes (or an element): &amp; &lt; &gt; " and ' escaped.</summary>
		internal static string Attribute(string text) {
			var s = new StringBuilder(text.Length);
			foreach (char c in text) {
				switch (c) {
					case '&': s.Append("&amp;"); break;
					case '<': s.Append("&lt;"); break;
					case '>': s.Append("&gt;"); break;
					case '"': s.Append("&quot;"); break;
					case '\'': s.Append("&#39;"); break;
					default: s.Append(c); break;
				}
			}
			return s.ToString();
		}
	}
}
