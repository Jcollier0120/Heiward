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

using VDF.Core.Utils;

namespace VDF.Agent.Tests;

/// <summary>What a whole-drive scan leaves out: judged by the same matcher the file enumeration uses.</summary>
public sealed class ScanScopeTests {
	static readonly List<string> Excluded = ScanScope.Exclusions(new AgentConfig());

	static bool IsExcluded(string folder) => Excluded.Any(p => FileUtils.IsExcludedFolder(p, new DirectoryInfo(folder)));

	[Theory]
	[InlineData(@"C:\Windows")]
	[InlineData(@"C:\Program Files")]
	[InlineData(@"D:\Program Files (x86)")]
	[InlineData(@"C:\ProgramData")]
	[InlineData(@"C:\$Recycle.Bin")]
	[InlineData(@"C:\Users\someone\AppData")]
	[InlineData(@"C:\Users\someone\.vscode")]
	[InlineData(@"D:\SteamLibrary")]
	[InlineData(@"E:\Games\Epic Games")]
	[InlineData(@"C:\Users\Public\AccountPictures")]
	[InlineData(@"D:\Lightroom\Lightroom Catalog Previews.lrdata")]
	[InlineData(@"D:\Mac backup\Photos Library.photoslibrary")]
	[InlineData(@"C:\code\site\node_modules")]
	public void SystemAppAndGameFolders_AreLeftOut(string folder) => Assert.True(IsExcluded(folder), folder);

	[Theory]
	[InlineData(@"C:\Users\someone\Pictures")]
	[InlineData(@"C:\Users\someone\OneDrive\Pictures")]
	[InlineData(@"C:\Users\someone\iCloudPhotos\Photos")]
	[InlineData(@"C:\Users\Public\Pictures")]
	[InlineData(@"D:\Photos\2019")]
	[InlineData(@"D:\Games\Screenshots")]
	[InlineData(@"E:\Program Files backup\Pictures")] // "Program Files" only counts at a drive's root
	public void TheUsersOwnFolders_AreScanned(string folder) => Assert.False(IsExcluded(folder), folder);

	[Fact]
	public void UserExclusions_AreAdded() {
		var excluded = ScanScope.Exclusions(new AgentConfig { ExcludeFolders = { @"D:\Scans" } });
		Assert.Contains(@"D:\Scans", excluded);
	}

	[Fact]
	public void ExtraFolders_UnderAScannedDrive_AreNotScannedTwice() {
		string temp = Path.GetTempPath();
		var roots = ScanScope.Roots(new AgentConfig { ScanAllDrives = false, Folders = { temp, Path.Combine(temp, ".") } });
		Assert.Single(roots);
	}

	[Fact]
	public void MissingFolders_AreNotedAndSkipped() {
		var notes = new List<string>();
		var roots = ScanScope.Roots(new AgentConfig { ScanAllDrives = false, Folders = { @"Z:\does\not\exist" } }, notes);
		Assert.Empty(roots);
		Assert.Contains(notes, n => n.Contains(@"Z:\does\not\exist"));
	}
}
