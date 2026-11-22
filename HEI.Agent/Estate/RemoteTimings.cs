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
using System.Text;
using System.Text.Json;

namespace HEI.Agent.Estate {
	/// <summary>
	/// Where a scan of another PC's files spends its time, for its log: every ask of the other PC (asked → first byte →
	/// done), every file read (the engine's time on it, and how much of that waited for its bytes, by what they were for),
	/// and the other PC's own reading of them (<see cref="PeerFiles.Timing"/>). <see cref="Summary"/> says it all since
	/// the last summary, in one line.
	/// </summary>
	sealed class RemoteTimings {
		/// <summary>What an ask's bytes were for: a file whole (a photo), a video's parts as its decoder read them, or a file's ends (its fingerprint, its first bytes).</summary>
		public enum For { Whole, Parts, Ends }

		readonly object gate = new();
		Window now = new();
		long started = Stopwatch.GetTimestamp();

		sealed class Window {
			public int Asks, Refused, Retried;
			public long Bytes, HeadTicks, BodyTicks;
			public readonly List<int> AskMs = new();
			public readonly long[] WaitTicks = new long[3];
			public readonly int[] Waits = new int[3];
			public int Photos, Videos, Failed;
			public long PhotoTicks, VideoTicks, PhotoWait, VideoWait;
		}

		/// <summary>One ask answered with bytes: how long until its head came, how long its body took after, and how many bytes.</summary>
		public void Ask(long headTicks, long bodyTicks, long bytes) {
			lock (gate) {
				Window w = now;
				w.Asks++;
				w.Bytes += bytes;
				w.HeadTicks += headTicks;
				w.BodyTicks += bodyTicks;
				if (w.AskMs.Count < 100_000) w.AskMs.Add((int)Ms(headTicks + bodyTicks));
			}
		}

		/// <summary>An ask the other PC refused (a file gone, a game), or one tried again (the link dropped, a reply cut short).</summary>
		public void Refused() { lock (gate) now.Refused++; }
		public void Retried() { lock (gate) now.Retried++; }

		/// <summary>A read waited <paramref name="ticks"/> for bytes the other PC sent, for <paramref name="what"/>.</summary>
		public void Waited(For what, long ticks) {
			lock (gate) {
				now.WaitTicks[(int)what] += ticks;
				now.Waits[(int)what]++;
			}
		}

		/// <summary>A file the engine is done with: its time from the first read to the last, and how much of it waited for bytes.</summary>
		public void File(bool photo, long ticks, long waitTicks, bool failed) {
			lock (gate) {
				Window w = now;
				if (failed) w.Failed++;
				if (photo) {
					w.Photos++;
					w.PhotoTicks += ticks;
					w.PhotoWait += waitTicks;
				}
				else {
					w.Videos++;
					w.VideoTicks += ticks;
					w.VideoWait += waitTicks;
				}
			}
		}

		/// <summary>
		/// What happened since the last summary, in a line for the log, and the window started again:
		/// <paramref name="done"/> of <paramref name="total"/> files checked, <paramref name="steps"/> the engine's own split
		/// of its time (null: not had), <paramref name="target"/> the other PC's reading since the last (null: not had).
		/// </summary>
		public string Summary(string name, int doneBefore, int done, int total, string? steps, Target? target) {
			Window w;
			long end = Stopwatch.GetTimestamp(), began;
			lock (gate) {
				w = now;
				now = new Window();
				began = started;
				started = end;
			}
			double secs = Math.Max(0.001, Stopwatch.GetElapsedTime(began, end).TotalSeconds);
			int files = done - doneBefore;
			var s = new StringBuilder();
			s.Append($"scan for {name}, timings: files {doneBefore:N0}–{done:N0} of {total:N0} in {secs:N0} s ({files / secs:N1}/s); ");
			int notFetched = Math.Max(0, files - w.Photos - w.Videos);
			s.Append($"{w.Photos:N0} photos fetched whole, {w.Videos:N0} videos read in parts, {notFetched:N0} with nothing to fetch (known, or left out), {w.Failed:N0} not fetched. ");
			if (w.Photos > 0) s.Append($"Per photo: {Each(w.PhotoTicks, w.Photos)} in the engine, {Each(w.PhotoWait, w.Photos)} of it waiting for its bytes. ");
			if (w.Videos > 0) s.Append($"Per video: {Each(w.VideoTicks, w.Videos)} in the engine, {Each(w.VideoWait, w.Videos)} of it waiting for its bytes. ");
			if (steps != null) s.Append($"Engine steps: {steps}. ");
			string[] names = ["files whole", "videos' parts", "files' ends"];
			var waits = new List<string>();
			for (int i = 0; i < 3; i++)
				if (w.Waits[i] > 0) waits.Add($"{names[i]} {w.Waits[i]:N0} × {Each(w.WaitTicks[i], w.Waits[i])}");
			if (waits.Count > 0) s.Append($"Waits for bytes: {string.Join(", ", waits)}. ");
			if (w.Asks > 0) {
				w.AskMs.Sort();
				s.Append($"Asks: {w.Asks:N0} ({w.Asks / secs:N1}/s), {Format.Bytes(w.Bytes / w.Asks)} on average, {Format.Bytes((long)(w.Bytes / secs))}/s; ");
				s.Append($"asked → first byte {Ms(w.HeadTicks) / w.Asks:N0} ms, first byte → done {Ms(w.BodyTicks) / w.Asks:N0} ms ");
				s.Append($"(whole ask p50 {Pct(w.AskMs, 0.5):N0} ms, p90 {Pct(w.AskMs, 0.9):N0} ms, most {w.AskMs[^1]:N0} ms)");
				if (w.Refused > 0 || w.Retried > 0) s.Append($"; {w.Refused:N0} refused, {w.Retried:N0} asked again");
				s.Append(". ");
			}
			else s.Append("No asks. ");
			if (target is { Asks: > 0 } t)
				s.Append($"On {name}: {t.Asks:N0} asks served, per ask opening {t.OpenMs / t.Asks:N0} ms, reading {t.ReadMs / t.Asks:N0} ms, " +
					$"pacing {t.PaceMs / t.Asks:N0} ms, sending {t.SendMs / t.Asks:N0} ms.");
			else if (target != null) s.Append($"On {name}: no asks served.");
			else s.Append($"On {name}: its reading isn't reported (an older Heiward there).");
			return s.ToString().TrimEnd();
		}

		/// <summary>The other PC's reading for this one's asks, as its <see cref="PeerFiles.Timing"/> says: totals, in ms.</summary>
		public sealed record Target(long Asks, long Bytes, double OpenMs, double ReadMs, double PaceMs, double SendMs) {
			public Target Minus(Target? before) => before == null ? this
				: new Target(Asks - before.Asks, Bytes - before.Bytes, OpenMs - before.OpenMs, ReadMs - before.ReadMs, PaceMs - before.PaceMs, SendMs - before.SendMs);

			public static Target? Parse(JsonElement e) {
				if (e.ValueKind != JsonValueKind.Object) return null;
				double D(string k) => e.TryGetProperty(k, out JsonElement v) && v.TryGetDouble(out double d) ? d : 0;
				return new Target((long)D("asks"), (long)D("bytes"), D("openMs"), D("readMs"), D("paceMs"), D("sendMs"));
			}
		}

		static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

		static string Each(long ticks, int n) {
			double ms = Ms(ticks) / Math.Max(1, n);
			return ms >= 1000 ? $"{ms / 1000:N1} s" : $"{ms:N0} ms";
		}

		static int Pct(List<int> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
	}
}
