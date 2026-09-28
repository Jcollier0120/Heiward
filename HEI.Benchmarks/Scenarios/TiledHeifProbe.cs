// /*
//     Copyright (C) 2026 0x90d
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

using System.Collections.Concurrent;
using System.Diagnostics;
using HEI.Core;
using HEI.Core.FFTools;
using HEI.Core.Utils;

namespace HEI.Benchmarks.Scenarios;

/// <summary>
/// Tiled HEIF (iPhone photo) probe: the in-process tile-grid decode against the FFmpeg
/// process, on real photos (no tiled HEIF can be generated, and personal photos cannot be
/// checked in). Run with:
///
///   dotnet run -c Release --project HEI.Benchmarks -- --probe-heif-tiles &lt;folder&gt; [parallelism]
///
/// For every .heic/.heif in the folder it reports how closely the in-process gray bytes match
/// the process path's, both derived from the AI frame as photos are hashed (VDF's own similarity,
/// where 96% makes a duplicate), and the AI frames'
/// mean difference, then photos/s for both paths, one at a time and in parallel.
/// </summary>
public static class TiledHeifProbe {
	public static int Run(string[] args) {
		if (args.Length == 4 && args[1] == "--pair")
			return ComparePair(args[2], args[3]);
		if (args.Length < 2 || !Directory.Exists(args[1])) {
			Console.Error.WriteLine("usage: --probe-heif-tiles <folder> [parallelism]  |  --probe-heif-tiles --pair <a> <b>");
			return 1;
		}
		int parallelism = args.Length > 2 ? int.Parse(args[2]) : 8;
		var files = Directory.EnumerateFiles(args[1]).Where(FileUtils.IsHeifImageFile).OrderBy(f => f, StringComparer.Ordinal).ToList();
		if (files.Count == 0) {
			Console.Error.WriteLine("No .heic/.heif files in the folder.");
			return 1;
		}
		if (!ScanEngine.NativeFFmpegExists || !ScanEngine.FFmpegExists) {
			Console.Error.WriteLine("Needs both the FFmpeg libraries and ffmpeg.exe in <app>/bin.");
			return 1;
		}
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;
		FfmpegEngine.UseNativeBinding = true;

		Console.WriteLine($"== Tiled HEIF probe: {files.Count} files, parallelism {parallelism} ==");

		// Warm up both paths (library load, first process start).
		Native(files[0]);
		Cli(files[0]);

		var native = new (byte[]? Gray, byte[]? Rgb)[files.Count];
		var cli = new (byte[]? Gray, byte[]? Rgb)[files.Count];
		double nativeSeq = Time(() => { for (int i = 0; i < files.Count; i++) native[i] = Native(files[i]); });
		double cliSeq = Time(() => { for (int i = 0; i < files.Count; i++) cli[i] = Cli(files[i]); });

		var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism };
		double nativePar = Time(() => Parallel.For(0, files.Count, options, i => Native(files[i])));
		double cliPar = Time(() => Parallel.For(0, files.Count, options, i => Cli(files[i])));

		// The GPU's decoder lane (HeifHardwareLane): on its own, and what it adds beside the CPU.
		var lane = HEI.Core.FFTools.FFmpegNative.HeifHardwareLane.Mode;
		HEI.Core.FFTools.FFmpegNative.HeifHardwareLane.Mode = HEI.Core.FFTools.FFmpegNative.HeifHardwareLane.LaneMode.Off;
		double cpuOnlyPar = Time(() => Parallel.For(0, files.Count, options, i => Native(files[i])));
		HEI.Core.FFTools.FFmpegNative.HeifHardwareLane.Mode = HEI.Core.FFTools.FFmpegNative.HeifHardwareLane.LaneMode.Always;
		var hardware = new (byte[]? Gray, byte[]? Rgb)[files.Count];
		double hardwareSeq = Time(() => { for (int i = 0; i < files.Count; i++) hardware[i] = Native(files[i]); });
		HEI.Core.FFTools.FFmpegNative.HeifHardwareLane.Mode = lane;
		var laneSims = Enumerable.Range(0, files.Count).Where(i => hardware[i].Gray != null && native[i].Gray != null)
			.Select(i => (1 - GrayBytesUtils.PercentageDifference(hardware[i].Gray!, native[i].Gray!)) * 100).ToList();
		var laneRgb = Enumerable.Range(0, files.Count).Where(i => hardware[i].Rgb != null && native[i].Rgb != null)
			.Select(i => MeanAbsoluteDifference(hardware[i].Rgb!, native[i].Rgb!)).ToList();

		var similarities = new List<double>();
		var rgbDiffs = new List<double>();
		int nativeFailed = 0, cliFailed = 0;
		for (int i = 0; i < files.Count; i++) {
			if (native[i].Gray == null) { nativeFailed++; continue; }
			if (cli[i].Gray == null) { cliFailed++; continue; }
			// Photos hash the gray frame derived from the AI frame (GrayBytesUtils.FromRgb224), whichever
			// decoder made it, so that is what the process path's result is compared as.
			if (cli[i].Rgb == null) { cliFailed++; continue; }
			double similarity = (1 - GrayBytesUtils.PercentageDifference(native[i].Gray!, GrayBytesUtils.FromRgb224(cli[i].Rgb!))) * 100;
			similarities.Add(similarity);
			if (native[i].Rgb != null && cli[i].Rgb != null)
				rgbDiffs.Add(MeanAbsoluteDifference(native[i].Rgb!, cli[i].Rgb!));
			if (similarity < 96)
				Console.WriteLine($"  below the duplicate threshold: {Path.GetFileName(files[i])} {similarity:F2}%");
		}

