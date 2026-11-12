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

using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent.Tests;

/// <summary>
/// The folder view's sizes: each folder's size on disk, measured in the background for what the page shows and kept in
/// memory only, never through a link, and past what Windows won't list.
/// </summary>
public sealed class FolderSizesTests : IDisposable {
	readonly string root = RealTemp.Folder("hei-sizes-");
	DateTime now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

	public FolderSizesTests() => Directory.CreateDirectory(root);

	public void Dispose() { try { Directory.Delete(root, true); } catch { } }

	string Dir(params string[] parts) {
		string p = Path.Combine(new[] { root }.Concat(parts).ToArray());
		Directory.CreateDirectory(p);
		return p;
	}

	/// <summary>A file of whole 64 KB blocks: never kept in the folder's own record, and as big on disk as it is long, whatever the drive's cluster size.</summary>
	static string File64(string folder, string name, int blocks) {
		string path = Path.Combine(folder, name);
		File.WriteAllBytes(path, Enumerable.Repeat((byte)7, blocks * 65536).ToArray());
		return path;
	}

	FolderSizes Sizes() => new(() => now);

	[Fact]
	public void AFolder_CountsEveryFileBelowIt_ByItsSizeOnDisk() {
		string photos = Dir("Photos");
		string trip = Dir("Photos", "Trip");
		File64(photos, "a.jpg", 1);
		File64(trip, "b.jpg", 2);
		File64(Dir("Photos", "Trip", "Day 1"), "c.jpg", 3);
		File64(root, "d.jpg", 1);

		FolderTotals totals = Sizes().Measure(root);

		Assert.Equal(7 * 65536L, totals.Bytes);
		Assert.Equal(4, totals.Files);
		Assert.Equal(0, totals.Unreadable);
		Assert.False(totals.Denied);
	}

