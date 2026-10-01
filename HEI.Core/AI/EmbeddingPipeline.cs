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
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>
	/// Receives decoded 224×224 RGB24 frames from the hashing phase. Implemented by
	/// <see cref="EmbeddingPipeline"/>; the indirection keeps FfmpegEngine free of any
	/// ONNX Runtime dependency (and lets tests inject a sink without native components).
	/// </summary>
	interface IEmbeddingFrameSink {
		/// <summary>Whether the entry still needs an embedding for this position key.</summary>
		bool WantsEmbedding(FileEntry entry, double positionKey);
		/// <summary>
		/// Hands a decoded frame over for embedding. Never throws for data reasons.
		/// Ownership of <paramref name="rgb224"/> transfers to the sink — it is recycled
		/// through <see cref="FramePool"/> after use, so callers must not touch it again.
		/// </summary>
		void SubmitFrame(FileEntry entry, double positionKey, byte[] rgb224);
	}

	/// <summary>
	/// Bounded producer/consumer stage between the (parallel, I/O-bound) frame decoders
	/// and the (serial, CPU-bound) ONNX inference: decode workers submit RGB frames, one
	/// worker thread batches them through <see cref="OnnxEmbedder"/> and writes int8
	/// embeddings into the <see cref="UnionEmbeddingStore"/> sidecar (thread-safe, so the
	/// probing decode workers never race the writer). On an inference failure the
	/// pipeline faults: remaining frames are drained and discarded (so producers never
	/// block on the bounded queue) and affected entries simply stay without embeddings —
	/// the AI pass abstains for them.
	/// </summary>
	sealed class EmbeddingPipeline : IEmbeddingFrameSink, IDisposable {
		/// <summary>
		/// How long a static (NPU) batch that isn't full waits for frames to fill it. The NPU does a full
		/// batch's work whatever it holds, and the decoders deliver a frame every few milliseconds while
		/// they're busy, so a short wait saves NPU runs. Shorter than the decoders' gaps on a big file, so
		/// a lone frame doesn't wait long.
		/// </summary>
		static readonly TimeSpan FillWait = TimeSpan.FromMilliseconds(100);
		/// <summary>
		/// How long the queue may stay empty before the NPU lock goes back to other NPU tools: shorter gaps are
		/// the decoders between frames, and releasing and retaking the lock for each costs file operations.
		/// The lease's own limit (2 s) still applies.
		/// </summary>
		static readonly TimeSpan IdleGrace = TimeSpan.FromMilliseconds(100);

		readonly BlockingCollection<(FileEntry entry, double key, byte[] rgb)> queue = new(boundedCapacity: 256);
		readonly OnnxEmbedder embedder;
		readonly UnionEmbeddingStore store;
		readonly CancellationToken token;
		readonly Task worker;
		readonly Stopwatch sinceFirstFrame = new();
		volatile bool faulted;
		int embeddedCount;

		public EmbeddingPipeline(string modelPath, UnionEmbeddingStore store, CancellationToken token)
			: this(new OnnxEmbedder(modelPath), store, token) { }

		/// <summary>Takes ownership of <paramref name="embedder"/> (disposed with the pipeline).</summary>
		public EmbeddingPipeline(OnnxEmbedder embedder, UnionEmbeddingStore store, CancellationToken token) {
			this.store = store;
			this.token = token;
			this.embedder = embedder;
			// A thread of its own, a step above the decoders: it needs little CPU but needs it at once, and
			// under a background scan's cap on the processor it otherwise queued behind every decoder before
			// it could hand the NPU the next frame (49 ms a frame for 3 ms of NPU work). The process's own
			// below-normal priority still puts it behind the user's programs.
			worker = Task.Factory.StartNew(() => {
				Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
				WorkerLoop();
			}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		}

		public string DeviceName => embedder.DeviceName;

		public int EmbeddedCount => embeddedCount;
		public bool Faulted => faulted;

		public bool WantsEmbedding(FileEntry entry, double positionKey) =>
			!faulted && !store.HasEmbedding(entry, positionKey);

		public void SubmitFrame(FileEntry entry, double positionKey, byte[] rgb224) {
			if (faulted) {
				FramePool.Shared.Return(rgb224);
				return;
			}
			try {
				queue.Add((entry, positionKey, rgb224), token);
			}
			catch (OperationCanceledException) { FramePool.Shared.Return(rgb224); }
			catch (InvalidOperationException) { FramePool.Shared.Return(rgb224); /* completed while a decoder was mid-submit */ }
		}

		/// <summary>No more frames will arrive; returns when everything queued is embedded.</summary>
		public Task CompleteAsync() {
			queue.CompleteAdding();
			return worker;
		}

		/// <summary>
		/// Where the AI time went, for the scan's log: e.g. "AI on the NPU: 4,812 frames in 4,812 runs of 1
		/// (0% padding); model 10.2 s, input 0.3 s, NPU lock 0.4 s over 31 turns; busy 13% of 78 s".
		/// </summary>
		public string Describe() {
			EmbedderStats s = embedder.Stats;
			double wall = sinceFirstFrame.Elapsed.TotalSeconds, model = EmbedderStats.Seconds(s.ModelTicks);
			string runs = embedder.FixedBatch > 0
				? $"{s.Runs:N0} runs of {embedder.FixedBatch} ({(s.Slots == 0 ? 0 : 1 - (double)s.Images / s.Slots):P0} padding)"
				: $"{s.Runs:N0} runs";
			string text = $"AI on the {embedder.DeviceName}: {s.Images:N0} frames in {runs}; " +
				$"model {model:N1} s, input {EmbedderStats.Seconds(s.InputTicks):N2} s";
			if (s.LockTurns > 0)
				text += $", NPU lock {EmbedderStats.Seconds(s.LockTicks):N1} s over {s.LockTurns:N0} turns";
			if (wall > 0)
				text += $"; busy {model / wall:P0} of {wall:N0} s";
			return text;
		}

		void WorkerLoop() {
			var batchEntries = new List<(FileEntry entry, double key)>(OnnxEmbedder.MaxBatch);
			var batchFrames = new List<byte[]>(OnnxEmbedder.MaxBatch);
			try {
				while (!queue.IsCompleted) {
					batchEntries.Clear();
					batchFrames.Clear();
					try {
						if (!Gather(batchEntries, batchFrames))
							break; // completed and empty
					}
					catch (OperationCanceledException) {
						ReturnBatchFrames();
						break;
					}

					if (faulted) {
						ReturnBatchFrames();
						continue; // keep draining, discard
					}

					byte[][] embeddings = embedder.EmbedBatchQuantized(batchFrames);
					for (int i = 0; i < embeddings.Length; i++) {
						store.Put(batchEntries[i].entry, batchEntries[i].key, embeddings[i]);
						Interlocked.Increment(ref embeddedCount);
					}
					// Only the 384-byte embedding is kept - the frames recycle (#878).
					ReturnBatchFrames();
				}

				void ReturnBatchFrames() {
					foreach (byte[] frame in batchFrames)
						FramePool.Shared.Return(frame);
					batchFrames.Clear();
					batchEntries.Clear();
				}
			}
			catch (Exception e) {
				// One inference failure must not tear down the scan: fault, then keep
				// draining so bounded Add in the decode workers never blocks forever.
				faulted = true;
				Logger.Instance.Error($"AI embedding stage failed — continuing scan without AI matching for the remaining files: {e}");
				try {
					while (queue.TryTake(out var dropped, Timeout.Infinite))
						FramePool.Shared.Return(dropped.rgb);
				}
				catch (InvalidOperationException) { /* completed and empty — done */ }
			}
			finally {
				sinceFirstFrame.Stop();
			}
		}

		/// <summary>
		/// The next batch: waits for a first frame (holding the NPU lock for <see cref="IdleGrace"/>, then
		/// letting other NPU tools in), takes what else is queued, and gives a static batch that isn't full
		/// <see cref="FillWait"/> to fill up. False once the queue is completed and empty.
		/// </summary>
		bool Gather(List<(FileEntry entry, double key)> entries, List<byte[]> frames) {
			(FileEntry entry, double key, byte[] rgb) item;
			if (!queue.TryTake(out item, (int)IdleGrace.TotalMilliseconds, token)) {
				// Nothing queued for a while: the decoders are busy. Let other NPU tools in meanwhile.
				embedder.YieldNpu();
				try {
					item = queue.Take(token);
				}
				catch (InvalidOperationException) { return false; } // completed and empty
			}
			sinceFirstFrame.Start(); // no-op after the first frame
			Add(item);
			while (frames.Count < OnnxEmbedder.MaxBatch && queue.TryTake(out item))
				Add(item);

			int batch = embedder.FixedBatch;
			if (batch > 0 && frames.Count % batch != 0) {
				var waited = Stopwatch.StartNew();
				while (frames.Count % batch != 0) {
					int left = (int)(FillWait - waited.Elapsed).TotalMilliseconds;
					// A completed queue answers at once: the scan's last frames don't wait.
					if (left <= 0 || !queue.TryTake(out item, left, token))
						break;
					Add(item);
				}
			}
			return true;

			void Add((FileEntry entry, double key, byte[] rgb) i) {
				entries.Add((i.entry, i.key));
				frames.Add(i.rgb);
			}
		}

		public void Dispose() {
			// Stop embedding the backlog but KEEP draining (faulted mode) so decode
			// workers blocked in a bounded Add always unblock; the worker then exits
			// after at most the one in-flight inference batch.
			faulted = true;
			queue.CompleteAdding();
			bool exited;
			try { exited = worker.Wait(TimeSpan.FromSeconds(30)); }
			catch { exited = worker.IsCompleted; }
			if (exited) {
				embedder.Dispose();
				queue.Dispose();
			}
			else {
				// Never free the native InferenceSession under an in-flight Run — that is
				// a native use-after-free that kills the whole process. Leaking one wedged
				// session is the lesser evil; the finalizers reclaim it eventually.
				Logger.Instance.Warn("AI embedding worker did not stop in time — leaving the ONNX session alive instead of freeing it mid-inference.");
			}
		}
	}
}
