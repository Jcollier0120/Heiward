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

using System.Text.Json;
using HEI.Core;
using HEI.Core.Utils;

namespace HEI.Agent.Tests;

/// <summary>
/// Scans plan their listing from the drive's change journal: the real one, on the drive the temp
/// folder really is on (NTFS on any Windows PC), or where that drive keeps none (a Dev Drive, whose
/// journal is off), the local app data's Temp. The tests let themselves off where neither has one.
/// </summary>
[Collection(AgentHomeCollection.Name)]
public sealed class JournalListingTests : IDisposable {
	readonly string dir = Path.Combine(JournaledTemp(), "hei-journal-" + Guid.NewGuid().ToString("N"));
	readonly string photos, home;
	readonly string? homeBefore = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public JournalListingTests() {
		photos = Path.Combine(dir, "Photos");
		home = Path.Combine(dir, "home");
		Directory.CreateDirectory(photos);
		Directory.CreateDirectory(home);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", homeBefore);
		foreach (string d in Directory.EnumerateDirectories(dir).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0))
			Directory.Delete(d); // a junction alone, never what it points to
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>The temp folder by its real path, or the local app data's Temp when the temp folder's drive keeps no journal.</summary>
	static string JournaledTemp() {
		string temp = RealPath.Of(Path.GetTempPath());
		if (HasJournal(temp)) return temp;
		string local = RealPath.Of(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"));
		return Directory.Exists(local) && HasJournal(local) ? local : temp;
	}

	Settings Settings(string? root = null) {
		var s = new Settings { IncludeImages = true, IncludeSubDirectories = true, SkipCloudPlaceholders = true, SkipFolderLinks = true, ListingProvesExistence = true };
		s.IncludeList.Add(root ?? photos);
		foreach (string marker in ScanScope.RepositoryMarkers) s.SkipFoldersContaining.Add(marker);
		return s;
	}

	AgentConfig Config() => new() { ScanAllDrives = false, Folders = new() { photos } };

	string Photo(string name, int size = 64) {
		string path = Path.Combine(photos, name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, new byte[size]);
		return path;
	}

	ListingPlan Plan(DateTime? nowUtc = null, Settings? settings = null) =>
		ListingPlan.Make(settings ?? Settings(), Config(), nowUtc ?? DateTime.UtcNow, CancellationToken.None);

	/// <summary>After a scan: what it listed, as the engine would walk it (by the scanned folder as written: <paramref name="root"/>).</summary>
	void Scanned(ListingPlan plan, DateTime? nowUtc = null, string? root = null) {
		Settings s = Settings(root);
		var found = FileUtils.GetFilesRecursive(root ?? photos, s.IgnoreReadOnlyFolders, s.IgnoreReparsePoints, recursive: true, s.IncludeImages,
			s.BlackList.Concat(s.SubfolderBlackList).ToList(), CancellationToken.None, s.SkipCloudPlaceholders, s.ExcludedExtensions, s.SkipFoldersContaining,
			s.SkipFolderLinks).Select(f => (f.FullName, f.Length)).ToList();
		plan.Save(found, p => File.GetLastWriteTimeUtc(p), nowUtc ?? DateTime.UtcNow);
	}

	/// <summary>What a plan hands the engine for the scanned folder: the files listed again, and those it vouches for.</summary>
	static void AssertHanded(RootPlan r, string[] listed, string[] unchanged) {
		Assert.Equal(listed.Order(StringComparer.OrdinalIgnoreCase), r.Listing!.Listed.Select(f => f.FullName).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
		Assert.Equal(unchanged.Order(StringComparer.OrdinalIgnoreCase), r.Listing.Unchanged.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
	}

	static bool HasJournal(string path) {
		using VolumeJournal? journal = VolumeJournal.Open(path);
		return journal != null;
	}

	[Fact]
	public void TheFirstScanWalks_ThenOnlyWhatChangedIsListed() {
		if (!HasJournal(dir)) return;
		Photo("a.png");
		Photo("b.jpg");

		ListingPlan first = Plan();
		Assert.Equal(ListingMode.Walk, Assert.Single(first.Roots).Mode);
		Scanned(first);

		// Nothing since: nothing is read, and a scheduled scan has nothing to do.
		ListingPlan quiet = Plan();
		RootPlan r = Assert.Single(quiet.Roots);
		Assert.Equal(ListingMode.Unchanged, r.Mode);
		Assert.True(quiet.NothingChanged);
		Assert.Empty(r.Listing!.Listed);
		Assert.Equal(2, r.Listing.Unchanged.Count);

		// A document saved next to the photos: its folder is listed again, and it's the same photos.
		File.WriteAllText(Path.Combine(photos, "notes.txt"), "not a photo");
		Assert.Equal(ListingMode.Unchanged, Assert.Single(Plan().Roots).Mode);

		// A photo added: that folder is listed again, the rest isn't.
		string c = Photo("c.png", 32);
		r = Assert.Single(Plan().Roots);
		Assert.Equal(ListingMode.Changed, r.Mode);
		Assert.Equal(1, r.ChangedFolders);
		Assert.Contains(r.Listing!.Listed, f => f.FullName == c);
	}

	[Fact]
	public void AFolderAdded_WhereScansLook_IsListedWithWhatsBelowIt_AndNothingElse() {
		if (!HasJournal(dir)) return;
		string a = Photo("a.png");
		Scanned(Plan());
		string b = Photo(@"Trip\b.jpg"), c = Photo(@"Trip\Day 2\c.jpg");
		RootPlan r = Assert.Single(Plan().Roots);
		Assert.Equal(ListingMode.Changed, r.Mode);
		AssertHanded(r, new[] { c, b }, new[] { a });
	}

	[Fact]
	public void AnEmptyFolderAdded_ChangesNothing() {
		if (!HasJournal(dir)) return;
		Photo("a.png");
		Scanned(Plan());
		Directory.CreateDirectory(Path.Combine(photos, "New folder"));
		ListingPlan plan = Plan();
		Assert.Equal(ListingMode.Unchanged, Assert.Single(plan.Roots).Mode);
		Assert.True(plan.NothingChanged); // a scheduled scan doesn't run
	}

	[Fact]
	public void AFolderDeleted_TakesWhatWasListedInIt_AndNothingElse() {
		if (!HasJournal(dir)) return;
		string a = Photo("a.png");
		Photo(@"Trip\b.jpg");
		Photo(@"Trip\Day 2\c.jpg");
		Scanned(Plan());
		Directory.Delete(Path.Combine(photos, "Trip"), true);
		RootPlan r = Assert.Single(Plan().Roots);
		Assert.Equal(ListingMode.Changed, r.Mode);
		AssertHanded(r, Array.Empty<string>(), new[] { a });
	}

	[Fact]
	public void AFolderRenamed_IsListedByItsNewName() {
		if (!HasJournal(dir)) return;
		string a = Photo("a.png");
		Photo(@"Trip\b.jpg");
		Scanned(Plan());
		Directory.Move(Path.Combine(photos, "Trip"), Path.Combine(photos, "Holiday"));
		RootPlan r = Assert.Single(Plan().Roots);
		Assert.Equal(ListingMode.Changed, r.Mode);
		AssertHanded(r, new[] { Path.Combine(photos, "Holiday", "b.jpg") }, new[] { a });
	}

	[Fact]
	public void AFolderMovedOut_OrMadeARepository_TakesWhatWasListedInIt() {
		if (!HasJournal(dir)) return;
		string a = Photo("a.png");
		Photo(@"Trip\b.jpg");
		Photo(@"Art\c.png");
		Scanned(Plan());
		Directory.Move(Path.Combine(photos, "Trip"), Path.Combine(dir, "Trip"));
		Directory.CreateDirectory(Path.Combine(photos, "Art", ".git"));
		RootPlan r = Assert.Single(Plan().Roots);
		Assert.Equal(ListingMode.Changed, r.Mode);
		AssertHanded(r, Array.Empty<string>(), new[] { a });
	}

	[Fact]
	public void AFolderAdded_WhereScansDontLook_ChangesNothing() {
		if (!HasJournal(dir)) return;
		Photo("a.png");
		Scanned(Plan());
		// A code repository: scans leave it out, whatever happens in it.
		Directory.CreateDirectory(Path.Combine(photos, "project", ".git"));
		File.WriteAllBytes(Path.Combine(photos, "project", "icon.png"), new byte[16]);
		Assert.Equal(ListingMode.Unchanged, Assert.Single(Plan().Roots).Mode); // new where scans look, but a repository
		Scanned(Plan());
		File.WriteAllBytes(Path.Combine(photos, "project", "logo.png"), new byte[16]);
		Assert.Equal(ListingMode.Unchanged, Assert.Single(Plan().Roots).Mode);
	}

	/// <summary>
	/// A scanned folder written through a junction, as C:\Projects is the drive D:\ on a PC with Dev Drives: the journal is
	/// its real drive's, and names each change by its real path, which the plan places in the folder as the scan walks it.
	/// </summary>
	[Fact]
	public void AFolderScannedThroughAJunction_SeesWhatChangedInIt() {
		if (!HasJournal(dir)) return;
		string link = RealTemp.Junction(Path.Combine(dir, "link"), photos);
		Photo("a.png");
		ListingPlan Planned() => ListingPlan.Make(Settings(link), Config(), DateTime.UtcNow, CancellationToken.None);

		ListingPlan first = Planned();
		Assert.Equal(ListingMode.Walk, Assert.Single(first.Roots).Mode);
		Scanned(first, root: link);
		Assert.Equal(ListingMode.Unchanged, Assert.Single(Planned().Roots).Mode);

		// A photo added in the real folder: the folder is listed again, by the path the scan knows it by.
		Photo("b.png", 32);
		RootPlan r = Assert.Single(Planned().Roots);
		Assert.Equal(ListingMode.Changed, r.Mode);
		Assert.Equal(1, r.ChangedFolders);
		Assert.Equal(new[] { Path.Combine(link, "a.png"), Path.Combine(link, "b.png") },
			r.Listing!.Listed.Select(f => f.FullName).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

		// A folder added in it: listed, by the path the scan knows it by.
		Scanned(Planned(), root: link);
		Photo(@"Trip\c.jpg");
		r = Assert.Single(Planned().Roots);
		Assert.Equal(ListingMode.Changed, r.Mode);
		Assert.Equal(new[] { Path.Combine(link, "Trip", "c.jpg") }, r.Listing!.Listed.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
	}

	string Index => Path.Combine(home, "listing", "index.json");

	/// <summary>The scanned folder's entry in listing\index.json.</summary>
	JsonElement Kept() => JsonDocument.Parse(File.ReadAllText(Index)).RootElement.GetProperty("roots").GetProperty(photos).Clone();

	static bool HasPending(JsonElement kept) => kept.TryGetProperty("pending", out JsonElement p) && p.ValueKind != JsonValueKind.Null;

	static string[] PendingIn(JsonElement kept, string set) =>
		kept.GetProperty("pending").GetProperty(set).EnumerateArray().Select(e => e.GetString()!).ToArray();

	/// <summary>The page's look, at once: a test's few changes never put the journal at risk.</summary>
	List<string> Keep(Func<bool>? scanRunning = null) =>
		ListingPlan.Keep(Config(), DateTime.UtcNow, scanRunning ?? (() => false), CancellationToken.None, atRisk: 0);

	[Fact]
	public void ThePageLeavesAJournalNotAtRisk_ForTheScan() {
		if (!HasJournal(dir)) return;
		using (VolumeJournal? journal = VolumeJournal.Open(dir))
			if (journal!.MaximumSize < 1 << 20) return; // a journal so small a test's changes could fill a quarter of it
		Photo("a.png");
		Scanned(Plan());
		Photo("b.png", 32);
		string before = File.ReadAllText(Index);
		Assert.Empty(ListingPlan.Keep(Config(), DateTime.UtcNow, () => false, CancellationToken.None));
		Assert.Equal(before, File.ReadAllText(Index));
	}

	[Fact]
	public void ThePageKeepsTheJournalsPlace_AndTheNextScanListsWhatItPlaced() {
		if (!HasJournal(dir)) return;
		string a = Photo("a.png");
		Scanned(Plan());
		long usn = Kept().GetProperty("usn").GetInt64();
		string b = Photo("b.png", 32), c = Photo(@"Trip\c.jpg");

		Assert.Empty(Keep());
		JsonElement kept = Kept();
		Assert.True(kept.GetProperty("usn").GetInt64() > usn);
		Assert.Contains(photos, PendingIn(kept, "dirty"), StringComparer.OrdinalIgnoreCase);
		Assert.Contains(Path.Combine(photos, "Trip"), PendingIn(kept, "subtrees"), StringComparer.OrdinalIgnoreCase);

		// The scan reads the journal from the page's look on, and lists what the page placed with what came after.
		string d = Photo(@"Trip\d.jpg", 48);
		ListingPlan plan = Plan();
		RootPlan r = Assert.Single(plan.Roots);
		Assert.Equal(ListingMode.Changed, r.Mode);
		AssertHanded(r, new[] { a, b, c, d }, Array.Empty<string>());
		Scanned(plan);
		Assert.False(HasPending(Kept())); // read by the scan, and gone with its listing
	}

	[Fact]
	public void AFolderThePagePlaced_IsJudgedAsItIsWhenTheScanLists() {
		if (!HasJournal(dir)) return;
		string a = Photo("a.png");
		Scanned(Plan());
		Photo(@"Art\b.png");
		Keep();
		// A new folder when the page looked, a repository by the time the scan lists it.
		Directory.CreateDirectory(Path.Combine(photos, "Art", ".git"));
		RootPlan r = Assert.Single(Plan().Roots);
		Assert.Equal(ListingMode.Unchanged, r.Mode);
		AssertHanded(r, Array.Empty<string>(), new[] { a });
	}

	[Fact]
	public void ThePageWritesNothingWhileAScanRuns_NorOverAScanThatSavedSince() {
		if (!HasJournal(dir)) return;
		Photo("a.png");
		Scanned(Plan());
		Photo("b.png", 32);
		string before = File.ReadAllText(Index);
		Keep(() => true);
		Assert.Equal(before, File.ReadAllText(Index));

		// A scan saves while the page reads the journal: the scan's listing stands.
		int asked = 0;
		Keep(() => {
			if (++asked == 2) Scanned(Plan());
			return false;
		});
		Assert.Equal(2, asked);
		Assert.False(HasPending(Kept()));
		Assert.Equal(ListingMode.Unchanged, Assert.Single(Plan().Roots).Mode);
	}

	[Fact]
	public void AJournalOverwritten_IsWalked_AndARefusedRead_IsToldApart() {
		if (!HasJournal(dir)) return;
		using (VolumeJournal? journal = VolumeJournal.Open(dir))
			if (journal!.FirstUsn <= 1) return; // a journal that still holds everything since it was made
		Photo("a.png");
		Scanned(Plan());
		string saved = File.ReadAllText(Index);
		string usn = $"\"usn\": {Kept().GetProperty("usn").GetInt64()}";
		Assert.Contains(usn, saved);

		// Overwritten past the last scan: the page keeps the reason, and the next scan walks for it.
		File.WriteAllText(Index, saved.Replace(usn, "\"usn\": 1"));
		const string overwritten = "the journal no longer reaches back to the last scan or the page's last look";
		Assert.Equal($"{photos}: {overwritten}", Assert.Single(Keep()));
		RootPlan r = Assert.Single(Plan().Roots);
		Assert.Equal((ListingMode.Walk, overwritten), (r.Mode, r.Why));

		// A read Windows refuses: not kept as a reason to walk (the next look tries again), and the scan says which it was.
		File.WriteAllText(Index, saved.Replace(usn, "\"usn\": 9000000000000000000"));
		Assert.Equal($"{photos}: the journal couldn't be read (Windows error 87)", Assert.Single(Keep()));
		Assert.Equal(9000000000000000000, Kept().GetProperty("usn").GetInt64());
		r = Assert.Single(Plan().Roots);
		Assert.Equal((ListingMode.Walk, "the journal couldn't be read (Windows error 87)"), (r.Mode, r.Why));
	}

	[Fact]
	public void AJournalRecordsRealPath_IsPlacedInTheScannedFolderAsWritten() {
		Assert.Equal(@"C:\Projects\Photos\2019", ListingPlan.AsWalked(@"D:\Photos\2019", @"D:\", @"C:\Projects"));
		Assert.Equal(@"C:\Projects", ListingPlan.AsWalked(@"D:\", @"D:\", @"C:\Projects\"));
		Assert.Equal(@"C:\Photos\2019", ListingPlan.AsWalked(@"C:\Photos\2019", @"C:\Photos", @"C:\Photos"));
		Assert.Null(ListingPlan.AsWalked(@"D:\Other", @"D:\Photos", @"C:\Projects\Photos"));
	}

	[Fact]
	public void AWeekWithoutAFullListing_OrNewSettings_Walks() {
		if (!HasJournal(dir)) return;
		Photo("a.png");
		DateTime now = DateTime.UtcNow;
		Scanned(Plan(now), now);
		Assert.Equal(ListingMode.Unchanged, Assert.Single(Plan(now.AddDays(6)).Roots).Mode);
		Assert.Equal(ListingMode.Walk, Assert.Single(Plan(now.AddDays(8)).Roots).Mode);

		Settings other = Settings();
		other.ExcludedExtensions.Add(".png");
		Assert.Equal(ListingMode.Walk, Assert.Single(Plan(settings: other).Roots).Mode);
	}

	[Fact]
	public void TheListingsKey_IsTheSettingsAndTheListingFormat_NotTheBuild() {
		string text = ListingPlan.ListingKeyText(Settings());
		Assert.DoesNotContain(AppBuild.Current, text);
		Assert.StartsWith("listing " + ListingPlan.ListingFormat + "\n", text);
		Assert.Equal(ListingPlan.ListingKey(Settings()), ListingPlan.ListingKey(Settings()));
		Assert.NotEqual(ListingPlan.ListingKey(Settings()), ListingPlan.ListingKey(Settings(), ListingPlan.ListingFormat + 1));
	}

	[Fact]
	public void ANewBuild_KeepsTheListing_ButANewListingFormatWalks() {
		if (!HasJournal(dir)) return;
		Photo("a.png");
		Scanned(Plan());
		string index = Path.Combine(home, "listing", "index.json");
		string saved = File.ReadAllText(index);
		// The listing is kept under a key a new build of the same format makes too: the next build's first scan
		// lists only what changed, where it used to walk every drive.
		Assert.Contains(ListingPlan.ListingKey(Settings()), saved);
		Assert.Equal(ListingMode.Unchanged, Assert.Single(Plan().Roots).Mode);

		// A listing from an older format: walked once, then trusted again.
		File.WriteAllText(index, saved.Replace(ListingPlan.ListingKey(Settings()), ListingPlan.ListingKey(Settings(), ListingPlan.ListingFormat - 1)));
		ListingPlan older = Plan();
		RootPlan r = Assert.Single(older.Roots);
		Assert.Equal(ListingMode.Walk, r.Mode);
		Assert.Equal("settings changed, or how Heiward lists", r.Why);
		Scanned(older);
		Assert.Equal(ListingMode.Unchanged, Assert.Single(Plan().Roots).Mode);
	}

	[Fact]
	public void ASkippedScan_MovesTheJournalOn_AndTheSameSettingsAreTheSameScan() {
		if (!HasJournal(dir)) return;
		Photo("a.png");
		Scanned(Plan());
		ListingPlan quiet = Plan();
		Assert.True(quiet.SameScanAsLast);
		quiet.SaveSkipped();
		Settings other = Settings();
		other.UseAiMatching = !other.UseAiMatching;
		Assert.False(Plan(settings: other).SameScanAsLast);
	}

	[Fact]
	public void ADriveScannedOnlyWhenAsked_IsLeftAlone_UnlessThisScanIsAsked() {
		Photo("a.png");
		Photo("b.jpg");
		Scanned(Plan()); // its last scan, when it was scanned automatically (or asked)
		string drive = AgentConfig.DriveOf(photos)!;
		AgentConfig cfg = Config();
		cfg.OnRequestDrives.Add(drive.ToLowerInvariant());
		Photo("c.png"); // a change it doesn't look at

		ListingPlan resting = ListingPlan.Make(Settings(), cfg, DateTime.UtcNow, CancellationToken.None);
		RootPlan r = Assert.Single(resting.Roots);
		Assert.Equal(ListingMode.Resting, r.Mode);
		Assert.True(resting.NothingChanged);
		Assert.Empty(resting.ReadWhole);
		Assert.Equal(2, r.Listing!.Unchanged.Count); // its files as its last scan found them still count
		Assert.Empty(r.Listing.Listed);
		Assert.False(resting.MayRead!(Path.Combine(photos, "a.png")));
		Assert.True(resting.MayRead!(Path.Combine(dir, "elsewhere.png"))); // only its scanned folders rest

		ListingPlan asked = ListingPlan.Make(Settings(), cfg, DateTime.UtcNow, CancellationToken.None, new[] { drive });
		Assert.NotEqual(ListingMode.Resting, Assert.Single(asked.Roots).Mode);
		Assert.Null(asked.MayRead);
	}

	/// <summary>After a scan of what <paramref name="plan"/> handed the engine: what it listed, and what it vouched for, as the engine finds them.</summary>
	static void ScannedAsPlanned(ListingPlan plan) {
		var found = plan.Roots.Where(r => r.Listing != null)
			.SelectMany(r => r.Listing!.Listed.Select(f => (f.FullName, f.Length)).Concat(r.Listing.Unchanged.Select(p => (p, new FileInfo(p).Length))))
			.ToList();
		plan.Save(found, p => File.GetLastWriteTimeUtc(p), DateTime.UtcNow);
	}

	ListingPlan FolderPlan(AgentConfig? cfg = null, params string[] folders) =>
		ListingPlan.Make(Settings(), cfg ?? Config(), DateTime.UtcNow, CancellationToken.None, folders: folders);

	[Fact]
	public void ScanNowOnAFolder_ReadsOnlyIt_AndTheNextScanFindsItAsListed() {
		if (!HasJournal(dir)) return;
		string a = Photo("a.png"), b = Photo(@"Trip\b.jpg");
		ListingPlan first = Plan();
		Scanned(first);
		var lastScanned = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
		DriveScans.Record(AgentScanner.ScannedWhole(first, new[] { photos }), lastScanned);
		Assert.Equal(lastScanned, DriveScans.Of(DriveScans.Load(), photos));
		string c = Photo(@"Trip\Day 2\c.jpg"), d = Photo("d.png", 32);
		string trip = Path.Combine(photos, "Trip");

		ListingPlan folder = FolderPlan(null, trip + @"\");
		RootPlan r = Assert.Single(folder.Roots);
		Assert.Equal(ListingMode.Folder, r.Mode);
		Assert.Equal(new[] { trip }, r.Only);
		Assert.False(folder.NothingChanged);
		Assert.Empty(folder.ReadWhole); // the drive's last full scan is still the one before
		// So the drive's "Last scanned" stays that scan's (drive-scans.json), and one folder doesn't set it.
		DriveScans.Record(AgentScanner.ScannedWhole(folder, new[] { photos }), DateTime.UtcNow);
		Assert.Equal(lastScanned, DriveScans.Of(DriveScans.Load(), photos));
		Assert.Equal(new[] { photos }, AgentScanner.ScannedWhole(null, new[] { photos })); // no plan: every root was walked
		// Its folder's files are listed; the rest of the scanned folder is handed over as the last scan found it, unread.
		AssertHanded(r, new[] { b, c }, new[] { a });
		Assert.True(folder.MayRead!(c));
		Assert.False(folder.MayRead!(a));
		Assert.False(folder.MayRead!(d));
		Assert.True(folder.MayRead!(Path.Combine(dir, "elsewhere.png"))); // outside every scanned folder
		Assert.Contains(trip, folder.Describe());
		long usn = Kept().GetProperty("usn").GetInt64();
		ScannedAsPlanned(folder);
		Assert.Equal(usn, Kept().GetProperty("usn").GetInt64()); // where the journal was read up to stands

		// The next scan reads the changes since the last one: the folder's are as the folder scan listed them, and the other is found.
		ListingPlan next = Plan();
		Assert.Equal(new[] { photos }, next.ReadWhole);
		r = Assert.Single(next.Roots);
		Assert.Equal(ListingMode.Changed, r.Mode);
		Assert.Contains(r.Listing!.Listed, f => f.FullName == d);
		Assert.DoesNotContain(r.Listing.Listed, f => f.FullName == c || f.FullName == b);
		Assert.Contains(c, r.Listing.Unchanged);
		Assert.Contains(b, r.Listing.Unchanged);
	}

	[Fact]
	public void ScanNowOnAFolder_BeforeAnyScan_ListsIt_AndTheNextScanWalks() {
		string a = Photo("a.png"), b = Photo(@"Trip\b.jpg");
		ListingPlan folder = FolderPlan(null, Path.Combine(photos, "Trip"));
		RootPlan r = Assert.Single(folder.Roots);
		Assert.Equal(ListingMode.Folder, r.Mode);
		AssertHanded(r, new[] { b }, Array.Empty<string>());
		Assert.False(folder.MayRead!(a));
		ScannedAsPlanned(folder);
		Assert.False(Kept().TryGetProperty("journalId", out JsonElement id) && id.ValueKind != JsonValueKind.Null); // no place in the journal yet

		if (!HasJournal(dir)) return;
		r = Assert.Single(Plan().Roots);
		Assert.Equal((ListingMode.Walk, ListingPlan.OnlyFoldersBefore), (r.Mode, r.Why));
		Scanned(Plan());
		Assert.Equal(ListingMode.Unchanged, Assert.Single(Plan().Roots).Mode);
	}

	[Fact]
	public void ScanNowOnAFolder_OnADriveScannedOnlyWhenAsked_ReadsJustIt() {
		string a = Photo("a.png"), b = Photo(@"Trip\b.jpg");
		Scanned(Plan());
		AgentConfig cfg = Config();
		cfg.OnRequestDrives.Add(AgentConfig.DriveOf(photos)!);
		string c = Photo(@"Trip\c.jpg");
		Photo("d.png"); // a change the folder scan doesn't look at

		ListingPlan folder = FolderPlan(cfg, Path.Combine(photos, "Trip"));
		RootPlan r = Assert.Single(folder.Roots);
		Assert.Equal(ListingMode.Folder, r.Mode);
		AssertHanded(r, new[] { b, c }, new[] { a });
		ScannedAsPlanned(folder);

		// Resting again: the folder as the folder scan found it counts, and the rest as the drive's last scan did.
		r = Assert.Single(ListingPlan.Make(Settings(), cfg, DateTime.UtcNow, CancellationToken.None).Roots);
		Assert.Equal(ListingMode.Resting, r.Mode);
		Assert.Equal(new[] { a, b, c }.Order(StringComparer.OrdinalIgnoreCase), r.Listing!.Unchanged.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void ScanNowOnAFolder_AboveTheScannedOne_ReadsAllOfIt_AndOneLeftOutOrGone_NothingAtAll() {
		string a = Photo("a.png");
		Photo(@"project\icon.png");
		Directory.CreateDirectory(Path.Combine(photos, "project", ".git"));

		RootPlan whole = Assert.Single(FolderPlan(null, dir).Roots);
		Assert.Equal(ListingMode.Folder, whole.Mode);
		Assert.Equal(new[] { photos }, whole.Only);
		AssertHanded(whole, new[] { a }, Array.Empty<string>()); // as a scan walks it: the repository left out

		foreach (string notScanned in new[] { Path.Combine(photos, "project"), Path.Combine(photos, "Gone") }) {
			ListingPlan plan = FolderPlan(null, notScanned);
			Assert.Equal(ListingMode.Resting, Assert.Single(plan.Roots).Mode);
			Assert.True(plan.NothingChanged);
		}
	}

	[Theory]
	[InlineData(@"D:\Archive\a.jpg", @"d:\", true)]
	[InlineData(@"D:\", @"D:", true)]
	[InlineData(@"C:\Pictures\a.jpg", @"D:\", false)]
	public void OnRequestDrives_AreMatchedByDrive(string path, string drive, bool onRequest) =>
		Assert.Equal(onRequest, new AgentConfig { OnRequestDrives = new() { drive } }.IsOnRequest(path));

	[Theory]
	[InlineData(@"C:\Photos\2019", @"C:\Photos", true)]
	[InlineData(@"C:\Photos", @"C:\Photos\", true)]
	[InlineData(@"C:\Photos2019", @"C:\Photos", false)]
	[InlineData(@"C:\Users\me", @"C:\", true)]
	[InlineData(@"D:\Photos", @"C:\", false)]
	public void IsUnder_KnowsWhatAFolderHolds(string path, string folder, bool under) => Assert.Equal(under, ListingPlan.IsUnder(path, folder));
}
