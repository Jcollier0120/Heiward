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
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <summary>
	/// Burst shots and quick retakes: photos the camera numbered one after another (IMG_1234, IMG_1235,
	/// IMG_1236; 20260101_120000_001, _002; a Pixel's …BURST20260101120000123…), or named after the time
	/// they were taken, seconds apart (20201105_205359, 20201105_205401). They can match 99% and more, but
	/// each is its own moment, not a copy, so they don't belong in the report at all, not even as
	/// look-alikes. A file is in a series when a name next to it in its folder, sorted A–Z with the
	/// numbers in order, is the same name with a number at most <see cref="Shot.Step"/> away. One cache
	/// per report: each folder is listed once, names only.
	/// </summary>
	sealed class BurstSeries {
		/// <summary>Neighbours this far apart still make a series (shots deleted in between).</summary>
		internal const int MaxStep = 5;
		/// <summary>Two files in series are shots of one burst when their numbers are at most this far apart.</summary>
		internal const int MaxSpan = 20;
		/// <summary>The same for names that are the time taken, in milliseconds: a minute between neighbours, five in all.</summary>
		internal const long MaxStepMs = 60_000, MaxSpanMs = 300_000;

		/// <summary>
		/// A name read as a series: the name with its number taken out (lower case), and the number.
		/// <paramref name="Timed"/>: the number is the time taken, in milliseconds.
		/// </summary>
		internal readonly record struct Shot(string Series, long Number, bool Timed = false) {
			public long Step => Timed ? MaxStepMs : MaxStep;
			public long Span => Timed ? MaxSpanMs : MaxSpan;
		}

		readonly Dictionary<string, Dictionary<string, List<long>>?> folders = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Two shots of one series: the same name apart from the number, different numbers at most
		/// <see cref="Shot.Span"/> apart, and at least one of them in a series in its own folder. One is
		/// enough: a backup of IMG_1002, alone in its folder, is still not a copy of IMG_1004 from the
		/// burst next to it, while "Photo 1" and "Photo 2" in two folders with no series are judged as usual.
		/// </summary>
		public bool AreSiblings(string a, string b) {
			if (Parse(Path.GetFileNameWithoutExtension(a)) is not { } sa || Parse(Path.GetFileNameWithoutExtension(b)) is not { } sb)
				return false;
			if (!sa.Series.Equals(sb.Series, StringComparison.Ordinal) || sa.Number == sb.Number || Math.Abs(sa.Number - sb.Number) > sa.Span)
				return false;
			return InSeries(a, sa) || InSeries(b, sb);
		}

		/// <summary>Whether a name next to this one in its folder continues the series.</summary>
		bool InSeries(string path, Shot shot) {
			string folder = Path.GetDirectoryName(path) ?? "";
			if (!folders.TryGetValue(folder, out var series))
				folders[folder] = series = List(folder);
			if (series == null || !series.TryGetValue(shot.Series, out List<long>? numbers))
				return false;
			int i = numbers.BinarySearch(shot.Number);
			if (i < 0) return false; // gone since the scan
			return (i > 0 && shot.Number - numbers[i - 1] <= shot.Step) || (i + 1 < numbers.Count && numbers[i + 1] - shot.Number <= shot.Step);
		}

		/// <summary>The folder's photos and videos by series, each series' numbers sorted and once each (IMG_1234.HEIC, .MOV and "(1)" are one shot).</summary>
		static Dictionary<string, List<long>>? List(string folder) {
			try {
				var series = new Dictionary<string, List<long>>(StringComparer.Ordinal);
				foreach (string file in Directory.EnumerateFiles(folder)) {
					if (!FileUtils.IsMediaExtension(Path.GetExtension(file)) || Parse(Path.GetFileNameWithoutExtension(file)) is not { } shot)
						continue;
					if (!series.TryGetValue(shot.Series, out List<long>? numbers))
						series[shot.Series] = numbers = new();
					numbers.Add(shot.Number);
				}
				foreach (string k in series.Keys.ToList())
					series[k] = series[k].Distinct().Order().ToList();
				return series;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return null;
			}
		}

		// "IMG_1234 (1)", "IMG_1234 - Copy", "IMG_1234_Original", "IMG_1234-edited": the same shot as IMG_1234.
		static readonly Regex CopyOrEdit = new(@"(\s*\(\d+\)|\s*-\s*copy(\s*\(\d+\))?|\s+copy|[\s_-]*\(?(original|edited)\)?)+$",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		// A Pixel burst: 00001IMG_00001_BURST20260101120000123[_COVER]; the burst's id names the series.
		static readonly Regex PixelBurst = new(@"^(\d+).*BURST(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		// The time taken: 20201105_205359_HDR (Samsung), IMG_20140830_092242, PXL_20231105_205359123.MP (Pixel,
		// with milliseconds), Screenshot_20231105-205359_Chrome, "2020-11-05 20.53.59" (Dropbox). Read as a
		// plain number, 20:53:59 and 20:54:01 would be 42 apart. What follows the time (_HDR, .MP) is no part
		// of the series: an HDR shot and the plain one after it are one burst. A counter after the time
		// (20260101_120000_001) is left to Numbered.
		static readonly Regex TimeTaken = new(@"^(.*?)(?<!\d)((?:19|20)\d{2})(-?)(\d{2})\3(\d{2})[ _-]?(\d{2})([.:-]?)(\d{2})\7(\d{2})(\d{3})?\D*$",
			RegexOptions.CultureInvariant);
		// Everything else: the last run of digits is the shot's number.
		static readonly Regex Numbered = new(@"^(.*?)(\d{1,18})(\D*)$", RegexOptions.CultureInvariant);

		/// <summary>The series and number in a file name (without its extension), or null when it has no number.</summary>
		internal static Shot? Parse(string stem) {
			stem = CopyOrEdit.Replace(stem, "");
			Match m = PixelBurst.Match(stem);
			if (m.Success && m.Groups[1].Value.Length <= 18)
				return new Shot("burst" + m.Groups[2].Value, long.Parse(m.Groups[1].Value));
			if (TimeTaken.Match(stem) is { Success: true } t && TakenAt(t) is long ms)
				return new Shot(t.Groups[1].Value.ToLowerInvariant() + "@", ms, Timed: true);
			m = Numbered.Match(stem);
			if (!m.Success) return null;
			return new Shot((m.Groups[1].Value + "#" + m.Groups[3].Value).ToLowerInvariant(), long.Parse(m.Groups[2].Value));
		}

		/// <summary>The time in a <see cref="TimeTaken"/> match, in milliseconds, or null when it's no real date and time.</summary>
		static long? TakenAt(Match t) {
			int N(int group) => int.Parse(t.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
			try {
				var at = new DateTime(N(2), N(4), N(5), N(6), N(8), N(9));
				return at.Ticks / TimeSpan.TicksPerMillisecond + (t.Groups[10].Success ? N(10) : 0);
			}
			catch (ArgumentOutOfRangeException) {
				return null; // 20201345_...: a number that only looks like a date
			}
		}
	}
}
