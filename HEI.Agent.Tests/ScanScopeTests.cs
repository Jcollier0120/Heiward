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
using HEI.Core.Utils;

namespace HEI.Agent.Tests;

/// <summary>What a whole-drive scan leaves out: judged by the same matcher the file enumeration uses.</summary>
public sealed class ScanScopeTests : IDisposable {
	static readonly List<string> Excluded = ScanScope.Exclusions();

	readonly string temp = Path.Combine(Path.GetTempPath(), "hei-scope-" + Guid.NewGuid().ToString("N"));

	public void Dispose() { try { Directory.Delete(temp, true); } catch { } }

	string Dir(params string[] parts) {
		string p = Path.Combine(new[] { temp }.Concat(parts).ToArray());
		Directory.CreateDirectory(p);
		return p;
	}

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
	public void UserExclusions_CoverTheWholePath_BuiltInOnesOnlyTheFoldersBelowTheScannedOnes() {
		var s = new Settings();
		ScanScope.Apply(s, new AgentConfig { ScanAllDrives = false, ExcludeFolders = { @"D:\Scans" } });
		Assert.Equal(new[] { @"D:\Scans" }, s.BlackList);
		Assert.Contains(".*", s.SubfolderBlackList);
		Assert.Contains(@"?:\Users\*\AppData", s.SubfolderBlackList);
		Assert.DoesNotContain(@"D:\Scans", s.SubfolderBlackList);
	}

	/// <summary>
	/// The bug: folders = [~\.npu-agent\npu-vision\testset\camera] listed 600 photos, then compared 0,
	/// because ".*" (and "?:\Users\*\AppData", for a temp folder) matched a folder above it.
	/// </summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AListedFolder_InsideABuiltInExclusion_IsScanned(bool allDrives) {
		string camera = Dir(".npu-agent", "npu-vision", "testset", "camera");
		Dir(".npu-agent", "npu-vision", "testset", "camera", ".thumbnails");
		File.WriteAllBytes(Path.Combine(camera, "a.jpg"), new byte[] { 1 });
		File.WriteAllBytes(Path.Combine(camera, ".thumbnails", "a.jpg"), new byte[] { 1 });
		var notes = new List<string>();
		var engine = new ScanEngine();
		ScanScope.Apply(engine.Settings, new AgentConfig { ScanAllDrives = allDrives, Folders = { camera } }, notes);

		// On a whole-drive scan too: the drive's walk never gets there, so it's a root of its own.
		Assert.Contains(camera, engine.Settings.IncludeList);
		Assert.Empty(notes);
		var files = FileUtils.GetFilesRecursive(camera, false, false, recursive: true, includeImages: true,
			engine.Settings.BlackList.Concat(engine.Settings.SubfolderBlackList).ToList(), CancellationToken.None,
			skipFoldersContaining: engine.Settings.SkipFoldersContaining, skipFolderLinks: true);
		FileInfo found = Assert.Single(files); // the rules still apply below it
		Assert.False(engine.InvalidEntry(new FileEntry(found), out _, out string? reason), reason);
	}

	[Fact]
	public void AListedFolder_InsideTheUsersOwnExclusion_IsSkippedWithANote() {
		string keep = Dir("Backups", "Keep");
		foreach (string exclusion in new[] { Path.Combine(temp, "Backups"), "Backups", Path.Combine(temp, "Back*") }) {
			var notes = new List<string>();
			var roots = ScanScope.Roots(new AgentConfig { ScanAllDrives = false, Folders = { keep }, ExcludeFolders = { exclusion } }, notes);
			Assert.Empty(roots);
			string note = Assert.Single(notes);
			Assert.Contains(keep, note);
			Assert.Contains(exclusion, note);
			Assert.Contains("excludeFolders", note);
		}
	}

	[Fact]
	public void ListedFoldersInsideEachOther_AreScannedOnce() {
		string outer = Dir("Photos"), inner = Dir("Photos", "2019");
		Assert.Equal(new[] { outer }, ScanScope.Roots(new AgentConfig { ScanAllDrives = false, Folders = { inner, outer } }));
		Assert.Equal(new[] { outer }, ScanScope.Roots(new AgentConfig { ScanAllDrives = false, Folders = { outer, inner } }));
		// Unless the outer one's walk leaves it out on the way.
		string hidden = Dir("Photos", ".app", "Exports");
		Assert.Equal(new[] { outer, hidden }, ScanScope.Roots(new AgentConfig { ScanAllDrives = false, Folders = { outer, hidden } }));
	}

	[Fact]
	public void BuiltInExclusionOver_NamesTheLeftOutFolder() {
		var over = ScanScope.BuiltInExclusionOver(@"C:\Users\me\.npu-agent\npu-vision\testset\camera");
		Assert.NotNull(over);
		Assert.Equal(@"C:\Users\me\.npu-agent", over.Value.Folder);
		Assert.Equal("App data", over.Value.Rule.Reason);
		Assert.Null(ScanScope.BuiltInExclusionOver(@"D:\Photos\2019"));
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
