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

/// <summary>What a rescan reads from the disk again, and at what priority.</summary>
[Collection(AgentHomeCollection.Name)]
public sealed class GentleRescanTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-rescan-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public GentleRescanTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>Writes the bytes and dates the file as given: a rewrite that keeps size and date can only be told apart by reading it.</summary>
	string Write(string name, byte fill, DateTime modifiedUtc) {
		string path = Path.Combine(dir, name);
		File.WriteAllBytes(path, Enumerable.Repeat(fill, 4096).ToArray());
		File.SetLastWriteTimeUtc(path, modifiedUtc);
		return path;
	}

	[Fact]
	public void AHashTheLastScanKept_IsReused_WhileTheFileKeepsItsSizeAndDate() {
		var date = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
		string a = Write("a.jpg", 1, date);
		var first = ReportBuilder.ContentHashes.Load();
		string? kept = first.Get(a);
		first.Save();

		// Same size and date, other bytes: the next scan doesn't read the file again.
		Write("a.jpg", 2, date);
		Assert.Equal(kept, ReportBuilder.ContentHashes.Load().Get(a));

		// A new date: it's hashed again.
		Write("a.jpg", 2, date.AddMinutes(1));
		Assert.NotEqual(kept, ReportBuilder.ContentHashes.Load().Get(a));
	}

	[Fact]
	public void OnlyTheHashesAScanUsed_AreKept() {
		var date = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
		string a = Write("a.jpg", 1, date), b = Write("b.jpg", 2, date);
		var first = ReportBuilder.ContentHashes.Load();
		first.Get(a);
		first.Get(b);
		first.Save();

		var second = ReportBuilder.ContentHashes.Load();
		second.Get(a); // b is in no set any more
		second.Save();

		string kept = File.ReadAllText(ReportBuilder.ContentHashes.FilePath);
		Assert.Contains("a.jpg", kept);
		Assert.DoesNotContain("b.jpg", kept);
	}

	[Fact]
	public void ABackgroundScan_ReadsTheDiskAtVeryLowPriority_AndFullSpeedPutsItBack() {
		try {
			Assert.True(Power.SetPace(fullSpeed: false));
			Assert.Equal(0, Power.IoPriority());
			Assert.True(Power.SetPace(fullSpeed: true));
			Assert.Equal(2, Power.IoPriority());
		}
		finally {
			Power.SetPace(fullSpeed: true);
		}
	}
}
