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

using System.Globalization;
using System.Linq;
using System.Text;

namespace HEI.Core.AI {
	/// <summary>Two pictures' words: the same (or none in either), other words, or clearly another document.</summary>
	public enum TextVerdict { Same, Differs, Unrelated }

	/// <summary>
	/// Whether two pictures say the same thing, from their words as the text reader read them (<see cref="OcrReader"/>),
	/// both at one size (<see cref="TextCheck.Sizes"/>). What a misread changes doesn't count:
	/// <list type="bullet">
	/// <item>a box of text the reader was unsure of (<see cref="BoxConfidence"/>: a photo's grass or brickwork read as
	/// letters), a box of a single character, and a word it was unsure of (<see cref="MinConfidence"/>) are left out;</item>
	/// <item>words are compared lower-case, letters and figures only, accents dropped, with the shapes the reader confuses
	/// taken as one (0 and O, 1 l and I, 5 S and $, 8 and B, 2 and Z, 6 and G, E and £ or €, rn and m);</item>
	/// <item>a space read or lost doesn't count, nor a stretch the reader was sure of no word in (<see cref="SureConfidence"/>);</item>
	/// <item>a word or two read a letter or two apart (<see cref="Forgiven"/>) doesn't count, unless a figure is in them:
	/// another total, date, time or page number always counts.</item>
	/// </list>
	/// Measured with the benchmarks' copy probe (<c>--probe-copies --text</c>) on screenshots, receipts, forms and
	/// captioned photos, their copies (half and quarter size, 1,024 wide, JPEG, PNG, HEIC) and twins with other words.
	/// </summary>
	public static class TextMatch {
		/// <summary>Words read less sure than this (the mean of their characters' probabilities) are left out.</summary>
		internal const float MinConfidence = 0.5f;
		/// <summary>A box of text read less sure than this, over all its characters, is left out whole.</summary>
		internal const float BoxConfidence = 0.75f;
		/// <summary>A stretch of words that differs counts only when one of its words was read at least this sure.</summary>
		internal const float SureConfidence = 0.9f;
		/// <summary>Of the fewer words, less than this share in common, with at least <see cref="UnrelatedWords"/> words each: another document.</summary>
		internal const double UnrelatedShare = 0.5;
		internal const int UnrelatedWords = 5;
		/// <summary>
		/// A stretch without figures, read at most a quarter of its letters apart (one at least), is a misread; but more than
		/// this many such letters in all (or 5% of the text, if more) and the pictures' words differ.
		/// </summary>
		internal const int Forgiven = 3;

		const char Mark = '\u001f';

		/// <summary>As kept: a line a row, its boxes a tab apart, their words a space apart, each word's confidence in percent after a unit separator.</summary>
		internal static string Serialize(IEnumerable<IEnumerable<IEnumerable<TextWord>>> lines) =>
			string.Join("\n", lines.Select(l => string.Join("\t", l.Select(b => string.Join(" ", b.Select(w =>
				Clean(w.Text) + Mark + ((int)Math.Round(w.Confidence * 100)).ToString(CultureInfo.InvariantCulture)))))));

		static string Clean(string text) => text.Replace(' ', ' ').Replace('\t', ' ').Replace('\n', ' ').Replace(Mark, '?');

