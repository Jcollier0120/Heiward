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

namespace HEI.Agent {
	/// <summary>
	/// Heiward's first scan in the manor's first-round line (the Steward's kit 2.43.0, kit\spec\ROUND.md's "Pace").
	/// Agents new to the manor do their heavy first round one at a time, so a first install, with every agent hired
	/// together, never grinds the PC to a halt; the rounds after that only catch up on what changed. Heiward's first scan
	/// reads every drive, so it is one of them:
	/// <list type="bullet">
	/// <item>A scheduled scan with no scan finished before (no report yet) waits its turn in the line, unless Manor's
	/// "backgroundPace" is "full", and keeps the turn for the whole scan.</item>
	/// <item>While it holds the turn it runs at full speed: nothing else heavy runs then. Every scan after it keeps the
	/// pace the person chose.</item>
	/// <item>A scan the person starts (Scan now) never waits.</item>
	/// </list>
	/// While it waits or runs, first-round.json says so, for /api/ping's firstRound and the page's "Settling in".
	/// </summary>
	static class FirstRound {
		public const string Waiting = "waiting", Running = "running";

		/// <summary>How long a scan waits for its turn before it lets it go; the next scheduled scan tries again.</summary>
		public static readonly TimeSpan Wait = TimeSpan.FromHours(6);

		static string StateFile => Path.Combine(AgentPaths.Home, "first-round.json");

		/// <summary>Whether a scan has ever finished on this PC: it left a report, whichever build made it.</summary>
		public static bool Done() => File.Exists(AgentPaths.Report);

		/// <summary>
		/// Whether this scan waits its turn: one the person didn't start by hand (scheduled, or the install's), before any
		/// scan finished, at a gentle pace, on a PC where the manor's line is (<paramref name="lineHere"/>). Without a manor
		/// Heiward scans as it always has.
		/// </summary>
		public static bool Waits(bool scheduled, bool done, string? pace, bool lineHere = true) => scheduled && !done && pace != "full" && lineHere;

		/// <summary>
		/// The turn for a scan, or null when this one doesn't wait (<see cref="Waits"/>). Waits up to <paramref name="wait"/>
		/// (<see cref="Wait"/>), saying so in first-round.json, then throws <see cref="TimeoutException"/>. The turn, disposed,
		/// is given up and the state cleared.
		/// </summary>
		public static IDisposable? Take(bool scheduled, Manor? manor, Action<string> log, TimeSpan? wait = null, Func<TimeSpan, IDisposable>? acquire = null) {
			if (!Waits(scheduled, Done(), manor?.BackgroundPace, acquire != null || NpuLock.FirstRoundDirectory != null)) return null;
			Write(Waiting);
			log("first scan: waiting its turn among the agents' first rounds, one at a time");
			IDisposable turn;
			try { turn = (acquire ?? NpuLock.AcquireFirstRound)(wait ?? Wait); }
			catch {
				Clear();
				throw;
			}
			Write(Running);
			log("first scan: its turn; at full speed while it holds it");
			return new Turn(turn);
		}

		/// <summary>Its first scan now: waiting or running, while the process that said so lives; null otherwise.</summary>
		public static string? Now() {
			try {
				if (!File.Exists(StateFile)) return null;
				using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
				JsonElement root = doc.RootElement;
				string? state = root.GetProperty("state").GetString();
				int pid = root.GetProperty("pid").GetInt32();
				if (state is not (Waiting or Running)) return null;
				try {
					using var p = Process.GetProcessById(pid);
					return p.HasExited ? null : state;
				}
				catch (ArgumentException) { return null; } // that scan has gone
			}
			catch { return null; }
		}

		static void Write(string state) {
			try { AgentPaths.WriteAtomic(StateFile, JsonSerializer.Serialize(new { state, pid = Environment.ProcessId, since = DateTime.UtcNow })); }
			catch { /* only the page reads it */ }
		}

		static void Clear() {
			try { File.Delete(StateFile); } catch { }
		}

		sealed class Turn(IDisposable held) : IDisposable {
			int done;
			public void Dispose() {
				if (Interlocked.Exchange(ref done, 1) != 0) return;
				Clear();
				held.Dispose();
			}
		}
	}
}
