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

/// <summary>"Show in File Explorer": only a file or folder that's on this PC now, and only ever shown, never run.</summary>
public sealed class RevealTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-reveal-" + Guid.NewGuid().ToString("N"));

	public RevealTests() => Directory.CreateDirectory(dir);

	public void Dispose() {
		try { Directory.Delete(dir, true); } catch { }
	}

	[Fact]
	public void AFileOrFolderThatsThere_CanBeShown() {
		string file = Path.Combine(dir, "IMG 0001, copy.jpg");
		File.WriteAllText(file, "x");
		Assert.Null(Reveal.Check(file));
		Assert.Null(Reveal.Check(dir));
		Assert.Null(Reveal.Check(Path.GetPathRoot(dir)));
	}

	[Fact]
	public void OneThatsGone_SaysSo() =>
		Assert.Contains("isn't there any more", Reveal.Check(Path.Combine(dir, "moved.jpg")));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData(@"Photos\a.jpg")]          // not a full path
	[InlineData(@"C:Photos\a.jpg")]        // relative to C:'s current folder
	[InlineData(@"\Windows")]              // rooted, but on whichever drive is current
	[InlineData(@"\\?\C:\Windows")]        // device paths
	[InlineData(@"\\.\PhysicalDrive0")]
	[InlineData("C:\\Windows\" /e,\"C:\\")] // a quote would end the path in File Explorer's arguments
	[InlineData(@"C:\Windows\*")]
	public void AnythingElse_IsRefused(string? path) => Assert.NotNull(Reveal.Check(path));

	[Theory]
	[InlineData(@"C:\Photos\IMG 0001.jpg", false, "/select,\"C:\\Photos\\IMG 0001.jpg\"")]
	[InlineData(@"C:\Photos\a,b.jpg", false, "/select,\"C:\\Photos\\a,b.jpg\"")]
	[InlineData(@"C:\Photos\", false, "/select,\"C:\\Photos\"")]   // a folder's trailing backslash would escape the quote
	[InlineData(@"C:\Photos", true, "\"C:\\Photos\"")]
	[InlineData(@"D:\", false, @"/select,D:\")]                    // a drive's root goes unquoted, for the same reason
	[InlineData(@"D:\", true, @"D:\")]
	public void FileExplorersArguments(string path, bool open, string expected) => Assert.Equal(expected, Reveal.Arguments(path, open));
}
