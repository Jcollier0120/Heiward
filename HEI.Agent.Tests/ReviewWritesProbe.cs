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
using HEI.Core;
using HEI.Core.ViewModels;

namespace HEI.Agent.Tests;

/// <summary>
/// What the review writes to disk, the owner's rule measured (docs/LOOKALIKE-BAR.md): on a made-up library of
/// HEI_WRITES_PHOTOS photos (default 50,000), a third of them in sets (copies, and look-alikes the AI matched), each
/// copy a small file on disk under HEI_WRITES_PROBE so its hash is real. It counts this process's bytes written
/// (GetProcessIoCounters) for the review's first scan (its sets sorted, the hashes and close looks kept, the
/// report saved), then for a scan that finds the same files again, as a drive without a journal is scanned
/// every 6 hours while nothing changes. Runs only when HEI_WRITES_PROBE is set, with HEIWARD_HOME on a scratch
/// folder; prints to HEI_WRITES_PROBE\writes.txt.
/// </summary>
public class ReviewWritesProbe {
	[StructLayout(LayoutKind.Sequential)]
	struct IoCounters {
		public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool GetProcessIoCounters(nint process, out IoCounters counters);

	static ulong Written() {
		GetProcessIoCounters(System.Diagnostics.Process.GetCurrentProcess().Handle, out IoCounters c);
		return c.WriteTransferCount;
	}

	sealed class Fingerprints : IFingerprints {
		public readonly Dictionary<string, (float Gray, float Ai)> Look = new(StringComparer.OrdinalIgnoreCase);
		public float? GrayPercent(string a, string b) => Look.TryGetValue(a, out var x) && Look.TryGetValue(b, out var y) ? Math.Min(x.Gray, y.Gray) : null;
		public float? AiPercent(string a, string b) => Look.TryGetValue(a, out var x) && Look.TryGetValue(b, out var y) ? Math.Min(x.Ai, y.Ai) : null;
		public float? AudioPercent(string a, string b) => null;
		public int? MaxCellDifference(string a, string b) => 0;
		public bool? SameUpClose(string a, string b) => true;
		public bool? SameColors(string a, string b) => true;
	}

	[Fact]
	public void Measure() {
		string? root = Environment.GetEnvironmentVariable("HEI_WRITES_PROBE");
		if (string.IsNullOrEmpty(root))
			return;
		int photos = int.TryParse(Environment.GetEnvironmentVariable("HEI_WRITES_PHOTOS"), out int n) ? n : 50_000;
		string library = Path.Combine(root, $"library-{photos}");
		var fingerprints = new Fingerprints();
		var duplicates = new List<DuplicateItem>();
		var rng = new Random(1);
		DateTime when = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		// A third of the library in sets of 2 or 3: three in four are copies (the same bytes), one in four look-alikes.
		for (int set = 0, photo = 0; photo < photos / 3; set++) {
			int size = 2 + set % 2;
			bool copies = set % 4 != 3;
			var group = Guid.NewGuid();
			byte[] content = new byte[1500 + rng.Next(3000)];
			rng.NextBytes(content);
			for (int k = 0; k < size; k++, photo++) {
				// Names without numbers: numbered names next to each other are a burst (BurstSeries), never copies.
				string path = Path.Combine(library, $"{set / 1000:D3}", $"photo {Letters(set)}{string.Concat(Enumerable.Repeat(" - copy", k))}.jpg");
				if (!copies) content[0] = (byte)k;
				if (!File.Exists(path)) {
					Directory.CreateDirectory(Path.GetDirectoryName(path)!);
					File.WriteAllBytes(path, content);
					File.SetLastWriteTimeUtc(path, when);
				}
				fingerprints.Look[path] = copies ? (100f, 100f) : (94f, 95.5f);
				duplicates.Add(new DuplicateItem {
					Path = path, Folder = Path.GetDirectoryName(path)!, SizeLong = content.Length, IsImage = true, FrameSize = "4032x3024",
					FrameSizeInt = 4032 + 3024, Similarity = copies ? 100f : 95.5f, GroupId = group,
					Flags = copies ? DuplicateFlags.None : DuplicateFlags.AiMatched, DateModified = when.ToLocalTime(),
				});
			}
		}
		var lines = new List<string> { $"{photos:N0} photos, {duplicates.Count:N0} in sets" };
		for (int scan = 1; scan <= 2; scan++) {
			ulong before = Written();
			var hashes = ReportBuilder.ContentHashes.Load();
			List<ReportGroup> groups = ReportBuilder.Build(duplicates, fingerprints, hashes);
			hashes.Save();
			new Report(Report.CurrentVersion, DateTime.UtcNow, 1, "cpu", photos, new() { library }, new(), new(), groups, AppBuild.Current).Save();
			ulong bytes = Written() - before;
			long onDisk = new[] { AgentPaths.Report, ReportBuilder.ContentHashes.FilePath, ReportBuilder.ContentHashes.LooksPath, ReportBuilder.ContentHashes.TextsPath }
				.Where(File.Exists).Sum(f => new FileInfo(f).Length);
			lines.Add($"scan {scan}: {groups.Count:N0} sets, {bytes:N0} bytes written ({(double)bytes / photos:F1} per photo); report and hashes on disk {onDisk:N0} bytes");
		}
		File.WriteAllLines(Path.Combine(root, $"writes-{photos}.txt"), lines);
	}

	static string Letters(int i) {
		var s = new System.Text.StringBuilder();
		do { s.Insert(0, (char)('a' + i % 26)); i /= 26; } while (i > 0);
		return s.ToString();
	}
}
