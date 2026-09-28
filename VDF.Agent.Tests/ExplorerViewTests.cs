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

/// <summary>The review page's drive and folder view: the scan index, the folder tree, and per-folder duplicate counts.</summary>
public sealed class ExplorerViewTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "vdf-explorer-" + Guid.NewGuid().ToString("N"));

	public ExplorerViewTests() => Directory.CreateDirectory(root);
	public void Dispose() { try { Directory.Delete(root, true); } catch { } }

	string Dir(params string[] parts) {
		string p = Path.Combine(new[] { root }.Concat(parts).ToArray());
		Directory.CreateDirectory(p);
		return p;
	}

	static ReportItem Item(string path, long size, bool suggested, bool keep = false) =>
		new(path, Path.GetFileName(path), Path.GetDirectoryName(path)!, size, 100, 100, "jpg", 0, 0, 0, DateTime.UtcNow, 100, false,
			keep ? "keep" : "identical", keep, suggested, false);

	static ReportGroup Group(string kind, params ReportItem[] items) =>
		new(Guid.NewGuid().ToString("N"), kind, "image", items.First(i => i.Keep).Path, "", items.Where(i => i.Suggested).Sum(i => i.Size), 100, items.ToList());

	[Fact]
	public void Index_SumsFoldersAndDrives_AndKeepsSiblingPrefixesApart() {
		var index = ScanIndex.Build(DateTime.UtcNow, new[] { @"C:\" },
			new (string, long)[] { (@"C:\Photos\a.jpg", 10), (@"C:\Photos\2019\b.jpg", 20), (@"C:\Photos2\c.jpg", 40) },
			new Dictionary<string, TimeSpan> { [@"C:\"] = TimeSpan.FromSeconds(1.5) },
			new Dictionary<string, TimeSpan> { [@"C:\"] = TimeSpan.FromSeconds(30) });
		Assert.Equal((2, 30L), index.Subtree(@"C:\Photos"));          // not C:\Photos2
		Assert.Equal((3, 70L), index.Subtree(@"C:\"));
		Assert.True(index.HasFilesBelow(@"C:\Photos"));
		Assert.False(index.HasFilesBelow(@"C:\Photos\2019"));
		Assert.Equal(new DriveScan(1.5, 30, 3, 70), index.Drives[@"C:\"]);
	}

	[Fact]
	public void Stats_CountCopiesAndLookAlikes_AndOnlyTheTickedBytesInside() {
		string photos = Dir("Photos"), other = Dir("Other");
		var pending = new List<ReportGroup> {
			// The kept file elsewhere, the ticked copy here: 5 bytes to free here.
			Group("identical", Item(Path.Combine(other, "a.jpg"), 5, false, keep: true), Item(Path.Combine(photos, "a (1).jpg"), 5, true)),
			// Both here: the kept one frees nothing.
			Group("copies", Item(Path.Combine(photos, "b.jpg"), 7, false, keep: true), Item(Path.Combine(photos, "b small.jpg"), 3, true)),
			Group("similar", Item(Path.Combine(photos, "c.jpg"), 9, false, keep: true), Item(Path.Combine(photos, "d.jpg"), 9, false)),
			Group("identical", Item(Path.Combine(other, "e.jpg"), 1, false, keep: true), Item(Path.Combine(other, "e (1).jpg"), 1, true)),
		};
		Assert.Equal(new DupStats(2, 1, 8), ExplorerView.Stats(photos, pending));
		// Other only keeps a.jpg's original: nothing to clean up there for that set.
		Assert.Equal(new DupStats(1, 0, 1), ExplorerView.Stats(other, pending));
		Assert.Equal(new DupStats(0, 0, 0), ExplorerView.Stats(photos + "2", pending)); // a sibling that shares the prefix
	}

	[Fact]
	public void Tree_MarksExemptFolders_AndFoldsAwayEmptyOnesBelowTheTop() {
		string photos = Dir("Photos");
		Dir("Photos", "2019");
		Dir("Photos", "Empty");
		Dir("Photos", "node_modules");
		Dir("Photos", "site", ".git");
		File.WriteAllBytes(Path.Combine(photos, "2019", "a.jpg"), new byte[] { 1 });
		var cfg = new AgentConfig { ScanAllDrives = false, Folders = { root } };
		var index = ScanIndex.Build(DateTime.UtcNow, new[] { root }, new[] { (Path.Combine(photos, "2019", "a.jpg"), 1L) },
			new Dictionary<string, TimeSpan>(), new Dictionary<string, TimeSpan>());

		TreeListing top = ExplorerView.Tree(root, all: false, cfg, index, new())!;
		Assert.Contains(top.Children, c => c.Name == "Photos" && c.Files == 1 && c.Expandable);

		TreeListing listing = ExplorerView.Tree(photos, all: false, cfg, index, new())!;
		Assert.Equal(new[] { "2019", "node_modules", "site" }, listing.Children.Select(c => c.Name));
		Assert.Equal("Code", listing.Children.Single(c => c.Name == "node_modules").Exempt);
		Assert.Equal("Code repository", listing.Children.Single(c => c.Name == "site").Exempt);
		Assert.False(listing.Children.Single(c => c.Name == "site").Expandable);
		Assert.Equal(1, listing.HiddenEmpty); // Empty
		Assert.Contains(ExplorerView.Tree(photos, all: true, cfg, index, new())!.Children, c => c.Name == "Empty");

		// Inside an exempt folder: the reason, and nothing listed.
		TreeListing inside = ExplorerView.Tree(Path.Combine(photos, "site"), all: false, cfg, index, new())!;
		Assert.Equal("Code repository", inside.Exempt);
		Assert.Empty(inside.Children);
	}

	[Fact]
	public void Tree_RefusesFoldersOutsideTheScan() {
		var cfg = new AgentConfig { ScanAllDrives = false, Folders = { Dir("Scanned") } };
		Assert.Null(ExplorerView.Tree(Dir("Elsewhere"), all: false, cfg, null, new()));
		Assert.Null(ExplorerView.Tree("relative\\path", all: false, cfg, null, new()));
	}
}
