// /*
//     Copyright (C) 2026 0x90d
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

using HEI.Core.ViewModels;
using HEI.GUI.ViewModels;

namespace HEI.GUI.HeadlessTests;

/// <summary>
/// A view model holding two duplicate groups of two videos each, list already built: every
/// kind of row the Results view draws. The first group's files are on disk; of the second,
/// wedding.mkv is on a drive that is not there (the "Offline" badge) and wedding_small.mp4 is
/// gone from a drive that is ("Already deleted").
/// </summary>
/// <remarks>
/// The badges come from the real filesystem (<see cref="HEI.Core.ScanEngine.PathIsOffline"/>),
/// so the paths are picked on the machine that runs the tests. Fixed paths on D:\ and E:\
/// showed four Offline rows where those drives were missing and four Already deleted rows where
/// they were present, and the contrast guard checked whichever badge it got.
/// </remarks>
static class ResultsFixture {
	/// <summary>Per test run; deleted when the test process exits.</summary>
	static readonly string root = CreateRoot();

	/// <summary>The folder of the first group's original; the file exists.</summary>
	public static string HolidayFolder { get; } = Path.Combine(root, "Videos", "Holiday");

	/// <summary>A drive letter this machine does not have, so a file on it is offline.</summary>
	static readonly string absentDrive = AbsentDriveRoot();

	public static MainWindowVM CreatePopulatedViewModel() {
		var vm = new MainWindowVM();
		var first = Guid.NewGuid();
		var second = Guid.NewGuid();
		string original = Path.Combine(HolidayFolder, "beach_2019_final.mp4");
		string copy = Path.Combine(HolidayFolder, "copy", "beach_2019_final (1).mp4");
		string offline = absentDrive + @"Archive\Old\wedding.mkv";
		string deleted = Path.Combine(root, "Archive", "Old", "wedding_small.mp4");
		OnDisk(original);
		OnDisk(copy);
		Add(vm, original, first, 100f, 1_900_000_000, 1920, best: true);
		Add(vm, copy, first, 98.4f, 700_000_000, 1280, best: false);
		Add(vm, offline, second, 100f, 3_100_000_000, 3840, best: true);
		Add(vm, deleted, second, 91.2f, 400_000_000, 854, best: false);

		// Tests measure what these rows show, so a machine that classifies them otherwise
		// has to stop the run here, not pass it on whichever badges it happened to draw.
		var d = vm.Duplicates;
		if (d[0].IsOffline || d[0].IsTombstone || d[1].IsOffline || d[1].IsTombstone
			|| !d[2].IsOffline || !d[3].IsTombstone)
			throw new InvalidOperationException(
				"The results fixture needs two files on disk, one offline and one already deleted; this machine shows " +
				string.Join(", ", d.Select(i => $"{i.ItemInfo.Path}: {(i.IsOffline ? "offline" : i.IsTombstone ? "already deleted" : "on disk")}")));

		vm.RebuildResultsList();
		return vm;
	}

	static void Add(MainWindowVM vm, string path, Guid group, float similarity, long size, int width, bool best) {
		int height = width * 9 / 16;
		vm.Duplicates.Add(new DuplicateItemVM(new DuplicateItem {
			Path = path,
			GroupId = group,
			Similarity = similarity,
			SizeLong = size,
			// Not Path.GetDirectoryName: the offline path keeps its backslashes on Linux too.
			Folder = path[..path.LastIndexOfAny(['\\', '/'])],
			Duration = TimeSpan.FromSeconds(754),
			FrameSize = $"{width}x{height}",
			FrameSizeInt = width * height,
			Format = "h264",
			AudioFormat = "aac",
			AudioChannel = "stereo",
			Fps = 29.97f,
			BitRateKbs = 4200,
			AudioBitRateKbs = 192,
			DateCreated = new DateTime(2024, 5, 1),
			IsBestSize = best,
			IsBestFrameSize = best,
			IsBestBitRateKbs = best,
			IsBestDuration = true,
		}));
	}

	/// <summary>An empty file: the view only asks whether it is there.</summary>
	static void OnDisk(string path) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		if (!File.Exists(path))
			File.WriteAllBytes(path, []);
	}

	static string CreateRoot() {
		string path = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hei-results-" + Guid.NewGuid().ToString("N"))).FullName;
		AppDomain.CurrentDomain.ProcessExit += (_, _) => {
			try { Directory.Delete(path, recursive: true); } catch { }
		};
		return path;
	}

	/// <summary>
	/// A drive letter no volume has here, local or mapped, so nothing on it is looked up on a
	/// network (a UNC path would be). Linux has no drive letters: the path does not start at
	/// a root there, which counts as offline as well.
	/// </summary>
	static string AbsentDriveRoot() {
		if (!OperatingSystem.IsWindows())
			return @"Z:\";
		var present = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
		for (char letter = 'Z'; letter >= 'D'; letter--)
			if (!present.Contains(letter))
				return letter + @":\";
		throw new InvalidOperationException("Every drive letter is in use; the results fixture needs one that is not.");
	}
}
