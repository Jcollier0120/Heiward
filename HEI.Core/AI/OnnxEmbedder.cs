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

using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using Microsoft.ML.OnnxRuntime;
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>
	/// Wraps the ONNX image-embedding model (DINOv2-small vision transformer). Input is a
	/// raw 224×224 RGB24 frame; output an L2-normalized (optionally int8-quantized)
	/// embedding whose dot product is the frames' cosine similarity. Instances are
	/// created per scan; <see cref="EmbedBatch"/> is called from a single worker thread.
	/// Runs VDF's int8 model on the CPU or, through <see cref="Create"/>, the FP32 model on a
	/// Hexagon NPU in FP16 (<see cref="NpuComponents"/>). The input buffer is allocated once and
	/// reused by every run, and only the output read is fetched.
	/// </summary>
	internal sealed class OnnxEmbedder : IDisposable {
		public const int InputSide = 224;
		public const int MaxBatch = 16;
		const int PixelsPerChannel = InputSide * InputSide;
		const int FrameBytes = PixelsPerChannel * 3;

		// ImageNet normalization, the preprocessing DINOv2 was trained with.
		static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
		static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

		readonly InferenceSession session;
		readonly string[] inputNames;
		readonly string[] outputNames;
		readonly bool clsFromHiddenState;
		readonly RunOptions runOptions = new();
		/// <summary>NPU only, when its pack sets the pace per run: the options for a background scan (runOptions is full speed's).</summary>
		readonly RunOptions? backgroundRun;
		/// <summary>The graph's static batch size (the NPU's), or 0 when the batch dimension is dynamic.</summary>
		readonly int fixedBatch;
		// The input, allocated on first use for the largest batch and reused by every run. A static
		// batch also keeps the tensor over it (the array stays pinned, which costs nothing on the large object heap).
		float[]? inputBuffer;
		OrtValue[]? staticInput;
		/// <summary>
		/// The int8 model on the CPU runs each frame on its own, this many at once, each on one thread.
		/// It quantizes its activations with one scale for the whole batch, so a frame's embedding
		/// depended on the frames queued with it: the same frame alone and in a batch of 16 scored a
		/// cosine of 0.992, in two different batches 0.982, and the groups changed from scan to scan
		/// with how the decoders kept up. Frames side by side are also the faster way: 6.7 ms a frame
		/// against 8–10 ms for batches of 16 on 8 threads, and 10–11 ms for one frame on 8 threads,
		/// which made a background scan of 1,200 photos take twice as long. 0 for the other devices.
		/// </summary>
		readonly int framesAtOnce;
		/// <summary>
		/// NPU only: the machine-wide NPU lock (<see cref="NpuLock"/>), held across back-to-back batches
		/// for at most <see cref="LeaseLimit"/> so another NPU tool never waits longer than that.
		/// </summary>
		IDisposable? npuLease;
		readonly Stopwatch leaseAge = new();
		static readonly TimeSpan LeaseLimit = TimeSpan.FromSeconds(2);

		/// <summary>"CPU" or "NPU", for logs and diagnostics.</summary>
		public string DeviceName { get; }
		/// <summary>Which embedding sidecars this model's vectors belong in (null = VDF's int8 model).</summary>
		public string? CacheKey { get; }
		/// <summary>The NPU's static batch size, or 0 when any batch size runs.</summary>
		public int FixedBatch => fixedBatch;
		/// <summary>Where the time went, for the scan's log.</summary>
		public EmbedderStats Stats { get; } = new();

		public OnnxEmbedder(string modelPath) {
			AiComponents.EnsureResolverInstalled();
			// InferenceSession does NOT take ownership of caller-supplied options —
			// without the using this native handle waited for its finalizer.
			using var options = new SessionOptions();
			// The embedder shares the machine with the decode workers during hashing;
			// give inference a portion of the cores, not all of them: that many frames at once.
			options.IntraOpNumThreads = 1;
			framesAtOnce = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
			session = new InferenceSession(modelPath, options);
			DeviceName = "CPU";
			(inputNames, outputNames, clsFromHiddenState) = DescribeOutputs(session);
		}

		OnnxEmbedder(InferenceSession acceleratedSession, string deviceName, string cacheKey, int fixedBatch,
			Dictionary<string, string>? fullSpeedRun = null, Dictionary<string, string>? backgroundRun = null) {
			session = acceleratedSession;
			this.fixedBatch = fixedBatch;
			DeviceName = deviceName;
			CacheKey = cacheKey;
			(inputNames, outputNames, clsFromHiddenState) = DescribeOutputs(session);
			if (fullSpeedRun != null && backgroundRun != null) {
				foreach (var (key, value) in fullSpeedRun)
					runOptions.AddRunConfigEntry(key, value);
				this.backgroundRun = new RunOptions();
				foreach (var (key, value) in backgroundRun)
					this.backgroundRun.AddRunConfigEntry(key, value);
			}
		}

		/// <summary>The run options for the scan's pace now (<see cref="Pace.FullSpeed"/>).</summary>
		RunOptions CurrentRunOptions => backgroundRun != null && !Pace.FullSpeed ? backgroundRun : runOptions;

		/// <summary>
		/// The embedder for <paramref name="device"/>: the NPU when it is requested (or Auto) and
		/// available, the GPU when requested (DirectML), otherwise VDF's int8 model on the CPU. An
		/// accelerator that fails to open a session is logged and falls back to the CPU, so AI matching
		/// never breaks because of it.
		/// </summary>
		internal static OnnxEmbedder Create(AiDevice device) {
			if (device == AiDevice.Gpu) {
				// Before anything else touches ONNX Runtime: the DirectML build must be the one loaded.
				if (GpuComponents.TrySelectRuntime()) {
					try {
						return new OnnxEmbedder(OpenGpuSession(), "GPU", GpuComponents.ModelKey, fixedBatch: 0);
					}
					catch (Exception e) {
						Logger.Instance.Warn($"The GPU could not run the AI model, falling back to the CPU: {e.Message}");
					}
				}
				else
					Logger.Instance.Info("AI device is GPU, but the GPU pack is not installed (or another runtime was loaded first) - using the CPU.");
			}
			else if (device != AiDevice.Cpu) {
				IReadOnlyList<OrtEpDevice> npus = NpuComponents.GetNpuDevices();
				if (npus.Count > 0) {
					try {
						return new OnnxEmbedder(OpenNpuSession(npus), "NPU", NpuComponents.ModelKey, NpuComponents.NpuBatch,
							NpuComponents.RunConfig(fullSpeed: true), NpuComponents.RunConfig(fullSpeed: false));
					}
					catch (Exception e) {
						Logger.Instance.Warn($"The NPU could not run the AI model, falling back to the CPU: {e.Message}");
					}
				}
				else if (device == AiDevice.Npu)
					Logger.Instance.Info("AI device is NPU, but no NPU is available (none on this PC, one this build cannot drive, or the NPU pack is not installed) - using the CPU.");
			}
			return new OnnxEmbedder(AiComponents.ModelPath);
		}

		/// <summary>
		/// The FP32 model on the NPU. The export's dynamic dimensions are pinned (NPUs only run static
		/// shapes) and every node must run on the NPU. On Qualcomm's HTP the compiled graph is cached as
		/// an EP-context model (compiling takes a few seconds, loading the cache ~0.2 s), and only the
		/// graph in use is kept; OpenVINO and Vitis AI keep their own cache (a provider option, see <see cref="NpuPack"/>).
		/// </summary>
		static InferenceSession OpenNpuSession(IReadOnlyList<OrtEpDevice> npus) {
			// Compiling or loading the graph is NPU work too: take turns with other NPU tools.
			using IDisposable npuTurn = NpuLock.Acquire();
			if (!NpuComponents.UsesEpContextModel)
				return Open(NpuComponents.ModelPath, contextOut: null);
			string cacheDir = NpuComponents.ContextCacheFolder;
			string stem = $"{Path.GetFileNameWithoutExtension(NpuComponents.ModelFileName)}_b{NpuComponents.NpuBatch}_ctx";
			string ctx = Path.Combine(cacheDir, stem + ".onnx");
			InferenceSession? session = null;
			if (File.Exists(ctx)) {
				try {
					session = Open(ctx, contextOut: null);
				}
				catch (OnnxRuntimeException e) {
					// A torn or stale cache: rebuild it.
					Logger.Instance.Info($"Compiled NPU graph unusable, recompiling ({e.Message})");
					foreach (string f in Directory.EnumerateFiles(cacheDir, stem + "*"))
						try { File.Delete(f); } catch { }
				}
			}
			if (session == null) {
				Directory.CreateDirectory(cacheDir);
				session = Open(NpuComponents.ModelPath, ctx);
			}
			DropOtherCompiledGraphs(cacheDir, stem);
			return session;

			InferenceSession Open(string modelPath, string? contextOut) {
				using var options = new SessionOptions();
				// Every node on the NPU, or fail: a silent CPU fallback would be slower than the int8 CPU path.
				options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
				options.AddFreeDimensionOverrideByName("batch_size", NpuComponents.NpuBatch);
				options.AddFreeDimensionOverrideByName("num_channels", 3);
				options.AddFreeDimensionOverrideByName("height", InputSide);
				options.AddFreeDimensionOverrideByName("width", InputSide);
				if (contextOut != null) {
					options.AddSessionConfigEntry("ep.context_enable", "1");
					options.AddSessionConfigEntry("ep.context_file_path", contextOut);
				}
				options.AppendExecutionProvider(OrtEnv.Instance(), npus, NpuComponents.ProviderOptions());
				return new InferenceSession(modelPath, options);
			}
		}

		/// <summary>
		/// Deletes the compiled graphs other than the one in use (<paramref name="inUse"/>), e.g. another
		/// batch size's after that changed. Each is tens of megabytes.
		/// </summary>
		static void DropOtherCompiledGraphs(string cacheDir, string inUse) {
			try {
				foreach (string f in Directory.EnumerateFiles(cacheDir, "*_ctx*"))
					if (!Path.GetFileName(f).StartsWith(inUse, StringComparison.OrdinalIgnoreCase))
						try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
			}
			catch (IOException) { }
		}

		/// <summary>The FP32 model on DirectML: sequential execution and no memory patterns, as the DML EP requires.</summary>
		static InferenceSession OpenGpuSession() {
			AiComponents.EnsureResolverInstalled();
			using var options = new SessionOptions();
			options.EnableMemoryPattern = false;
			options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
			options.AppendExecutionProvider_DML(0);
			return new InferenceSession(NpuComponents.ModelPath, options);
		}

		static (string[] Inputs, string[] Outputs, bool ClsFromHiddenState) DescribeOutputs(InferenceSession session) {
			string[] input = { session.InputMetadata.Keys.First() };
			// DINOv2 exports emit last_hidden_state (CLS token = the image embedding);
			// keep the generic fallbacks so a future model swap keeps working.
			// Only the output read is fetched: last_hidden_state is every token's, 257 × 384 floats per image.
			if (session.OutputMetadata.ContainsKey("image_embeds")) return (input, new[] { "image_embeds" }, false);
			if (session.OutputMetadata.ContainsKey("pooler_output")) return (input, new[] { "pooler_output" }, false);
			return (input, new[] { session.OutputMetadata.Keys.First() }, true);
		}

		/// <summary>Receives one embedding, L2-normalized; the span is reused for the next one.</summary>
		delegate void EmbeddingSink(int index, ReadOnlySpan<float> embedding);

		/// <summary>L2-normalized float embeddings, one per input frame (each 224·224·3 RGB24 bytes).</summary>
		internal float[][] EmbedBatch(IReadOnlyList<byte[]> rgbFrames) {
			var embeddings = new float[rgbFrames.Count][];
			Embed(rgbFrames, (i, e) => embeddings[i] = e.ToArray());
			return embeddings;
		}

		/// <summary>Embeddings quantized for storage in the embedding sidecar caches, straight from the model's output.</summary>
		internal byte[][] EmbedBatchQuantized(IReadOnlyList<byte[]> rgbFrames) {
			var quantized = new byte[rgbFrames.Count][];
			Embed(rgbFrames, (i, e) => quantized[i] = EmbeddingMath.QuantizeUnitVector(e));
			return quantized;
		}

		void Embed(IReadOnlyList<byte[]> rgbFrames, EmbeddingSink sink) {
			foreach (byte[] frame in rgbFrames)
				CheckFrameSize(frame);
			if (framesAtOnce > 0) {
				// The CPU's int8 model: each frame on its own (see framesAtOnce). A session runs calls concurrently.
				try {
					Parallel.For(0, rgbFrames.Count, new ParallelOptions { MaxDegreeOfParallelism = framesAtOnce }, i => EmbedOne(rgbFrames, i, sink));
				}
				catch (AggregateException e) when (e.InnerExceptions.Count == 1) {
					ExceptionDispatchInfo.Throw(e.InnerExceptions[0]); // as a batch would have thrown it
				}
				return;
			}
			// A static-batch graph (the NPU's) runs in chunks of its batch size, the last one padded.
			int step = fixedBatch > 0 ? fixedBatch : MaxBatch;
			for (int start = 0; start < rgbFrames.Count; start += step)
				EmbedChunk(rgbFrames, start, Math.Min(step, rgbFrames.Count - start), sink);
		}

		/// <summary>One frame on the CPU, in one of the single-frame inputs the frames side by side reuse.</summary>
		void EmbedOne(IReadOnlyList<byte[]> rgbFrames, int index, EmbeddingSink sink) {
			long began = Stopwatch.GetTimestamp();
			SingleFrame slot = singleFrames.TryTake(out SingleFrame? idle) ? idle : new SingleFrame();
			try {
				Normalize(rgbFrames[index], slot.Buffer);
				Interlocked.Add(ref Stats.InputTicks, Stopwatch.GetTimestamp() - began);
				Run(slot.Input, index, 1, 1, sink);
			}
			finally {
				singleFrames.Add(slot);
			}
		}

		void EmbedChunk(IReadOnlyList<byte[]> rgbFrames, int start, int count, EmbeddingSink sink) {
			int batch = fixedBatch > 0 ? fixedBatch : count;
			long began = Stopwatch.GetTimestamp();
			OrtValue[] input = Input(rgbFrames, start, count, batch);
			try {
				Interlocked.Add(ref Stats.InputTicks, Stopwatch.GetTimestamp() - began);
				if (fixedBatch > 0)
					EnterNpu();
				Run(input, start, count, batch, sink);
			}
			finally {
				// A static batch keeps its tensor for the next run.
				if (input != staticInput)
					input[0].Dispose();
			}
		}

		/// <summary>Runs the model on <paramref name="input"/> and hands the sink its first <paramref name="count"/> embeddings, frames <paramref name="start"/> on.</summary>
		void Run(OrtValue[] input, int start, int count, int batch, EmbeddingSink sink) {
			long run = Stopwatch.GetTimestamp();
			using IDisposableReadOnlyCollection<OrtValue> results = session.Run(CurrentRunOptions, inputNames, input, outputNames);
			Interlocked.Add(ref Stats.ModelTicks, Stopwatch.GetTimestamp() - run);
			Interlocked.Increment(ref Stats.Runs);
			Interlocked.Add(ref Stats.Images, count);
			Interlocked.Add(ref Stats.Slots, batch);

			OrtValue output = results.First();
			long[] dims = output.GetTensorTypeAndShape().Shape;
			int dim = (int)dims[^1];
			// last_hidden_state is [batch, tokens, dim]; the CLS token (index 0) is the embedding.
			int stride = clsFromHiddenState && dims.Length == 3 ? (int)dims[1] * dim : dim;
			ReadOnlySpan<float> data = output.GetTensorDataAsSpan<float>();
			Span<float> embedding = dim <= 4096 ? stackalloc float[dim] : new float[dim];
			for (int k = 0; k < count; k++) {
				data.Slice(k * stride, dim).CopyTo(embedding);
				Normalize(embedding);
				sink(start + k, embedding);
			}
		}

		static void CheckFrameSize(byte[] img) {
			if (img.Length != FrameBytes)
				throw new ArgumentException($"Expected {FrameBytes} bytes of RGB24, got {img.Length}.");
		}

		/// <summary>
		/// The chunk as the model's input, ImageNet-normalized floats in NCHW, in the reused buffer. A static
		/// batch's padded slots keep the last run's pixels, since their embeddings are never read.
		/// </summary>
		OrtValue[] Input(IReadOnlyList<byte[]> rgbFrames, int start, int count, int batch) {
			inputBuffer ??= new float[Math.Max(fixedBatch, MaxBatch) * FrameBytes];
			for (int k = 0; k < count; k++)
				Normalize(rgbFrames[start + k], inputBuffer.AsSpan(k * FrameBytes, FrameBytes));
			if (staticInput != null)
				return staticInput;
			var input = new[] {
				OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, inputBuffer.AsMemory(0, batch * FrameBytes), new long[] { batch, 3, InputSide, InputSide })
			};
			if (fixedBatch > 0)
				staticInput = input;
			return input;
		}

		/// <summary>One frame's input for the CPU's frames side by side, kept for the next frame: the buffer and the tensor over it.</summary>
		sealed class SingleFrame : IDisposable {
			public readonly float[] Buffer = new float[FrameBytes];
			public readonly OrtValue[] Input;

			public SingleFrame() =>
				Input = new[] { OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, Buffer.AsMemory(), new long[] { 1, 3, InputSide, InputSide }) };

			public void Dispose() => Input[0].Dispose();
		}
		readonly System.Collections.Concurrent.ConcurrentBag<SingleFrame> singleFrames = new();

		/// <summary>One RGB24 frame as ImageNet-normalized floats in CHW.</summary>
		static void Normalize(byte[] img, Span<float> chw) {
			for (int c = 0; c < 3; c++) {
				float mean = Mean[c] * 255f;
				float invStd = 1f / (Std[c] * 255f);
				Span<float> channel = chw.Slice(c * PixelsPerChannel, PixelsPerChannel);
				for (int p = 0; p < PixelsPerChannel; p++)
					channel[p] = (img[p * 3 + c] - mean) * invStd;
			}
		}

		static void Normalize(Span<float> v) {
			double sum = 0;
			for (int i = 0; i < v.Length; i++)
				sum += (double)v[i] * v[i];
			float inv = (float)(1.0 / Math.Sqrt(Math.Max(sum, 1e-12)));
			for (int i = 0; i < v.Length; i++)
				v[i] *= inv;
		}

		void EnterNpu() {
			if (npuLease != null && leaseAge.Elapsed < LeaseLimit)
				return;
			YieldNpu();
			long waiting = Stopwatch.GetTimestamp();
			npuLease = NpuLock.Acquire();
			Stats.LockTicks += Stopwatch.GetTimestamp() - waiting;
			Stats.LockTurns++;
			leaseAge.Restart();
		}

		/// <summary>Releases the NPU lock between bursts of work (the pipeline calls this when nothing is queued).</summary>
		internal void YieldNpu() {
			npuLease?.Dispose();
			npuLease = null;
		}

		public void Dispose() {
			YieldNpu();
			if (staticInput != null)
				staticInput[0].Dispose();
			while (singleFrames.TryTake(out SingleFrame? frame))
				frame.Dispose();
			runOptions.Dispose();
			backgroundRun?.Dispose();
			session.Dispose();
		}
	}

	/// <summary>Where an embedder's time went: added to with Interlocked (the CPU's frames run side by side), read once the work is done.</summary>
	internal sealed class EmbedderStats {
		/// <summary>Model runs, the images they embedded, and the batch slots they ran (images plus padding).</summary>
		public long Runs, Images, Slots;
		/// <summary>Stopwatch ticks: preparing the input, running the model, and waiting for the NPU lock.</summary>
		public long InputTicks, ModelTicks, LockTicks;
		/// <summary>Times the NPU lock was taken.</summary>
		public long LockTurns;

		public static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
	}
}
