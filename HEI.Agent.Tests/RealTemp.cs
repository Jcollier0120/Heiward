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

using System.Diagnostics;

namespace HEI.Agent.Tests;

/// <summary>
/// Temporary folders by their real path (the Steward's kit, spec/DEV-DRIVES.md): %TEMP% may be written through a junction
/// or a folder a drive is mounted in (C:\DevData\Temp is the drive E:\'s Temp), and the code under test takes folders by
/// where they really are. And junctions, to test that it does.
/// </summary>
static class RealTemp {
	/// <summary>A new folder's path (not made yet) in the temp folder, by its real path.</summary>
	public static string Folder(string prefix) => Path.Combine(RealPath.Of(Path.GetTempPath()), prefix + Guid.NewGuid().ToString("N"));

	/// <summary>Makes <paramref name="link"/> a junction to <paramref name="target"/>, as mklink /J does (no rights needed); returns the link.</summary>
	public static string Junction(string link, string target) {
		using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
		p.StandardOutput.ReadToEnd();
		p.WaitForExit();
		Assert.Equal(0, p.ExitCode);
		Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, $"{link} isn't a junction");
		return link;
	}
}
