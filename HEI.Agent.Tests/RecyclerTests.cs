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

using HEI.Core.Utils;

namespace HEI.Agent.Tests;

/// <summary>
/// The Recycle Bin guard's refusals. These requests never reach the shell, so no test here moves
/// anything to the user's Recycle Bin.
/// </summary>
public sealed class RecyclerTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-recycler-tests-" + Guid.NewGuid().ToString("N"));

	public RecyclerTests() => Directory.CreateDirectory(dir);
	public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

	ReportItem File(string name, bool keep, string relation = "smaller") {
		string path = Path.Combine(dir, name);
		System.IO.File.WriteAllBytes(path, new byte[1000]);
		return Item(path, keep, relation);
	}

	/// <summary>The file as the scan saw it, already on disk.</summary>
	static ReportItem Item(string path, bool keep, string relation) {
		var fi = new FileInfo(path);
		return new ReportItem(path, fi.Name, fi.DirectoryName!, fi.Length, 100, 100, "jpg", 0, 0, 0, fi.LastWriteTimeUtc, 100, false,
			keep ? "keep" : relation, keep, !keep);
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
	public void RefusesWhenTheCopyThatStaysChangedSinceTheScan() {
		// Then the file removed may be the only original left.
		var keep = File("a.jpg", keep: true);
		var copy = File("b.jpg", keep: false);
		System.IO.File.WriteAllBytes(keep.Path, new byte[2000]);
		var r = Recycler.Recycle(Group(keep, copy), new[] { copy.Path });
		Assert.Empty(r.Recycled);
		Assert.Contains("the copy you'd keep changed since the scan", Assert.Single(r.Failed).Reason);
		Assert.True(System.IO.File.Exists(copy.Path));
	}

	[Fact]
	public void RefusesWhenTheCopyThatStaysIsTheSameFile() {
		// A hard link (or the folder under another drive letter) is no second copy: it frees nothing, or goes too.
		var keep = File("a.jpg", keep: true);
		string link = Path.Combine(dir, "b.jpg");
		HardLinkUtils.CreateHardLink(link, keep.Path);
		var copy = Item(link, keep: false, "identical");
		var r = Recycler.Recycle(Group(keep, copy), new[] { copy.Path });
		Assert.Empty(r.Recycled);
		Assert.Contains("same file under another path", Assert.Single(r.Failed).Reason);
		Assert.True(System.IO.File.Exists(link));
	}

	[Fact]
	public void RefusesAPathTooLongForTheRecycleBin() {
		string deep = Path.Combine(dir, new string('d', 120), new string('e', 120));
		Directory.CreateDirectory(deep);
		string path = Path.Combine(deep, "copy.jpg");
		System.IO.File.WriteAllBytes(path, new byte[1000]);
		Assert.True(path.Length > RecycleBin.MaxPath);
		var keep = File("a.jpg", keep: true);
		var copy = Item(path, keep: false, "smaller");
		var r = Recycler.Recycle(Group(keep, copy), new[] { copy.Path });
		Assert.Empty(r.Recycled);
		Assert.Contains("longer than the Recycle Bin takes", Assert.Single(r.Failed).Reason);
		Assert.True(System.IO.File.Exists(path));
	}

	[Fact]
	public void SameBytes_ReadsBothFilesToTheEnd() {
		string a = Path.Combine(dir, "a.bin"), b = Path.Combine(dir, "b.bin"), c = Path.Combine(dir, "c.bin");
		var bytes = Enumerable.Range(0, (9 << 20) + 5).Select(i => (byte)(i * 7)).ToArray(); // over two 4 MB steps
		System.IO.File.WriteAllBytes(a, bytes);
		System.IO.File.WriteAllBytes(b, bytes);
		bytes[^1] ^= 1;
		System.IO.File.WriteAllBytes(c, bytes);
		Assert.True(Recycler.SameBytes(a, b));
		Assert.False(Recycler.SameBytes(a, c));
		Assert.False(Recycler.SameBytes(a, Path.Combine(dir, "missing.bin")));
	}

	[Fact]
	public void ABigIdenticalCopy_ThatDiffersBetweenTheSamples_IsRefused() {
		// Over 256 MB, "identical" was the size and nine samples: a copy damaged between them passed.
		long size = ReportBuilder.ContentHashes.FullHashBytes + (1 << 20);
		string a = Path.Combine(dir, "movie.mkv"), b = Path.Combine(dir, "movie (1).mkv");
		foreach (string p in new[] { a, b }) {
			using var fs = new FileStream(p, FileMode.Create);
			fs.SetLength(size);
		}
		using (var fs = new FileStream(b, FileMode.Open)) { // a byte away from every sample
			fs.Position = size / 16;
			fs.WriteByte(1);
		}
		var keep = Item(a, keep: true, "keep");
		var copy = Item(b, keep: false, "identical");
		var r = Recycler.Recycle(new ReportGroup("k", "identical", "video", a, "test", size, 100, new() { keep, copy }), new[] { b });
		Assert.Empty(r.Recycled);
		Assert.Contains("not the same bytes", Assert.Single(r.Failed).Reason);
		Assert.True(System.IO.File.Exists(b));
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
