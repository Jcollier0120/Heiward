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

using HEI.Core.Utils;

namespace HEI.Core.Tests;

/// <summary>
/// Settings.SubfolderBlackList only applies below the IncludeList folders, so a folder the user
/// chose is scanned even inside a built-in exclusion. Heiward's ".*" once dropped every file of
/// ~\.npu-agent\...\camera after listing them: 600 added, 0 compared, no reason given. BlackList
/// keeps covering the whole path, as in upstream VDF.
/// </summary>
public class SubfolderBlackListTests {
	static ScanEngine Engine(params string[] includes) {
		var engine = new ScanEngine();
		// Hand-built entries for files that don't exist: only the folder rules decide.
		engine.Settings.IncludeNonExistingFiles = true;
		foreach (string include in includes)
			engine.Settings.IncludeList.Add(include);
		return engine;
	}

	static bool Excluded(ScanEngine engine, string folder) =>
		engine.InvalidEntry(new FileEntry { _Path = Path.Combine(folder, "a.mp4"), Folder = folder, FileSize = 1000 }, out _, out _);

	[Fact]
	public void BlackList_StillCoversTheIncludedFolderAndThoseAboveIt() {
		if (!OperatingSystem.IsWindows()) return; // Windows paths
		var engine = Engine(@"C:\Users\me\.npu-agent\testset\camera");
		engine.Settings.BlackList.Add(".*");
		Assert.True(Excluded(engine, @"C:\Users\me\.npu-agent\testset\camera"));
	}

	[Theory]
	[InlineData(".*", @"C:\Users\me\.npu-agent\testset\camera", @"C:\Users\me\.npu-agent\testset\camera", false)]
	[InlineData(".*", @"C:\Users\me\.npu-agent\testset\camera", @"C:\Users\me\.npu-agent\testset\camera\2024", false)]
	[InlineData(".*", @"C:\Users\me\.npu-agent\testset\camera", @"C:\Users\me\.npu-agent\testset\camera\.thumbnails", true)]
	[InlineData(".*", @"C:\Users\me\.npu-agent\testset\camera", @"C:\Users\me\.npu-agent\testset\camera\.thumbnails\small", true)]
	[InlineData("$*", @"D:\$Backup\Photos", @"D:\$Backup\Photos", false)]
	[InlineData("node_modules", @"C:\code\app\node_modules\pics", @"C:\code\app\node_modules\pics\icons", false)]
	[InlineData(@"?:\Users\*\AppData", @"C:\Users\me\AppData\Local\Photos", @"C:\Users\me\AppData\Local\Photos\2024", false)]
	[InlineData(@"?:\Users\*\AppData", @"C:\", @"C:\Users\me\AppData\Local\Photos", true)]
	[InlineData(@"?:\Users\*\AppData", @"C:\Users\me\AppData", @"C:\Users\me\AppData", false)]
	[InlineData(@"C:\Windows", @"C:\Windows\Web\Wallpaper", @"C:\Windows\Web\Wallpaper", false)]
	[InlineData(@"C:\Windows", @"C:\", @"C:\Windows\Web", true)]
	[InlineData(@"C:\Windows", @"C:\", @"C:\Windows", true)]
	[InlineData(@"C:\Windows", @"C:\", @"C:\Windows old", false)]
	public void SubfolderBlackList_OnlyAppliesBelowTheIncludedFolder(string pattern, string include, string folder, bool excluded) {
		if (!OperatingSystem.IsWindows()) return; // Windows paths
		var engine = Engine(include);
		engine.Settings.SubfolderBlackList.Add(pattern);
		Assert.Equal(excluded, Excluded(engine, folder));
	}

	[Fact]
	public void SubfolderBlackList_IsJudgedFromTheDeepestIncludedFolder() {
		if (!OperatingSystem.IsWindows()) return; // Windows paths
		// A whole-drive scan plus a folder the user listed inside a left-out one.
		var engine = Engine(@"C:\", @"C:\Users\me\.npu-agent\testset\camera");
		engine.Settings.SubfolderBlackList.Add(".*");
		Assert.False(Excluded(engine, @"C:\Users\me\.npu-agent\testset\camera\2024"));
		Assert.True(Excluded(engine, @"C:\Users\me\.npu-agent\other"));
		Assert.True(Excluded(engine, @"C:\Users\me\.npu-agent\testset\camera\.cache"));
	}

	[Fact]
	public void SubfolderBlackList_JudgesFoldersOutsideTheIncludeListAlongTheWholePath() {
		if (!OperatingSystem.IsWindows()) return; // Windows paths
		var engine = Engine(@"D:\Photos");
		engine.Settings.ScanAgainstEntireDatabase = true;
		engine.Settings.SubfolderBlackList.Add(".*");
		Assert.True(Excluded(engine, @"C:\Users\me\.cache\thumbs"));
		Assert.False(Excluded(engine, @"C:\Users\me\Pictures"));
	}

	[Fact]
	public void SubfolderBlackList_IsEmptyByDefault() => Assert.Empty(new Settings().SubfolderBlackList);

	[Fact]
	public void FilesTheEnumerationFinds_AreComparedToo() {
		string dir = Path.Combine(Path.GetTempPath(), "hei-subfolder-blacklist-" + Guid.NewGuid().ToString("N"));
		string root = Path.Combine(dir, ".hidden", "camera");
		try {
			Directory.CreateDirectory(Path.Combine(root, "2024"));
			Directory.CreateDirectory(Path.Combine(root, ".cache"));
			File.WriteAllBytes(Path.Combine(root, "a.mp4"), new byte[] { 1 });
			File.WriteAllBytes(Path.Combine(root, "2024", "b.mp4"), new byte[] { 1 });
			File.WriteAllBytes(Path.Combine(root, ".cache", "c.mp4"), new byte[] { 1 });

			var files = FileUtils.GetFilesRecursive(root, false, false, recursive: true, includeImages: false,
				new List<string> { ".*" }, CancellationToken.None);
			Assert.Equal(new[] { "a.mp4", "b.mp4" }, files.Select(f => f.Name).OrderBy(n => n));

			var engine = new ScanEngine();
			engine.Settings.IncludeList.Add(root);
			engine.Settings.SubfolderBlackList.Add(".*");
			Assert.All(files, f => Assert.False(engine.InvalidEntry(new FileEntry(f), out _, out string? reason), reason));

			// The same rule in BlackList drops them all after listing them: upstream VDF's meaning.
			var upstream = new ScanEngine();
			upstream.Settings.IncludeList.Add(root);
			upstream.Settings.BlackList.Add(".*");
			Assert.All(files, f => Assert.True(upstream.InvalidEntry(new FileEntry(f), out _, out _)));
		}
		finally {
			if (Directory.Exists(dir))
				Directory.Delete(dir, recursive: true);
		}
	}
}
