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

using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>
	/// Wraps the ONNX image-embedding model (DINOv2-small vision transformer). Input is a
	/// raw 224×224 RGB24 frame; output an L2-normalized (optionally int8-quantized)
	/// embedding whose dot product is the frames' cosine similarity. Instances are
	/// created per scan; <see cref="EmbedBatch"/> is called from a single worker thread.
	/// Runs VDF's int8 model on the CPU or, through <see cref="Create"/>, the FP32 model on a
	/// Hexagon NPU in FP16 (<see cref="NpuComponents"/>).
	/// </summary>
	internal sealed class OnnxEmbedder : IDisposable {
		public const int InputSide = 224;
		public const int MaxBatch = 16;
		const int PixelsPerChannel = InputSide * InputSide;

		// ImageNet normalization, the preprocessing DINOv2 was trained with.
		static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
		static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

		readonly InferenceSession session;
		readonly string inputName;
		readonly string outputName;
		readonly bool clsFromHiddenState;
		/// <summary>The graph's static batch size (the NPU's), or 0 when the batch dimension is dynamic.</summary>
		readonly int fixedBatch;
		/// <summary>
		/// NPU only: the machine-wide NPU lock (<see cref="NpuLock"/>), held across back-to-back batches
		/// for at most <see cref="LeaseLimit"/> so another NPU tool never waits longer than that.
		/// </summary>
		IDisposable? npuLease;
		readonly System.Diagnostics.Stopwatch leaseAge = new();
		static readonly TimeSpan LeaseLimit = TimeSpan.FromSeconds(2);

		/// <summary>"CPU" or "NPU", for logs and diagnostics.</summary>
		public string DeviceName { get; }
		/// <summary>Which embedding sidecars this model's vectors belong in (null = VDF's int8 model).</summary>
		public string? CacheKey { get; }

		public OnnxEmbedder(string modelPath) {
			AiComponents.EnsureResolverInstalled();
			// InferenceSession does NOT take ownership of caller-supplied options —
			// without the using this native handle waited for its finalizer.
			using var options = new SessionOptions();
			// The embedder shares the machine with the decode workers during hashing;
			// give inference a portion of the cores, not all of them.
			options.IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
			session = new InferenceSession(modelPath, options);
			DeviceName = "CPU";
			(inputName, outputName, clsFromHiddenState) = DescribeOutputs(session);
		}

		OnnxEmbedder(InferenceSession acceleratedSession, string deviceName, string cacheKey, int fixedBatch) {
			session = acceleratedSession;
			this.fixedBatch = fixedBatch;
			DeviceName = deviceName;
			CacheKey = cacheKey;
			(inputName, outputName, clsFromHiddenState) = DescribeOutputs(session);
		}

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
						return new OnnxEmbedder(OpenNpuSession(npus), "NPU", NpuComponents.ModelKey, NpuComponents.NpuBatch);
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
		/// an EP-context model (compiling takes ~5 s, loading the cache ~0.2 s); OpenVINO and Vitis AI
		/// keep their own cache (a provider option, see <see cref="NpuPack"/>).
		/// </summary>
		static InferenceSession OpenNpuSession(IReadOnlyList<OrtEpDevice> npus) {
			// Compiling or loading the graph is NPU work too: take turns with other NPU tools.
			using IDisposable npuTurn = NpuLock.Acquire();
			if (!NpuComponents.UsesEpContextModel)
				return Open(NpuComponents.ModelPath, contextOut: null);
			string cacheDir = NpuComponents.ContextCacheFolder;
			string stem = $"{Path.GetFileNameWithoutExtension(NpuComponents.ModelFileName)}_b{NpuComponents.NpuBatch}_ctx";
			string ctx = Path.Combine(cacheDir, stem + ".onnx");
			if (File.Exists(ctx)) {
				try {
					return Open(ctx, contextOut: null);
				}
				catch (OnnxRuntimeException e) {
					// A torn or stale cache: rebuild it.
					Logger.Instance.Info($"Compiled NPU graph unusable, recompiling ({e.Message})");
					foreach (string f in Directory.EnumerateFiles(cacheDir, stem + "*"))
						try { File.Delete(f); } catch { }
				}
			}
			Directory.CreateDirectory(cacheDir);
			return Open(NpuComponents.ModelPath, ctx);

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

		/// <summary>The FP32 model on DirectML: sequential execution and no memory patterns, as the DML EP requires.</summary>
		static InferenceSession OpenGpuSession() {
			AiComponents.EnsureResolverInstalled();
			using var options = new SessionOptions();
			options.EnableMemoryPattern = false;
			options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
			options.AppendExecutionProvider_DML(0);
			return new InferenceSession(NpuComponents.ModelPath, options);
		}

		static (string Input, string Output, bool ClsFromHiddenState) DescribeOutputs(InferenceSession session) {
			string input = session.InputMetadata.Keys.First();
			// DINOv2 exports emit last_hidden_state (CLS token = the image embedding);
			// keep the generic fallbacks so a future model swap keeps working.
			if (session.OutputMetadata.ContainsKey("image_embeds")) return (input, "image_embeds", false);
			if (session.OutputMetadata.ContainsKey("pooler_output")) return (input, "pooler_output", false);
			return (input, session.OutputMetadata.Keys.First(), true);
		}

		/// <summary>L2-normalized float embeddings, one per input frame (each 224·224·3 RGB24 bytes).</summary>
		internal float[][] EmbedBatch(IReadOnlyList<byte[]> rgbFrames) {
			int total = rgbFrames.Count;
			if (total == 0) return Array.Empty<float[]>();
			var embeddings = new float[total][];
			// A static-batch graph (the NPU's) runs in chunks of its batch size, the last one zero-padded.
			int step = fixedBatch > 0 ? fixedBatch : total;
			for (int start = 0; start < total; start += step)
				EmbedChunk(rgbFrames, start, Math.Min(step, total - start), embeddings);
			return embeddings;
		}

		void EmbedChunk(IReadOnlyList<byte[]> rgbFrames, int start, int count, float[][] embeddings) {
			int batch = fixedBatch > 0 ? fixedBatch : count;
			var tensor = new DenseTensor<float>(new[] { batch, 3, InputSide, InputSide });
			Span<float> buffer = tensor.Buffer.Span;
			for (int k = 0; k < count; k++) {
				byte[] img = rgbFrames[start + k];
				if (img.Length != PixelsPerChannel * 3)
					throw new ArgumentException($"Expected {PixelsPerChannel * 3} bytes of RGB24, got {img.Length}.");
				int baseIdx = k * 3 * PixelsPerChannel;
				for (int c = 0; c < 3; c++) {
					float mean = Mean[c] * 255f;
					float invStd = 1f / (Std[c] * 255f);
					int channelBase = baseIdx + c * PixelsPerChannel;
					for (int p = 0; p < PixelsPerChannel; p++)
						buffer[channelBase + p] = (img[p * 3 + c] - mean) * invStd;
				}
			}

			if (fixedBatch > 0)
				EnterNpu();
			using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
				session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
			var outputTensor = (DenseTensor<float>)results.First(v => v.Name == outputName).AsTensor<float>();
			ReadOnlySpan<int> dims = outputTensor.Dimensions;
			int dim = dims[^1];
			// last_hidden_state is [batch, tokens, dim]; the CLS token (index 0) is the embedding.
			int stride = clsFromHiddenState && dims.Length == 3 ? dims[1] * dim : dim;
			Span<float> output = outputTensor.Buffer.Span;

			for (int k = 0; k < count; k++) {
				var e = new float[dim];
				output.Slice(k * stride, dim).CopyTo(e);
				Normalize(e);
				embeddings[start + k] = e;
			}
		}

		/// <summary>Embeddings quantized for storage in the embedding sidecar caches.</summary>
		internal byte[][] EmbedBatchQuantized(IReadOnlyList<byte[]> rgbFrames) {
			float[][] floats = EmbedBatch(rgbFrames);
			var quantized = new byte[floats.Length][];
			for (int i = 0; i < floats.Length; i++)
				quantized[i] = EmbeddingMath.QuantizeUnitVector(floats[i]);
			return quantized;
		}

		static void Normalize(float[] v) {
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
			npuLease = NpuLock.Acquire();
			leaseAge.Restart();
		}

		/// <summary>Releases the NPU lock between bursts of work (the pipeline calls this when nothing is queued).</summary>
		internal void YieldNpu() {
			npuLease?.Dispose();
			npuLease = null;
		}

		public void Dispose() {
			YieldNpu();
			session.Dispose();
		}
	}
}
