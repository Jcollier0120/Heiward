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

using System.Buffers;
using System.Runtime.InteropServices;
using HEI.Core.Utils;

namespace HEI.Agent {
	sealed record RecycleFailure(string Path, string Reason);
	sealed record RecycleResult(List<string> Recycled, List<RecycleFailure> Failed, long RecycledBytes);

	/// <summary>
	/// Moves files the user checked to the Recycle Bin — and only there. With "no confirmation", the
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
			// At least one copy must survive: still on disk as the scan saw it (else it may not be the picture the user
			// saw kept, and the file removed could be the only original left), and not the very file being removed under
			// another path (a hard link, or the same folder under another drive letter).
			var others = group.Items.Where(i => !chosen.Contains(i.Path, StringComparer.OrdinalIgnoreCase)).ToList();
			var unchanged = others.Where(i => ChangedSinceScan(i) == null).ToList();
			var survivors = unchanged.Where(i => !chosen.Any(p => HardLinkUtils.AreSameFile(i.Path, p))).ToList();
			if (survivors.Count == 0) {
				string why = others.Count == 0 || others.All(i => !File.Exists(i.Path)) ? "that would remove every copy; keep at least one"
					: unchanged.Count == 0 ? "the copy you'd keep changed since the scan; scan again first"
					: "the copy you'd keep is this same file under another path (a hard link, or another drive letter for the folder); keep another copy";
				failed.AddRange(chosen.Select(p => new RecycleFailure(p, why)));
				return new(new(), failed, 0);
			}

			var ok = new List<string>();
			var room = new BinRoom();
			foreach (string p in chosen) {
				string? why = RefuseReason(p, byPath[p], survivors, room);
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
			RecycleBinSize.Forget();
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
			RecycleBinSize.Forget();
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
		/// <param name="survivors">The copies that stay, unchanged since the scan.</param>
		static string? RefuseReason(string path, ReportItem item, List<ReportItem> survivors, BinRoom room) {
			if (ChangedSinceScan(item) is { } changed)
				return changed;
			var fi = new FileInfo(path);
			// The shell deletes for good what it can't put in the bin, a path too long for it among them.
			if (fi.FullName.Length > RecycleBin.MaxPath)
				return $"its path is longer than the Recycle Bin takes ({RecycleBin.MaxPath} characters); delete it yourself if you're sure";
			return NotTheSameBytes(item, survivors) ?? BinRefusal(fi.FullName, fi.Length, room);
		}

		/// <summary>Why the file isn't as the scan saw it (gone, or another size or last-modified time), or null.</summary>
		static string? ChangedSinceScan(ReportItem item) {
			var fi = new FileInfo(item.Path);
			if (!fi.Exists)
				return "already gone";
			if (fi.Length != item.Size || Math.Abs((fi.LastWriteTimeUtc - item.ModifiedUtc).TotalSeconds) > 2)
				return "changed since the scan; scan again first";
			return null;
		}

		/// <summary>
		/// An "identical" file over <see cref="ReportBuilder.ContentHashes.FullHashBytes"/> was matched by its size and samples
		/// across it, not every byte: an unfinished or damaged copy can pass. Before it goes, it's read through beside a copy
		/// that stays; the reason when they differ, or either can't be read.
		/// </summary>
		static string? NotTheSameBytes(ReportItem item, List<ReportItem> survivors) {
			if (item.Relation != "identical" || item.Size <= ReportBuilder.ContentHashes.FullHashBytes)
				return null;
			ReportItem? kept = survivors.FirstOrDefault(s => s.Keep) ?? survivors.FirstOrDefault(s => s.Relation == "identical");
			if (kept == null || SameBytes(item.Path, kept.Path))
				return null;
			return $"not the same bytes as {kept.Name} after all, read through to the end (it may be an unfinished or damaged copy); compare them yourself";
		}

		/// <summary>The two files hold the same bytes, each read once from start to end in 4 MB steps; false when either can't be read.</summary>
		internal static bool SameBytes(string a, string b) {
			const int Step = 4 << 20;
			byte[] ba = ArrayPool<byte>.Shared.Rent(Step), bb = ArrayPool<byte>.Shared.Rent(Step);
			try {
				using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
				using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
				if (fa.Length != fb.Length)
					return false;
				while (true) {
					int ra = fa.ReadAtLeast(ba.AsSpan(0, Step), Step, throwOnEndOfStream: false);
					int rb = fb.ReadAtLeast(bb.AsSpan(0, Step), Step, throwOnEndOfStream: false);
					if (ra != rb || !ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb)))
						return false;
					if (ra < Step)
						return true;
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return false;
			}
			finally {
				ArrayPool<byte>.Shared.Return(ba);
				ArrayPool<byte>.Shared.Return(bb);
			}
		}

		/// <summary>Why the Recycle Bin can't take it, so the shell would delete it for good (<see cref="RecycleBin.Refusal"/>); null when it can.</summary>
		internal static string? BinRefusal(string fullPath, long bytes, BinRoom? room = null) => RecycleBin.Refusal(fullPath, bytes, room);

		/// <summary>The room left in each drive's Recycle Bin, counted down as a batch fills it (<see cref="RecycleBin.Room"/>).</summary>
		internal sealed class BinRoom(Func<string, long>? used = null) : RecycleBin.Room(used);
	}
}
