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

/// <summary>The review page's right-click "Include in scans" / "Leave out of scans", and how hard scans work.</summary>
public sealed class FolderOverrideTests : IDisposable {
	// Under %TEMP%, which is inside AppData: the test folder is listed in "folders", as a user would.
	readonly string root = Path.Combine(Path.GetTempPath(), "hei-override-" + Guid.NewGuid().ToString("N"));

	public FolderOverrideTests() => Directory.CreateDirectory(root);
	public void Dispose() { try { Directory.Delete(root, true); } catch { } }

	string Dir(params string[] parts) {
		string p = Path.Combine(new[] { root }.Concat(parts).ToArray());
		Directory.CreateDirectory(p);
		return p;
	}

	AgentConfig Config() => new() { ScanAllDrives = false, Folders = { root } };

	static TreeNode Child(TreeListing listing, string name) => listing.Children.Single(c => c.Name == name);

	[Fact]
	public void LeavingOutAScannedFolder_AndIncludingItAgain_RoundTrips() {
		string photos = Dir("Photos");
		var cfg = Config();
		Assert.True(FolderOverride.IsScanned(cfg, photos));

		OverrideResult left = FolderOverride.Exclude(cfg, photos);
		Assert.Null(left.Error);
		Assert.False(left.Scanned);
		Assert.Equal(new[] { photos }, cfg.ExcludeFolders);
		Assert.Equal(ScanScope.UserReason, Child(ExplorerView.Tree(root, all: true, cfg, null, new())!, "Photos").Exempt);

		OverrideResult back = FolderOverride.Include(cfg, photos, null);
		Assert.True(back.Scanned);
		Assert.Empty(cfg.ExcludeFolders);
		Assert.Equal(new[] { root }, cfg.Folders); // the exclusion went; nothing was added
	}

	[Fact]
	public void IncludingAFolderTheBuiltInRulesLeaveOut_ListsIt_AndLeavingItOutUnlistsIt() {
		string modules = Dir("node_modules");
		var cfg = Config();
		Assert.False(FolderOverride.IsScanned(cfg, modules));

		Assert.True(FolderOverride.Include(cfg, modules, null).Scanned);
		Assert.Contains(modules, cfg.Folders);
		Assert.Null(Child(ExplorerView.Tree(root, all: true, cfg, null, new())!, "node_modules").Exempt);

		Assert.False(FolderOverride.Exclude(cfg, modules).Scanned);
		Assert.Equal(new[] { root }, cfg.Folders);
		Assert.Empty(cfg.ExcludeFolders); // taking it out of "folders" was enough
	}

	[Fact]
	public void IncludingAFolderUnderAWiderRuleOfTheUsers_AsksBeforeRemovingTheRule() {
		string old = Dir("Old photos");
		var cfg = Config();
		cfg.ExcludeFolders.Add("Old*");

		OverrideResult asked = FolderOverride.Include(cfg, old, null);
		Assert.Equal("Old*", asked.Rule);
		Assert.NotNull(asked.Error);
		Assert.Equal(new[] { "Old*" }, cfg.ExcludeFolders);

		OverrideResult done = FolderOverride.Include(cfg, old, "Old*");
		Assert.True(done.Scanned);
		Assert.Empty(cfg.ExcludeFolders);
	}

	[Fact]
	public void AWholeDrive_CantBeLeftOut() {
		string drive = Path.GetPathRoot(Environment.SystemDirectory)!;
		var cfg = new AgentConfig { ScanAllDrives = true };
		Assert.NotNull(FolderOverride.Exclude(cfg, drive).Error);
		Assert.Empty(cfg.ExcludeFolders);
	}

	[Theory]
	[InlineData("auto", false, false, true)]      // Scan now
	[InlineData("auto", true, false, false)]      // scheduled, nobody looking
	[InlineData("auto", true, true, true)]        // scheduled, the page is open
	[InlineData("background", false, true, false)]
	[InlineData("background", true, true, false)]
	[InlineData("full", true, false, true)]        // scheduled, nobody looking: full speed all the same
	[InlineData("full", false, false, true)]
	public void Scans_RunAtFullSpeed_OnlyWhenSomeoneWaits(string speed, bool scheduled, bool pageOpen, bool fullSpeed) =>
		Assert.Equal(fullSpeed, ScanPace.FullSpeed(new AgentConfig { ScanSpeed = speed }, scheduled, pageOpen));

	[Fact]
	public void Parallelism_FromTheSettings_WinsAtEitherSpeed() {
		Assert.Equal(3, new AgentConfig { Parallelism = 3 }.ParallelismFor(true));
		Assert.Equal(3, new AgentConfig { Parallelism = 3 }.ParallelismFor(false));
		Assert.True(new AgentConfig().ParallelismFor(true) >= new AgentConfig().ParallelismFor(false));
	}
}
