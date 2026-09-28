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
/// The file listing walks every disk at once. Whatever order the walks finish in, every file of every
/// root must reach the database exactly once, with each root's listing time recorded.
/// </summary>
[Collection("DatabaseUtils")] // BuildFileList loads the static DatabaseUtils database
public class ParallelListingTests {
	static void Touch(string path) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, new byte[64]);
	}

	[Theory]
	[InlineData(false)] // one walk per root, as on a PC with a disk per root
	[InlineData(true)]  // one walk for all of them, as on a single disk
	public async Task EveryRootsFiles_ReachTheDatabase_WhateverTheDisks(bool oneDisk) {
		string dir = Directory.CreateTempSubdirectory("hei-listing-test").FullName;
		try {
			string db = Path.Combine(dir, "db"), a = Path.Combine(dir, "a"), b = Path.Combine(dir, "b"), c = Path.Combine(dir, "c");
			Directory.CreateDirectory(db);
			foreach (string f in new[] { @"a\1.jpg", @"a\sub\2.png", @"b\3.mp4", @"b\4.jpg", @"b\deep\er\5.mkv", @"c\6.gif" })
				Touch(Path.Combine(dir, f));
			Touch(Path.Combine(dir, @"b\notes.txt")); // not media: never listed

			// StartSearch points the database at the settings' folder; this test calls the listing directly.
			DatabaseUtils.CustomDatabaseFolder = db;
			DatabaseUtils.InvalidateDatabaseFolder();
			DatabaseUtils.Database.Clear();
			var engine = new ScanEngine();
			engine.Settings.CustomDatabaseFolder = db;
			engine.Settings.MaxDegreeOfParallelism = 4; // 1 (VDF's default) means one thing at a time: one walk
			engine.Settings.IncludeImages = true;
			engine.Settings.IncludeSubDirectories = true;
			foreach (string root in new[] { a, b, c }) engine.Settings.IncludeList.Add(root);
			engine.Settings.IncludeList.Add(Path.Combine(dir, "missing")); // skipped with a warning, as before
			var walks = new List<List<string>>();
			engine.GroupRootsForListing = roots => {
				var groups = oneDisk ? new List<List<string>> { roots.ToList() } : roots.Select(r => new List<string> { r }).ToList();
				walks.AddRange(groups);
				return groups;
			};

			await engine.BuildFileList(CancellationToken.None);

			Assert.Equal(oneDisk ? 1 : 3, walks.Count);
			var inDb = DatabaseUtils.Database.Select(e => Path.GetRelativePath(dir, e.Path)).Order(StringComparer.OrdinalIgnoreCase).ToList();
			Assert.Equal(new[] { @"a\1.jpg", @"a\sub\2.png", @"b\3.mp4", @"b\4.jpg", @"b\deep\er\5.mkv", @"c\6.gif" }, inDb);
			Assert.Equal(6, engine.FoundFiles.Count);
			Assert.Equal(new[] { a, b, c }.Order(StringComparer.OrdinalIgnoreCase), engine.ListingTimes.Keys.Order(StringComparer.OrdinalIgnoreCase));
		}
		finally {
			DatabaseUtils.Database.Clear();
			DatabaseUtils.CustomDatabaseFolder = null;
			DatabaseUtils.InvalidateDatabaseFolder();
			try { Directory.Delete(dir, true); } catch { /* best effort */ }
		}
	}
}
