// /*
//     Copyright (C) 2026 0x90d
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

using System.Buffers.Binary;
using HEI.Core.Utils;
using MemoryPack;

namespace HEI.Core.Tests.Utils;

/// <summary>
/// A save appends the entries that changed to the database's journal, after its terminator, instead of writing the
/// whole database; the journal is folded in once it would reach a fifth of it. Builds before the journal stop at the
/// terminator, so the bytes up to it stay the database as last written whole.
/// </summary>
[Collection("DatabaseUtils")] // DatabaseUtils is static
public class DatabaseJournalTests : IDisposable {
	readonly string dir;

	public DatabaseJournalTests() {
		dir = Directory.CreateTempSubdirectory("hei-journal-test").FullName;
		DatabaseUtils.CustomDatabaseFolder = dir;
		DatabaseUtils.InvalidateDatabaseFolder();
		DatabaseUtils.Database.Clear();
		Assert.True(DatabaseUtils.LoadDatabase()); // no file: forgets what another test's database left behind
	}

	public void Dispose() {
		DatabaseUtils.Database.Clear();
		DatabaseUtils.CustomDatabaseFolder = null;
		DatabaseUtils.InvalidateDatabaseFolder();
		try { Directory.Delete(dir, true); } catch { /* best effort */ }
	}

	string DbPath => Path.Combine(dir, "ScannedFiles.db");

	static FileEntry Photo(int i, int seed = 0) {
		var entry = new FileEntry {
			_Path = $@"P:\Pictures\{i / 1000:D3}\IMG_{i:D6}.jpg",
			Folder = $@"P:\Pictures\{i / 1000:D3}",
			FileSize = 2_000_000 + i,
			DateModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
		};
		var gray = new byte[32 * 32];
		new Random(i * 31 + seed).NextBytes(gray);
		entry.grayBytes[0] = gray;
		entry.PHashes[0] = (ulong)(i * 7919 + seed);
		return entry;
	}

	static void AddPhotos(int from, int count) {
		for (int i = from; i < from + count; i++)
			DatabaseUtils.Database.Add(Photo(i));
	}

	/// <summary>Every entry, serialized, by path: what a load must give back.</summary>
	static Dictionary<string, string> Snapshot() =>
		DatabaseUtils.Database.ToDictionary(e => e.Path, e => Convert.ToBase64String(MemoryPackSerializer.Serialize(e)), StringComparer.OrdinalIgnoreCase);

	static Dictionary<string, string> Reloaded() {
		DatabaseUtils.Database.Clear();
		Assert.True(DatabaseUtils.LoadDatabase());
		return Snapshot();
	}