		Console.WriteLine($"failed: in-process {nativeFailed}, process {cliFailed}");
		if (similarities.Count > 0) {
			similarities.Sort();
			Console.WriteLine($"gray similarity in-process vs process: min {similarities[0]:F2}%  median {similarities[similarities.Count / 2]:F2}%  mean {similarities.Average():F2}%");
		}
		if (rgbDiffs.Count > 0)
			Console.WriteLine($"AI frame mean |difference| per byte: mean {rgbDiffs.Average():F2}  max {rgbDiffs.Max():F2} (of 255)");
		if (WicImageDecoder.IsAvailable) {
			// Windows' own HEIF codec as the referee; slow (a couple of photos per second), so 20.
			var wicSims = new List<double>();
			for (int i = 0; i < Math.Min(20, files.Count); i++)
				if (native[i].Gray != null && WicImageDecoder.TryDecode(files[i], out byte[]? wicGray, out _, out _, out _) && wicGray != null)
					wicSims.Add((1 - GrayBytesUtils.PercentageDifference(native[i].Gray!, wicGray)) * 100);
			if (wicSims.Count > 0)
				Console.WriteLine($"gray similarity in-process vs WIC: {wicSims.Count} photos, min {wicSims.Min():F2}%  mean {wicSims.Average():F2}%");
		}
		if (laneSims.Count > 0)
			Console.WriteLine($"hardware lane vs CPU lane: {laneSims.Count} photos, gray min {laneSims.Min():F2}%, AI frame mean |difference| {laneRgb.DefaultIfEmpty().Average():F3}");
		Console.WriteLine($"{"",-12} {"1 at a time",14} {"parallel " + parallelism,14}");
		Console.WriteLine($"{"in-process",-12} {Rate(files.Count, nativeSeq),14} {Rate(files.Count, nativePar),14}   (CPU with the hardware lane beside it)");
		Console.WriteLine($"{"CPU only",-12} {"",14} {Rate(files.Count, cpuOnlyPar),14}");
		Console.WriteLine($"{"GPU only",-12} {Rate(files.Count, hardwareSeq),14}");
		Console.WriteLine($"{"process",-12} {Rate(files.Count, cliSeq),14} {Rate(files.Count, cliPar),14}");
		return 0;
	}

	/// <summary>
	/// VDF's gray similarity between two files (say a HEIC and its JPEG export) for each
	/// combination of in-process and process decoding.
	/// </summary>
	static int ComparePair(string a, string b) {
		if (!ScanEngine.NativeFFmpegExists || !ScanEngine.FFmpegExists) {
			Console.Error.WriteLine("Needs both the FFmpeg libraries and ffmpeg.exe in <app>/bin.");
			return 1;
		}
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;
		byte[]? Gray(string file, bool native) {
			FfmpegEngine.UseNativeBinding = native;
			byte[]? rgb = native ? Native(file).Rgb : Cli(file).Rgb;
			return rgb != null ? GrayBytesUtils.FromRgb224(rgb) : null;
		}
		Console.WriteLine($"{Path.GetFileName(a)} vs {Path.GetFileName(b)}");
		foreach (bool nativeA in new[] { true, false })
			foreach (bool nativeB in new[] { true, false }) {
				byte[]? ga = Gray(a, nativeA), gb = Gray(b, nativeB);
				string similarity = ga != null && gb != null ? $"{(1 - GrayBytesUtils.PercentageDifference(ga, gb)) * 100:F2}%" : "decode failed";
				Console.WriteLine($"  {(nativeA ? "in-process" : "process"),-10} vs {(nativeB ? "in-process" : "process"),-10}: {similarity}");
			}
		return 0;
	}

	/// <summary>The scan's in-process path: the AI frame, and the gray bytes derived from it.</summary>
	static (byte[]? Gray, byte[]? Rgb) Native(string file) =>
		FfmpegEngine.TryGetImageInfoAndRgb224(file, out byte[]? rgb, out _, out _, extendedLogging: true) && rgb != null
			? (GrayBytesUtils.FromRgb224(rgb), rgb)
			: (null, null);

	static (byte[]? Gray, byte[]? Rgb) Cli(string file) =>
		FfmpegEngine.GetGrayAndRgb224Cli(file, TimeSpan.Zero, softwareDecodeOnly: true, extendedLogging: false);

	static double Time(Action action) {
		var sw = Stopwatch.StartNew();
		action();
		return sw.Elapsed.TotalSeconds;
	}

	static string Rate(int count, double seconds) => $"{count / seconds,7:F1} photos/s";

	static double MeanAbsoluteDifference(byte[] a, byte[] b) {
		long sum = 0;
		for (int i = 0; i < a.Length; i++)
			sum += Math.Abs(a[i] - b[i]);
		return (double)sum / a.Length;
	}
}
