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
// HardLinkUtils.ReplaceWithLink: a duplicate is replaced by a link to the copy kept without ever being lost on the way.
// The link is made beside it first and only then takes its place, so a link that can't be made leaves the file.

using HEI.Core.Utils;

namespace HEI.Core.Tests;

public class ReplaceWithLinkTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-link-" + Guid.NewGuid().ToString("N"));

	public ReplaceWithLinkTests() => Directory.CreateDirectory(dir);

	public void Dispose() {
		try { Directory.Delete(dir, true); } catch { }
	}

	string Write(string name, string text) {
		string p = Path.Combine(dir, name);
		File.WriteAllText(p, text);
		return p;
	}

	[Fact]
	public void HardLink_TakesTheDuplicatesPlace() {
		string keep = Write("keep.txt", "the same"), dup = Write("dup.txt", "the same");

		HardLinkUtils.ReplaceWithLink(dup, keep, hard: true);

		Assert.True(HardLinkUtils.AreSameFile(dup, keep));
		Assert.Equal("the same", File.ReadAllText(dup));
		Assert.Equal(new[] { "dup.txt", "keep.txt" }, Directory.GetFiles(dir).Select(Path.GetFileName).Order());
	}

	[Fact]
	public void KeptFileGone_LeavesTheDuplicate() {
		string dup = Write("dup.txt", "mine");

		Assert.Throws<IOException>(() => HardLinkUtils.ReplaceWithLink(dup, Path.Combine(dir, "gone.txt"), hard: true));

		Assert.Equal("mine", File.ReadAllText(dup));
		Assert.Single(Directory.GetFiles(dir));
	}
}
