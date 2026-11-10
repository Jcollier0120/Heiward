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

/// <summary>
/// A drive's listing (listing\&lt;id&gt;.tsv) is saved as what changed, appended after it, and written whole only now and
/// then, and at a clean stop: the listing read back is always exactly the last one saved, a save cut short leaves the one
/// before, and a build from before the journal reads the listing as it is or, with a journal, as missing (it walks).
/// </summary>
[Collection(AgentHomeCollection.Name)]
public sealed class ListingJournalTests : IDisposable {
	readonly string home = Path.Combine(Path.GetTempPath(), "hei-listing-" + Guid.NewGuid().ToString("N"));
	readonly string? homeBefore = Environment.GetEnvironmentVariable("HEIWARD_HOME");
	const string File1 = "abcdef123456.tsv";
	static readonly DateTime When = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

	public ListingJournalTests() {
		Directory.CreateDirectory(home);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", homeBefore);
		try { Directory.Delete(home, true); } catch { }
	}

	string PathOf(string file = File1) => Path.Combine(StoredListing.Folder, file);

	/// <summary>A made-up library: <paramref name="n"/> photos, a hundred a folder.</summary>
	static List<(string Path, long Size, DateTime ModifiedUtc)> Library(int n) =>
		Enumerable.Range(0, n).Select(i => ($@"D:\Photos\{i / 100:D4}\IMG_{i:D6}.jpg", 1_000_000L + i, When.AddMinutes(i))).ToList();

	/// <summary>1% of the files edited, a third as many gone and as many new, and a name's case changed.</summary>
	static List<(string Path, long Size, DateTime ModifiedUtc)> Changed(List<(string Path, long Size, DateTime ModifiedUtc)> files, int round) {
		var now = files.ToList();
		int count = Math.Max(1, now.Count / 100), step = Math.Max(1, now.Count / count);
		for (int k = 0, i = round % step; k < count && i < now.Count; k++, i += step)
			now[i] = (now[i].Path, now[i].Size + 1, now[i].ModifiedUtc.AddSeconds(round + 1));
		for (int k = 0; k < count / 3 + 1 && now.Count > 1; k++) now.RemoveAt((round * 7 + k * step) % now.Count);
		for (int i = 0; i < count / 3 + 1; i++) now.Add(($@"D:\Photos\new{round}\IMG_{i:D6}.jpg", 2_000 + i, When.AddDays(round)));
		if (now.Count > 3) now[3] = (now[3].Path.ToUpperInvariant(), now[3].Size, now[3].ModifiedUtc);
		return now;
	}

	/// <summary>The listing as read back: each file, sorted.</summary>
	static List<string> Read(StoredListing? listing) {
		Assert.NotNull(listing);
		return listing.PathsOutside(new HashSet<string>()).Order(StringComparer.Ordinal).ToList();
	}

	static List<string> Expected(IEnumerable<(string Path, long Size, DateTime ModifiedUtc)> files) =>
		files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

	/// <summary>Every file's size and time as read back, for files listed (the folders' FileInfo can't be made up).</summary>
	static void SameFacts(string path, IEnumerable<(string Path, long Size, DateTime ModifiedUtc)> files) {
		var old = OldLoad(path) ?? Fold(path);
		Assert.Equal(files.ToDictionary(f => f.Path, f => (f.Size, f.ModifiedUtc), StringComparer.Ordinal), old);
	}

	/// <summary>
	/// v1.10.1's StoredListing.Load, as it is in that release (JournalListing.cs, unchanged since 1.9.59): each line split
	/// in three, size and ticks numbers, else the whole listing is missing. Returns each file's size and time, or null.
	/// </summary>
	static Dictionary<string, (long, DateTime)>? OldLoad(string path) {
		if (!File.Exists(path)) return null;
		var files = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
		foreach (string line in File.ReadLines(path)) {
			string[] parts = line.Split('\t', 3);
			if (parts.Length != 3 || !long.TryParse(parts[0], out long size) || !long.TryParse(parts[1], out long ticks)) return null;
			files.Add(parts[2], (size, new DateTime(ticks, DateTimeKind.Utc))); // a path twice would throw in its Same()
		}
		return files;
	}

	/// <summary>The listing with its journal folded in, read as v1.10.1 reads it.</summary>
	static Dictionary<string, (long, DateTime)> Fold(string path) {
		Assert.True(StoredListing.FoldAll() > 0);
		return OldLoad(path) ?? throw new InvalidOperationException("v1.10.1 can't read the folded listing");
	}

