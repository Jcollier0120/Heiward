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

using HEI.Core;

namespace HEI.Agent.Tests;

/// <summary>
/// Scans plan their listing from the drive's change journal: the real one, on the drive the temp
/// folder is on (NTFS on any Windows PC; the tests let themselves off where there's none).
/// </summary>
[Collection(AgentHomeCollection.Name)]
public sealed class JournalListingTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-journal-" + Guid.NewGuid().ToString("N"));
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
		try { Directory.Delete(dir, true); } catch { }
	}

	Settings Settings() {
		var s = new Settings { IncludeImages = true, IncludeSubDirectories = true, SkipCloudPlaceholders = true, SkipFolderLinks = true, ListingProvesExistence = true };
		s.IncludeList.Add(photos);
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

	/// <summary>After a scan: what it listed, as the engine would report it.</summary>
	void Scanned(ListingPlan plan, DateTime? nowUtc = null) {
		var found = Directory.EnumerateFiles(photos, "*", SearchOption.AllDirectories)
			.Where(f => !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).Select(f => (f, new FileInfo(f).Length)).ToList();
		plan.Save(found, p => File.GetLastWriteTimeUtc(p), nowUtc ?? DateTime.UtcNow);
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
	public void AFolderAdded_WhereScansLook_IsWalked() {
		if (!HasJournal(dir)) return;
		Photo("a.png");
		Scanned(Plan());
		Photo(@"Trip\b.jpg");
		RootPlan r = Assert.Single(Plan().Roots);
		Assert.Equal(ListingMode.Walk, r.Mode);
		Assert.StartsWith("folders were added", r.Why);
	}

	[Fact]
	public void AFolderAdded_WhereScansDontLook_ChangesNothing() {
		if (!HasJournal(dir)) return;
		Photo("a.png");
		Scanned(Plan());
		// A code repository: scans leave it out, whatever happens in it.
		Directory.CreateDirectory(Path.Combine(photos, "project", ".git"));
		File.WriteAllBytes(Path.Combine(photos, "project", "icon.png"), new byte[16]);
		Assert.Equal(ListingMode.Walk, Assert.Single(Plan().Roots).Mode); // the repository's folder is new where scans look
		Scanned(Plan());
		File.WriteAllBytes(Path.Combine(photos, "project", "logo.png"), new byte[16]);
		Assert.Equal(ListingMode.Unchanged, Assert.Single(Plan().Roots).Mode);
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

	[Theory]
	[InlineData(@"C:\Photos\2019", @"C:\Photos", true)]
	[InlineData(@"C:\Photos", @"C:\Photos\", true)]
	[InlineData(@"C:\Photos2019", @"C:\Photos", false)]
	[InlineData(@"C:\Users\me", @"C:\", true)]
	[InlineData(@"D:\Photos", @"C:\", false)]
	public void IsUnder_KnowsWhatAFolderHolds(string path, string folder, bool under) => Assert.Equal(under, ListingPlan.IsUnder(path, folder));
}
