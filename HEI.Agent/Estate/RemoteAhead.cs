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

using HEI.Core;

namespace HEI.Agent.Estate {
	/// <summary>
	/// The files just ahead of the engine, fetched before it gets to them (<see cref="Plan"/>): over a link with a round trip
	/// of tens of ms, an ask spends most of its time waiting, so a reader that fetched as it read waited for every ask in
	/// turn. Here up to <see cref="AheadAsks"/> asks are under way at once, for the files the engine reads next, in its own
	/// order: a photo whole, a video's first and last <see cref="EndBytes"/> (its header, its index, its fingerprint's ends),
	/// a small video whole. At most <see cref="AheadFiles"/> files and <see cref="AheadBytes"/> are held ahead, so the other
	/// PC's pace (20 MB/s in all) and this PC's room are never outrun; a file the engine passes without reading is emptied.
	/// </summary>
	sealed partial class RemoteMirror {
		/// <summary>Asks under way at once for the files ahead.</summary>
		internal int AheadAsks { get; init; } = AheadAsksDefault;
		internal const int AheadAsksDefault = 16;
		/// <summary>The most files fetched ahead and not yet begun.</summary>
		internal int AheadFiles { get; init; } = 64;
		/// <summary>
		/// The most photos fetched ahead of the sorting of the sets (<see cref="SortAhead"/>), within <see cref="AheadBytes"/> still: it
		/// looks at a batch of sets in rounds (every set's close looks and words, then each set's colours), so the batch's photos
		/// are needed ahead, not the next 64; with 64 the link stood idle while it went over the ones it had.
		/// </summary>
		internal int SortAheadFiles { get; init; } = 256;
		/// <summary>The most bytes fetched ahead and not yet begun.</summary>
		internal long AheadBytes { get; init; } = 256L << 20;
		/// <summary>A video's ends fetched ahead: its first and last 512 KB.</summary>
		internal const int EndBytes = 512 * 1024;
		/// <summary>A file this many places behind the furthest the engine has begun, not begun itself, was passed (left out, or known).</summary>
		const int PassedBy = 32;

		/// <summary>One drive's files to fetch ahead, in the order the engine reads them, and how far each side has got.</summary>
		sealed class Lane(List<Stand> plan) {
			public readonly List<Stand> Plan = plan;
			/// <summary>The next to fetch, and the furthest the engine has begun.</summary>
			public int Cursor, Reached = -1;
		}

		readonly object planGate = new();
		List<Lane> lanes = new();
		readonly HashSet<Stand> ahead = new();
		long aheadBytes;
		int nextLane;
		Thread[]? workers;
		volatile bool aheadOver;

		/// <summary>
		/// <see cref="ScanEngine.Planned"/>: the files the engine will read, each drive's in its order. The ones with bytes to
		/// fetch are fetched just ahead of it from now on.
		/// </summary>
		public void Plan(IReadOnlyList<IReadOnlyList<FileEntry>> groups) {
			var planned = new List<Lane>();
			foreach (IReadOnlyList<FileEntry> group in groups) {
				var list = new List<Stand>();
				foreach (FileEntry e in group)
					if (stands.TryGetValue(e.Path, out Stand? s) && ToFetch(s, e, WantsEmbedding) is long cost) {
						s.Lane = planned.Count;
						s.PlanAt = list.Count;
						s.AheadCost = cost;
						list.Add(s);
					}
				planned.Add(new Lane(list));
			}
			lock (planGate) {
				lanes = planned;
				nextLane = 0;
				Monitor.PulseAll(planGate);
			}
			StartAhead();
		}

		/// <summary>
		/// Whether the engine's AI still wants a photo read for its embedding (<see cref="ScanEngine.WantsPhotoEmbedding"/>), its
		/// fingerprint known: a scan picking up where one stopped, ended before its embeddings were kept. Null: never.
		/// </summary>
		internal Func<FileEntry, bool>? WantsEmbedding { get; set; }

		/// <summary>What the engine will read of a file, in bytes, if it's to be fetched ahead; null if it isn't (it's known, or left out).</summary>
		static long? ToFetch(Stand s, FileEntry e, Func<FileEntry, bool>? wantsEmbedding) {
			if (e.Flags.Has(EntryFlags.ManuallyExcluded) || e.Flags.Has(EntryFlags.TooDark) || e.Flags.Has(EntryFlags.ThumbnailError) || e.Flags.Has(EntryFlags.NotMedia)) return null;
			long size = s.File.Size;
			if (size <= 0) return null;
			if (s.IsPhoto) return (ScanEngine.ImageNeedsDecode(e) || wantsEmbedding?.Invoke(e) == true) && size <= MaxWhole ? size : null;
			if (e.mediaInfo != null && e.OsHash != null && e.grayBytes is { Count: > 0 }) return null;
			return Math.Min(size, 2L * EndBytes);
		}

		/// <summary>The engine has begun <paramref name="s"/>: it isn't ahead any more, and the files before it are behind.</summary>
		void Reached(Stand s) {
			if (s.PlanAt < 0) return;
			lock (planGate) {
				s.Begun = true;
				if (ahead.Remove(s)) aheadBytes -= s.AheadCost;
				if (s.Lane < lanes.Count && lanes[s.Lane] is { } lane && s.PlanAt > lane.Reached) lane.Reached = s.PlanAt;
				Monitor.PulseAll(planGate);
			}
		}

