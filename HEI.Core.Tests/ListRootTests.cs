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

namespace HEI.Core.Tests;

/// <summary>
/// A caller that knows what changed since the last search lists a folder for the engine (ListRoot):
/// the files it vouches for aren't read at all, and with ListingProvesExistence the listing alone says
/// which files exist.
/// </summary>
[Collection("DatabaseUtils")] // BuildFileList loads the static DatabaseUtils database
public class ListRootTests {
	static void Write(string path, int size) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, new byte[size]);
	}

	[Fact]
	public async Task FilesTheCallerVouchesFor_AreNotRead_AndListedOnesAreMerged() {
		string dir = Directory.CreateTempSubdirectory("hei-listroot-test").FullName;
		try {
			string db = Path.Combine(dir, "db"), root = Path.Combine(dir, "photos");
			string kept = Path.Combine(root, "kept.jpg"), added = Path.Combine(root, "new", "added.png"), gone = Path.Combine(root, "gone.jpg");
			Directory.CreateDirectory(db);
			Write(kept, 64);
			Write(gone, 64);
			DatabaseUtils.CustomDatabaseFolder = db;
			DatabaseUtils.InvalidateDatabaseFolder();
			DatabaseUtils.Database.Clear();
			ScanEngine Engine() {
				var engine = new ScanEngine();
				engine.Settings.CustomDatabaseFolder = db;
				engine.Settings.IncludeImages = true;
				engine.Settings.IncludeSubDirectories = true;
				engine.Settings.ListingProvesExistence = true;
				engine.Settings.IncludeList.Add(root);
				return engine;
			}
			await Engine().BuildFileList(CancellationToken.None); // the walk: kept and gone reach the database

			// Since: kept.jpg rewritten at another size (a caller vouching for it says it isn't), a photo
			// added in a new folder, gone.jpg left on disk but not listed.
			Write(kept, 128);
			Write(added, 32);
			var engine = Engine();
			engine.ListRoot = r => r == root ? new ScanEngine.RootListing(new[] { new FileInfo(added) }, new[] { kept }) : null;
			await engine.BuildFileList(CancellationToken.None);

			var found = engine.FoundFiles.ToDictionary(f => Path.GetFileName(f.Path), f => f.Size);
			Assert.Equal(64, found["kept.jpg"]);   // the database's size: the file wasn't read
			Assert.Equal(32, found["added.png"]);  // listed now: merged as the walk would
			Assert.False(found.ContainsKey("gone.jpg"));
			Assert.Contains(DatabaseUtils.Database, e => e.Path == added);

			// Not in the listing: left out of the search, though the file is still on disk.
			FileEntry stale = DatabaseUtils.Database.Single(e => e.Path == gone);
			Assert.True(engine.InvalidEntry(stale, out _, out string? reason));
			Assert.Equal("file does not exist", reason);
			Assert.False(engine.InvalidEntry(DatabaseUtils.Database.Single(e => e.Path == kept), out _, out reason) && reason == "file does not exist");
		}
		finally {
			DatabaseUtils.Database.Clear();
			DatabaseUtils.CustomDatabaseFolder = null;
			DatabaseUtils.InvalidateDatabaseFolder();
			try { Directory.Delete(dir, true); } catch { /* best effort */ }
		}
	}
}