		/// <summary>A kept reading's boxes, in reading order, each its words with their confidences.</summary>
		internal static List<List<TextWord>> Boxes(string? read) {
			var boxes = new List<List<TextWord>>();
			if (string.IsNullOrEmpty(read)) return boxes;
			foreach (string box in read.Split('\n', '\t')) {
				var words = new List<TextWord>();
				foreach (string token in box.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
					int mark = token.LastIndexOf(Mark);
					words.Add(mark < 0 ? new TextWord(token, 1f)
						: new TextWord(token[..mark], int.TryParse(token.AsSpan(mark + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p / 100f : 1f));
				}
				if (words.Count > 0) boxes.Add(words);
			}
			return boxes;
		}

		/// <summary>The reading's text alone (the probe prints it).</summary>
		public static string Plain(string? read) => string.Join(" ", Boxes(read).SelectMany(b => b).Select(w => w.Text));

		/// <summary>A word as compared: lower-case letters and figures, accents dropped, the shapes the reader confuses as one.</summary>
		internal static string Normalize(string word) {
			var sb = new StringBuilder(word.Length);
			foreach (char c0 in word.Normalize(NormalizationForm.FormKD)) {
				if (CharUnicodeInfo.GetUnicodeCategory(c0) == UnicodeCategory.NonSpacingMark) continue;
				char c = char.ToLowerInvariant(c0) switch {
					'0' => 'o', '1' or 'i' => 'l', '5' or '$' => 's', '8' => 'b', '2' => 'z', '6' => 'g', '£' or '€' => 'e', char x => x,
				};
				if (char.IsLetterOrDigit(c)) sb.Append(c);
			}
			return sb.Replace("rn", "m").Replace("vv", "w").ToString();
		}

		/// <summary>Mostly figures as read: a price, a date, a time, a page number. Such a word is never a misread to forgive.</summary>
		internal static bool IsNumber(string word) {
			int digits = 0, alnum = 0;
			foreach (char c in word)
				if (char.IsLetterOrDigit(c)) {
					alnum++;
					if (char.IsDigit(c)) digits++;
				}
			return digits > 0 && 2 * digits >= alnum;
		}

		readonly record struct Word(string Text, float Confidence, bool Number);

		static List<Word> Kept(string? read) {
			var kept = new List<Word>();
			foreach (List<TextWord> box in Boxes(read)) {
				int chars = box.Sum(w => w.Text.Length);
				if (chars == 0 || box.Sum(w => w.Confidence * w.Text.Length) / chars < BoxConfidence) continue;
				var words = box.Where(w => w.Confidence >= MinConfidence)
					.Select(w => new Word(Normalize(w.Text), w.Confidence, IsNumber(w.Text))).Where(w => w.Text.Length > 0).ToList();
				if (words.Sum(w => w.Text.Length) >= 2) kept.AddRange(words);
			}
			return kept;
		}

		/// <summary>How two readings compare (<see cref="Serialize"/>): the verdict, the stretches that differ, and the words each has as compared.</summary>
		public static (TextVerdict Verdict, int Stretches, int WordsA, int WordsB) Compare(string? a, string? b) {
			var wa = Kept(a);
			var wb = Kept(b);
			if (string.Concat(wa.Select(w => w.Text)) == string.Concat(wb.Select(w => w.Text)))
				return (TextVerdict.Same, 0, wa.Count, wb.Count);
			// The words both have, in order (a longest common subsequence), and the stretches between them.
			int n = wa.Count, m = wb.Count;
			var lcs = new int[n + 1, m + 1];
			for (int i = n - 1; i >= 0; i--)
				for (int j = m - 1; j >= 0; j--)
					lcs[i, j] = wa[i].Text == wb[j].Text ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
			int stretches = 0, forgiven = 0, x = 0, y = 0;
			while (x < n || y < m) {
				int x0 = x, y0 = y;
				while (x < n && y < m && wa[x].Text != wb[y].Text) {
					if (lcs[x + 1, y] >= lcs[x, y + 1]) x++;
					else y++;
				}
				if (x >= n || y >= m) { x = n; y = m; }
				int d = Stretch(wa.GetRange(x0, x - x0), wb.GetRange(y0, y - y0));
				if (d < 0) stretches++;
				else forgiven += d;
				if (x < n && y < m) { x++; y++; }
			}
			int chars = Math.Min(wa.Sum(w => w.Text.Length), wb.Sum(w => w.Text.Length));
			if (forgiven > Math.Max(Forgiven, 0.05 * chars)) stretches++;
			if (stretches == 0)
				return (TextVerdict.Same, 0, n, m);
			int fewer = Math.Min(n, m);
			bool unrelated = fewer >= UnrelatedWords && lcs[0, 0] < UnrelatedShare * fewer && Apart(wa, wb) > UnrelatedShare;
			return (unrelated ? TextVerdict.Unrelated : TextVerdict.Differs, stretches, n, m);
		}

		/// <summary>
		/// How far apart the two texts are letter by letter (0–1), so a copy whose every word was misread a little isn't taken
		/// for another document. The first 2,000 letters of each: a page's worth tells.
		/// </summary>
		static double Apart(List<Word> a, List<Word> b) {
			string sa = string.Concat(a.Select(w => w.Text)), sb = string.Concat(b.Select(w => w.Text));
			if (sa.Length > 2000) sa = sa[..2000];
			if (sb.Length > 2000) sb = sb[..2000];
			int longer = Math.Max(sa.Length, sb.Length);
			return longer == 0 ? 0 : (double)Distance(sa, sb) / longer;
		}

		/// <summary>
		/// A stretch between the words both have: -1 when it differs; otherwise how many letters it was misread by (0 when
		/// its words run together the same, a space read or lost, or the reader was sure of none of them).
		/// </summary>
		static int Stretch(List<Word> a, List<Word> b) {
			if (a.Count == 0 && b.Count == 0) return 0;
			string sa = string.Concat(a.Select(w => w.Text)), sb = string.Concat(b.Select(w => w.Text));
			if (sa == sb || !a.Concat(b).Any(w => w.Confidence >= SureConfidence)) return 0;
			if (a.Concat(b).Any(w => w.Number)) return -1;
			int d = Distance(sa, sb);
			return d <= Math.Max(1, Math.Max(sa.Length, sb.Length) / 4) ? d : -1;
		}

		/// <summary>Levenshtein's distance: the fewest letters put in, taken out or changed to make one the other.</summary>
		internal static int Distance(string a, string b) {
			var row = new int[b.Length + 1];
			for (int j = 0; j <= b.Length; j++) row[j] = j;
			for (int i = 1; i <= a.Length; i++) {
				int diagonal = row[0];
				row[0] = i;
				for (int j = 1; j <= b.Length; j++) {
					int up = row[j];
					row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
					diagonal = up;
				}
			}
			return row[b.Length];
		}
	}

	/// <summary>Reading a pair's words at one size.</summary>
	internal static class TextCheck {
		/// <summary>How the words were read: the model's version and the reading's (changed whenever reading or keeping them changes), so words kept from another are read again.</summary>
		internal const string Version = TextComponents.ModelVersion + ".1";

		/// <summary>
		/// Neither picture is read bigger than this a side. A phone photo's caption read at full size is letters a tenth of the
		/// picture high, too big for the detector's tiles, which breaks it up; at 2,048 it reads whole, in a quarter of the time.
		/// </summary>
		internal const int MaxSide = 2048;

		/// <summary>The two have the same shape (within 1.5%): a copy at another size, not a crop.</summary>
		internal static bool SameShape((int W, int H) a, (int W, int H) b) =>
			Math.Abs((double)a.W / a.H / ((double)b.W / b.H) - 1) <= 0.015;

		/// <summary>
		/// The sizes two pictures are read at: both at the smaller one's when they have the same shape, so a copy's words come
		/// from nearly the same pixels; otherwise each at its own. Never past <see cref="MaxSide"/>.
		/// </summary>
		internal static ((int W, int H) A, (int W, int H) B) Sizes((int W, int H) a, (int W, int H) b) {
			if (SameShape(a, b)) {
				(int W, int H) small = Fit((long)a.W * a.H <= (long)b.W * b.H ? a : b);
				return (small, small);
			}
			return (Fit(a), Fit(b));

			static (int, int) Fit((int W, int H) s) {
				double scale = (double)MaxSide / Math.Max(s.W, s.H);
				return scale >= 1 ? s : (Math.Max(1, (int)Math.Round(s.W * scale)), Math.Max(1, (int)Math.Round(s.H * scale)));
			}
		}

		/// <summary>
		/// Two readings' verdict (<see cref="TextMatch.Compare"/>). Another document only when they have the same shape: a crop
		/// of a page has fewer of its words, not other ones, and is the user's call, not a picture to leave out.
		/// </summary>
		internal static TextVerdict Judge(string a, string b, bool sameShape) {
			TextVerdict v = TextMatch.Compare(a, b).Verdict;
			return v == TextVerdict.Unrelated && !sameShape ? TextVerdict.Differs : v;
		}

		/// <summary>The picture's words at <paramref name="size"/>, as kept (<see cref="TextMatch.Serialize"/>): "" for none.</summary>
		internal static string ReadAt(OcrReader reader, TextPicture picture, (int W, int H) size) {
			TextPicture pic = picture.Width == size.W && picture.Height == size.H ? picture : picture.Resize(size.W, size.H);
			return TextMatch.Serialize(reader.Read(pic));
		}
	}
}
