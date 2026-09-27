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

namespace VDF.Agent.Tests;

/// <summary>
/// The Recycle Bin guard's refusals. These requests never reach the shell, so no test here moves
/// anything to the user's Recycle Bin.
/// </summary>
public sealed class RecyclerTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "vdf-recycler-tests-" + Guid.NewGuid().ToString("N"));

	public RecyclerTests() => Directory.CreateDirectory(dir);
	public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

	ReportItem File(string name, bool keep) {
		string path = Path.Combine(dir, name);
		System.IO.File.WriteAllBytes(path, new byte[1000]);
		var fi = new FileInfo(path);
		return new ReportItem(path, name, dir, fi.Length, 100, 100, "jpg", 0, 0, 0, fi.LastWriteTimeUtc, 100, false,
			keep ? "keep" : "smaller", keep, !keep);
	}

	ReportGroup Group(params ReportItem[] items) =>
		new("k", "copies", "image", items.First(i => i.Keep).Path, "test", 0, 100, items.ToList());

	[Fact]
	public void RefusesToRemoveEveryCopy() {
		var g = Group(File("a.jpg", keep: true), File("b.jpg", keep: false));
		var r = Recycler.Recycle(g, g.Items.Select(i => i.Path).ToList());
		Assert.Empty(r.Recycled);
		Assert.All(r.Failed, f => Assert.Contains("keep at least one", f.Reason));
		Assert.All(g.Items, i => Assert.True(System.IO.File.Exists(i.Path)));
	}

	[Fact]
	public void RefusesAPathOutsideTheGroup() {
		var g = Group(File("a.jpg", keep: true), File("b.jpg", keep: false));
		string stranger = Path.Combine(dir, "not-in-group.jpg");
		System.IO.File.WriteAllBytes(stranger, new byte[10]);
		var r = Recycler.Recycle(g, new[] { stranger });
		Assert.Empty(r.Recycled);
		Assert.Contains("not part of this group", Assert.Single(r.Failed).Reason);
		Assert.True(System.IO.File.Exists(stranger));
	}

	[Fact]
	public void RefusesAFileChangedSinceTheScan() {
		var keep = File("a.jpg", keep: true);
		var copy = File("b.jpg", keep: false);
		System.IO.File.WriteAllBytes(copy.Path, new byte[2000]); // edited after the scan
		var r = Recycler.Recycle(Group(keep, copy), new[] { copy.Path });
		Assert.Empty(r.Recycled);
		Assert.Contains("changed since the scan", Assert.Single(r.Failed).Reason);
		Assert.True(System.IO.File.Exists(copy.Path));
	}

	[Fact]
	public void KeepingOnlyAFileThatIsAlreadyGone_CountsAsRemovingEveryCopy() {
		var keep = File("a.jpg", keep: true);
		var copy = File("b.jpg", keep: false);
		System.IO.File.Delete(keep.Path);
		var r = Recycler.Recycle(Group(keep, copy), new[] { copy.Path });
		Assert.Empty(r.Recycled);
		Assert.True(System.IO.File.Exists(copy.Path));
	}
}
