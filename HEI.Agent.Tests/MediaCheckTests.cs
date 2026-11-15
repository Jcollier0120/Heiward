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

/// <summary>
/// The look on disk for photos and videos where the last scan counted none (the owner, 2026-11-15: "pre-scan, some
/// directories that definitely have photo files are showing up as 'folders without photos or videos'"): by the scan's own
/// rules, at any depth, whatever the extension's case, and never past its budget.
/// </summary>
public sealed class MediaCheckTests : IDisposable {
	readonly string root = RealTemp.Folder("hei-media-check-");

	public MediaCheckTests() => Directory.CreateDirectory(root);
	public void Dispose() { try { Directory.Delete(root, true); } catch { } }

	string Dir(string relative) => Directory.CreateDirectory(Path.Combine(root, relative)).FullName;

	string Put(string relative, byte[]? content = null) {
		string p = Path.Combine(root, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(p)!);
		System.IO.File.WriteAllBytes(p, content ?? new byte[] { 1 });
		return p;
	}

	MediaCheck Check(AgentConfig? cfg = null, int? maxFolders = null) {
		cfg ??= new AgentConfig { ScanAllDrives = false, Folders = { root } };
		return new MediaCheck(cfg, ScanScope.ExclusionRules(cfg), maxFolders, TimeSpan.FromMinutes(1));
	}

	MediaOnDisk Below(string relative, AgentConfig? cfg = null) => Check(cfg).Below(Path.Combine(root, relative));

	[Fact]
	public void PhotosTwoOrMoreLevelsDown_AreFound() {
		Put(@"backup\Nested\a\b\shot.png");
		Put(@"backup\Nested\readme.txt");
		Assert.Equal(MediaOnDisk.Found, Below(@"backup\Nested"));
		Assert.Equal(MediaOnDisk.Found, Below("backup"));
		// Directly in it, only what's directly in it.
		Assert.Equal(MediaOnDisk.None, Check().Own(Path.Combine(root, @"backup\Nested")));
	}

	[Fact]
	public void PhotosInTheFolderItself_AreFound() {
		Put(@"Camera\IMG_0001.jpg");
		Assert.Equal(MediaOnDisk.Found, Check().Own(Path.Combine(root, "Camera")));
		Assert.Equal(MediaOnDisk.Found, Below("Camera"));
	}

	[Theory]
	[InlineData("IMG_0001.JPG")]
	[InlineData("Screenshot.PNG")]
	[InlineData("clip.MP4")]
	[InlineData("photo.Heic")]
	public void UpperCaseExtensions_AreFound(string name) {
		Put(@"Upper\deep\" + name);
		Assert.Equal(MediaOnDisk.Found, Below("Upper"));
	}

	[Fact]
	public void ATrulyEmptyTree_HasNone() {
		Put(@"Saves\slot1\save.sav");
		Put(@"Saves\slot1\notes.txt");
		Put(@"Saves\roms\game.iso");
		Dir(@"Saves\empty\still empty");
		Assert.Equal(MediaOnDisk.None, Below("Saves"));
		Assert.Equal(MediaOnDisk.None, Below(@"Saves\empty"));
	}

	[Fact]
	public void WhatTheScanLeavesOut_DoesntCount() {
		Put(@"Left\node_modules\pkg\icon.png");          // a built-in exclusion
		Put(@"Left\.cache\thumb.jpg");                    // a dot-folder (app data)
		Put(@"Left\site\.git\HEAD");                      // a code repository: its pictures belong to the project
		Put(@"Left\site\assets\logo.png");
		Put(@"Left\Mine\old\private.jpg");                // the user's own exclusion
		Put(@"Left\Raw\IMG_0001.webp");                   // a type the user left out (written without the dot)
		string system = Dir(@"Left\Sys");                  // Windows' hidden system folder
		Put(@"Left\Sys\x.jpg");
		System.IO.File.SetAttributes(system, FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory);
		var cfg = new AgentConfig { ScanAllDrives = false, Folders = { root }, ExcludeFolders = { Path.Combine(root, @"Left\Mine") }, ExcludeExtensions = { "webp" } };
		try {
			Assert.Equal(MediaOnDisk.None, Below("Left", cfg));
			// Each of them is found when the check starts inside it, so it's the rules that leave them out on the way down.
			foreach (string inside in new[] { @"Left\node_modules\pkg", @"Left\.cache", @"Left\site\assets", @"Left\Mine\old", @"Left\Sys" })
				Assert.Equal(MediaOnDisk.Found, Below(inside));
			Assert.Equal(MediaOnDisk.Found, Below(@"Left\Raw"));
		}
		finally {
			System.IO.File.SetAttributes(system, FileAttributes.Directory);
		}
	}

	[Fact]
	public void AFolderLink_IsNotFollowed() {
		string photos = Dir(@"Elsewhere\Photos");
		Put(@"Elsewhere\Photos\a.jpg");
		RealTemp.Junction(Path.Combine(Dir("Shortcuts"), "Photos link"), photos);
		Assert.Equal(MediaOnDisk.None, Below("Shortcuts"));
	}

	[Fact]
	public void TypeScriptSources_AreNotVideos_ButATransportStreamIs() {
		Put(@"Code\src\app.ts", "export const a = 1;\n"u8.ToArray());
		Assert.Equal(MediaOnDisk.None, Below("Code"));
		var packet = new byte[188 * 3];
		for (int i = 0; i < 3; i++) packet[i * 188] = 0x47;
		Put(@"Recordings\show.ts", packet);
		Assert.Equal(MediaOnDisk.Found, Below("Recordings"));
	}

	[Fact]
	public void PastItsBudget_ItsUnfinished_AndTheBudgetIsShared() {
		Put(@"Deep\1\2\3\4\a.jpg");
		Assert.Equal(MediaOnDisk.Unfinished, Check(maxFolders: 3).Below(Path.Combine(root, "Deep")));
		Assert.Equal(MediaOnDisk.Found, Check(maxFolders: 5).Below(Path.Combine(root, "Deep")));
		// One check is one listing's: what one folder used is gone for the next.
		Put(@"Other\b.jpg");
		MediaCheck check = Check(maxFolders: 5);
		Assert.Equal(MediaOnDisk.Found, check.Below(Path.Combine(root, "Deep")));
		Assert.Equal(MediaOnDisk.Unfinished, check.Below(Path.Combine(root, "Other")));
	}

	[Fact]
	public void AFolderThatIsGone_HasNone() =>
		Assert.Equal(MediaOnDisk.None, Below("Not there"));
}