		void Work() {
			aheadThread = true;
			while (!Stopping.IsCancellationRequested && !Over && !aheadOver) {
				Stand? s;
				List<Stand>? passed;
				lock (planGate) {
					passed = Passed();
					s = Next();
					if (s == null && passed == null) {
						Monitor.Wait(planGate, 500);
						continue;
					}
				}
				// Sorting the sets counts no readers, so one it got to late may be read just now: it's left to Trim, which empties
				// none read lately.
				if (passed != null && !sorting)
					foreach (Stand p in passed) Empty(p);
				if (s == null) continue;
				try {
					long size = s.File.Size;
					if (s.IsPhoto || size <= 2L * EndBytes) Fetch(s.Path, 0, -1);
					else if (Fetch(s.Path, 0, EndBytes)) Fetch(s.Path, size - EndBytes, EndBytes);
					// The engine got to it, read it and let it go while this fetched it: not to be held for nothing. Not while the sets
					// are sorted: they count no readers, and one that got to it may be about to read it.
					if (!sorting && s.Begun && Volatile.Read(ref s.Readers) == 0) Empty(s);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { } // Fetch keeps why in Failed
			}
		}

		/// <summary>The next file to fetch ahead, while there's room ahead; the lanes in turn. Under <see cref="planGate"/>.</summary>
		Stand? Next() {
			if (ahead.Count >= (sorting ? SortAheadFiles : AheadFiles) || aheadBytes >= AheadBytes) return null;
			for (int tried = 0; tried < lanes.Count; tried++) {
				Lane lane = lanes[nextLane];
				nextLane = (nextLane + 1) % lanes.Count;
				lane.Cursor = Math.Max(lane.Cursor, lane.Reached + 1);
				while (lane.Cursor < lane.Plan.Count) {
					Stand s = lane.Plan[lane.Cursor++];
					if (s.Begun || Volatile.Read(ref s.Readers) > 0 || Failed.ContainsKey(s.Path)) continue;
					ahead.Add(s);
					aheadBytes += s.AheadCost;
					return s;
				}
			}
			return null;
		}

		/// <summary>The files fetched ahead that the engine went past without reading: no longer ahead, to be emptied. Under <see cref="planGate"/>.</summary>
		List<Stand>? Passed() {
			List<Stand>? passed = null;
			foreach (Stand s in ahead)
				if (s.Lane < lanes.Count && lanes[s.Lane].Reached - s.PlanAt > PassedBy && Volatile.Read(ref s.Readers) == 0) (passed ??= new()).Add(s);
			if (passed != null)
				foreach (Stand s in passed) {
					ahead.Remove(s);
					aheadBytes -= s.AheadCost;
				}
			return passed;
		}

		/// <summary>The report's sets are being sorted (<see cref="SortAhead"/>): a photo is reached when it's fetched for them.</summary>
		volatile bool sorting;

		/// <summary>
		/// The photos the report's sets will look at again (up close, for their words, lined up), in the order they look at them
		/// (<see cref="ReportBuilder.Build"/>'s first pass, <see cref="Refuse"/>): fetched just ahead of the sorting from now
		/// on, <see cref="AheadAsks"/> asks at once, as the files ahead of the engine are. The sorting asked for each in turn,
		/// one ask at a time, so it waited for every one: 26.6 GB at 3.3 MB/s on one PC, while the checking fetched 117.6 GB at
		/// 15 MB/s. How many are to be fetched, and their bytes.
		/// </summary>
		public (int Photos, long Bytes) SortAhead(IReadOnlyList<string> paths) {
			var list = new List<Stand>();
			var planned = new HashSet<Stand>();
			long bytes = 0;
			lock (planGate) {
				// The engine's lanes are done with: no stand-in is anywhere in them now.
				foreach (Stand s in stands.Values) s.PlanAt = -1;
				foreach (string path in paths)
					if (stands.TryGetValue(path, out Stand? s) && s.IsPhoto && s.File.Size is > 0 and <= MaxWhole && planned.Add(s) &&
							!(Failed.TryGetValue(s.Path, out string? why) && Final(why))) {
						s.Lane = 0;
						s.PlanAt = list.Count;
						s.AheadCost = s.File.Size;
						s.Begun = false;
						bytes += s.File.Size;
						list.Add(s);
					}
				sorting = true;
				ahead.Clear();
				aheadBytes = 0;
				lanes = new() { new Lane(list) };
				nextLane = 0;
				Monitor.PulseAll(planGate);
			}
			StartAhead();
			return (list.Count, bytes);
		}

		void StartAhead() {
			if (workers != null || AheadAsks <= 0) return;
			workers = new Thread[AheadAsks];
			for (int i = 0; i < workers.Length; i++) {
				workers[i] = new Thread(Work) { IsBackground = true, Name = "Heiward: ahead of the scan", Priority = ThreadPriority.BelowNormal };
				workers[i].Start();
			}
		}

		/// <summary>The files fetched ahead and not reached yet: not emptied to make room, or they'd be fetched again. Under <see cref="planGate"/>.</summary>
		bool IsAhead(Stand s) {
			lock (planGate) return ahead.Contains(s);
		}

		/// <summary>The fetching ahead stops (the scan is over, or stopped): its threads end once their asks do.</summary>
		void StopAhead() {
			lock (planGate) {
				aheadOver = true;
				lanes = new();
				ahead.Clear();
				aheadBytes = 0;
				Monitor.PulseAll(planGate);
			}
		}
	}
}
