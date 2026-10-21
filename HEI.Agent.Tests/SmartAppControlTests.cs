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

public sealed class SmartAppControlTests {
	[Theory]
	[InlineData(1, true)] // on
	[InlineData(2, true)] // evaluation: Windows may turn it on at any time
	[InlineData(0, false)] // off
	[InlineData(3, false)]
	public void SmartAppControlOn_ReadsThePolicyState(int state, bool on) =>
		Assert.Equal(on, Installer.SmartAppControlOn(() => state));

	[Fact]
	public void SmartAppControlOn_IsOffWhereItCantBeRead() {
		Assert.False(Installer.SmartAppControlOn(() => null));
		Assert.False(Installer.SmartAppControlOn(() => "1"));
		Assert.False(Installer.SmartAppControlOn(() => throw new UnauthorizedAccessException()));
	}

	[Fact]
	public void Signed_TellsASignedLibraryFromAnUnsignedOne() {
		// .NET's own core library carries Microsoft's signature; a program built on the PC carries none.
		Assert.True(Installer.Signed(typeof(object).Assembly.Location));
		string unsigned = Path.Combine(Path.GetTempPath(), $"heiward-unsigned-{Guid.NewGuid():N}.exe");
		File.WriteAllBytes(unsigned, new byte[] { 0x4D, 0x5A, 0, 0 });
		try { Assert.False(Installer.Signed(unsigned)); }
		finally { File.Delete(unsigned); }
		Assert.False(Installer.Signed(Path.Combine(Path.GetTempPath(), "no-such-ffmpeg.exe")));
	}
}