	[Fact]
	public void TheFirstSave_IsTheListingWhole_AsEveryBuildWritesIt() {
		var files = Library(250);
		StoredListing.Save(File1, files);
		string expected = string.Concat(files.Select(f => $"{f.Size}\t{f.ModifiedUtc.Ticks}\t{f.Path}\n"));
		Assert.Equal(expected, File.ReadAllText(PathOf()));
		Assert.Equal(Expected(files), Read(StoredListing.Load(File1)));
		SameFacts(PathOf(), files);
	}

	[Fact]
	public void ASaveWithAFewChanges_AppendsOnlyThose_AndReadsBackExactly() {
		var files = Library(10_000);
		StoredListing.Save(File1, files);
		long whole = new FileInfo(PathOf()).Length;
		byte[] before = File.ReadAllBytes(PathOf());
		var now = Changed(files, 0);
		StoredListing.Save(File1, now);
		byte[] after = File.ReadAllBytes(PathOf());
		// What was there stays as it was: the save only added after it, a few percent of the listing.
		Assert.True(after.AsSpan(0, before.Length).SequenceEqual(before));
		Assert.InRange(after.Length - before.Length, 1, whole / 20);
		Assert.Equal(Expected(now), Read(StoredListing.Load(File1)));
		Assert.Null(OldLoad(PathOf())); // v1.10.1: missing, so it walks the drive
		SameFacts(PathOf(), now);
	}

	[Fact]
	public void ASaveOfTheSameFiles_WritesNothing() {
		var files = Library(500);
		StoredListing.Save(File1, files);
		StoredListing.Save(File1, Changed(files, 0));
		var stamp = File.GetLastWriteTimeUtc(PathOf());
		byte[] bytes = File.ReadAllBytes(PathOf());
		File.SetLastWriteTimeUtc(PathOf(), stamp.AddHours(-1));
		StoredListing.Save(File1, Changed(files, 0).AsEnumerable().Reverse());
		Assert.Equal(bytes, File.ReadAllBytes(PathOf()));
		Assert.Equal(stamp.AddHours(-1), File.GetLastWriteTimeUtc(PathOf()));
	}

	[Fact]
	public void ManySaves_ReadBackTheLast_AndTheJournalIsFoldedIn_OnceItsAsBigAsTheListing() {
		var files = Library(2_000);
		StoredListing.Save(File1, files);
		long whole = new FileInfo(PathOf()).Length;
		int folds = 0;
		for (int round = 0; round < 120; round++) {
			files = Changed(files, round);
			long was = new FileInfo(PathOf()).Length;
			StoredListing.Save(File1, files);
			long length = new FileInfo(PathOf()).Length;
			if (length < was) folds++;
			Assert.Equal(Expected(files), Read(StoredListing.Load(File1)));
			// Never past twice the listing: a load reads at most that.
			Assert.True(length <= 2 * whole + 4096, $"round {round}: {length:N0} bytes for a listing of {whole:N0}");
		}
		Assert.InRange(folds, 1, 20);
		SameFacts(PathOf(), files);
	}

	[Fact]
	public void ASaveCutShort_AtAnyByte_LeavesTheListingBefore_AndTheNextSaveAppendsAfterIt() {
		var files = Library(300);
		StoredListing.Save(File1, files);
		var first = Changed(files, 0);
		StoredListing.Save(File1, first); // the mark and a batch
		byte[] good = File.ReadAllBytes(PathOf());
		var second = Changed(first, 1);
		StoredListing.Save(File1, second);
		byte[] full = File.ReadAllBytes(PathOf());
		var third = Changed(second, 2);
		for (int cut = good.Length; cut < full.Length; cut++) {
			File.WriteAllBytes(PathOf(), full.AsSpan(0, cut).ToArray());
			Assert.Equal(Expected(first), Read(StoredListing.Load(File1)));
			StoredListing.Save(File1, third);
			Assert.Equal(Expected(third), Read(StoredListing.Load(File1)));
		}
		// The first batch cut short, the mark with it: the listing as first saved.
		int listingEnd = System.Text.Encoding.UTF8.GetByteCount(string.Concat(files.Select(f => $"{f.Size}\t{f.ModifiedUtc.Ticks}\t{f.Path}\n")));
		for (int cut = listingEnd; cut < good.Length; cut++) {
			File.WriteAllBytes(PathOf(), good.AsSpan(0, cut).ToArray());
			Assert.Equal(Expected(files), Read(StoredListing.Load(File1)));
		}
	}

