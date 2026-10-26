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

namespace HEI.Core.Tests.Utils;

/// <summary>
/// A save that would write the database's same bytes again writes nothing: most scans change nothing, and the
/// database can be hundreds of megabytes. It writes whenever the file isn't the one this process read or wrote last.
/// </summary>
[Collection("DatabaseUtils")] // DatabaseUtils is static
public class DatabaseSaveSkipTests : IDisposable {
	readonly string dir;

	public DatabaseSaveSkipTests() {
		dir = Directory.CreateTempSubdirectory("hei-saveskip-test").FullName;
		Use(dir);
		DatabaseUtils.Database.Clear();
	}

	public void Dispose() {
		DatabaseUtils.Database.Clear();
		DatabaseUtils.CustomDatabaseFolder = null;
		DatabaseUtils.InvalidateDatabaseFolder();
		try { Directory.Delete(dir, true); } catch { /* best effort */ }
	}

	static void Use(string folder) {
		DatabaseUtils.CustomDatabaseFolder = folder;
		DatabaseUtils.InvalidateDatabaseFolder();
	}

	string DbPath => Path.Combine(dir, "ScannedFiles.db");

	static void Add(string path) =>
		DatabaseUtils.Database.Add(new FileEntry { _Path = path, Folder = Path.GetDirectoryName(path)!, FileSize = 1000 });

	// Saves, and says whether the file was written. (Not by the file's write time: two saves within one tick of the
	// system clock, some 15 ms, get the same one.)
	static bool SaveWrote() {
		bool wrote = false;
		void Handler(LogEntry entry) {
			if (entry.Message.StartsWith("Save scanned files to disk", StringComparison.Ordinal)) wrote = true;
		}
		Logger.Instance.LogEntryAdded += Handler;
		try { DatabaseUtils.SaveDatabase(); }
		finally { Logger.Instance.LogEntryAdded -= Handler; }
		return wrote;
	}

	[Fact]
	public void Unchanged_IsNotWrittenAgain_Changed_Is() {
		Add(@"C:\v\a.mp4");
		Assert.True(SaveWrote());
		DatabaseUtils.Database.Clear();
		Assert.True(DatabaseUtils.LoadDatabase());
		long length = new FileInfo(DbPath).Length;

		Assert.False(SaveWrote());
		Assert.Equal(length, new FileInfo(DbPath).Length);

		Add(@"C:\v\b.mp4");
		Assert.True(SaveWrote());

		// What it just wrote, it doesn't write again.
		Assert.False(SaveWrote());
		DatabaseUtils.Database.Clear();
		Assert.True(DatabaseUtils.LoadDatabase());
		Assert.Equal(2, DatabaseUtils.Database.Count);
	}

	[Fact]
	public void WrittenByAnotherProgram_IsWrittenOver() {
		Add(@"C:\v\a.mp4");
		Add(@"C:\v\b.mp4");
		DatabaseUtils.SaveDatabase();
		string theirs = Path.Combine(dir, "theirs.db");
		File.Copy(DbPath, theirs);
		DatabaseUtils.Database.Clear();
		Add(@"C:\v\a.mp4");
		DatabaseUtils.SaveDatabase();
		DatabaseUtils.Database.Clear();
		Assert.True(DatabaseUtils.LoadDatabase());

		File.Copy(theirs, DbPath, overwrite: true); // another program wrote it since
		Assert.True(SaveWrote());

		DatabaseUtils.Database.Clear();
		Assert.True(DatabaseUtils.LoadDatabase());
		Assert.Single(DatabaseUtils.Database);
	}

	[Fact]
	public void AnotherDatabaseFolder_IsWrittenTo() {
		string other = Path.Combine(dir, "other");
		Directory.CreateDirectory(other);
		Use(other);
		Add(@"C:\v\old.mp4");
		DatabaseUtils.SaveDatabase();
		DatabaseUtils.Database.Clear();

		Use(dir);
		Add(@"C:\v\a.mp4");
		DatabaseUtils.SaveDatabase();
		DatabaseUtils.Database.Clear();
		Assert.True(DatabaseUtils.LoadDatabase());

		// The folder moves; the same entries are saved there, over what it held.
		Use(other);
		Assert.True(SaveWrote());
		DatabaseUtils.Database.Clear();
		Assert.True(DatabaseUtils.LoadDatabase());
		Assert.Equal(@"C:\v\a.mp4", Assert.Single(DatabaseUtils.Database).Path);
	}
}
