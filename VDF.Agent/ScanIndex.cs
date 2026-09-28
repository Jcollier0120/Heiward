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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace VDF.Agent {
	/// <summary>One drive's part of the last scan: wall-clock times and what it holds.</summary>
	sealed record DriveScan(double ListingSec, double AnalysisSec, int Files, long Bytes) {
		[JsonIgnore] public double TotalSec => ListingSec + AnalysisSec;
	}

	/// <summary>The photos and videos directly in one folder (not its subfolders).</summary>
	sealed record FolderFiles(int Files, long Bytes);

	/// <summary>
	/// Where the last scan found photos and videos, per folder, and how long each drive took. The review
	/// page's drive cards and folder tree read it; the report only holds the duplicates.
	/// </summary>
	sealed class ScanIndex {
		public DateTime ScannedAtUtc { get; set; }
		public List<string> Roots { get; set; } = new();
		public Dictionary<string, DriveScan> Drives { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, FolderFiles> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

		string[]? sorted;

		public static string FilePath => Path.Combine(AgentPaths.Home, "index.json");

		/// <summary>
		/// From a finished scan: the files it listed, each listed folder's listing time, and each drive's
		/// analysis time (keyed by drive root). A folder root's listing time counts toward its drive.
		/// </summary>
		public static ScanIndex Build(DateTime scannedAtUtc, IEnumerable<string> roots, IEnumerable<(string Path, long Size)> files,
			IReadOnlyDictionary<string, TimeSpan> listing, IReadOnlyDictionary<string, TimeSpan> analysis) {
			var index = new ScanIndex { ScannedAtUtc = scannedAtUtc, Roots = roots.ToList() };
			var perDrive = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
			foreach ((string path, long size) in files) {
				string folder = Path.GetDirectoryName(path) ?? path;
				index.Folders.TryGetValue(folder, out FolderFiles? f);
				index.Folders[folder] = new FolderFiles((f?.Files ?? 0) + 1, (f?.Bytes ?? 0) + size);
				string drive = DriveOf(path);
				perDrive.TryGetValue(drive, out var d);
				perDrive[drive] = (d.Files + 1, d.Bytes + size);
			}
			var driveRoots = index.Roots.Select(DriveOf).Concat(perDrive.Keys).Concat(analysis.Keys.Select(DriveOf))
				.Distinct(StringComparer.OrdinalIgnoreCase);
			foreach (string drive in driveRoots) {
				double listingSec = listing.Where(kv => DriveOf(kv.Key).Equals(drive, StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value.TotalSeconds);
				double analysisSec = analysis.Where(kv => DriveOf(kv.Key).Equals(drive, StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value.TotalSeconds);
				perDrive.TryGetValue(drive, out var d);
				index.Drives[drive] = new DriveScan(Math.Round(listingSec, 2), Math.Round(analysisSec, 2), d.Files, d.Bytes);
			}
			return index;
		}

		/// <summary>C:\ for C:\Photos\a.jpg, \\nas\share\ for a share.</summary>
		public static string DriveOf(string path) {
			string root = Path.GetPathRoot(path) ?? path;
			return root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
		}

		/// <summary>Photos and videos in <paramref name="folder"/> and everything below it.</summary>
		public (int Files, long Bytes) Subtree(string folder) {
			int files = 0;
			long bytes = 0;
			foreach (string f in Below(folder, includeSelf: true)) {
				files += Folders[f].Files;
				bytes += Folders[f].Bytes;
			}
			return (files, bytes);
		}

		/// <summary>Whether any subfolder of <paramref name="folder"/> holds photos or videos.</summary>
		public bool HasFilesBelow(string folder) => Below(folder, includeSelf: false).Any();

		// Folder paths sorted ordinally (ignoring case): the ones below a folder form one contiguous run.
		IEnumerable<string> Below(string folder, bool includeSelf) {
			sorted ??= Folders.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
			string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar);
			if (includeSelf && Folders.ContainsKey(trimmed))
				yield return trimmed;
			string prefix = trimmed + Path.DirectorySeparatorChar;
			int i = Array.BinarySearch(sorted, prefix, StringComparer.OrdinalIgnoreCase);
			for (i = i < 0 ? ~i : i; i < sorted.Length && sorted[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase); i++)
				yield return sorted[i];
		}

		public static ScanIndex? Load() {
			try {
				if (File.Exists(FilePath))
					return JsonSerializer.Deserialize<ScanIndex>(File.ReadAllText(FilePath), AgentConfig.Json) is { } index
						? Normalized(index) : null;
			}
			catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
				AgentPaths.AppendLog($"index.json unreadable: {e.Message}");
			}
			return null;
		}

		// A deserialized dictionary loses the case-insensitive comparer.
		static ScanIndex Normalized(ScanIndex index) {
			index.Drives = new(index.Drives, StringComparer.OrdinalIgnoreCase);
			index.Folders = new(index.Folders, StringComparer.OrdinalIgnoreCase);
			return index;
		}

		public void Save() => AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(this, AgentConfig.Json));
	}
}
