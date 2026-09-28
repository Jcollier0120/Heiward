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
	sealed record Decision(string Action, DateTime AtUtc, List<string> Recycled, long RecycledBytes, bool Auto = false);

	/// <summary>
	/// decisions.json, keyed by <see cref="ReportBuilder.GroupKey"/>. Written by the review page and by
	/// automatic cleanup in a scan, always under <see cref="CleanLock"/>.
	/// </summary>
	static class DecisionStore {
		static readonly object gate = new();

		public static Dictionary<string, Decision> Load() {
			lock (gate) {
				try {
					if (File.Exists(AgentPaths.Decisions))
						return JsonSerializer.Deserialize<Dictionary<string, Decision>>(File.ReadAllText(AgentPaths.Decisions), AgentConfig.Json) ?? new();
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
				AgentPaths.WriteAtomic(AgentPaths.Decisions, JsonSerializer.Serialize(all, AgentConfig.Json));
			}
		}
	}
}
