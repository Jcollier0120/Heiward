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

/// <summary>One folder, two paths: through a real junction, as C:\worktrees is E:\worktrees on a PC with Dev Drives.</summary>
public sealed class RealPathTests : IDisposable {
	readonly string root = RealTemp.Folder("hei-realpath-");
	readonly string real, link;

	public RealPathTests() {
		real = Path.Combine(root, "real");
		Directory.CreateDirectory(Path.Combine(real, "wt", "src"));
		link = RealTemp.Junction(Path.Combine(root, "link"), real);
	}

	public void Dispose() {
		try { Directory.Delete(link); } catch { } // the junction alone, never what it points to
		try { Directory.Delete(root, true); } catch { }
	}

	[Fact]
	public void AFolderThroughAJunction_IsItsRealFolder() {
		string viaLink = Path.Combine(link, "wt"), there = Path.Combine(real, "wt");
		Assert.Equal(there, RealPath.Of(viaLink), StringComparer.OrdinalIgnoreCase);
		Assert.Equal(there, RealPath.Of(viaLink + Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase);
		Assert.True(RealPath.Same(viaLink, there));
		Assert.True(RealPath.Same(viaLink.ToUpperInvariant(), there));
		Assert.True(RealPath.IsUnder(Path.Combine(link, "wt", "src"), there));
		Assert.True(RealPath.IsUnder(Path.Combine(real, "wt", "src"), viaLink));
		Assert.False(RealPath.Same(viaLink, Path.Combine(real, "other")));
		// As written, they differ: what every comparison did before.
		Assert.False(ListingPlan.IsUnder(Path.Combine(link, "wt", "src"), there));
	}

	[Fact]
	public void APathThatDoesntExistYet_IsResolvedAsFarAsItGoes() {
		Assert.Equal(Path.Combine(real, "wt", "new", "file.txt"), RealPath.Of(Path.Combine(link, "wt", "new", "file.txt")), StringComparer.OrdinalIgnoreCase);
		Assert.True(RealPath.Same(Path.Combine(link, "gone"), Path.Combine(real, "gone")));
	}

	[Theory]
	[InlineData(@"\\server\share\photos")]
	[InlineData(@"\\wsl.localhost\Ubuntu\home\me")]
	public void NetworkPaths_AreLeftAsTheyAre(string path) => Assert.Equal(path, RealPath.Of(path));

	[Fact]
	public void ADrivesRoot_IsItself() {
		Assert.Equal(@"C:\", RealPath.Of(@"C:\"));
		Assert.Equal(@"C:\", RealPath.VolumeOf(@"C:\"));
		Assert.Equal(@"C:\", RealPath.VolumeOf(@"c:"));
	}

	[Fact]
	public void TheDriveAFolderIsOn_IsTheDriveItReallyIsOn() {
		Assert.Equal(RealPath.VolumeOf(real), RealPath.VolumeOf(Path.Combine(link, "wt")));
		Assert.Equal(Path.GetPathRoot(real)!.ToUpperInvariant(), RealPath.VolumeOf(Path.Combine(link, "wt")));
	}

	[Fact]
	public void Spellings_HoldThePathAsWrittenAndItsRealPath() {
		List<string> spellings = RealPath.Spellings(Path.Combine(link, "wt"));
		Assert.Contains(Path.Combine(link, "wt"), spellings, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(Path.Combine(real, "wt"), spellings, StringComparer.OrdinalIgnoreCase);
	}
}
