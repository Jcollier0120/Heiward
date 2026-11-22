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

using HEI.Agent.Estate;

namespace HEI.Agent.Tests;

/// <summary>
/// The listing lent to another PC: only the photos and videos (what this PC knows isn't one is left out, so its count is the
/// one the listing found), and its files looked up by key whatever letters their names hold.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME is process-wide
public sealed class LentListingTests : IDisposable {
	readonly string dir = Directory.CreateTempSubdirectory("hei-lent-").FullName;
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public LentListingTests() => Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch (IOException) { }
	}

	static readonly DateTime When = new(2026, 11, 21, 3, 0, 0, DateTimeKind.Utc);

	static readonly string[] Paths = [
		"C:\\Photos\\plain.jpg", "C:\\Photos\\MiXeD CaSe.JPG", "C:\\Fotos\\Ärger über Öl.jpg", "C:\\Fotos\\İstanbul\\KIŞ.jpg",
		"C:\\Fotos\\𐐀 Deseret.jpg", "C:\\Фото\\ПРИВЕТ.png", "C:\\写真\\東京.heic", "C:\\Videos\\notes.ts",
	];

	[Fact]
	public void EveryFile_IsFoundByItsKey_WhateverLettersItsNameHolds() {
		using LentListing listing = LentListing.Write([("C:\\", Paths.Select((p, i) => (p, 100L + i, When)).ToList())], "1.12.0");
		Assert.Equal(Paths.Length, listing.Files);
		foreach (string p in Paths) {
			Assert.Equal(p, listing.Find(p.ToUpperInvariant())?.Path);
			Assert.Equal(p, listing.PathOf(PeerFiles.KeyOf(p)!));
		}
	}

	[Fact]
	public void WhatThisPcKnowsIsntAPhotoOrVideo_IsLeftOut() {
		var notMedia = new HashSet<string>(["c:\\videos\\NOTES.ts"], StringComparer.OrdinalIgnoreCase);
		using LentListing listing = LentListing.Write([("C:\\", Paths.Select(p => (p, 100L, When)).ToList())], "1.12.0", leaveOut: notMedia.Contains);
		Assert.Equal(Paths.Length - 1, listing.Files);
		Assert.Null(listing.Find("C:\\Videos\\notes.ts"));
		Assert.NotNull(listing.Find("C:\\Photos\\plain.jpg"));
	}

	[Fact]
	public void WhatTheListingFound_IsTakenOnce() {
		LentListing.Listed.Save(new LentListing.Listed(16.2, "C:\\ from its change journal", ["C:\\Videos\\notes.ts"]));
		LentListing.Listed? found = LentListing.Listed.Take();
		Assert.NotNull(found);
		Assert.Equal(16.2, found.Seconds);
		Assert.Equal("C:\\ from its change journal", found.How);
		Assert.Equal(["C:\\Videos\\notes.ts"], found.NotMedia);
		Assert.Null(LentListing.Listed.Take());
	}
}
