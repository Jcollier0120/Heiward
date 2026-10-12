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
using HEI.Core.Utils;

namespace HEI.Agent {
	sealed record RecycleFailure(string Path, string Reason);
	sealed record RecycleResult(List<string> Recycled, List<RecycleFailure> Failed, long RecycledBytes);

	/// <summary>
	/// Moves files the user ticked to the Recycle Bin — and only there. With "no confirmation", the
	/// shell silently deletes for good whatever the bin can't take (network and removable drives,
	/// files bigger than the bin, volumes set to "don't move files to the Recycle Bin"), so every
	/// such file is refused up front instead. Also refused: files outside the group, files changed
	/// since the scan, and any request that would leave the group without a single copy.
	/// </summary>
	static class Recycler {
		public static RecycleResult Recycle(ReportGroup group, IReadOnlyCollection<string> requested) {
			var failed = new List<RecycleFailure>();
			var byPath = group.Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
			var chosen = requested.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

			foreach (string p in chosen.Where(p => !byPath.ContainsKey(p)).ToList()) {
				failed.Add(new(p, "not part of this group"));
				chosen.Remove(p);
			}
			// At least one copy must survive, and it must still be on disk.
			var survivors = group.Items.Where(i => !chosen.Contains(i.Path, StringComparer.OrdinalIgnoreCase) && File.Exists(i.Path)).ToList();
			if (survivors.Count == 0) {
				failed.AddRange(chosen.Select(p => new RecycleFailure(p, "that would remove every copy; keep at least one")));
				return new(new(), failed, 0);
			}

			var ok = new List<string>();
			var room = new BinRoom();
			foreach (string p in chosen) {
				string? why = RefuseReason(p, byPath[p], room);
				if (why != null) failed.Add(new(p, why));
				else ok.Add(p);
			}
			if (ok.Count == 0)
				return new(new(), failed, 0);

			var op = new FileUtils.SHFILEOPSTRUCT {
				wFunc = FileUtils.FileOperationType.FO_DELETE,
				pFrom = string.Join('\0', ok) + "\0\0",
				fFlags = FileUtils.FileOperationFlags.FOF_ALLOWUNDO | FileUtils.FileOperationFlags.FOF_NOCONFIRMATION |
						 FileUtils.FileOperationFlags.FOF_NOERRORUI | FileUtils.FileOperationFlags.FOF_SILENT,
			};
			int rc = FileUtils.SHFileOperation(ref op);
			var recycled = new List<string>();
			long bytes = 0;
			foreach (string p in ok) {
				if (File.Exists(p))
					failed.Add(new(p, rc != 0 ? $"Windows refused (error {rc})" : "still there after the move"));
				else {
					recycled.Add(p);
					bytes += byPath[p].Size;
				}
			}
			AgentPaths.AppendLog($"recycled {recycled.Count} file(s) ({bytes:N0} bytes) from group {group.Key}; {failed.Count} refused");
			return new(recycled, failed, bytes);
		}

		/// <summary>
		/// Moves files and folders to the Recycle Bin, the games area's way: each refused up front where the bin can't take it
		/// (<see cref="BinRefusal"/>), so the shell never deletes anything for good. A folder's size is what it holds.
		/// </summary>
		public static RecycleResult RecyclePaths(IReadOnlyCollection<string> paths) {
			var failed = new List<RecycleFailure>();
			var ok = new List<(string Path, long Bytes)>();
			var room = new BinRoom();
			foreach (string p in paths.Distinct(StringComparer.OrdinalIgnoreCase)) {
				bool folder = Directory.Exists(p);
				if (!folder && !File.Exists(p)) continue; // already gone
				long size = folder ? DevScanner.Measure(p).Bytes : new FileInfo(p).Length;
				// The shell deletes for good what it can't put in the bin, a path too long for it among them.
				if (folder && TooLongInside(p) is { } tooLong) failed.Add(new(p, $"a path in it is too long for the Recycle Bin ({tooLong}); delete it yourself if you're sure"));
				else if (BinRefusal(Path.GetFullPath(p), size, room) is { } why) failed.Add(new(p, why));
				else ok.Add((p, size));
			}
			if (ok.Count == 0) return new(new(), failed, 0);
			var op = new FileUtils.SHFILEOPSTRUCT {
				wFunc = FileUtils.FileOperationType.FO_DELETE,
				pFrom = string.Join('\0', ok.Select(o => o.Path)) + "\0\0",
				fFlags = FileUtils.FileOperationFlags.FOF_ALLOWUNDO | FileUtils.FileOperationFlags.FOF_NOCONFIRMATION |
						 FileUtils.FileOperationFlags.FOF_NOERRORUI | FileUtils.FileOperationFlags.FOF_SILENT,
			};
			int rc = FileUtils.SHFileOperation(ref op);
			var recycled = new List<string>();
			long bytes = 0;
			foreach (var (p, size) in ok) {
				if (Path.Exists(p))
					failed.Add(new(p, rc != 0 ? $"Windows refused (error {rc}): a file in it may be in use" : "still there after the move"));
				else {
					recycled.Add(p);
					bytes += size;
				}
			}
			return new(recycled, failed, bytes);
		}

		/// <summary>The first path below <paramref name="folder"/> longer than the Recycle Bin takes (<see cref="RecycleBin.TooLongInside"/>).</summary>
		internal static string? TooLongInside(string folder) => RecycleBin.TooLongInside(folder);

		/// <summary>Why <paramref name="path"/> must not go through the shell's delete, or null when it may.</summary>
		static string? RefuseReason(string path, ReportItem item, BinRoom room) {
			var fi = new FileInfo(path);
			if (!fi.Exists)
				return "already gone";
			if (fi.Length != item.Size || Math.Abs((fi.LastWriteTimeUtc - item.ModifiedUtc).TotalSeconds) > 2)
				return "changed since the scan; scan again first";
			return BinRefusal(fi.FullName, fi.Length, room);
		}

		/// <summary>Why the Recycle Bin can't take it, so the shell would delete it for good (<see cref="RecycleBin.Refusal"/>); null when it can.</summary>
		internal static string? BinRefusal(string fullPath, long bytes, BinRoom? room = null) => RecycleBin.Refusal(fullPath, bytes, room);

		/// <summary>The room left in each drive's Recycle Bin, counted down as a batch fills it (<see cref="RecycleBin.Room"/>).</summary>
		internal sealed class BinRoom(Func<string, long>? used = null) : RecycleBin.Room(used);
	}
}
