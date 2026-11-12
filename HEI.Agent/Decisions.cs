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

using System.Text.Json;

namespace HEI.Agent {
	/// <summary>
	/// What the user did with a group: <c>kept</c> (keep all, not duplicates — hidden from later
	/// reports while the same files stay together) or <c>recycled</c> (these files went to the
	/// Recycle Bin); also the developer cleanups. <see cref="Auto"/>: automatic cleanup did it.
	/// </summary>
	/// <param name="Batch">One action on a whole folder ("Skip all", a folder's cleanup): its sets show as one History row.</param>
	/// <param name="Folder">The folder a <paramref name="Batch"/> was done in.</param>
	/// <param name="Unlisted">Not on the History (cleared, or history is off): it still does its job, e.g. a kept set stays hidden.</param>
	sealed record Decision(string Action, DateTime AtUtc, List<string> Recycled, long RecycledBytes, bool Auto = false,
		string? Batch = null, string? Folder = null, bool Unlisted = false) {
		/// <summary>Off the History, with no file or folder names left in it.</summary>
		public Decision Forgotten() => this with { Recycled = new(), Folder = null, Unlisted = true };
	}

	/// <summary>
	/// decisions.json, keyed by <see cref="ReportBuilder.GroupKey"/>. Written by the review page and by
	/// automatic cleanup in a scan, always under <see cref="CleanLock"/>.
	/// </summary>
	static class DecisionStore {
		static readonly object gate = new();

		/// <summary>Records <paramref name="decision"/>, off the History when the user keeps none (<see cref="AgentConfig.KeepHistory"/>).</summary>
		public static void Record(AgentConfig cfg, string key, Decision decision) => SetMany(cfg, new[] { (key, decision) });

		public static void SetMany(AgentConfig cfg, IEnumerable<(string Key, Decision Decision)> decisions) {
			lock (gate) {
				var all = Load();
				foreach (var (key, d) in decisions)
					all[key] = cfg.KeepHistory ? d : d.Forgotten();
				Save(all);
			}
		}

		/// <summary>Takes every decision off the History and drops the file names they kept. Kept sets stay hidden; "freed so far" stays.</summary>
		public static int ClearHistory() {
			lock (gate) {
				var all = Load();
				int listed = all.Values.Count(d => !d.Unlisted);
				foreach (string key in all.Keys.ToList())
					all[key] = all[key].Forgotten();
				Save(all);
				return listed;
			}
		}

		/// <summary>Removes the decisions of one batch that <paramref name="action"/> made (e.g. "Skip all", to review those sets again).</summary>
		public static int RemoveBatch(string batch, string action) {
			lock (gate) {
				var all = Load();
				var keys = all.Where(kv => kv.Value.Batch == batch && kv.Value.Action == action).Select(kv => kv.Key).ToList();
				foreach (string key in keys) all.Remove(key);
				if (keys.Count > 0) Save(all);
				return keys.Count;
			}
		}

		/// <summary>
		/// decisions.json as last parsed, parsed again only once it has changed (<see cref="StampedFile{T}"/>). Each caller gets
		/// its own copy, since they change it (<see cref="SetMany"/>).
		/// </summary>
		internal static readonly StampedFile<Dictionary<string, Decision>> Saved =
			new(json => JsonSerializer.Deserialize<Dictionary<string, Decision>>(json, AgentConfig.Json), Copy);

		static Dictionary<string, Decision> Copy(Dictionary<string, Decision> all) {
			var copy = new Dictionary<string, Decision>(all.Count, all.Comparer);
			foreach (var (key, d) in all) copy[key] = d.Recycled == null ? d : d with { Recycled = [.. d.Recycled] };
			return copy;
		}

		static void Save(Dictionary<string, Decision> all) {
			string json = JsonSerializer.Serialize(all, AgentConfig.Json);
			AgentPaths.WriteAtomic(AgentPaths.Decisions, json);
			// This process's change shows on its next read at once, without parsing the file again.
			Saved.Wrote(AgentPaths.Decisions, all, json);
		}

		public static Dictionary<string, Decision> Load() {
			lock (gate) {
				try {
					return Saved.Read(AgentPaths.Decisions) ?? new();
				}
				catch (Exception e) {
					AgentPaths.AppendLog($"decisions.json unreadable: {e.Message}");
				}
				return new();
			}
		}

		public static void Set(string key, Decision? decision) {
			lock (gate) {
				var all = Load();
				if (decision == null) all.Remove(key);
				else all[key] = decision;
				Save(all);
			}
		}
	}
}
