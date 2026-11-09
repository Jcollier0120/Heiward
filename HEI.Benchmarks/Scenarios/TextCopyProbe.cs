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

using System.Diagnostics;
using System.Text.Json;
using HEI.Core.AI;
using HEI.Core.Utils;

namespace HEI.Benchmarks.Scenarios;

/// <summary>
/// <c>--probe-copies &lt;folder&gt; --text --state &lt;folder holding ai\&gt; [--models &lt;folder&gt;] [--npu] [--background] [--reads &lt;file&gt;] [--rows]</c>:
/// how master and the text check judge each of a picture's copies and its twin whose words differ. The folder is laid
/// out as <c>tools/text-fixtures.ps1</c> lays it out: <c>NAME__orig.*</c> beside its true copies (<c>half</c>,
/// <c>quarter</c>, <c>w1024</c>, <c>jpeg</c>, <c>png</c>, <c>heic</c>) and its twin (<c>diff</c>, and <c>diff-half</c>
/// made from it). Master's plain copy is the 32×32 match, the cells and the close look; the new check is that and the
/// words the same too (<see cref="TextMatch"/>), both read at one size (<see cref="TextCheck.Sizes"/>). Prints, per kind
/// and per family, how many each calls a copy, the text verdicts, any true copy the words lost, and the reader's time.
/// <c>--reads</c> keeps what was read in a file, so another run (tuning <see cref="TextMatch"/>) reads nothing again.
/// </summary>
internal static class TextCopyProbe {
	sealed record Row(string Family, string Name, string Kind, bool Twin, bool Master, TextVerdict Verdict, int Stretches, int WordsA, int WordsB, string ReadA, string ReadB) {
		public bool Copy => Master && Verdict == TextVerdict.Same;
	}

	public static int Run(string[] args) {
		string folder = args[1];
		string? Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
		if (Arg("--state") is { } state) CoreUtils.UseStateFolder(state);
		if (Arg("--models") is { } models) {
			if (!TextComponents.Verify(models, out string? why)) { Console.Error.WriteLine($"{models}: {why}"); return 2; }
			TextComponents.FolderOverride = models;
		}
		bool npu = args.Contains("--npu");
		string? readsFile = Arg("--reads");
		var reads = readsFile != null && File.Exists(readsFile)
			? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(readsFile)) ?? new()
			: new Dictionary<string, string>();
		int cached = reads.Count;

		OcrReader? reader = null;
		var open = new Stopwatch();
		OcrReader Reader() {
			if (reader != null) return reader;
			open.Start();
			reader = OcrReader.Open(npu, fullSpeed: !args.Contains("--background")) ?? throw new InvalidOperationException("the text reader can't start: is the model there, and ONNX Runtime in --state?");
			open.Stop();
			Console.Error.WriteLine($"reading on the {reader.DeviceName}{(reader.FallbackReason is { } f ? $" ({f})" : "")}, opened in {open.ElapsedMilliseconds:N0} ms");
			return reader;
		}
		string Read(string path, TextPicture? pic, (int W, int H) size) {
			string key = $"{Path.GetFileName(path)}@{size.W}x{size.H}";
			if (reads.TryGetValue(key, out string? r)) return r;
			pic ??= TextPicture.Load(path) ?? throw new InvalidOperationException($"can't decode {path}");
			return reads[key] = TextCheck.ReadAt(Reader(), pic, size);
		}

		var rows = new List<Row>();
		try {
			foreach (string orig in Directory.EnumerateFiles(folder, "*__orig.*").Order()) {
				string stem = Path.GetFileName(orig)[..Path.GetFileName(orig).IndexOf("__", StringComparison.Ordinal)];
				string family = stem.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
				if (Decode(orig) is not { } da) { Console.Error.WriteLine($"can't decode {orig}"); continue; }
				var (ga, sa) = da;
				TextPicture? pa = null;
				foreach (string copy in Directory.EnumerateFiles(folder, stem + "__*.*").Where(f => f != orig).Order()) {
					string kind = Path.GetFileNameWithoutExtension(copy)[(stem.Length + 2)..];
					if (Decode(copy) is not { } db) { Console.Error.WriteLine($"can't decode {copy}"); continue; }
					var (gb, sb) = db;
					double sim = 100.0 * (1.0 - GrayBytesUtils.PercentageDifference(ga, gb));
					bool master = sim >= 99.5 && GrayBytesUtils.MaxCellDifference(ga, gb) <= 8 && CloseLook.Difference(orig, copy) <= 5;
					var (at, bt) = TextCheck.Sizes(sa, sb);
					string ra = reads.ContainsKey($"{Path.GetFileName(orig)}@{at.W}x{at.H}") ? Read(orig, null, at) : Read(orig, pa ??= TextPicture.Load(orig), at);
					string rb = Read(copy, null, bt);
					var (verdict, stretches, wa, wb) = TextMatch.Compare(ra, rb);
					if (verdict == TextVerdict.Unrelated && !TextCheck.SameShape(sa, sb)) verdict = TextVerdict.Differs;
					rows.Add(new Row(family, stem, kind, kind.StartsWith("diff", StringComparison.Ordinal), master, verdict, stretches, wa, wb, ra, rb));
				}
			}
		}
		finally {
			if (readsFile != null && reads.Count > cached) File.WriteAllText(readsFile, JsonSerializer.Serialize(reads));
		}