	[Fact]
	public void ABatchThatDoesntCheck_IsLeftOut_WithWhatComesAfterIt() {
		var files = Library(300);
		StoredListing.Save(File1, files);
		var first = Changed(files, 0);
		StoredListing.Save(File1, first);
		int firstEnd = (int)new FileInfo(PathOf()).Length;
		StoredListing.Save(File1, Changed(first, 1));
		byte[] bytes = File.ReadAllBytes(PathOf());
		int flip = firstEnd + 5; // inside the second batch's first line: its digits still parse
		bytes[flip] = bytes[flip] == (byte)'1' ? (byte)'2' : (byte)'1';
		File.WriteAllBytes(PathOf(), bytes);
		Assert.Equal(Expected(first), Read(StoredListing.Load(File1)));
	}

	[Fact]
	public void AListingFromV1_10_1_IsReadAsItIs_AndAppendedTo() {
		var files = Library(400);
		Directory.CreateDirectory(StoredListing.Folder);
		// As v1.10.1's Save wrote it.
		File.WriteAllText(PathOf(), string.Concat(files.Select(f => $"{f.Size}\t{f.ModifiedUtc.Ticks}\t{f.Path}\n")));
		Assert.Equal(Expected(files), Read(StoredListing.Load(File1)));
		var now = Changed(files, 0);
		StoredListing.Save(File1, now);
		Assert.Equal(Expected(now), Read(StoredListing.Load(File1)));
	}

	[Fact]
	public void TheFold_LeavesAListingV1_10_1_ReadsAsItIs_AndNothingWithoutAJournal() {
		var files = Library(1_000);
		StoredListing.Save(File1, files);
		StoredListing.Save("second000000.tsv", files);
		Assert.Equal(0, StoredListing.FoldAll()); // no journal: nothing written
		files = Changed(files, 0);
		StoredListing.Save(File1, files);
		Assert.Null(OldLoad(PathOf()));
		var lines = new List<string>();
		Assert.Equal(1, StoredListing.FoldAll(lines.Add));
		Assert.Equal(files.ToDictionary(f => f.Path, f => (f.Size, f.ModifiedUtc), StringComparer.Ordinal), OldLoad(PathOf()));
		Assert.Contains("1 journal(s) folded in", Assert.Single(lines));
		// And it reads back here as before, and takes the next save's journal.
		Assert.Equal(Expected(files), Read(StoredListing.Load(File1)));
		StoredListing.Save(File1, Changed(files, 1));
		Assert.Equal(Expected(Changed(files, 1)), Read(StoredListing.Load(File1)));
	}

	[Fact]
	public void TheFold_WaitsForARunningScan() {
		var files = Library(500);
		StoredListing.Save(File1, files);
		StoredListing.Save(File1, Changed(files, 0));
		var lines = new List<string>();
		using (FileStream? scan = AgentScanner.TryLock()) {
			Assert.NotNull(scan);
			Assert.Equal(0, StoredListing.FoldAll(lines.Add));
		}
		Assert.Contains("a scan is running", Assert.Single(lines));
		Assert.Null(OldLoad(PathOf()));
		Assert.Equal(1, StoredListing.FoldAll());
	}

	[Fact]
	public void FoldersAndFilesComeBackGroupedAsBefore() {
		var files = Library(1_000);
		StoredListing.Save(File1, files);
		var now = Changed(files, 0);
		StoredListing.Save(File1, now);
		StoredListing listing = StoredListing.Load(File1)!;
		var ci = StringComparer.OrdinalIgnoreCase;
		List<string> all = listing.FoldersUnder(@"D:\");
		Assert.Equal(now.Select(f => Path.GetDirectoryName(f.Path)!).Distinct(ci).Order(ci), all.Order(ci));
		foreach (string folder in all) {
			// Every other folder listed again: what's left is this folder's files.
			var others = all.Where(x => !ci.Equals(x, folder)).ToHashSet(ci);
			Assert.Equal(now.Where(f => ci.Equals(Path.GetDirectoryName(f.Path), folder)).Select(f => f.Path).Order(StringComparer.Ordinal),
				listing.PathsOutside(others).Order(StringComparer.Ordinal));
		}
		Assert.Contains(@"D:\Photos\new0", listing.FoldersUnder(@"D:\Photos"));
		Assert.DoesNotContain(@"D:\Photos\new1", listing.FoldersUnder(@"D:\Photos"));
	}
}
