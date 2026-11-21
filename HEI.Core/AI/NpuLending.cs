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
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HEI.Core.AI {
	/// <summary>
	/// The PC-wide lending record (the Steward's kit 2.159.0: kit\spec\LENDING.md, run against its lending-vectors.json): which
	/// programs on this PC lend its NPU to another PC, and how much each matters, so a long background scan steps back while
	/// a call is captioned for another PC and takes turns again after. It's a folder of small files beside the NPU's lock,
	/// which any program joins with no server. Heiward's scan for another PC lends as background work (<see cref="Join"/>):
	/// it keeps an entry there while it scans, and takes no new NPU turn while another entry has a lower rank
	/// (<see cref="WaitTurn"/>, called by <see cref="NpuLock"/> before every NPU turn). The NPU's lock and line still decide
	/// each turn; the record only says which lending may ask for one.
	/// </summary>
	public static partial class NpuLending {
		/// <summary>The ranks: live work (a call's captions), work someone waits on now, everything else.</summary>
		public const int Live = 0, Waited = 1, Background = 2;
		static readonly string[] RankNames = { "live", "waited", "background" };

		/// <summary>How often the record is looked at again while giving way, and how long a look that said go holds.</summary>
		internal const int LookMs = 250;

		/// <summary>The record's folder for the NPU's lock folder <paramref name="lockDir"/>: its parent's parent, then <c>lending</c>.</summary>
		public static string? FolderFor(string lockDir) =>
			Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(lockDir))) is { Length: > 0 } home ? Path.Combine(home, "lending") : null;

		/// <summary>This PC's record; null where the NPU's lock isn't in use (no other NPU tool here, so nobody to give way to).</summary>
		public static string? Folder => NpuLock.LockDirectory is string lockDir ? FolderFor(lockDir) : null;

		/// <summary>An entry's file name, read: its rank, when it joined (microseconds since the epoch), its process and nonce.</summary>
		public sealed record Entry(int Rank, long Time, int Pid, string Nonce);

		[GeneratedRegex(@"^([012])-([0-9]{17})-([0-9]+)-([0-9a-z]+)\.lend\z", RegexOptions.CultureInvariant)]
		private static partial Regex EntryName();

		/// <summary>An entry read from its file name; null for a name that isn't one, which is ignored.</summary>
		public static Entry? Read(string name) {
			Match m = EntryName().Match(name);
			if (!m.Success
				|| !long.TryParse(m.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long time)
				|| !int.TryParse(m.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
				return null;
			return new Entry(m.Groups[1].ValueSpan[0] - '0', time, pid, m.Groups[4].Value);
		}

		/// <summary>The queue's rule for a ticket: no heartbeat for 15 s whatever its pid says, or for 5 s with no such process.</summary>
		public static bool IsDead(TimeSpan age, bool alive) => NpuLock.IsDeadTicket(age, () => alive);

		/// <summary>Whether a lending of rank <paramref name="mine"/> gives way: another entry of a lower rank is on the record.</summary>
		public static bool GivesWay(int mine, IEnumerable<int> others) => System.Linq.Enumerable.Any(others, other => other < mine);

		/// <summary>The other entries' ranks on the record, but <paramref name="own"/>'s; dead ones are deleted on the way (anyone reading may).</summary>
		internal static List<int> Others(string folder, string? own) {
			var ranks = new List<int>();
			List<string> files;
			try { files = new List<string>(Directory.EnumerateFiles(folder, "*.lend")); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ranks; }
			DateTime now = DateTime.UtcNow;
			foreach (string path in files) {
				string name = Path.GetFileName(path);
				if (name == own || Read(name) is not { } entry) continue;
				DateTime touched;
				try {
					var info = new FileInfo(path);
					if (!info.Exists) continue; // left just now
					touched = info.LastWriteTimeUtc;
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
				if (NpuLock.IsDeadTicket(now - touched, () => NpuLock.PidAlive(entry.Pid))) {
					try { File.Delete(path); }
					catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
					continue;
				}
				ranks.Add(entry.Rank);
			}
			return ranks;
		}

		static Lending? current;

		/// <summary>
		/// Joins the record for this process's lending of the NPU, until disposed: its entry is written, kept fresh, written
		/// again if it's taken for dead, and deleted at the end. Meanwhile every NPU turn this process takes first waits while
		/// something that matters more is lent (<see cref="WaitTurn"/>); <paramref name="givingWay"/> hears when that starts
		/// and stops, and <paramref name="ct"/> ends the wait (<see cref="OperationCanceledException"/>). A record that can't
		/// be written never stops the lending: it only can't make anyone give way to it.
		/// </summary>
		/// <param name="to">The PC it lends to, by name.</param>
		/// <param name="doing">What for, in a person's words (at most 80 characters are kept).</param>
		public static Lending Join(int rank, string who, string? to, string? doing, CancellationToken ct, Action<bool>? givingWay = null) {
			if (rank is < Live or > Background) throw new ArgumentOutOfRangeException(nameof(rank));
			var lending = new Lending(Folder, rank, who, to, doing, ct, givingWay);
			Volatile.Write(ref current, lending);
			return lending;
		}

		/// <summary>Before an NPU turn: at once, unless this process lends and something that matters more is lent; then once it isn't.</summary>
		public static void WaitTurn() => Volatile.Read(ref current)?.WaitTurn();

		/// <summary>Whether this process lends, and something that matters more is lent now (at the head of the NPU's line).</summary>
		internal static bool MustGiveWay() => Volatile.Read(ref current)?.MustGiveWay() ?? false;

		internal static void Left(Lending lending) => Interlocked.CompareExchange(ref current, null, lending);

		/// <summary>One lending's entry on the record (<see cref="Join"/>).</summary>
		public sealed class Lending : IDisposable {
			readonly string? folder, path;
			readonly string name = "", body = "";
			readonly int rank;
			readonly CancellationToken ct;
			readonly Action<bool>? givingWay;
			readonly Timer? beat;
			readonly object gate = new();
			bool giving, left;
			long wentAt; // when a look last said go (Stopwatch ticks)

			internal Lending(string? folder, int rank, string who, string? to, string? doing, CancellationToken ct, Action<bool>? givingWay) {
				(this.folder, this.rank, this.ct, this.givingWay) = (folder, rank, ct, givingWay);
				if (folder == null) return;
				long us = NpuLock.NowUs();
				name = $"{rank}-{us:D17}-{Environment.ProcessId}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}.lend";
				path = Path.Combine(folder, name);
				static string Str(string s) => "\"" + JsonEncodedText.Encode(s) + "\"";
				body = $"{{\"pid\":{Environment.ProcessId},\"since\":{us / 1000},\"rank\":{Str(RankNames[rank])},\"who\":{Str(who)}" +
					(to != null ? $",\"to\":{Str(to)}" : "") + (doing != null ? $",\"doing\":{Str(doing.Length > 80 ? doing[..80] : doing)}" : "") + "}";
				Write();
				// The heartbeat is at least every 2 s; every second keeps it well inside that.
				beat = new Timer(_ => Beat(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
			}

			/// <summary>The entry's file name (tests).</summary>
			internal string Name => name;

			void Write() {
				try {
					Directory.CreateDirectory(folder!);
					string tmp = path + ".tmp"; // not an entry's name, so never read half written
					File.WriteAllText(tmp, body);
					File.Move(tmp, path!, overwrite: true);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			}

			void Beat() {
				lock (gate) {
					if (left) return;
					try { File.SetLastWriteTimeUtc(path!, DateTime.UtcNow); }
					catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { Write(); } // taken for dead: back, as it was
					catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
				}
			}

			internal bool MustGiveWay() {
				if (folder == null) return false;
				// A turn is taken often (each picture's words): a look that said go holds for a moment.
				if (Stopwatch.GetElapsedTime(Interlocked.Read(ref wentAt)) < TimeSpan.FromMilliseconds(LookMs)) return false;
				if (GivesWay(rank, Others(folder, name))) return true;
				Interlocked.Exchange(ref wentAt, Stopwatch.GetTimestamp());
				return false;
			}

			internal void WaitTurn() {
				while (MustGiveWay()) {
					Giving(true);
					if (ct.WaitHandle.WaitOne(LookMs)) {
						Giving(false);
						ct.ThrowIfCancellationRequested();
					}
				}
				Giving(false);
			}

			void Giving(bool now) {
				bool changed;
				lock (gate) {
					changed = giving != now;
					giving = now;
				}
				if (changed) givingWay?.Invoke(now);
			}

			/// <summary>Leaves the record: the entry is deleted, however the lending ended.</summary>
			public void Dispose() {
				lock (gate) {
					if (left) return;
					left = true;
				}
				beat?.Dispose();
				if (path != null) {
					try { File.Delete(path); }
					catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
				}
				Left(this);
			}
		}
	}
}