	[Fact]
	public void ASparseFile_CountsWhatItTakesOnDisk_NotItsLength() {
		// As a file kept only online in OneDrive: long, but next to nothing on this PC's drive.
		string path = Path.Combine(Dir("Online"), "film.mp4");
		using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite)) {
			Assert.True(DeviceIoControl(f.SafeFileHandle, SetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero), "the drive keeps no sparse files");
			f.SetLength(256L * 1024 * 1024);
		}
		Assert.Equal(256L * 1024 * 1024, new FileInfo(path).Length);

		FolderTotals totals = Sizes().Measure(root);

		Assert.Equal(1, totals.Files);
		Assert.True(totals.Bytes < 1024 * 1024, $"{totals.Bytes} bytes counted for a file that takes next to none");
	}

	[Fact]
	public void AJunction_IsNeverFollowed_SoNothingIsCountedTwice() {
		string photos = Dir("Photos");
		File64(photos, "a.jpg", 2);
		string link = RealTemp.Junction(Path.Combine(Dir("Shortcuts"), "Photos link"), photos);
		var sizes = Sizes();

		FolderTotals totals = sizes.Measure(root);

		Assert.Equal(2 * 65536L, totals.Bytes);
		Assert.Equal(1, totals.Files);
		// The link shows as one, with nothing counted in it.
		FolderSizesView shortcuts = sizes.Look([Path.Combine(root, "Shortcuts")]).Single();
		FolderSizeView linked = Assert.Single(shortcuts.Children);
		Assert.Equal(link, linked.Path, ignoreCase: true);
		Assert.True(linked.Link);
		Assert.Equal(0, linked.Bytes);
		Assert.Equal(0, shortcuts.Own!.Bytes);
	}

	[Fact]
	public void AFolderWindowsWontList_IsSkippedAndCounted_AndTheRestIsMeasured() {
		File64(Dir("Open"), "a.jpg", 1);
		string closed = Dir("Open", "Closed");
		File64(closed, "secret.jpg", 4);
		var me = WindowsIdentity.GetCurrent().User!;
		var deny = new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny);
		var dir = new DirectoryInfo(closed);
		DirectorySecurity acl = dir.GetAccessControl();
		acl.AddAccessRule(deny);
		dir.SetAccessControl(acl);
		try {
			var sizes = Sizes();
			FolderTotals totals = sizes.Measure(root);

			Assert.Equal(65536L, totals.Bytes);
			Assert.Equal(1, totals.Files);
			Assert.Equal(1, totals.Unreadable);
			FolderSizeView shown = sizes.Look([Path.Combine(root, "Open")]).Single().Children.Single();
			Assert.True(shown.Denied);
		}
		finally {
			acl = dir.GetAccessControl();
			acl.RemoveAccessRule(deny);
			dir.SetAccessControl(acl);
		}
	}

	[Fact]
	public void ThePage_GetsWhatsMeasured_AndTheRestIsMeasuredApart() {
		File64(Dir("Photos"), "a.jpg", 1);
		File64(Dir("Music"), "b.mp3", 2);
		var sizes = Sizes();

		// Nothing is measured on the page's request: it's asked for, and the page asks again.
		FolderSizesView first = sizes.Look([root]).Single();
		Assert.True(first.Pending);
		Assert.Null(first.Own);
		Assert.Empty(first.Children);

		sizes.Drain();
		FolderSizesView second = sizes.Look([root]).Single();
		Assert.False(second.Pending);
		Assert.Equal(root, second.Path);
		Assert.Equal(3 * 65536L, second.Own!.Bytes);
		Assert.Equal(["Music", "Photos"], second.Children.Select(c => Path.GetFileName(c.Path)));
		Assert.Equal([2 * 65536L, 65536L], second.Children.Select(c => c.Bytes!.Value));

		// A subfolder's own subfolders are kept too: opening it needs nothing more.
		Assert.False(sizes.Look([Path.Combine(root, "Photos")]).Single().Pending);
	}

	[Fact]
	public void ASize_StandsForAWhile_ThenIsMeasuredAgain_ShowingTheOldMeanwhile() {
		string photos = Dir("Photos");
		File64(photos, "a.jpg", 1);
		var sizes = Sizes();
		sizes.Look([root]);
		sizes.Drain();
		File64(photos, "b.jpg", 1);

		now += FolderSizes.Keep - TimeSpan.FromMinutes(1);
		Assert.Equal(65536L, sizes.Look([root]).Single().Own!.Bytes); // still the size measured

		now += TimeSpan.FromMinutes(2);
		FolderSizesView stale = sizes.Look([root]).Single();
		Assert.True(stale.Pending);
		Assert.Equal(65536L, stale.Own!.Bytes);
		sizes.Drain();
		Assert.Equal(2 * 65536L, sizes.Look([root]).Single().Own!.Bytes);
	}

	[Fact]
	public void AFolderThePageStoppedAskingAbout_IsntMeasured() {
		File64(Dir("Photos"), "a.jpg", 1);
		var sizes = Sizes();
		sizes.Look([root]);

		now += FolderSizes.Wanted + TimeSpan.FromSeconds(1);
		sizes.Drain();

		Assert.Null(sizes.Look([root]).Single().Own);
	}

	[Fact]
	public void WhatHeiwardRecycles_IsMeasuredAgain_InEveryFolderAboveIt() {
		string trip = Dir("Photos", "Trip");
		string gone = File64(trip, "a.jpg", 2);
		File64(Dir("Music"), "b.mp3", 1);
		var sizes = Sizes();
		sizes.Look([root, Path.Combine(root, "Photos")]);
		sizes.Drain();

		File.Delete(gone);
		sizes.Forget([gone]);

		FolderSizesView top = sizes.Look([root]).Single();
		Assert.True(top.Pending);
		Assert.Null(top.Own);
		Assert.Equal(65536L, top.Children.Single(c => c.Path.EndsWith("Music", StringComparison.Ordinal)).Bytes); // beside it: as it was
		sizes.Drain();
		Assert.Equal(65536L, sizes.Look([root]).Single().Own!.Bytes);
	}

	[Fact]
	public void TheEndpoint_AnswersOnlyForWhatHeiwardScans_UnderThePathAsked() {
		File64(Dir("Photos"), "a.jpg", 1);
		string outside = RealTemp.Folder("hei-sizes-outside-");
		Directory.CreateDirectory(outside);
		try {
			var sizes = Sizes();
			string asked = root + Path.DirectorySeparatorChar;
			string json = JsonSerializer.Serialize(ReviewServer.Sizes([asked, outside, "relative\\path", null, Path.Combine(root, "Gone")], [root], sizes), AgentConfig.Json);
			using var answer = JsonDocument.Parse(json);
			JsonElement folders = answer.RootElement.GetProperty("folders");
			Assert.Equal(1, folders.GetArrayLength());
			Assert.Equal(asked, folders[0].GetProperty("path").GetString());
			Assert.True(answer.RootElement.GetProperty("pending").GetBoolean());

			sizes.Drain();
			using var again = JsonDocument.Parse(JsonSerializer.Serialize(ReviewServer.Sizes([asked], [root], sizes), AgentConfig.Json));
			Assert.False(again.RootElement.GetProperty("pending").GetBoolean());
			JsonElement own = again.RootElement.GetProperty("folders")[0].GetProperty("own");
			Assert.Equal(65536L, own.GetProperty("bytes").GetInt64());
			Assert.Equal(1, own.GetProperty("files").GetInt64());

			// At most as many as the page's tree and folder take.
			var many = Enumerable.Range(0, 50).Select(i => Dir("F" + i)).ToList();
			using var capped = JsonDocument.Parse(JsonSerializer.Serialize(ReviewServer.Sizes(many, [root], sizes), AgentConfig.Json));
			Assert.Equal(FolderSizes.MaxAsked, capped.RootElement.GetProperty("folders").GetArrayLength());
		}
		finally { Directory.Delete(outside, true); }
	}

	[Fact]
	public void Measuring_WritesNothing() {
		File64(Dir("Photos"), "a.jpg", 1);
		var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order().ToList();
		var sizes = Sizes();
		sizes.Look([root]);
		sizes.Drain();
		Assert.Equal(before, Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order().ToList());
	}

	const uint SetSparse = 0x000900C4; // FSCTL_SET_SPARSE

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