	/// <summary>Where the whole database ends: just past its terminator (what builds before the journal read).</summary>
	static long WholeEnd(byte[] file) {
		int at = 16;
		while (true) {
			int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at));
			at += 4;
			if (length == -1) return at;
			at += length;
		}
	}

	[Fact]
	public void ChangedEntries_AreAppended_TheWholeDatabaseStaysAsWritten() {
		AddPhotos(0, 500);
		DatabaseUtils.SaveDatabase();
		byte[] whole = File.ReadAllBytes(DbPath);
		Assert.True(DatabaseUtils.LoadDatabase());

		// One new photo, one re-hashed, one gone.
		DatabaseUtils.Database.Add(Photo(500));
		var changed = DatabaseUtils.Database.First(e => e.Path.EndsWith("IMG_000007.jpg"));
		changed.PHashes[0] = 42;
		DatabaseUtils.Database.RemoveWhere(e => e.Path.EndsWith("IMG_000009.jpg"));
		var expected = Snapshot();
		DatabaseUtils.SaveDatabase();

		byte[] after = File.ReadAllBytes(DbPath);
		Assert.Equal(whole, after.AsSpan(0, whole.Length).ToArray());
		long appended = after.Length - whole.Length;
		Assert.InRange(appended, 1, 2 * 1500); // two entries and a path, not 500 entries
		Assert.Equal(whole.Length, WholeEnd(after));

		Assert.Equal(expected, Reloaded());
	}

	[Fact]
	public void NothingChanged_WritesNothing_AfterAppends() {
		AddPhotos(0, 200);
		DatabaseUtils.SaveDatabase();
		DatabaseUtils.Database.Add(Photo(200));
		DatabaseUtils.SaveDatabase();
		long length = new FileInfo(DbPath).Length;
		DateTime written = File.GetLastWriteTimeUtc(DbPath);

		DatabaseUtils.SaveDatabase();
		Reloaded();
		DatabaseUtils.SaveDatabase();

		Assert.Equal(length, new FileInfo(DbPath).Length);
		Assert.Equal(written, File.GetLastWriteTimeUtc(DbPath));
	}

	[Fact]
	public void Journal_IsFoldedIn_OnceItWouldReachAFifthOfTheDatabase() {
		AddPhotos(0, 1000);
		DatabaseUtils.SaveDatabase();
		long whole = new FileInfo(DbPath).Length;
		int next = 1000;
		bool folded = false;
		for (int round = 0; round < 40 && !folded; round++) {
			AddPhotos(next, 25);
			next += 25;
			DatabaseUtils.SaveDatabase();
			byte[] file = File.ReadAllBytes(DbPath);
			long journal = file.Length - WholeEnd(file);
			if (journal == 0)
				folded = true;
			else
				Assert.True(journal <= whole * DatabaseUtils.FoldShare, $"journal {journal:N0} past a fifth of {whole:N0}");
		}
		Assert.True(folded, "never folded");
		Assert.Equal(next, Reloaded().Count);
	}

	[Fact]
	public void ManyGone_FoldsRatherThanJournalingThem() {
		AddPhotos(0, 300);
		DatabaseUtils.SaveDatabase();
		DatabaseUtils.ClearDatabase();
		byte[] file = File.ReadAllBytes(DbPath);
		Assert.Equal(file.Length, WholeEnd(file));
		Assert.Empty(Reloaded());
	}

	[Fact]
	public void HeaderChange_IsJournaled() {
		AddPhotos(0, 300);
		DatabaseUtils.SaveDatabase();
		Assert.True(DatabaseUtils.LoadDatabase());
		DatabaseUtils.Create16x16Database(); // Version = 1
		byte[] file = File.ReadAllBytes(DbPath);
		Assert.True(file.Length > WholeEnd(file));
		Reloaded();
		Assert.Equal(1, DatabaseUtils.DbVersion);
	}

	/// <summary>
	/// A process killed while appending, at every byte of the append: the load gives what the save before had, with
	/// at most some of this save's entries (each one whole), and the next save cuts the record short off before appending.
	/// </summary>
	[Fact]
	public void AppendCutShort_AtAnyByte_LeavesTheSaveBefore_AndTheNextSaveCutsItOff() {
		AddPhotos(0, 300);
		DatabaseUtils.SaveDatabase();
		DatabaseUtils.Database.Add(Photo(300));
		DatabaseUtils.SaveDatabase(); // a first append, so the cut falls in a journal with records before it
		var before = Snapshot();
		byte[] previous = File.ReadAllBytes(DbPath);

		DatabaseUtils.Database.Add(Photo(301));
		DatabaseUtils.Database.First(e => e.Path.EndsWith("IMG_000003.jpg")).PHashes[0] = 3;
		var after = Snapshot();
		DatabaseUtils.SaveDatabase();
		byte[] full = File.ReadAllBytes(DbPath);
		Assert.True(full.Length > previous.Length);

		for (int cut = previous.Length; cut < full.Length; cut++) {
			File.WriteAllBytes(DbPath, full.AsSpan(0, cut).ToArray());
			var loaded = Reloaded();
			Assert.Superset(new HashSet<string>(loaded.Keys, StringComparer.OrdinalIgnoreCase), new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase));
			foreach ((string path, string entry) in loaded)
				Assert.True(before.GetValueOrDefault(path) == entry || after.GetValueOrDefault(path) == entry, $"cut at {cut}: {path} is neither as saved before nor after");
		}
		File.WriteAllBytes(DbPath, full.AsSpan(0, previous.Length + 1).ToArray());
		Assert.Equal(before, Reloaded());

		// Cut inside its last record, then saved again with less: the record cut short is gone, every change is
		// there, and the file ends at its last record.
		File.WriteAllBytes(DbPath, full.AsSpan(0, full.Length - 3).ToArray());
		DatabaseUtils.Database.Clear();
		Assert.True(DatabaseUtils.LoadDatabase());
		DatabaseUtils.Database.RemoveWhere(e => e.Path.EndsWith("IMG_000004.jpg"));
		var expected = Snapshot();
		DatabaseUtils.SaveDatabase();
		Assert.Equal(expected, Reloaded());
		Assert.Equal(new FileInfo(DbPath).Length, JournalEnd(File.ReadAllBytes(DbPath)));
	}

	/// <summary>Just past the journal's last record that checks: the file's length, when nothing was cut short.</summary>
	static long JournalEnd(byte[] file) {
		int at = (int)WholeEnd(file);
		if (file.Length - at < 8) return at;
		at += 8;
		while (file.Length - at >= 13) {
			int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 1));
			if (length < 0 || length > file.Length - at - 13) break;
			byte[] hash = System.Security.Cryptography.SHA256.HashData(file.AsSpan(at, 5 + length));
			if (!hash.AsSpan(0, 8).SequenceEqual(file.AsSpan(at + 5 + length, 8))) break;
			at += 13 + length;
		}
		return at;
	}

	/// <summary>
	/// A record whose bytes aren't what was written (a power cut can leave a file's new end unwritten) ends the
	/// journal, however well it would deserialize.
	/// </summary>
	[Fact]
	public void RecordThatDoesntCheck_EndsTheJournal() {
		AddPhotos(0, 300);
		DatabaseUtils.SaveDatabase();
		var before = Snapshot();
		DatabaseUtils.Database.First(e => e.Path.EndsWith("IMG_000003.jpg")).grayBytes[0]![100] ^= 0xFF;
		DatabaseUtils.SaveDatabase();
		byte[] file = File.ReadAllBytes(DbPath);
		file[^(8 + 300)] ^= 0x01; // a gray byte of the journaled entry, well inside its payload
		File.WriteAllBytes(DbPath, file);
		Assert.Equal(before, Reloaded());
	}

	/// <summary>A save left halfway (the temp file, whole) is read instead of the database, and the database's journal isn't read over it.</summary>
	[Fact]
	public void TempFileLeftWhole_IsReadWithoutTheJournal() {
		AddPhotos(0, 300);
		DatabaseUtils.SaveDatabase();
		File.Copy(DbPath, Path.Combine(dir, "ScannedFiles_new.db"));
		DatabaseUtils.Database.Add(Photo(300));
		DatabaseUtils.SaveDatabase(); // appended to ScannedFiles.db only

		var loaded = Reloaded();
		Assert.Equal(300, loaded.Count);
		// And it's made the database by the next save, written whole.
		DatabaseUtils.SaveDatabase();
		Assert.False(File.Exists(Path.Combine(dir, "ScannedFiles_new.db")));
		byte[] file = File.ReadAllBytes(DbPath);
		Assert.Equal(file.Length, WholeEnd(file));
	}

	/// <summary>A file whose stamp matches but whose end isn't what this process left there is written over, never appended to.</summary>
	[Fact]
	public void SameStamp_OtherBytes_IsWrittenWhole() {
		AddPhotos(0, 300);
		DatabaseUtils.SaveDatabase();
		byte[] file = File.ReadAllBytes(DbPath);
		DateTime written = File.GetLastWriteTimeUtc(DbPath);
		file[^6] ^= 0x5A; // inside the last entry, before the terminator
		File.WriteAllBytes(DbPath, file);
		File.SetLastWriteTimeUtc(DbPath, written);

		DatabaseUtils.Database.Add(Photo(300));
		var expected = Snapshot();
		DatabaseUtils.SaveDatabase();
		byte[] now = File.ReadAllBytes(DbPath);
		Assert.Equal(now.Length, WholeEnd(now));
		Assert.Equal(expected, Reloaded());
	}

	/// <summary>
	/// What a build before the journal reads: the bytes up to the terminator, a database of the format it reads
	/// (the same writer). It gets the database as last written whole, and its own whole save drops the journal.
	/// </summary>
	[Fact]
	public void BuildsBeforeTheJournal_ReadTheDatabaseAsLastWrittenWhole() {
		AddPhotos(0, 300);
		var whole = Snapshot();
		DatabaseUtils.SaveDatabase();
		DatabaseUtils.Database.Add(Photo(300));
		DatabaseUtils.SaveDatabase();
		byte[] file = File.ReadAllBytes(DbPath);
		long end = WholeEnd(file);
		Assert.True(file.Length > end);

		// The old reader's view: the file cut at its terminator.
		File.WriteAllBytes(DbPath, file.AsSpan(0, (int)end).ToArray());
		Assert.Equal(whole, Reloaded());
	}

	/// <summary>The update: a database from today's builds (written whole, no journal) loads with every entry, and is appended to.</summary>
	[Fact]
	public void TodaysDatabase_LoadsWhole_AndIsAppendedTo() {
		AddPhotos(0, 400);
		var expected = Snapshot();
		DatabaseUtils.SaveDatabase();
		byte[] todays = File.ReadAllBytes(DbPath);
		Assert.Equal(todays.Length, WholeEnd(todays)); // what today's builds write
		Assert.Equal(expected, Reloaded());

		DatabaseUtils.Database.Add(Photo(400));
		DatabaseUtils.SaveDatabase();
		byte[] file = File.ReadAllBytes(DbPath);
		Assert.Equal(todays, file.AsSpan(0, todays.Length).ToArray());
		Assert.Equal(401, Reloaded().Count);
	}

	[Fact]
	public void PathChangedOnlyInCase_IsTheEntrysOwn() {
		if (!CoreUtils.IsWindows) return;
		AddPhotos(0, 300);
		DatabaseUtils.SaveDatabase();
		var entry = DatabaseUtils.Database.First(e => e.Path.EndsWith("IMG_000005.jpg"));
		DatabaseUtils.UpdateFilePath(entry.Path.Replace("IMG_", "img_"), entry);
		DatabaseUtils.SaveDatabase();
		var loaded = Reloaded();
		Assert.Equal(300, loaded.Count);
		Assert.Contains(DatabaseUtils.Database, e => e.Path.EndsWith("img_000005.jpg", StringComparison.Ordinal));
	}
}