		Console.WriteLine($"{"kind",-10} {"n",3}  {"master copy",11}  {"same",4} {"differs",7} {"unrelated",9}  {"new copy",8}  {"lost",4}");
		foreach (var g in rows.GroupBy(r => r.Kind).OrderBy(g => g.First().Twin ? 1 : 0).ThenBy(g => g.Key))
			Line(g.Key, g.ToList());
		Console.WriteLine();
		Console.WriteLine($"{"family",-10} {"n",3}  {"master copy",11}  {"same",4} {"differs",7} {"unrelated",9}  {"new copy",8}  {"lost",4}");
		foreach (var g in rows.GroupBy(r => (r.Family, r.Twin)).OrderBy(g => g.Key.Twin).ThenBy(g => g.Key.Family))
			Line(g.Key.Family + (g.Key.Twin ? " twin" : ""), g.ToList());
		var copies = rows.Where(r => !r.Twin).ToList();
		var twins = rows.Where(r => r.Twin).ToList();
		Console.WriteLine();
		Console.WriteLine($"true copies: {copies.Count}, master keeps {copies.Count(r => r.Master)} as copies, the text check {copies.Count(r => r.Copy)} (lost {copies.Count(r => r.Master && !r.Copy)})");
		Console.WriteLine($"twins (other words): {twins.Count}, master calls {twins.Count(r => r.Master)} copies, the text check {twins.Count(r => r.Copy)}; words differ in {twins.Count(r => r.Verdict != TextVerdict.Same)}, another document in {twins.Count(r => r.Verdict == TextVerdict.Unrelated)}");
		if (reader != null)
			Console.WriteLine($"reader: {reader.DeviceName}, opened in {open.ElapsedMilliseconds:N0} ms, {reader.Pictures} pictures read in {reader.Time.ElapsedMilliseconds:N0} ms ({(reader.Pictures == 0 ? 0 : reader.Time.ElapsedMilliseconds / reader.Pictures):N0} ms each){(cached > 0 ? $"; {cached} reads from {readsFile}" : "")}");
		else if (cached > 0)
			Console.WriteLine($"reader: every read from {readsFile}");

		if (args.Contains("--rows"))
			foreach (Row r in rows.OrderBy(r => r.Twin).ThenBy(r => r.Kind).ThenBy(r => r.Name)) {
				Console.WriteLine($"{r.Kind,-10} {r.Name,-9} master {(r.Master ? "copy" : "-"),-4} {r.Verdict,-9} stretches {r.Stretches} words {r.WordsA}/{r.WordsB}");
				if (r.Verdict != TextVerdict.Same || r.Twin) {
					Console.WriteLine($"    a: {Short(TextMatch.Plain(r.ReadA))}");
					Console.WriteLine($"    b: {Short(TextMatch.Plain(r.ReadB))}");
				}
			}
		reader?.Dispose();
		return 0;

		static void Line(string name, List<Row> g) =>
			Console.WriteLine($"{name,-10} {g.Count,3}  {g.Count(r => r.Master),11}  {g.Count(r => r.Verdict == TextVerdict.Same),4} {g.Count(r => r.Verdict == TextVerdict.Differs),7} {g.Count(r => r.Verdict == TextVerdict.Unrelated),9}  {g.Count(r => r.Copy),8}  {(g[0].Twin ? "" : g.Count(r => r.Master && !r.Copy).ToString()),4}");
		static string Short(string s) => s.Length <= 160 ? s : s[..157] + "...";
	}

	/// <summary>The 32×32 gray frame and the displayed size, as the scan reads a photo (WIC), or null.</summary>
	static (byte[] Gray, (int W, int H) Size)? Decode(string path) =>
		WicImageDecoder.TryDecode(path, out byte[]? gray, out _, out int w, out int h) && gray != null ? (gray, (w, h)) : null;
}
