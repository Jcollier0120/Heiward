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

using System.Collections.Concurrent;
using System.Diagnostics;
using HEI.Core;
using HEI.Core.AI;
using HEI.Core.FFTools;
using HEI.Core.FFTools.FFmpegNative;
using HEI.Core.Utils;

namespace HEI.Benchmarks.Scenarios;

/// <summary>
/// Video frames the way a scan reads them (the middle frame of each video: its 32×32 gray frame and the
/// AI's 224×224 frame), on a folder of real videos, decoded on the CPU and on the GPU's video decoder:
/// wall time, the process's CPU time, and whether the GPU's frames match the CPU's.
///
///   HEI.Benchmarks --probe-video-decode &lt;folder&gt; [--files 60] [--parallel 1,4,9,17] [--modes cpu,gpu]
///
/// Modes: cpu (software), gpu (<see cref="HardwareVideoDecode"/>, the scan's own path),
/// and d3d11va / d3d12va (FFmpeg's hardware mode as VDF set it, a device per file).
/// </summary>
static class VideoDecodeProbe {
	public static int Run(string[] args) {
		string? Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
		if (args.Length < 2 || !Directory.Exists(args[1])) {
			Console.Error.WriteLine("Usage: --probe-video-decode <folder> [--files 60] [--parallel 1,4,9,17] [--modes cpu,gpu]");
			return 1;
		}
		if (!ScanEngine.NativeFFmpegExists || !ScanEngine.FFmpegExists) {
			Console.Error.WriteLine("FFmpeg (native libraries and ffprobe) not found next to the probe.");
			return 1;
		}
		int fileCount = int.Parse(Arg("--files") ?? "60");
		int[] parallel = (Arg("--parallel") ?? "1,4,9,17").Split(',').Select(int.Parse).ToArray();
		string[] modes = (Arg("--modes") ?? "cpu,gpu").Split(',');

		var files = Directory.EnumerateFiles(args[1], "*", SearchOption.AllDirectories)
			.Where(f => FileUtils.VideoExtensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
			.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(fileCount).ToList();
		var infos = new ConcurrentDictionary<string, MediaInfo>();
		Parallel.ForEach(files, f => { if (FFProbeEngine.GetMediaInfo(f, false) is { } info) infos[f] = info; });
		files = files.Where(infos.ContainsKey).ToList();
		var codecs = files.GroupBy(f => infos[f].Streams?.FirstOrDefault(s => s.CodecType == "video")?.CodecName ?? "?")
			.Select(g => $"{g.Key} ×{g.Count()}");
		Console.WriteLine($"{files.Count} videos ({string.Join(", ", codecs)}), {files.Sum(f => new FileInfo(f).Length) / 1e9:N2} GB");

		FfmpegEngine.UseNativeBinding = true;
		// --cap 11: hold the process to that share of the processor, as a background scan is (a job's hard cap).
		if (Arg("--cap") is { } cap) {
			CapCpu(double.Parse(cap, System.Globalization.CultureInfo.InvariantCulture));
			Pace.FullSpeed = false;
			Console.WriteLine($"Capped at {cap}% of the processor, at a background scan's pace ({HardwareVideoDecode.Slots} GPU slots)");
		}
		if (args.Contains("--photos"))
			return Photos(args[1], fileCount, parallel);
		List<float> positions = ScanEngine.BuildSamplePositions(1);
		Dictionary<string, byte[]>? reference = null;
		foreach (int p in parallel) {
			foreach (string mode in modes) {
				FfmpegEngine.HardwareAccelerationMode = mode is "d3d11va" or "d3d12va" ? Enum.Parse<FFHardwareAccelerationMode>(mode) : FFHardwareAccelerationMode.none;
				HardwareVideoDecode.ResetForScan(); // counters per mode; HEI_VIDEO_HWDECODE=always stays
				if (mode != "gpu")
					HardwareVideoDecode.Mode = HardwareVideoDecode.LaneMode.Off;
				FfmpegEngine.UseNativeBinding = true; // resets the per-scan failure breaker
				var gray = new ConcurrentDictionary<string, byte[]>();
				int failed = 0;
				var sink = new DiscardingSink();
				Process self = Process.GetCurrentProcess();
				TimeSpan cpuBefore = self.TotalProcessorTime;
				// The most memory committed while it runs: on a PC whose GPU shares the RAM, decoder surfaces count too.
				long peakCommit = 0;
				using var sampling = new CancellationTokenSource();
				Task sampler = Task.Run(async () => {
					using Process me = Process.GetCurrentProcess();
					while (!sampling.IsCancellationRequested) {
						me.Refresh();
						peakCommit = Math.Max(peakCommit, me.PrivateMemorySize64);
						try { await Task.Delay(50, sampling.Token); } catch (OperationCanceledException) { }
					}
				});
				var wall = Stopwatch.StartNew();
				Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = p }, f => {
					var entry = new FileEntry(f) { mediaInfo = infos[f], grayBytes = new(), PHashes = new() };
					if (FfmpegEngine.GetGrayBytesFromVideo(entry, positions, 0, false, embeddingSink: sink) && entry.grayBytes.Values.FirstOrDefault() is { } g)
						gray[f] = g;
					else
						Interlocked.Increment(ref failed);
				});
				wall.Stop();
				sampling.Cancel();
				sampler.Wait();
				self.Refresh();
				double cpu = (self.TotalProcessorTime - cpuBefore).TotalSeconds;
				string parity = "";
				if (mode == "cpu")
					reference ??= new Dictionary<string, byte[]>(gray);
				else if (reference != null) {
					int compared = 0, differing = 0;
					double meanDiff = 0;
					var differ = new List<string>();
					foreach (var (f, g) in gray) {
						if (!reference.TryGetValue(f, out byte[]? r))
							continue;
						compared++;
						double d = g.Zip(r, (a, b) => Math.Abs(a - b)).Average();
						meanDiff += d;
						if (d > 0) {
							differing++;
							var v = infos[f].Streams?.FirstOrDefault(s => s.CodecType == "video");
							differ.Add($"{Path.GetFileName(f)} ({v?.CodecName} {v?.PixelFormat}, |diff| {d:N2})");
						}
					}
					if (args.Contains("--verbose") && differ.Count > 0)
						Console.WriteLine("  differ: " + string.Join("; ", differ));
					parity = $"; gray frames vs CPU: {differing} of {compared} differ, mean |diff| {(compared == 0 ? 0 : meanDiff / compared):N3}";
				}
				Console.WriteLine($"parallel {p,2}, {mode,-7}: {wall.Elapsed.TotalSeconds,6:N2} s wall, {cpu,6:N1} s CPU, peak {peakCommit / 1e6,5:N0} MB " +
					$"({cpu * 1000 / Math.Max(1, files.Count - failed),5:N0} ms CPU per video), {failed} failed, {sink.Frames} AI frames{parity}{HardwareVideoDecode.Describe()}");
			}
		}
		return 0;
	}

	/// <summary>
	/// --photos: iPhone photos (tiled HEIF) decoded as a scan does, on the CPU alone and with the GPU lane
	/// (<see cref="HeifHardwareLane"/>) at either pace, which sets when the lane takes a photo.
	/// </summary>
	static int Photos(string folder, int fileCount, int[] parallel) {
		var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(FileUtils.IsHeifImageFile)
			.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(fileCount).ToList();
		Console.WriteLine($"{files.Count} HEIF photos");
		bool capped = !Pace.FullSpeed;
		foreach (int p in parallel) {
			foreach (string mode in new[] { "cpu", "gpu-full-speed", "gpu-background" }) {
				HeifHardwareLane.Mode = mode == "cpu" ? HeifHardwareLane.LaneMode.Off : HeifHardwareLane.LaneMode.Auto;
				Pace.FullSpeed = mode != "gpu-background";
				int failed = 0;
				Process self = Process.GetCurrentProcess();
				TimeSpan cpuBefore = self.TotalProcessorTime;
				var wall = Stopwatch.StartNew();
				Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = p }, f => {
					if (!HeifTileGridDecoder.TryDecode(f, wantRgb: true, out _))
						Interlocked.Increment(ref failed);
				});
				wall.Stop();
				self.Refresh();
				double cpu = (self.TotalProcessorTime - cpuBefore).TotalSeconds;
				Console.WriteLine($"parallel {p,2}, {mode,-14}: {wall.Elapsed.TotalSeconds,6:N2} s wall ({files.Count / wall.Elapsed.TotalSeconds,5:N1} photos/s), {cpu,6:N1} s CPU ({cpu * 1000 / files.Count,4:N0} ms per photo), {failed} failed");
			}
		}
		Pace.FullSpeed = !capped;
		return 0;
	}

	static void CapCpu(double percent) {
		const int JobObjectCpuRateControlInformation = 15;
		IntPtr job = CreateJobObject(IntPtr.Zero, null);
		if (job == IntPtr.Zero || !AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
			throw new InvalidOperationException("Could not join a job to cap the processor.");
		var rate = new CpuRateControl { ControlFlags = 0x1 | 0x4, CpuRate = (uint)Math.Round(percent * 100) }; // enable, hard cap
		if (!SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref rate, System.Runtime.InteropServices.Marshal.SizeOf<CpuRateControl>()))
			throw new InvalidOperationException("Could not cap the processor.");
	}

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	struct CpuRateControl { public uint ControlFlags, CpuRate; }

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref CpuRateControl info, int size);

	/// <summary>Wants every AI frame (so the scan's conversion runs) and recycles it.</summary>
	sealed class DiscardingSink : IEmbeddingFrameSink {
		int frames;
		public int Frames => frames;
		public bool WantsEmbedding(FileEntry entry, double positionKey) => true;
		public void SubmitFrame(FileEntry entry, double positionKey, byte[] rgb224) {
			Interlocked.Increment(ref frames);
			FramePool.Shared.Return(rgb224);
		}
	}
}
