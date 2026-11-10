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

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HEI.Core;
using HEI.Core.AI;
using HEI.Core.Utils;
using HEI.Core.ViewModels;

namespace HEI.Agent {
	/// <summary>One file in a group, as the review page shows it.</summary>
	/// <param name="Relation">
	/// How the file relates to the kept one: <c>keep</c>, <c>identical</c>, <c>smaller</c> (lower
	/// resolution), <c>compressed</c> (same picture, fewer bytes), <c>resaved</c> (same picture saved
	/// again), <c>edited</c> (the AI sees the same picture with changes: colour, filter),
	/// <c>variant</c> (cropped, mirrored, or a different shot that looks alike), <c>original</c> (a smaller photo that may be
	/// the original the kept one was enlarged from: the user's call), <c>wording</c> (looks the same, but its words differ:
	/// another message in a chat, another total on a receipt; the user's call).
	/// </param>
	/// <param name="Suggested">Pre-checked for the Recycle Bin: identical files and pixel-level copies only.</param>
	/// <param name="Synced">In a cloud-synced folder: deleting it also deletes it from the cloud and other devices.</param>
	sealed record ReportItem(
		string Path, string Name, string Folder, long Size, int Width, int Height, string? Format,
		double DurationSec, decimal BitrateKbs, float Fps, DateTime ModifiedUtc, float Similarity, bool AiMatched,
		string Relation, bool Keep, bool Suggested, bool Synced = false);

	/// <summary>
	/// A set of files that look like copies of each other. <see cref="Kind"/>: <c>identical</c> (all
	/// byte-for-byte the same), <c>copies</c> (some files are plain copies of the kept one and are
	/// pre-checked), <c>similar</c> (only edited versions and look-alikes: nothing is pre-checked).
	/// </summary>
	sealed record ReportGroup(string Key, string Kind, string Media, string KeepPath, string KeepReason,
		long ReclaimBytes, float MinSimilarity, List<ReportItem> Items);

	/// <summary>
	/// This build of Heiward: its version and the commit it was built from ("1.3.0+87802c8…"). A report
	/// is the rules of the build that made it, so a new build, even under the same version number,
	/// sets the old report aside and finds the sets again.
	/// </summary>
	static class AppBuild {
		public static readonly string Current =
			typeof(AppBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
	}

