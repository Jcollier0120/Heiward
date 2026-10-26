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
/// Settings.ForgetUnlisted: what the listing didn't find in the folders it listed (deleted, or in a folder scans leave
/// out) leaves the database, which then holds what's there to compare. Everything the search didn't list stays: other
/// folders, a folder that isn't there, and a drive the search mustn't read (MayRead).
/// </summary>
[Collection("DatabaseUtils")] // BuildFileList loads the static DatabaseUtils database
public class ForgetUnlistedTests : IDisposable {
	readonly string dir, db;

	public ForgetUnlistedTests() {
		dir = Directory.CreateTempSubdirectory("hei-forget-test").FullName;
		db = Path.Combine(dir, "db");
		Directory.CreateDirectory(db);
		DatabaseUtils.CustomDatabaseFolder = db;
		DatabaseUtils.InvalidateDatabaseFolder();
		DatabaseUtils.Database.Clear();
	}

	public void Dispose() {
		DatabaseUtils.Database.Clear();
		DatabaseUtils.CustomDatabaseFolder = null;
		DatabaseUtils.InvalidateDatabaseFolder();
		try { Directory.Delete(dir, true); } catch { /* best effort */ }
	}

	string P(params string[] parts) => Path.Combine([dir, .. parts]);

	static void Write(string path) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, new byte[64]);
	}

	// Entries as an earlier scan left them, saved where BuildFileList loads them from.
	static void Remember(params string[] paths) {
		foreach (string path in paths)
			DatabaseUtils.Database.Add(new FileEntry { _Path = path, Folder = Path.GetDirectoryName(path)!, FileSize = 64 });
		DatabaseUtils.SaveDatabase();
		DatabaseUtils.Database.Clear();
	}

	ScanEngine Engine(bool forget) {
		var engine = new ScanEngine();
		engine.Settings.CustomDatabaseFolder = db;
		engine.Settings.IncludeImages = true;
		engine.Settings.IncludeSubDirectories = true;
		engine.Settings.ListingProvesExistence = true;
		engine.Settings.ForgetUnlisted = forget;
		engine.Settings.IncludeList.Add(P("photos"));
		engine.Settings.IncludeList.Add(P("resting"));
		engine.Settings.IncludeList.Add(P("unplugged")); // not there: its files are kept for when it's back
		engine.Settings.BlackList.Add(P("photos", "left-out"));
		// A resting drive: listed from what the caller knows, here nothing, and never read.
		engine.ListRoot = root => root == P("resting") ? new ScanEngine.RootListing(Array.Empty<FileInfo>(), Array.Empty<string>()) : null;
		engine.MayRead = path => !path.StartsWith(P("resting"), StringComparison.OrdinalIgnoreCase);
		return engine;
	}

	string[] Remembered => new[] {
		P("photos", "kept.jpg"),
		P("photos", "deleted.jpg"),
		P("photos", "left-out", "hidden.jpg"),
		P("photos2", "sibling.jpg"),      // beside the listed folder, its name starting the same
		P("elsewhere", "away.jpg"),       // a folder this search doesn't include
		P("resting", "sleeping.jpg"),
		P("unplugged", "offline.jpg"),
	};

	[Fact]
	public async Task ForgetsOnlyWhatTheListedFoldersNoLongerHold() {
		Write(P("photos", "kept.jpg"));
		Write(P("photos", "left-out", "hidden.jpg"));
		Write(P("photos2", "sibling.jpg"));
		Write(P("elsewhere", "away.jpg"));
		Directory.CreateDirectory(P("resting"));
		Remember(Remembered);

		await Engine(forget: true).BuildFileList(CancellationToken.None);

		var left = DatabaseUtils.Database.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
		Assert.Contains(P("photos", "kept.jpg"), left);
		Assert.DoesNotContain(P("photos", "deleted.jpg"), left);
		Assert.DoesNotContain(P("photos", "left-out", "hidden.jpg"), left);
		Assert.Contains(P("photos2", "sibling.jpg"), left);
		Assert.Contains(P("elsewhere", "away.jpg"), left);
		Assert.Contains(P("resting", "sleeping.jpg"), left);
		Assert.Contains(P("unplugged", "offline.jpg"), left);
	}

	[Fact]
	public async Task WithoutIt_EveryEntryStays() {
		Write(P("photos", "kept.jpg"));
		Directory.CreateDirectory(P("resting"));
		Remember(Remembered);

		await Engine(forget: false).BuildFileList(CancellationToken.None);

		Assert.Equal(Remembered.Length, DatabaseUtils.Database.Count);
	}
}
