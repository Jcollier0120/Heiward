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

using System.Text.RegularExpressions;
using HEI.Core.ViewModels;

namespace HEI.Agent {
	/// <summary>
	/// One video in two languages. Older games ship a cutscene once per language, with the same
	/// pictures and another soundtrack: intro_en.wmv and intro_de.wmv, Movies\English\intro.bik and
	/// Movies\German\intro.bik, or intro.wmv next to intro_fr.wmv. They match frame for frame, but each
	/// is the game's own file, not a copy. The names say so, and so do the audio tracks' language tags
	/// when a file has them.
	/// </summary>
	static class LanguageVersions {
		/// <summary>The file names, then the folders above them, this many in all.</summary>
		const int Levels = 4;

		/// <summary>
		/// The two paths name one video in different languages: at the first level from the file up
		/// where they differ (the name, its folder, the folder above...), they differ only by
		/// languages. Levels above that may differ too (a copy on another drive is still the German one).
		/// </summary>
		public static bool ByName(string a, string b) {
			string[] la = Names(a), lb = Names(b);
			for (int k = 0; k < Math.Min(la.Length, lb.Length); k++) {
				if (string.Equals(la[k], lb[k], StringComparison.OrdinalIgnoreCase))
					continue;
				var (restA, langA) = Split(la[k]);
				var (restB, langB) = Split(lb[k]);
				return restA.SequenceEqual(restB) && !langA.SetEquals(langB);
			}
			return false;
		}

		/// <summary>Both videos have tagged audio tracks, and the languages differ ("ENG" and "GER"; untagged tracks don't count).</summary>
		public static bool ByTags(DuplicateItem a, DuplicateItem b) {
			HashSet<string> ta = Tags(a.AudioLanguages), tb = Tags(b.AudioLanguages);
			return ta.Count > 0 && tb.Count > 0 && !ta.SetEquals(tb);
		}

		static HashSet<string> Tags(string languages) =>
			languages.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(t => t != "?").ToHashSet(StringComparer.OrdinalIgnoreCase);

		/// <summary>The file's name without its extension, then its folders' names, from the file up.</summary>
		static string[] Names(string path) {
			var names = new List<string> { Path.GetFileNameWithoutExtension(path) };
			for (string? dir = Path.GetDirectoryName(path); names.Count < Levels && !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)) {
				string name = Path.GetFileName(dir);
				if (name.Length == 0) break; // the drive
				names.Add(name);
			}
			return names.ToArray();
		}

		static readonly Regex Separators = new(@"[\s_.\-()\[\]{}]+", RegexOptions.CultureInvariant);

		/// <summary>A name's words, and the languages among them (each as one code: "en" for en, eng and English).</summary>
		static (List<string> Words, HashSet<string> Languages) Split(string name) {
			var rest = new List<string>();
			var languages = new HashSet<string>(StringComparer.Ordinal);
			foreach (string word in Separators.Split(name.ToLowerInvariant())) {
				if (word.Length == 0)
					continue;
				if (Language.TryGetValue(word, out string? code))
					languages.Add(code);
				else
					rest.Add(word);
			}
			return (rest, languages);
		}

		/// <summary>
		/// Words that name a language (ISO 639-1 and 639-2 codes, and English and native names), or a
		/// country after one ("en-US", "pt_BR"), to the language's code. Two-letter codes are words too
		/// ("it", "no", "de"): a name that differs from another by one of those alone is rare, and the
		/// cost of a mistake is a set not offered, never a file ticked.
		/// </summary>
		static readonly Dictionary<string, string> Language = Build(
			("en", "en eng english"), ("de", "de ger deu german deutsch"), ("fr", "fr fre fra french francais français"),
			("es", "es spa esp spanish espanol español castellano"), ("it", "it ita italian italiano"),
			("ja", "ja jp jpn jap japanese"), ("ru", "ru rus russian"), ("pl", "pl pol polish polski"),
			("pt", "pt por ptb portuguese portugues português brazilian"), ("nl", "nl dut nld dutch nederlands"),
			("sv", "sv swe swedish svenska"), ("no", "no nb nor norwegian norsk"), ("da", "da dan danish dansk"),
			("fi", "fi fin finnish suomi"), ("cs", "cs cz cze ces czech cesky"), ("hu", "hu hun hungarian magyar"),
			("tr", "tr tur turkish"), ("ko", "ko kr kor korean"), ("zh", "zh cn tw chi zho chs cht chinese"),
			("ar", "ar ara arabic"), ("el", "el gr gre ell greek"), ("he", "he heb hebrew"), ("uk", "ua ukr ukrainian"),
			("ro", "ro rum ron romanian"), ("bg", "bg bul bulgarian"), ("hr", "hr hrv croatian"), ("sk", "sk slk slo slovak"),
			("th", "th tha thai"), ("vi", "vi vie vietnamese"),
			// Countries, kept apart from the language: en-US and en-GB are two versions too.
			("us", "us"), ("gb", "gb uk"), ("au", "au"), ("ca", "ca"), ("at", "at"), ("ch", "ch"), ("be", "be"),
			("br", "br"), ("mx", "mx latam"), ("hk", "hk"));

		static Dictionary<string, string> Build(params (string Code, string Words)[] languages) {
			var map = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var (code, words) in languages)
				foreach (string word in words.Split(' '))
					map.TryAdd(word, code);
			return map;
		}
	}
}