	/// <param name="Build">The <see cref="AppBuild"/> that made the report; null in reports from before builds were recorded.</param>
	sealed record Report(int Version, DateTime ScannedAtUtc, double DurationSec, string Device, int FilesScanned,
		List<string> Folders, List<string> ExcludedExtensions, List<string> Notes, List<ReportGroup> Groups, string? Build = null) {
		public const int CurrentVersion = 1;

		/// <summary>
		/// The report, when this build made it. Another build's report is set aside (null): its sets
		/// were judged by other rules, so the page doesn't show them and nothing is cleaned up from them
		/// until a scan with this build replaces it.
		/// </summary>
		public static Report? Load() => LoadAny() is { } report && report.Build == AppBuild.Current ? report : null;

		/// <summary>A report another build made, which a scan should replace (<see cref="Load"/> sets it aside).</summary>
		public static bool IsStale() => LoadAny() is { } report && report.Build != AppBuild.Current;

		/// <summary>The report, whichever build made it: only for telling which sets are new.</summary>
		public static Report? LoadAny() {
			try {
				if (!File.Exists(AgentPaths.Report) || JsonSerializer.Deserialize<Report>(File.ReadAllText(AgentPaths.Report), AgentConfig.Json) is not { } report)
					return null;
				// A later scan that found the same sets wrote only its own facts.
				return ScanFacts.Load() is { } facts && facts.About(new FileInfo(AgentPaths.Report)) && facts.ScannedAtUtc >= report.ScannedAtUtc
					? report with { ScannedAtUtc = facts.ScannedAtUtc, DurationSec = facts.DurationSec, FilesScanned = facts.FilesScanned }
					: report;
			}
			catch (Exception e) {
				AgentPaths.AppendLog($"report.json unreadable: {e.Message}");
				return null;
			}
		}

		/// <summary>
		/// Saves the report, its sets only when they changed: a scan that finds the same sets (a drive with no journal is
		/// listed again every 6 hours) rewrote megabytes of the same report each time. When it ran, how long it took and
		/// how many files it saw go to <see cref="ScanFacts"/>, a few hundred bytes, which <see cref="LoadAny"/> lays over it.
		/// </summary>
		public void Save() {
			string sets = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
				JsonSerializer.Serialize(this with { ScannedAtUtc = default, DurationSec = 0, FilesScanned = 0 }, AgentConfig.Json))));
			var file = new FileInfo(AgentPaths.Report);
			if (!(ScanFacts.Load() is { } last && last.Sets == sets && last.About(file))) {
				AgentPaths.WriteAtomic(AgentPaths.Report, JsonSerializer.Serialize(this, AgentConfig.Json));
				file.Refresh();
			}
			AgentPaths.WriteAtomic(ScanFacts.FilePath, JsonSerializer.Serialize(
				new ScanFacts(ScannedAtUtc, DurationSec, FilesScanned, sets, file.Length, file.LastWriteTimeUtc), AgentConfig.Json));
		}
	}

	/// <summary>
	/// The last scan's own facts, beside report.json (<see cref="Report.Save"/>). <paramref name="Sets"/> is a hash of the
	/// report without them; <paramref name="Length"/> and <paramref name="WrittenUtc"/> tell the report.json they go with,
	/// so a report another build wrote since isn't given them.
	/// </summary>
	sealed record ScanFacts(DateTime ScannedAtUtc, double DurationSec, int FilesScanned, string Sets, long Length, DateTime WrittenUtc) {
		public static string FilePath => Path.Combine(AgentPaths.Home, "report-scan.json");

		public static ScanFacts? Load() {
			try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ScanFacts>(File.ReadAllText(FilePath), AgentConfig.Json) : null; }
			catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
		}

		public bool About(FileInfo report) => report.Exists && report.Length == Length && report.LastWriteTimeUtc == WrittenUtc;
	}

	/// <summary>How alike two scanned files are, from their stored fingerprints; null when unknown.</summary>
	interface IFingerprints {
		/// <summary>VDF's classic similarity (percent) of the two files' gray frames, averaged over common positions.</summary>
		float? GrayPercent(string a, string b);
		/// <summary>Cosine (percent) of the two files' AI embeddings, averaged over common positions.</summary>
		float? AiPercent(string a, string b);
		/// <summary>
		/// How alike (percent) the two videos' soundtracks are, from their audio fingerprints at the
		/// offset where they match best; null when either has no sound to compare.
		/// </summary>
		float? AudioPercent(string a, string b);
		/// <summary>The largest difference (0–255) of any one cell of the two files' gray frames, over common positions; null when unknown.</summary>
		int? MaxCellDifference(string a, string b);
		/// <summary>
		/// Photos: the two look the same up close too (<see cref="CloseLook"/>, at most <see cref="ReportBuilder.CloseLookLimit"/>
		/// apart); null when it can't be told: a file that can't be decoded, or one on a resting drive.
		/// </summary>
		bool? SameUpClose(string a, string b);
		/// <summary>
		/// Photos: the two have the same colours too (<see cref="ColorLook"/>, at most <see cref="ReportBuilder.SameColorsLimit"/>
		/// apart); null when it can't be told, as for <see cref="SameUpClose"/>.
		/// </summary>
		bool? SameColors(string a, string b);
		/// <summary>
		/// Photos: the two are as sharp as each other up close too (<see cref="CloseLook.DetailKept"/>, at least
		/// <see cref="ReportBuilder.SameDetailLimit"/>); null when it can't be told, as for <see cref="SameUpClose"/>.
		/// </summary>
		bool? SameDetail(string a, string b);
		/// <summary>
		/// Photos: one picture lines up with the other cropped, scaled or turned a little, with its colours changed at most
		/// (<see cref="AlignedLook.SamePicture"/>); null when it can't be told: a file that can't be decoded, a picture too
		/// even to place, or one on a resting drive.
		/// </summary>
		bool? LinesUp(string keep, string other) => null;
		/// <summary>Photos: the picture's size as the text reader decodes it (turned as it's shown); null when its words can't be read.</summary>
		(int W, int H)? PictureSize(string path) => null;
		/// <summary>
		/// Photos: the picture's words read at <paramref name="size"/> (<see cref="TextMatch.Serialize"/>, "" for none); null when they
		/// can't be read: no text reader (its model isn't downloaded), a file that can't be decoded, or one on a resting drive.
		/// </summary>
		string? ReadWords(string path, (int W, int H) size) => null;
	}

	/// <summary>The fingerprints of the scan that just ran: its database's gray frames and its embedding cache.</summary>
	sealed class ScanFingerprints : IFingerprints {
		readonly Dictionary<string, FileEntry> entries;
		readonly UnionEmbeddingStore? embeddings;
		readonly CancellationToken ct;

		/// <summary>An audio fingerprint was made for a video in the report: the database is worth saving again.</summary>
		public bool AudioAdded { get; private set; }
		/// <summary>False for a video on a resting drive (<see cref="ScanEngine.MayRead"/>): it isn't decoded.</summary>
		public Func<string, bool>? MayRead { get; init; }

		public ScanFingerprints(string? embeddingCacheKey, bool withAi, CancellationToken ct = default) {
			entries = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
			foreach (FileEntry e in DatabaseUtils.Database) entries[e.Path] = e;
			embeddings = withAi ? UnionEmbeddingStore.Load(embeddingCacheKey) : null;
			this.ct = ct;
		}

		/// <summary>The file's last-modified time as the scan's database has it; null for a file it doesn't have.</summary>
		public DateTime? ModifiedUtc(string path) => entries.TryGetValue(path, out FileEntry? e) ? e.DateModified : null;

		public float? AudioPercent(string a, string b) {
			if (Audio(a) is not { } fa || Audio(b) is not { } fb) return null;
			var (shorter, longer) = fa.Length <= fb.Length ? (fa, fb) : (fb, fa);
			return 100f * ScanEngine.SlidingWindowCompare(shorter, longer).similarity;
		}

		/// <summary>
		/// The video's audio fingerprint, made the first time a video needs one (the scan itself makes
		/// none) and kept in the database; null for no sound, silence, or a file FFmpeg can't read.
		/// </summary>
		uint[]? Audio(string path) {
			if (!entries.TryGetValue(path, out FileEntry? e) || e.IsImage)
				return null;
			// A video on a resting drive keeps the fingerprint it has, if any: it isn't decoded now.
			if (e.AudioFingerprint == null && MayRead?.Invoke(path) != false &&
					!e.Flags.Any(EntryFlags.NoAudioTrack | EntryFlags.AudioFingerprintError | EntryFlags.SilentAudioTrack)) {
				ScanEngine.ExtractAudioFingerprint(e, ct);
				AudioAdded |= e.AudioFingerprint != null;
			}
			return e.AudioFingerprint is { Length: > 0 } fp ? fp : null;
		}

		public float? GrayPercent(string a, string b) => Average(a, b, e => e.grayBytes,
			(x, y) => x.Length == y.Length ? 100f * (1f - GrayBytesUtils.PercentageDifference(x, y)) : null);

		public int? MaxCellDifference(string a, string b) {
			if (!entries.TryGetValue(a, out FileEntry? ea) || !entries.TryGetValue(b, out FileEntry? eb)) return null;
			int? worst = null;
			foreach (var (key, x) in ea.grayBytes)
				if (x != null && eb.grayBytes.TryGetValue(key, out byte[]? y) && y != null && x.Length == y.Length)
					worst = Math.Max(worst ?? 0, GrayBytesUtils.MaxCellDifference(x, y));
			return worst;
		}

		public bool? SameUpClose(string a, string b) {
			if (MayRead?.Invoke(a) == false || MayRead?.Invoke(b) == false || CloseLook.Look(a, b) is not { } look)
				return null;
			if (look.Side > 0) {
				// Kept for SameDetail: the close look's decode tells how sharp each one is too.
				details[DetailKey(a, look.Side)] = look.DetailA;
				details[DetailKey(b, look.Side)] = look.DetailB;
			}
			return look.Difference <= ReportBuilder.CloseLookLimit;
		}

		public bool? SameDetail(string a, string b) {
			if (MayRead?.Invoke(a) == false || MayRead?.Invoke(b) == false || CloseLook.SideFor(a, b) is not int side)
				return null;
			if (side == 0)
				return true; // under 64 pixels a side, the 32×32 frame already sees the whole picture
			return Detail(a, side) is double da && Detail(b, side) is double db ? CloseLook.DetailKept(da, db) >= ReportBuilder.SameDetailLimit : null;
		}

		/// <summary>
		/// Each photo's detail at a close-look side (<see cref="CloseLook.Detail(byte[], int)"/>): kept from its close look this
		/// review, so a pair looked at now isn't decoded again, or decoded now for a close look kept from an earlier review,
		/// once a photo (a set's kept photo is asked about with each of its copies).
		/// </summary>
		readonly Dictionary<string, double?> details = new(StringComparer.OrdinalIgnoreCase);

		static string DetailKey(string path, int side) => $"{side}\n{path}";

		double? Detail(string path, int side) {
			string key = DetailKey(path, side);
			if (!details.TryGetValue(key, out double? detail))
				details[key] = detail = CloseLook.Detail(path, side);
			return detail;
		}

		public bool? SameColors(string a, string b) =>
			MayRead?.Invoke(a) == false || MayRead?.Invoke(b) == false ? null
			: Colors(a) is { } x && Colors(b) is { } y ? ColorLook.Difference(x, y, ColorLook.Side) <= ReportBuilder.SameColorsLimit : null;

		/// <summary>The last two photos' colour frames (<see cref="ColorLook.Frame"/>): a set's kept photo is asked about with each of its copies in turn.</summary>
		readonly (string? Path, byte[]? Frame)[] colors = new (string?, byte[]?)[2];
		int colorsUsed; // the one asked for last, which stays

		byte[]? Colors(string path) {
			for (int k = 0; k < colors.Length; k++)
				if (string.Equals(colors[k].Path, path, StringComparison.OrdinalIgnoreCase)) {
					colorsUsed = k;
					return colors[k].Frame;
				}
			colorsUsed = 1 - colorsUsed;
			colors[colorsUsed] = (path, ColorLook.Frame(path));
			return colors[colorsUsed].Frame;
		}

		// The pictures last used for the aligned look, the last used at the end: a kept file's, asked about each of its set's
		// other files (several at once, so one being decoded is waited for, not decoded twice), and those files again when
		// what's left of the set is judged around a kept file of its own (up to about 440 KB a picture).
		readonly List<(string Path, Lazy<AlignedLook.Picture?> Picture)> placed = new(PlacedKept);
		const int PlacedKept = 64;

		public bool? LinesUp(string keep, string other) {
			if (MayRead?.Invoke(keep) == false || MayRead?.Invoke(other) == false) return null;
			ct.ThrowIfCancellationRequested();
			return Placed(keep) is { } a && Placed(other) is { } b ? AlignedLook.SamePicture(a, b) : null;
		}

		AlignedLook.Picture? Placed(string path) {
			Lazy<AlignedLook.Picture?> picture;
			lock (placed) {
				int at = placed.FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
				if (at >= 0) {
					picture = placed[at].Picture;
					placed.RemoveAt(at);
				}
				else {
					picture = new(() => AlignedLook.Picture.Load(path));
					if (placed.Count == PlacedKept) placed.RemoveAt(0);
				}
				placed.Add((path, picture));
			}
			return picture.Value;
		}

		public float? AiPercent(string a, string b) {
			if (embeddings == null || !entries.TryGetValue(a, out FileEntry? ea) || !entries.TryGetValue(b, out FileEntry? eb)) return null;
			var keys = ea.grayBytes.Keys.Intersect(eb.grayBytes.Keys).DefaultIfEmpty(0d);
			var sims = keys.Select(k => (x: embeddings.GetEmbedding(ea, k), y: embeddings.GetEmbedding(eb, k)))
				.Where(p => p.x != null && p.y != null).Select(p => 100f * EmbeddingMath.CosineSimilarity(p.x!, p.y!)).ToList();
			return sims.Count > 0 ? sims.Average() : null;
		}

		/// <summary>
		/// The AI setting the text reader follows: on the NPU for Auto and NPU (when this PC's NPU can read with it, see
		/// <see cref="OcrReader.Open"/>), otherwise on the processor; for the graphics card, through its DirectML runtime, so a
		/// later scan's AI can still use the card.
		/// </summary>
		public AiDevice TextDevice { get; init; } = AiDevice.Auto;

		OcrReader? ocr;
		bool ocrOpened, ocrFailed;
		// The pictures last decoded for their words: a pair's two, asked for their sizes and then read.
		readonly List<(string Path, TextPicture Picture)> decoded = new(2);

		/// <summary>The text reader, opened the first time a pair's words are asked for; null without its model or ONNX Runtime.</summary>
		OcrReader? Ocr() {
			if (ocrOpened) return ocr;
			ocrOpened = true;
			if (TextDevice == AiDevice.Gpu) GpuComponents.TrySelectRuntime();
			ocr = OcrReader.Open(useNpu: TextDevice is AiDevice.Auto or AiDevice.Npu, fullSpeed: Pace.FullSpeed);
			AgentPaths.AppendLog(ocr == null
				? "text check: the text-reading model isn't downloaded, so pictures are judged by their look alone"
				: $"text check: reading on the {ocr.DeviceName}{(ocr.FallbackReason is { } why ? $" ({why})" : "")}");
			return ocr;
		}

		TextPicture? Picture(string path) {
			foreach (var (p, pic) in decoded)
				if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return pic;
			if (TextPicture.Load(path) is not { } loaded) return null;
			if (decoded.Count == 2) decoded.RemoveAt(0);
			decoded.Add((path, loaded));
			return loaded;
		}

		public (int W, int H)? PictureSize(string path) =>
			MayRead?.Invoke(path) == false || Ocr() == null || Picture(path) is not { } pic ? null : (pic.Width, pic.Height);

		public string? ReadWords(string path, (int W, int H) size) {
			ct.ThrowIfCancellationRequested();
			if (MayRead?.Invoke(path) == false || Ocr() is not { } reader || Picture(path) is not { } pic) return null;
			try {
				return TextCheck.ReadAt(reader, pic, size);
			}
			catch (Exception e) when (e is not OperationCanceledException) {
				// The NPU failing mid-scan: the processor reads the rest. The processor failing too: judged by look alone.
				AgentPaths.AppendLog($"text check: reading {Path.GetFileName(path)} on the {reader.DeviceName} failed: {Accelerators.OneLine(e.Message)}");
				reader.Dispose();
				ocr = ocrFailed || reader.DeviceName == "CPU" ? null : OcrReader.Open(useNpu: false, fullSpeed: Pace.FullSpeed);
				ocrFailed = true;
				return null;
			}
		}

		/// <summary>Closes the text reader, and says for the scan's log how much it read and where; null when it read nothing.</summary>
		public string? FinishReading() {
			decoded.Clear();
			if (ocr == null) return null;
			string? summary = ocr.Pictures == 0 ? null
				: $"text check: {ocr.Pictures} pictures read on the {ocr.DeviceName} in {ocr.Time.Elapsed.TotalSeconds:F1} s";
			ocr.Dispose();
			ocr = null;
			return summary;
		}

		/// <summary>The frames both files have, compared and averaged. A plain loop: the report asks it for every pair in a set.</summary>
		float? Average(string a, string b, Func<FileEntry, Dictionary<double, byte[]?>> frames, Func<byte[], byte[], float?> compare) {
			if (!entries.TryGetValue(a, out FileEntry? ea) || !entries.TryGetValue(b, out FileEntry? eb)) return null;
			Dictionary<double, byte[]?> other = frames(eb);
			double sum = 0;
			int count = 0;
			foreach (var (key, x) in frames(ea)) {
				if (x == null || !other.TryGetValue(key, out byte[]? y) || y == null || compare(x, y) is not float v) continue;
				sum += v;
				count++;
			}
			return count > 0 ? (float)(sum / count) : null;
		}
	}

	/// <summary>
	/// Turns the engine's duplicate groups into the review report. Code decides everything here —
	/// which file to keep and why, and what each other file is to it — and only plain copies are
	/// pre-checked. What the AI alone matched (an edit, a crop, a look-alike shot) is labelled for the
	/// user to judge, never pre-checked: a model's "these look the same" is not a reason to delete.
	/// </summary>
	static class ReportBuilder {
		/// <summary>AI matches at or above this cosine (percent) are the same picture edited; below it, crops and look-alikes.</summary>
		internal const float SamePictureAiPercent = 97f;
		/// <summary>
		/// A classic (grayscale) match at or above this is the same picture, pixel for pixel: a plain copy
		/// that may be pre-checked. Calibrated on photos; videos should get their own value once measured.
		/// </summary>
		internal const float PlainCopyPercent = 99.5f;
		/// <summary>
		/// Photos: a plain copy also has no cell of its gray frame more than this (0–255) from the kept one's. The
		/// average above hides a small local change: a signature, an annotation, a date stamp or another line in a chat
		/// moves a few cells by 7 to 70 and leaves the average at 99.8%. Measured on 40 photos, resized down to a
		/// quarter, recompressed or saved as PNG: no cell moved more than 6 (WIC against FFmpeg on one JPEG: 4).
		/// </summary>
		internal const int PlainCopyCellLimit = 8;
		/// <summary>
		/// Photos: a plain copy also looks the same up close (<see cref="CloseLook"/>): no block of the two decoded at up to
		/// 512 pixels more than this (0–255) apart. A blurred face or number plate keeps each cell's average, so only a
		/// closer look sees it. The same 40 photos' copies came to 3.6 at most. Of 296 small edits, all of which passed
		/// the average, 133 passed the cells too and 65 the close look too: blurs over even areas that change almost
		/// nothing (the benchmarks' CopyCellProbe).
		/// </summary>
		internal const double CloseLookLimit = 5;
		/// <summary>
		/// Photos: a plain copy also has the same colours (<see cref="ColorLook"/>): no cell's colour more than this (0–255)
		/// from the kept one's. The gray frames and the close look see brightness only, so a black-and-white or warmer copy
		/// passed them all and was pre-checked as a copy of its colour original. Measured on the look-alike set's tune half
		/// (docs/LOOKALIKE-BAR.md, 249 photos): its 1,245 resaves and resizes, against their original or each other, came to
		/// 4.3 at most; its warm edits to 8.5 at least, and its black-and-white ones to 12.1, but for one of a photo that was
		/// gray already (0, a plain copy in every pixel).
		/// </summary>
		internal const double SameColorsLimit = 6;
		/// <summary>
		/// Photos: a plain copy is also as sharp as the kept one up close (<see cref="CloseLook.DetailKept"/>): the softer of the
		/// two keeps at least this much of the other's detail, decoded at the close look's side. A blur over the whole photo
		/// keeps every block's average, and on a large photo it's about a pixel at 512, so the close look passed three blurred
		/// copies in the look-alike set. Measured on its tune half (docs/LOOKALIKE-BAR.md): its resaves and resizes, against
		/// their original or each other, kept 0.84 of the detail at least; the blurred copies the close look passed, 0.50 at most.
		/// </summary>
		internal const double SameDetailLimit = 0.7;
		/// <summary>
		/// A file less alike than this to the kept one (the percentage the page shows) is a different
		/// picture, not a look-alike: it leaves the set. VDF's groups chain (A is like B, B is like C), so
		/// without this a set could hold a picture that looks nothing like the one kept.
		/// </summary>
		internal const float MinAlikePercent = 75f;
		/// <summary>
		/// Two videos' soundtracks at or above this are one soundtrack; below it, the video has another
		/// one (another language, other music). Measured with a score under a voice: re-encoded to WMA at
		/// 24 and 64 kb/s, as MP2, or trimmed by whole seconds, the same audio scored 96.0–97.0%; another
		/// voice, other lines or the score alone 79.3–84.7%; unrelated audio 56%. A copy whose sound is
		/// shifted by half a second (trimmed mid-second) scores like another soundtrack, 83%: it isn't
		/// offered, which costs a set, never a file.
		/// </summary>
		internal const float SameSoundtrackPercent = 90f;

		/// <param name="hashes">The content hashes the last scan kept (<see cref="ContentHashes.Load"/>); none kept when null.</param>
		/// <param name="mayRead">False for a file on a resting drive (<see cref="ScanEngine.MayRead"/>): judged from what the scan knows, never opened.</param>
		/// <param name="known">The files the scan listed: a resting drive's folders are known from it, not listed again.</param>
		/// <param name="progress">How many of the sets' files are sorted, of how many, after each of the scan's sets.</param>
		/// <param name="ct">Stop scan: a set of thousands takes a while, so it's heard between rounds.</param>
		public static List<ReportGroup> Build(IEnumerable<DuplicateItem> duplicates, IFingerprints fingerprints, ContentHashes? hashes = null,
			Func<string, bool>? mayRead = null, IEnumerable<string>? known = null, Action<int, int>? progress = null, CancellationToken ct = default) {
			Func<string, bool>? before = readable;
			ILookup<string, string>? knownBefore = knownByFolder;
			Dictionary<string, bool>? datesBefore = datesTaken;
			readable = mayRead;
			knownByFolder = mayRead == null ? null : (known ?? Array.Empty<string>()).Where(p => !mayRead(p))
				.ToLookup(p => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase);
			datesTaken = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			try { return BuildReadable(duplicates, fingerprints, hashes ?? new ContentHashes(), progress, ct); }
			finally { readable = before; knownByFolder = knownBefore; datesTaken = datesBefore; }
		}

		/// <summary>This report's <c>mayRead</c>: a file on a resting drive isn't checked, hashed or opened for its EXIF.</summary>
		[ThreadStatic] static Func<string, bool>? readable;
		[ThreadStatic] static ILookup<string, string>? knownByFolder;
		internal static bool CanRead(string path) => readable?.Invoke(path) ?? true;
		/// <summary>The files the scan knows in a resting drive's folder (it isn't listed).</summary>
		internal static IEnumerable<string> KnownIn(string folder) => knownByFolder?[Path.TrimEndingDirectorySeparator(folder)] ?? Enumerable.Empty<string>();
		/// <summary>In a cloud-synced folder; a file on a resting drive isn't asked.</summary>
		static bool Synced(string path) => CanRead(path) && CloudFiles.IsSynced(path);

		/// <summary>This report's photos with an EXIF capture date, each file read once: a set's kept file is picked again
		/// every round from what's left, and reading them all every round made a set of 15,000 screenshots take hours.</summary>
		[ThreadStatic] static Dictionary<string, bool>? datesTaken;
		/// <summary>How many files this thread has opened for their capture date (tests).</summary>
		[ThreadStatic] internal static int DatesRead;

		/// <summary>The photo has an EXIF capture date; one on a resting drive isn't opened.</summary>
		static bool HasDateTaken(string path) {
			if (!CanRead(path)) return false;
			if (datesTaken != null && datesTaken.TryGetValue(path, out bool known)) return known;
			DatesRead++;
			bool has = ExifReader.TryGetDateTaken(path, out _);
			if (datesTaken != null) datesTaken[path] = has;
			return has;
		}

		static List<ReportGroup> BuildReadable(IEnumerable<DuplicateItem> duplicates, IFingerprints fingerprints, ContentHashes hashes,
			Action<int, int>? progress, CancellationToken ct) {
			var bursts = new BurstSeries();
			// Each group's files, and the groups, in path order: wherever the builder takes the first of
			// equals (the kept one of equal videos, the group split copies join), it's the same file in
			// every scan, not whichever the scan happened to hand over first.
			List<List<DuplicateItem>> members = duplicates.GroupBy(d => d.GroupId)
				.Select(g => g.Where(d => !CanRead(d.Path) || File.Exists(d.Path)).OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToList())
				.OrderBy(g => g.Count > 0 ? g[0].Path : "", StringComparer.OrdinalIgnoreCase)
				.ToList();
			GatherSplitCopies(members, hashes, fingerprints, ct);
			int total = members.Sum(items => items.Count), done = 0;
			var groups = new List<ReportGroup>();
			// A batch of groups at a time: the aligned looks their sets ask for first (each picture's kept file and its
			// look-alikes in the close band) are made together, several at once, before the sets are built. A set's few
			// look-alikes alone can't keep the processor busy.
			for (int start = 0; start < members.Count; start += LineUpBatch) {
				var batch = new List<List<List<DuplicateItem>>>();
				var lineUps = new List<(string Keep, string Other)>();
				foreach (List<DuplicateItem> items in members.Skip(start).Take(LineUpBatch)) {
					progress?.Invoke(done, total);
					var pictures = SplitByPicture(items, hashes, fingerprints, bursts, ct).ToList();
					foreach (List<DuplicateItem> picture in pictures)
						lineUps.AddRange(FirstLineUps(picture, hashes, fingerprints, bursts));
					batch.Add(pictures);
					done += items.Count;
				}
				ct.ThrowIfCancellationRequested();
				hashes.LineUpAhead(lineUps, fingerprints.LinesUp);
				foreach (List<List<DuplicateItem>> pictures in batch)
					foreach (List<DuplicateItem> picture in pictures)
						foreach (List<DuplicateItem> set in LeaveOutUnrelated(picture, hashes, fingerprints, bursts, ct))
							if (set.Count >= 2)
								groups.Add(BuildGroup(set, hashes, fingerprints, bursts));
			}
			progress?.Invoke(done, total);
			return groups
				.OrderBy(g => g.Kind == "similar" ? 1 : 0)
				.ThenByDescending(g => g.ReclaimBytes)
				.ThenBy(g => g.Key, StringComparer.Ordinal)
				.ToList();
		}

		/// <summary>
		/// VDF puts each file in one group, so byte-identical files can end up apart: a burst shot's
		/// original grouped with its neighbour as a look-alike, and its copy elsewhere grouped with an
		/// export of it, kept there as the best file. Each set of identical files moves into the group
		/// holding its closest other match (a group of nothing but those copies counts as closest), and
		/// a group left with one file is dropped.
		/// </summary>
		static void GatherSplitCopies(List<List<DuplicateItem>> groups, ContentHashes hashes, IFingerprints fingerprints, CancellationToken ct) {
			var splitBySize = groups
				.SelectMany((items, g) => items.Select(item => (Item: item, Group: g)))
				.GroupBy(x => x.Item.SizeLong)
				.Where(same => same.Select(x => x.Group).Distinct().Count() > 1) // hash only what could be split
				.ToList();
			foreach (var sameSize in splitBySize) {
				ct.ThrowIfCancellationRequested();
				foreach (var identical in sameSize.GroupBy(x => hashes.Get(x.Item.Path)).Where(h => h.Key != null)) {
					List<int> apart = identical.Select(x => x.Group).Distinct().ToList();
					if (apart.Count < 2)
						continue;
					var copies = identical.Select(x => x.Item).ToHashSet();
					int target = apart.MaxBy(g => Closeness(groups[g], copies, fingerprints));
					foreach (int g in apart.Where(g => g != target))
						groups[g].RemoveAll(copies.Contains);
					groups[target].AddRange(copies.Where(c => !groups[target].Contains(c)));
				}
			}
		}

		/// <summary>How close the group's other files are to the copies in it (100 when there are none).</summary>
		static float Closeness(List<DuplicateItem> group, HashSet<DuplicateItem> copies, IFingerprints fingerprints) {
			var others = group.Where(i => !copies.Contains(i)).ToList();
			if (others.Count == 0)
				return 100f;
			var here = group.Where(copies.Contains).ToList();
			return others.SelectMany(o => here.Select(c => fingerprints.GrayPercent(o.Path, c.Path) ?? 0f)).Max();
		}

		/// <summary>
		/// A VDF group can hold more than one set of copies: two look-alike burst shots, each imported
		/// twice. Judged against a single kept file, the second shot's copy is only a look-alike and
		/// isn't checked. Such a group becomes one group per picture, each with its own kept file; files
		/// that copy none of the others (look-alikes) leave the report.
		/// </summary>
		static IEnumerable<List<DuplicateItem>> SplitByPicture(List<DuplicateItem> items, ContentHashes hashes, IFingerprints fingerprints, BurstSeries bursts, CancellationToken ct) {
			if (items.Count < 4) { // two sets of copies take four files
				yield return items;
				yield break;
			}
			var sets = new List<List<DuplicateItem>>();
			var rest = new List<DuplicateItem>(items);
			while (rest.Count > 0) {
				ct.ThrowIfCancellationRequested();
				DuplicateItem keep = rest.Count == 1 ? rest[0] : PickKeeper(rest).Item1;
				var set = rest.Where(i => ReferenceEquals(i, keep) || IsPlainCopy(Relation(i, keep, hashes, fingerprints, bursts, askAi: false))).ToList();
				sets.Add(set);
				rest.RemoveAll(set.Contains);
			}
			var copies = sets.Where(s => s.Count >= 2).ToList();
			if (copies.Count < 2) {
				yield return items;
				yield break;
			}
			foreach (var set in copies)
				yield return set;
		}

		/// <summary>
		/// What stays with each kept file: its copies, edits and look-alikes at <see cref="MinAlikePercent"/>
		/// or more, with one shot of each burst (<see cref="BurstSeries"/>) at most: the kept file, or else
		/// the shot most like it. The burst's other shots leave, however alike, as do less alike pictures.
		/// Without that, IMG_0569 and IMG_0570 both stayed as look-alikes of an older IMG_0538, and a
		/// burst shot scoring 99.9% against a renamed copy of its neighbour was checked as a resaved copy.
		/// Other versions of a video (<see cref="IsOtherVersion"/>: another language, another soundtrack)
		/// leave the same way. What's left over is judged again around its own kept file, so a burst
		/// shot's copy in a backup folder still finds its original; a version that a set already shows
		/// another version of goes only with its own copies, not into a second set of the same
		/// look-alikes. A set of one is no set.
		/// </summary>
		static IEnumerable<List<DuplicateItem>> LeaveOutUnrelated(List<DuplicateItem> items, ContentHashes hashes, IFingerprints fingerprints, BurstSeries bursts, CancellationToken ct) {
			var rest = new List<DuplicateItem>(items);
			var shown = new HashSet<DuplicateItem>(); // versions whose burst or video a set already shows
			while (rest.Count >= 2) {
				ct.ThrowIfCancellationRequested();
				DuplicateItem keep = PickKeeper(rest).Item1;
				var set = new List<DuplicateItem> { keep };
				var otherVersions = new List<DuplicateItem>();
				// The closest first: of two shots of one burst, the one more like the kept file stays.
				var candidates = rest.Where(i => !ReferenceEquals(i, keep))
					.Select(i => (Item: i, Relation: Relation(i, keep, hashes, fingerprints, bursts)))
					.Select(c => (c.Item, c.Relation, Alike(c.Item, keep, c.Relation, fingerprints).Percent))
					.OrderByDescending(c => c.Relation == "identical").ThenByDescending(c => IsPlainCopy(c.Relation)).ThenByDescending(c => c.Percent)
					.ToList();
				// The close-band pairs the loop below may ask to line up, looked at ahead and several at once. The first kept
				// file's were looked at with the rest of its batch (FirstLineUps): these are a later kept file's.
				hashes.LineUpAhead(candidates
					.Where(c => AsksToLineUp(c.Item, keep, c.Relation, c.Percent) && !shown.Contains(keep) && !shown.Contains(c.Item))
					.Select(c => (keep.Path, c.Item.Path)).ToList(), fingerprints.LinesUp);
				foreach (var (i, relation, percent) in candidates) {
					if (relation == "identical") {
						set.Add(i);
						continue;
					}
					if (IsOtherVersion(relation) || set.Skip(1).Any(s => Versions(s, i, bursts))) {
						otherVersions.Add(i);
						continue;
					}
					// Clearly another document (other words throughout): judged again around its own kept file, like a picture less alike.
					if (relation == "different" || percent < MinAlikePercent || (!IsPlainCopy(relation) && (shown.Contains(keep) || shown.Contains(i))))
						continue;
					// A look-alike photo that doesn't line up with the kept one is another shot, however alike it scores: judged
					// again around its own kept file, like a picture less alike.
					if (AsksToLineUp(i, keep, relation, percent) && !hashes.LinesUp(keep.Path, i.Path, () => fingerprints.LinesUp(keep.Path, i.Path)))
						continue;
					set.Add(i);
				}
				rest.RemoveAll(set.Contains);
				if (set.Count >= 2) {
					shown.UnionWith(otherVersions);
					yield return set;
				}
			}
		}

		/// <summary>
		/// How alike the file is to the kept one, as the page shows it: the AI's cosine where the pixels
		/// differ (an edit or a variant), otherwise the grayscale match.
		/// </summary>
		static (float Percent, bool ByAi) Alike(DuplicateItem i, DuplicateItem keep, string relation, IFingerprints fingerprints) {
			if (ReferenceEquals(i, keep)) return (100f, false);
			float? ai = relation is "edited" or "variant" or "wording" ? fingerprints.AiPercent(i.Path, keep.Path) : null;
			return ai != null ? (ai.Value, true) : (fingerprints.GrayPercent(i.Path, keep.Path) ?? i.Similarity, false);
		}

		/// <summary>
		/// The close band: two photos the AI sees as alike but not as the same picture edited (<c>variant</c>, below
		/// <see cref="SamePictureAiPercent"/>), or alike with other words. Edits (crops, turns, filters) and other shots of
		/// one scene both score here, 94–97%; the aligned look (<see cref="IFingerprints.LinesUp"/>) tells them apart. Not
		/// animated pictures: they're compared by their first frame only.
		/// </summary>
		static bool InCloseBand(DuplicateItem i, DuplicateItem keep, string relation) =>
			i.IsImage && keep.IsImage && relation is "variant" or "wording" && !MayBeAnimated(i.Path) && !MayBeAnimated(keep.Path);

		/// <summary>A look-alike in the close band, alike enough to stay but for the aligned look: <see cref="LeaveOutUnrelated"/> asks it to line up.</summary>
		static bool AsksToLineUp(DuplicateItem i, DuplicateItem keep, string relation, float percent) =>
			percent >= MinAlikePercent && InCloseBand(i, keep, relation);

		/// <summary>How many groups have their first aligned looks made together (<see cref="FirstLineUps"/>).</summary>
		const int LineUpBatch = 64;

		/// <summary>
		/// The pairs <see cref="LeaveOutUnrelated"/> asks to line up first: the picture's kept file and each of its look-alikes in
		/// the close band. Asked ahead with other groups', several at once.
		/// </summary>
		static IEnumerable<(string Keep, string Other)> FirstLineUps(List<DuplicateItem> items, ContentHashes hashes, IFingerprints fingerprints, BurstSeries bursts) {
			if (items.Count < 2) yield break;
			DuplicateItem keep = PickKeeper(items).Item1;
			foreach (DuplicateItem i in items) {
				if (ReferenceEquals(i, keep)) continue;
				string relation = Relation(i, keep, hashes, fingerprints, bursts);
				if (AsksToLineUp(i, keep, relation, Alike(i, keep, relation, fingerprints).Percent))
					yield return (keep.Path, i.Path);
			}
		}

		/// <summary>
		/// Another version of the kept file rather than a copy: another shot of its burst, or the same
		/// video in another language or with another soundtrack. It leaves the set however alike.
		/// </summary>
		static bool IsOtherVersion(string relation) => relation is "burst" or "language" or "soundtrack";

		/// <summary>Two files in a set are versions of each other (their soundtracks aren't compared here: that takes decoding).</summary>
		static bool Versions(DuplicateItem a, DuplicateItem b, BurstSeries bursts) =>
			bursts.AreSiblings(a.Path, b.Path) ||
			(!a.IsImage && !b.IsImage && (LanguageVersions.ByName(a.Path, b.Path) || LanguageVersions.ByTags(a, b)));

		static (DuplicateItem, string) PickKeeper(List<DuplicateItem> items) => items[0].IsImage ? PickPhotoKeeper(items) : PickVideoKeeper(items);

		/// <summary>The relations that are the same picture, pixel for pixel: the ones pre-checked.</summary>
		internal static bool IsPlainCopy(string relation) => relation is "identical" or "smaller" or "compressed" or "resaved";

		static ReportGroup BuildGroup(List<DuplicateItem> items, ContentHashes hashes, IFingerprints fingerprints, BurstSeries bursts) {
			bool isImage = items[0].IsImage;
			(DuplicateItem keep, string reason) = PickKeeper(items);

			var reportItems = new List<ReportItem>(items.Count);
			foreach (DuplicateItem i in items.OrderByDescending(i => ReferenceEquals(i, keep)).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)) {
				string relation = ReferenceEquals(i, keep) ? "keep" : Relation(i, keep, hashes, fingerprints, bursts);
				// A softer, black-and-white or warmer copy is the same picture in gray blocks, so it stays in the set, but it's an
				// edit: shown as one, never pre-checked (SameDetailLimit, SameColorsLimit). Only here: which pictures make a set is
				// still the gray frames' call.
				if (isImage && IsPlainCopy(relation) && relation != "identical" &&
						(!hashes.SameDetail(i.Path, keep.Path, () => fingerprints.SameDetail(i.Path, keep.Path)) ||
						!hashes.SameColors(i.Path, keep.Path, () => fingerprints.SameColors(i.Path, keep.Path))))
					relation = "edited";
				(int w, int h) = ParseFrameSize(i.FrameSize);
				bool suggested = IsPlainCopy(relation);
				// Shown similarity: to the kept file (the AI's cosine where the pixels differ).
				(float shown, bool byAi) = Alike(i, keep, relation, fingerprints);
				reportItems.Add(new ReportItem(i.Path, Path.GetFileName(i.Path), Path.GetDirectoryName(i.Path) ?? "", i.SizeLong, w, h, i.Format,
					i.Duration.TotalSeconds, i.BitRateKbs, i.Fps, i.DateModified.ToUniversalTime(), shown, byAi,
					relation, relation == "keep", suggested, Synced(i.Path)));
			}
			string kind = reportItems.All(i => i.Relation is "keep" or "identical") ? "identical"
				: reportItems.Any(i => i.Suggested) ? "copies" : "similar";
			const string EnlargedNote = "a smaller file looks like the original (it has the capture date, or it's older), so the larger one may be an enlarged copy";
			if (kind == "identical")
				reason = "identical files; kept the one with the original-looking name and folder";
			else if (reportItems.Any(i => i.Relation == "original"))
				reason = kind == "similar" ? $"your pick: {EnlargedNote}" : $"{reason}; but {EnlargedNote}: that one is your call";
			else if (kind == "similar")
				reason = (reportItems.Any(i => i.Relation == "wording") ? "your pick: these look alike, but their words differ" : "your pick: these are different shots or edits") + " (suggested: " +
					(reason.StartsWith("highest resolution", StringComparison.Ordinal) ? "the highest resolution" : "the first one taken") + ")";
			return new ReportGroup(GroupKey(items.Select(i => i.Path)), kind, isImage ? "image" : "video", keep.Path, reason,
				reportItems.Where(i => i.Suggested).Sum(i => i.Size), items.Min(i => i.Similarity), reportItems);
		}

		/// <summary>
		/// What <paramref name="i"/> is to the kept file, from the two files' own fingerprints (VDF's
		/// per-member similarity is to whichever member pulled it into the group, so it says nothing
		/// about the keeper). A grayscale match at or above <see cref="PlainCopyPercent"/> is the same
		/// picture pixel for pixel. Measured on the test set with RGB-derived gray frames: resized
		/// 99.91–99.98%, recompressed 99.79–99.97%, while crops scored 97.35–97.66%, colour edits
		/// 97.12–97.99%, flips 96.16–97.32% and two different shots 94.3%. Below that, the AI's
		/// cosine says "same picture, edited" (≥ <see cref="SamePictureAiPercent"/>) or "variant".
		/// <c>burst</c>: another shot of the kept file's burst, which is no duplicate however alike;
		/// <c>language</c> and <c>soundtrack</c>: the same video in another language or with another
		/// soundtrack (<see cref="LanguageVersions"/>, <see cref="SameSoundtrackPercent"/>).
		/// Photos are also read (<see cref="ContentHashes.SameWords"/>): <c>wording</c> when the words differ, a look-alike for
		/// the user to judge; <c>different</c> when they're clearly another document, which leaves the set.
		/// </summary>
		/// <param name="askAi">False where only <see cref="IsPlainCopy"/> matters: below the plain-copy match the AI tells edited from
		/// variant, neither of them a plain copy, so it isn't asked (says variant).</param>
		static string Relation(DuplicateItem i, DuplicateItem keep, ContentHashes hashes, IFingerprints fingerprints, BurstSeries bursts, bool askAi = true) {
			// IMG_1234 next to IMG_1235, or 0084.png next to 0085.png: a burst, a retake, or an image
			// sequence's frames. Burst shots match 99% and more, which below would pass for a resaved
			// copy and be checked. A still stretch of an animation is even the same bytes, and each frame
			// is still one the sequence needs, so this comes before "identical".
			if (bursts.AreSiblings(i.Path, keep.Path))
				return "burst";
			// One video in two languages, as the names or the audio tracks' tags say (an older game's
			// intro_en.wmv and intro_de.wmv): each is the game's own file, even where it ships the same bytes twice.
			if (!i.IsImage && !keep.IsImage && (LanguageVersions.ByName(i.Path, keep.Path) || LanguageVersions.ByTags(i, keep)))
				return "language";
			if (i.SizeLong == keep.SizeLong && hashes.Same(i.Path, keep.Path))
				return "identical";
			// Named as a photo and its edit (Google Photos' "-edited", Samsung's and Picasa's
			// "_Original"): however light the edit (one scored 99.68%), both stay; the user decides.
			if (IsOriginalAndEdit(i.Path, keep.Path))
				return "edited";
			if (!i.IsImage && !keep.IsImage) {
				// Another soundtrack (another language nothing names, other music): the pictures match
				// frame for frame, and each is its own file.
				if (fingerprints.AudioPercent(i.Path, keep.Path) < SameSoundtrackPercent)
					return "soundtrack";
				// One has sound and the other none: the same pictures, but not a plain copy.
				if ((i.AudioFormat == null) != (keep.AudioFormat == null))
					return "variant";
			}
			// An animated picture is compared by its first frame only: two GIFs that start alike, or a
			// still taken from one, are not copies of each other.
			if (MayBeAnimated(i.Path) || MayBeAnimated(keep.Path))
				return "variant";
			bool photos = i.IsImage && keep.IsImage;
			string look = ByLook(fingerprints.GrayPercent(i.Path, keep.Path), () => askAi ? fingerprints.AiPercent(i.Path, keep.Path) : null,
				smaller: i.FrameSizeInt > 0 && i.FrameSizeInt < keep.FrameSizeInt,
				shorterCut: !i.IsImage && Math.Round(i.Duration.TotalSeconds) < Math.Round(keep.Duration.TotalSeconds),
				fewerBytes: i.SizeLong < keep.SizeLong,
				maxCell: photos ? fingerprints.MaxCellDifference(i.Path, keep.Path) : null,
				upClose: photos ? () => hashes.UpClose(i.Path, keep.Path, () => fingerprints.SameUpClose(i.Path, keep.Path)) : null);
			// An enlarged export (an AI upscale, a print-size copy) matches its original pixel for pixel and is kept for its
			// resolution. When the smaller photo has the capture date the kept one lacks, or is clearly older, it may be the
			// original: that one is the user's call, never checked for them.
			string relation = look == "smaller" && photos && MayBeOriginal(i, keep) ? "original" : look;
			// Duplicates only when the words match too: two screenshots of a chat one message apart, two receipts with other
			// totals or two pages of one form look alike pixel for pixel. Other words are the user's call; clearly another
			// document leaves the set. Where only a plain copy matters (askAi false), look-alikes aren't read.
			if (photos && (IsPlainCopy(relation) || relation == "original" || askAi))
				switch (hashes.SameWords(i.Path, keep.Path, fingerprints)) {
					case TextVerdict.Differs: return "wording";
					case TextVerdict.Unrelated: return "different";
				}
			return relation;
		}

		/// <summary>The smaller photo has an EXIF capture date the kept one lacks, or is more than an hour older: it may be the original the kept one was enlarged from.</summary>
		static bool MayBeOriginal(DuplicateItem smaller, DuplicateItem keep) =>
			smaller.DateModified < keep.DateModified - TimeSpan.FromHours(1) || (HasDateTaken(smaller.Path) && !HasDateTaken(keep.Path));

		/// <summary>
		/// The last steps of <see cref="Relation"/>, by look alone, for two pictures that aren't the same bytes and
		/// neither of which is animated: a grayscale match (<paramref name="gray"/>, percent) at or above
		/// <see cref="PlainCopyPercent"/>, for photos with no cell past <see cref="PlainCopyCellLimit"/> and the same up close,
		/// is the same picture, pixel for pixel (<c>smaller</c>, <c>compressed</c> or
		/// <c>resaved</c>; a shorter cut of a video is a <c>variant</c>); below it, the AI's cosine (<paramref name="ai"/>,
		/// percent, asked only then) says <c>edited</c> (≥ <see cref="SamePictureAiPercent"/>) or <c>variant</c>. Also how
		/// Heiward compares two pictures taken out of documents for another agent (<see cref="PictureCompare"/>).
		/// </summary>
		/// <param name="smaller">The file has a lower resolution than the kept one.</param>
		/// <param name="fewerBytes">The file is smaller on disk than the kept one.</param>
		/// <param name="maxCell">Photos: the largest single-cell difference of the gray frames; above <see cref="PlainCopyCellLimit"/> no plain copy.</param>
		/// <param name="upClose">Photos: whether they look the same up close (<see cref="CloseLookLimit"/>), asked only when all else says plain copy; false (or unknown) is no plain copy.</param>
		internal static string ByLook(float? gray, Func<float?> ai, bool smaller, bool shorterCut, bool fewerBytes, int? maxCell = null, Func<bool>? upClose = null) {
			if (gray is float g && g >= PlainCopyPercent && !(maxCell > PlainCopyCellLimit) && upClose?.Invoke() != false) {
				if (smaller)
					return "smaller";
				if (shorterCut)
					return "variant"; // a shorter cut of the video is not a plain copy
				return fewerBytes ? "compressed" : "resaved";
			}
			return ai() >= SamePictureAiPercent ? "edited" : "variant";
		}

		static readonly string[] EditSuffixes = { "_original", "-original", " (original)", "-edited", "_edited", " (edited)" };

		/// <summary>One name is the other's plus an original/edit suffix: IMG_1.jpg and IMG_1_Original.jpg.</summary>
		internal static bool IsOriginalAndEdit(string a, string b) {
			ReadOnlySpan<char> sa = Path.GetFileNameWithoutExtension(a.AsSpan()), sb = Path.GetFileNameWithoutExtension(b.AsSpan());
			foreach (string s in EditSuffixes)
				if (NameWith(sa, sb, s) || NameWith(sb, sa, s))
					return true;
			return false;

			// name is stem + suffix, without making the string: this runs for every pair in a set.
			static bool NameWith(ReadOnlySpan<char> name, ReadOnlySpan<char> stem, string suffix) =>
				name.Length == stem.Length + suffix.Length && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
		}

		static readonly string[] AnimatedExtensions = { ".gif", ".webp", ".apng", ".heics" };

		static bool MayBeAnimated(string path) {
			ReadOnlySpan<char> extension = Path.GetExtension(path.AsSpan());
			foreach (string e in AnimatedExtensions)
				if (extension.Equals(e, StringComparison.OrdinalIgnoreCase))
					return true;
			return false;
		}

		/// <summary>Stable across scans for the same set of files, so a "keep all" answer sticks.</summary>
		public static string GroupKey(IEnumerable<string> paths) {
			string joined = string.Join("\n", paths.Select(p => p.ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal));
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
		}

		/// <summary>
		/// Photos: the highest resolution; then the camera original (has an EXIF capture date; copies
		/// made by apps and messengers often lose it); then the oldest file (a copy is newer than what it
		/// copied); then the largest; then the original-looking name. File size alone is not quality: an
		/// edit that boosts colour makes a bigger JPEG than the untouched original.
		/// </summary>
		static (DuplicateItem, string) PickPhotoKeeper(List<DuplicateItem> items) {
			int best = items.Max(i => i.FrameSizeInt);
			var top = items.Where(i => i.FrameSizeInt == best).ToList();
			if (top.Count == 1)
				return (top[0], $"highest resolution ({top[0].FrameSize?.Replace("x", " × ")})");
			var withExif = top.Where(i => HasDateTaken(i.Path)).ToList();
			if (withExif.Count == 1)
				return (withExif[0], "the camera original (the only copy with its capture date)");
			if (withExif.Count > 1) top = withExif;
			DateTime oldest = top.Min(i => i.DateModified);
			var first = top.Where(i => i.DateModified - oldest < TimeSpan.FromSeconds(2)).ToList();
			if (first.Count == 1)
				return (first[0], "the original: same resolution, and the oldest file");
			long biggest = first.Max(i => i.SizeLong);
			var largest = first.Where(i => i.SizeLong == biggest).ToList();
			if (largest.Count == 1)
				return (largest[0], "same resolution and age; least compressed (largest file)");
			return (PreferOriginalLooking(largest), "same picture and size; kept the original-looking name and folder");
		}

		/// <summary>
		/// Videos: VDF's own default order (duration, resolution, bitrate, fps), then the oldest, then the
		/// smaller file; one with sound before one without, right after the duration.
		/// </summary>
		static (DuplicateItem, string) PickVideoKeeper(List<DuplicateItem> items) {
			var ordered = items
				.OrderByDescending(i => Math.Round(i.Duration.TotalSeconds))
				.ThenByDescending(i => i.AudioFormat != null)
				.ThenByDescending(i => i.FrameSizeInt)
				.ThenByDescending(i => i.BitRateKbs)
				.ThenByDescending(i => i.Fps)
				.ThenBy(i => i.DateModified)
				.ThenBy(i => i.SizeLong)
				.ToList();
			DuplicateItem keep = ordered[0], next = ordered[1];
			string reason =
				Math.Round(keep.Duration.TotalSeconds) > Math.Round(next.Duration.TotalSeconds) ? "longest (the others are shorter cuts)" :
				keep.AudioFormat != null && next.AudioFormat == null ? "the one with sound" :
				keep.FrameSizeInt > next.FrameSizeInt ? $"highest resolution ({keep.FrameSize?.Replace("x", " × ")})" :
				keep.BitRateKbs > next.BitRateKbs ? $"highest bitrate ({keep.BitRateKbs:N0} kb/s)" :
				keep.Fps > next.Fps ? $"highest frame rate ({keep.Fps:0.##} fps)" :
				"same quality; the oldest file";
			return (keep, reason);
		}

		static readonly string[] CopyMarkers = { " - copy", " copy", "(1)", "(2)", "(3)", "_1.", "-1." };
		static readonly string[] TransientFolders = { "\\downloads\\", "\\temp\\", "\\tmp\\", "\\desktop\\", "\\whatsapp", "\\telegram" };

		/// <summary>
		/// The synced copy (removing it would remove it from the cloud too), then outside
		/// Downloads/Desktop/temp, without "copy"/"(1)" in the name, oldest, shortest name.
		/// </summary>
		static DuplicateItem PreferOriginalLooking(List<DuplicateItem> items) =>
			items
				.OrderBy(i => Synced(i.Path) ? 0 : 1)
				.ThenBy(i => TransientFolders.Any(f => (i.Path.ToLowerInvariant() + "\\").Contains(f)) ? 1 : 0)
				.ThenBy(i => CopyMarkers.Any(m => Path.GetFileName(i.Path).Contains(m, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
				.ThenBy(i => i.DateModified)
				.ThenBy(i => Path.GetFileName(i.Path).Length)
				.ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
				.First();

		/// <summary>
		/// Content hashes, computed once per file and only for files whose sizes match. Hashing reads a whole
		/// file (up to 256 MB), so a scan's hashes are kept (<see cref="Load"/>, <see cref="Save"/>) and the
		/// next scan reuses each one whose file has the same size and the same last-modified time: without
		/// that, every hourly scan read every copy in the report again.
		/// </summary>
		internal sealed class ContentHashes {
			/// <param name="ModifiedUtc">The file's last-modified time when it was hashed.</param>
			internal sealed record Stored(long Size, DateTime ModifiedUtc, string Hash);

			/// <summary>
			/// Two photos' close look (<see cref="UpClose"/>): each file's size and last-modified time when it was made, and the answer;
			/// <paramref name="Colors"/>: whether their colours are the same too (<see cref="SameColors"/>), null until asked;
			/// <paramref name="Detail"/>: whether they're as sharp as each other (<see cref="SameDetail"/>), null until asked.
			/// </summary>
			internal sealed record Look(long SizeA, DateTime ModifiedA, long SizeB, DateTime ModifiedB, bool Same, bool? Colors = null, bool? Detail = null);

			readonly Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);
			readonly Dictionary<string, Stored> earlier, now = new(StringComparer.OrdinalIgnoreCase);
			readonly Dictionary<string, bool> looked = new(StringComparer.OrdinalIgnoreCase);
			readonly Dictionary<string, Look> looksEarlier, looksNow = new(StringComparer.OrdinalIgnoreCase);

			/// <summary>
			/// A photo's words (<see cref="SameWords"/>): how they were read (<see cref="TextCheck.Version"/>), the file's size and
			/// last-modified time then, the picture's size as decoded, and what was read at each size it was read at ("1080x2340").
			/// </summary>
			internal sealed record Words(string Reader, long Size, DateTime ModifiedUtc, int Width, int Height, Dictionary<string, string> Reads);

			readonly Dictionary<string, Words> wordsEarlier, wordsNow = new(StringComparer.OrdinalIgnoreCase);
			readonly Dictionary<string, TextVerdict?> worded = new(StringComparer.OrdinalIgnoreCase);

			/// <summary>Hashes for this report alone, kept nowhere (tests).</summary>
			public ContentHashes() : this(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase)) { }

			ContentHashes(Dictionary<string, Stored> earlier, Dictionary<string, Look> looks, Dictionary<string, Words> words) =>
				(this.earlier, looksEarlier, wordsEarlier) = (earlier, looks, words);

			public static string FilePath => Path.Combine(AgentPaths.Home, "hashes.json");
			public static string LooksPath => Path.Combine(AgentPaths.Home, "looks.json");
			/// <summary>The words read in the report's photos: they stay on this PC, in this file, and nowhere else.</summary>
			public static string TextsPath => Path.Combine(AgentPaths.Home, "texts.json");

			/// <summary>The hashes, close looks and words the last scan kept.</summary>
			public static ContentHashes Load() => new(Read<Stored>(FilePath), Read<Look>(LooksPath), Read<Words>(TextsPath));

			static Dictionary<string, T> Read<T>(string path) {
				try {
					if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path), AgentConfig.Json) is { } stored)
						return new Dictionary<string, T>(stored, StringComparer.OrdinalIgnoreCase);
				}
				catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
				return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
			}

			/// <summary>Keeps this scan's hashes, close looks and words, and only those: a file no report needs any more drops out.</summary>
			public void Save() {
				AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(now, AgentConfig.Json));
				AgentPaths.WriteAtomic(LooksPath, JsonSerializer.Serialize(looksNow, AgentConfig.Json));
				AgentPaths.WriteAtomic(TextsPath, JsonSerializer.Serialize(wordsNow, AgentConfig.Json));
			}

			/// <summary>
			/// Whether two photos say the same in words (<see cref="TextMatch"/>), both read at one size (<see cref="TextCheck.Sizes"/>):
			/// asked once a pair, and each picture read once a size and kept like the hashes, so the next scan reads only what
			/// changed; on a resting drive what was read before is taken on trust. Null when it can't be told (no text reader, a
			/// file that can't be decoded, a resting drive's file never read at that size): the pair is judged by its look alone.
			/// </summary>
			public TextVerdict? SameWords(string a, string b, IFingerprints fingerprints) {
				if (string.Compare(a, b, StringComparison.OrdinalIgnoreCase) > 0) (a, b) = (b, a);
				string key = a + "\n" + b;
				if (!worded.TryGetValue(key, out TextVerdict? verdict))
					worded[key] = verdict = CompareWords(a, b, fingerprints);
				return verdict;
			}

			TextVerdict? CompareWords(string a, string b, IFingerprints fingerprints) {
				if (WordsOf(a, fingerprints) is not { } wa || WordsOf(b, fingerprints) is not { } wb) return null;
				(int, int) shapeA = (wa.Width, wa.Height), shapeB = (wb.Width, wb.Height);
				var (sa, sb) = TextCheck.Sizes(shapeA, shapeB);
				if (ReadAt(a, wa, sa, fingerprints) is not { } ra || ReadAt(b, wb, sb, fingerprints) is not { } rb) return null;
				return TextCheck.Judge(ra, rb, TextCheck.SameShape(shapeA, shapeB));
			}

			Words? WordsOf(string path, IFingerprints fingerprints) {
				if (wordsNow.TryGetValue(path, out Words? w)) return w;
				if (!CanRead(path)) {
					if (!wordsEarlier.TryGetValue(path, out Words? kept) || kept.Reader != TextCheck.Version) return null;
					return wordsNow[path] = kept;
				}
				try {
					var file = new FileInfo(path);
					if (!file.Exists) return null;
					if (wordsEarlier.TryGetValue(path, out Words? s) && s.Reader == TextCheck.Version && s.Size == file.Length && s.ModifiedUtc == file.LastWriteTimeUtc)
						return wordsNow[path] = s;
					if (fingerprints.PictureSize(path) is not (int width, int height)) return null;
					return wordsNow[path] = new Words(TextCheck.Version, file.Length, file.LastWriteTimeUtc, width, height, new Dictionary<string, string>());
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return null;
				}
			}

			static string? ReadAt(string path, Words words, (int W, int H) size, IFingerprints fingerprints) {
				string key = $"{size.W}x{size.H}";
				if (words.Reads.TryGetValue(key, out string? read)) return read;
				if (!CanRead(path) || fingerprints.ReadWords(path, size) is not { } now) return null;
				return words.Reads[key] = now;
			}

			/// <summary>
			/// The two photos look the same up close (<paramref name="look"/>, <see cref="IFingerprints.SameUpClose"/>), asked once
			/// a pair and kept like the hashes: the next scan reuses the answer while both files are unchanged, and on a resting
			/// drive takes it on trust. False when it can't be told: no plain copy is claimed that wasn't looked at.
			/// </summary>
			public bool UpClose(string a, string b, Func<bool?> look) {
				if (string.Compare(a, b, StringComparison.OrdinalIgnoreCase) > 0) (a, b) = (b, a);
				string key = a + "\n" + b;
				if (!looked.TryGetValue(key, out bool same))
					looked[key] = same = LookAt(key, a, b, look);
				return same;
			}

			/// <summary>
			/// The two photos line up (<paramref name="look"/>, <see cref="IFingerprints.LinesUp"/>), asked once a pair and kept
			/// in looks.json beside the close looks (under its own key, which names how it was looked at), like them. True when it
			/// can't be told: the set stays as it was without the aligned look.
			/// </summary>
			public bool LinesUp(string a, string b, Func<bool?> look) {
				if (string.Compare(a, b, StringComparison.OrdinalIgnoreCase) > 0) (a, b) = (b, a);
				string key = AlignedLook.Version + "\n" + a + "\n" + b;
				if (!looked.TryGetValue(key, out bool same))
					looked[key] = same = LookAt(key, a, b, look, unknown: true);
				return same;
			}

			/// <summary>
			/// <see cref="LinesUp"/> for each pair (a kept file and one of its set's photos), asked ahead and several at once: it
			/// takes about a tenth of a second a pair, and a report of look-alikes asks it of thousands. Each answer is kept as
			/// LinesUp keeps it, so LinesUp finds it; the pairs it already knows, or can't look at, are left to it.
			/// </summary>
			public void LineUpAhead(IReadOnlyList<(string Keep, string Other)> pairs, Func<string, string, bool?> look) {
				var fresh = new List<(string Key, string Keep, string Other, FileInfo A, FileInfo B)>();
				var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var (keep, other) in pairs) {
					(string a, string b) = string.Compare(keep, other, StringComparison.OrdinalIgnoreCase) > 0 ? (other, keep) : (keep, other);
					string key = AlignedLook.Version + "\n" + a + "\n" + b;
					if (looked.ContainsKey(key) || !asked.Add(key) || !CanRead(a) || !CanRead(b)) continue;
					try {
						FileInfo fa = new(a), fb = new(b);
						if (!fa.Exists || !fb.Exists) continue;
						if (looksEarlier.TryGetValue(key, out Look? s) && s.SizeA == fa.Length && s.ModifiedA == fa.LastWriteTimeUtc &&
								s.SizeB == fb.Length && s.ModifiedB == fb.LastWriteTimeUtc)
							continue;
						fresh.Add((key, keep, other, fa, fb));
					}
					catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
				}
				if (fresh.Count < 2) return;
				var answers = new bool?[fresh.Count];
				Exception? stopped = null;
				// On half the processor's threads, in order: a kept file's pairs come together, so its picture is decoded once.
				Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, fresh.Count, 1),
					new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, (range, loop) => {
						for (int k = range.Item1; k < range.Item2; k++) {
							try { answers[k] = look(fresh[k].Keep, fresh[k].Other); }
							catch (OperationCanceledException e) { stopped = e; loop.Stop(); }
							catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
						}
					});
				if (stopped != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(stopped);
				for (int k = 0; k < fresh.Count; k++) {
					var (key, _, _, fa, fb) = fresh[k];
					if (answers[k] is bool same) {
						looksNow[key] = new Look(fa.Length, fa.LastWriteTimeUtc, fb.Length, fb.LastWriteTimeUtc, same);
						looked[key] = same;
					}
					else looked[key] = true; // can't be told: as LinesUp answers it
				}
			}

			bool LookAt(string key, string a, string b, Func<bool?> look, bool unknown = false) {
				if (!CanRead(a) || !CanRead(b)) {
					if (!looksEarlier.TryGetValue(key, out Look? kept)) return unknown;
					looksNow[key] = kept;
					return kept.Same;
				}
				try {
					FileInfo fa = new(a), fb = new(b);
					if (!fa.Exists || !fb.Exists) return unknown;
					if (looksEarlier.TryGetValue(key, out Look? s) && s.SizeA == fa.Length && s.ModifiedA == fa.LastWriteTimeUtc &&
							s.SizeB == fb.Length && s.ModifiedB == fb.LastWriteTimeUtc) {
						looksNow[key] = s;
						return s.Same;
					}
					if (look() is not bool same) return unknown;
					looksNow[key] = new Look(fa.Length, fa.LastWriteTimeUtc, fb.Length, fb.LastWriteTimeUtc, same);
					return same;
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return unknown;
				}
			}

			/// <summary>
			/// The two photos have the same colours too (<paramref name="look"/>, <see cref="IFingerprints.SameColors"/>): asked once a
			/// pair, of a pair already the same up close (<see cref="UpClose"/>), and kept with that look, so the next scan reuses
			/// it while both files are unchanged. False when it can't be told: a pair not looked at up close, or one on a resting
			/// drive whose colours weren't compared before. No plain copy is claimed whose colours weren't compared.
			/// </summary>
			public bool SameColors(string a, string b, Func<bool?> look) =>
				AlsoSame(a, b, look, close => close.Colors, (close, same) => close with { Colors = same });

			/// <summary>
			/// The two photos are as sharp as each other (<paramref name="look"/>, <see cref="IFingerprints.SameDetail"/>): asked and
			/// kept as <see cref="SameColors"/> is, of a pair already the same up close. False when it can't be told.
			/// </summary>
			public bool SameDetail(string a, string b, Func<bool?> look) =>
				AlsoSame(a, b, look, close => close.Detail, (close, same) => close with { Detail = same });

			/// <summary>A further question of a pair already the same up close, its answer kept in that close look (<see cref="SameColors"/>).</summary>
			bool AlsoSame(string a, string b, Func<bool?> look, Func<Look, bool?> kept, Func<Look, bool, Look> keep) {
				if (string.Compare(a, b, StringComparison.OrdinalIgnoreCase) > 0) (a, b) = (b, a);
				// Only a close look made or kept this scan, for the files as they are now (LookAt checks that); the answer is kept in it.
				if (!looksNow.TryGetValue(a + "\n" + b, out Look? close) || !close.Same) return false;
				if (kept(close) is bool known) return known;
				if (!CanRead(a) || !CanRead(b) || look() is not bool same) return false;
				looksNow[a + "\n" + b] = keep(close, same);
				return same;
			}

			public bool Same(string a, string b) {
				string? ha = Get(a), hb = Get(b);
				return ha != null && ha == hb;
			}

			public string? Get(string path) {
				if (!cache.TryGetValue(path, out string? h))
					cache[path] = h = HashOf(path);
				return h;
			}

			/// <summary>The hash kept for the file when it hasn't changed since, otherwise a new one; null when it can't be read.</summary>
			string? HashOf(string path) {
				// On a resting drive: the hash kept from its last scan, taken on trust; none, and it isn't hashed now.
				if (!CanRead(path)) {
					if (!earlier.TryGetValue(path, out Stored? kept)) return null;
					now[path] = kept;
					return kept.Hash;
				}
				try {
					var file = new FileInfo(path);
					if (!file.Exists) return null;
					if (earlier.TryGetValue(path, out Stored? s) && s.Size == file.Length && s.ModifiedUtc == file.LastWriteTimeUtc) {
						now[path] = s;
						return s.Hash;
					}
					if (Compute(path) is not string hash) return null;
					now[path] = new Stored(file.Length, file.LastWriteTimeUtc, hash);
					return hash;
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return null;
				}
			}

			/// <summary>Files up to this size are hashed whole; a bigger one by samples, so it's read through before it's removed (<see cref="Recycler"/>).</summary>
			internal const long FullHashBytes = 256L << 20;

			/// <summary>SHA-256 of the file; beyond <see cref="FullHashBytes"/>, of its size plus nine 4 MB samples across it.</summary>
			static string? Compute(string path) {
				const long Full = FullHashBytes, Chunk = 4L << 20;
				try {
					using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
					if (fs.Length <= Full)
						return Convert.ToHexString(SHA256.HashData(fs));
					using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
					sha.AppendData(BitConverter.GetBytes(fs.Length));
					var buffer = new byte[Chunk];
					for (int k = 0; k < 9; k++) {
						fs.Position = (fs.Length - Chunk) * k / 8;
						int read = fs.ReadAtLeast(buffer, (int)Chunk, throwOnEndOfStream: false);
						sha.AppendData(buffer, 0, read);
					}
					return Convert.ToHexString(sha.GetHashAndReset());
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return null;
				}
			}
		}

		static (int, int) ParseFrameSize(string? frameSize) {
			if (string.IsNullOrEmpty(frameSize)) return (0, 0);
			int x = frameSize.IndexOf('x');
			return x > 0 && int.TryParse(frameSize.AsSpan(0, x), out int w) && int.TryParse(frameSize.AsSpan(x + 1), out int h) ? (w, h) : (0, 0);
		}
	}
}
