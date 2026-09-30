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

namespace HEI.Agent.Tests;

/// <summary>Tests that point HEIWARD_HOME at their own folder: one at a time, since it's process-wide.</summary>
[CollectionDefinition(Name)]
public sealed class AgentHomeCollection {
	public const string Name = "HEIWARD_HOME";
}

/// <summary>The review page's History: clearing it, not keeping one, and folder-wide actions.</summary>
[Collection(AgentHomeCollection.Name)]
public sealed class HistoryTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-history-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public HistoryTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

	[Fact]
	public void ClearingTheHistory_ForgetsTheNames_ButKeptSetsStayHidden() {
		var cfg = new AgentConfig();
		DecisionStore.Record(cfg, "kept1", new Decision("kept", Now, new(), 0));
		DecisionStore.Record(cfg, "gone1", new Decision("recycled", Now, new() { @"C:\P\a (1).jpg" }, 500, Batch: "0123456789abcdef", Folder: @"C:\P"));

		Assert.Equal(2, DecisionStore.ClearHistory());
		var all = DecisionStore.Load();
		Assert.Equal(new[] { "gone1", "kept1" }, all.Keys.Order());
		Assert.All(all.Values, d => Assert.True(d.Unlisted));
		Assert.All(all.Values, d => Assert.Empty(d.Recycled));
		Assert.All(all.Values, d => Assert.Null(d.Folder));
		Assert.Equal(500, all.Values.Sum(d => d.RecycledBytes)); // "freed so far" stays
		Assert.Equal(0, DecisionStore.ClearHistory());
	}

	[Fact]
	public void WithoutAHistory_DecisionsStillCount_ButListNothing() {
		var cfg = new AgentConfig { KeepHistory = false };
		DecisionStore.Record(cfg, "gone1", new Decision("recycled", Now, new() { @"C:\P\a (1).jpg" }, 500));
		Decision d = DecisionStore.Load()["gone1"];
		Assert.True(d.Unlisted);
		Assert.Empty(d.Recycled);
		Assert.Equal(500, d.RecycledBytes);
	}

	[Fact]
	public void ReviewingASkipAgain_ReopensOnlyThatBatch() {
		var cfg = new AgentConfig();
		DecisionStore.SetMany(cfg, new[] {
			("a", new Decision("kept", Now, new(), 0, Batch: "aaaaaaaaaaaaaaaa", Folder: @"C:\P")),
			("b", new Decision("kept", Now, new(), 0, Batch: "aaaaaaaaaaaaaaaa", Folder: @"C:\P")),
			("c", new Decision("kept", Now, new(), 0, Batch: "bbbbbbbbbbbbbbbb", Folder: @"C:\Q")),
			("d", new Decision("kept", Now, new(), 0)),
		});
		Assert.Equal(2, DecisionStore.RemoveBatch("aaaaaaaaaaaaaaaa", "kept"));
		Assert.Equal(new[] { "c", "d" }, DecisionStore.Load().Keys.Order());
	}
}
