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

using System.Diagnostics;
using HEI.Agent.Estate;

namespace HEI.Agent.Tests;

/// <summary>
/// What Heiward 1.11.0 to 1.11.5 kept of the PCs it scanned for (a stand-in of each of their files, never removed) goes by
/// itself after the update: a batch at a time, only Heiward's own mirror folders, never one a scan is using, and a sweep
/// cut off partway picks up where it stopped.
/// </summary>
[Collection(AgentHomeCollection.Name)] // OldMirrors' batch and rest are process-wide
public sealed class OldMirrorsTests : IDisposable {
	readonly string dir = Directory.CreateTempSubdirectory("hei-old-mirrors-").FullName;
	readonly string remote;
	readonly int batch = OldMirrors.Batch;
	readonly TimeSpan rest = OldMirrors.Rest;

	public OldMirrorsTests() {
		remote = Path.Combine(dir, "Heiward", "remote");
		OldMirrors.Batch = 100;
		OldMirrors.Rest = TimeSpan.Zero;
	}

	public void Dispose() {
		OldMirrors.Batch = batch;
		OldMirrors.Rest = rest;
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>An older Heiward's mirror of <paramref name="count"/> files, in folders of 40 as a PC's photos are; how many folders.</summary>
	static int Plant(string mirror, int count) {
		int folders = 0;
		for (int i = 0; i < count; i++) {
			string folder = Path.Combine(mirror, "C", "Users", "Sam", "Pictures", $"{i / 40:D3}");
			if (i % 40 == 0) {
				Directory.CreateDirectory(folder);
				folders++;
			}
			using var fs = new FileStream(Path.Combine(folder, $"IMG_{i:D5}.jpg"), FileMode.CreateNew);
			fs.SetLength(1000 + i);
		}
		return folders;
	}

	static string[] Files(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];

	static string[] Mirrors(string home) => Directory.GetDirectories(home, "mirror*");

	[Fact]
	public void OldStandIns_GoABatchAtATime_OnlyHeiwardsOwn_AndASweepCutOffPicksUpWhereItStopped() {
		string pcA = Path.Combine(remote, "pc-a"), pcB = Path.Combine(remote, "pc-b"), pcC = Path.Combine(remote, "pc-c");
		Plant(Path.Combine(pcA, "mirror"), 1_200);
		File.WriteAllText(Path.Combine(pcA, "mirror.empty"), "");
		string[] kept = [Path.Combine(pcA, "listing.tsv"), Path.Combine(pcA, "findings.json"), Path.Combine(pcA, "db", "ScannedFiles.db")];
		foreach (string k in kept) {
			Directory.CreateDirectory(Path.GetDirectoryName(k)!);
			File.WriteAllText(k, "kept");
		}
		// A link out of its mirror, to a folder that isn't Heiward's: the link goes, never what it links to.
		string outside = Path.Combine(dir, "Pictures");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "mine.jpg"), "mine");
		string link = Path.Combine(pcA, "mirror", "C", "Users", "Sam", "Linked");
		using (Process mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
			mklink.WaitForExit();
		Assert.True(File.Exists(Path.Combine(link, "mine.jpg")));
		// A scan for pc-b runs (the page's own); one for pc-c holds its scan lock.
		Plant(Path.Combine(pcB, "mirror"), 10);
		Plant(Path.Combine(pcC, "mirror"), 10);
		using var cLock = new FileStream(Path.Combine(pcC, "scan.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
		bool Scanning(string home) => string.Equals(home, pcB, StringComparison.OrdinalIgnoreCase);

		// Cut off after three batches (the page stopped, Heiward was updated).
		using var cut = new CancellationTokenSource();
		int rests = 0;
		OldMirrors.Swept first = OldMirrors.Sweep(remote, Scanning, cut.Token, hold: () => {
			if (++rests == 3) cut.Cancel();
			return false;
		});
		Assert.Equal(3, rests);
		Assert.InRange(first.Files, 200, 300);
		Assert.False(Directory.Exists(Path.Combine(pcA, "mirror")));
		Assert.False(File.Exists(Path.Combine(pcA, "mirror.empty")));
		string aside = Assert.Single(Mirrors(pcA));
		Assert.StartsWith("mirror" + RemoteMirror.OldSuffix, Path.GetFileName(aside));
		Assert.Equal(1_200 - first.Files, Files(aside).Count(f => f.EndsWith(".jpg", StringComparison.Ordinal) && !f.Contains("Linked")));

		// Picked up again: the rest go, and nothing else.
		OldMirrors.Swept second = OldMirrors.Sweep(remote, Scanning, CancellationToken.None);
		Assert.Equal(1_200, first.Files + second.Files);
		Assert.Equal(0, second.Left);
		Assert.Equal(2, second.Busy); // pc-b's and pc-c's left where they are
		Assert.Empty(Mirrors(pcA));
		Assert.All(kept, k => Assert.True(File.Exists(k), k));
		Assert.True(File.Exists(Path.Combine(outside, "mine.jpg")));
		Assert.Equal(10, Files(Path.Combine(pcB, "mirror")).Length);
		Assert.Equal(10, Files(Path.Combine(pcC, "mirror")).Length);

		// Once their scans are over, theirs go too.
		cLock.Dispose();
		OldMirrors.Swept third = OldMirrors.Sweep(remote, _ => false, CancellationToken.None);
		Assert.Equal(20, third.Files);
		Assert.Empty(Mirrors(pcB));
		Assert.Empty(Mirrors(pcC));
		Assert.Equal(0, OldMirrors.Sweep(remote, _ => false, CancellationToken.None).Files); // nothing left: a sweep finds nothing to do
	}

	[Fact]
	public void NoRemoteScansHere_NothingToDo() {
		Assert.Equal(default, OldMirrors.Sweep(remote, _ => false, CancellationToken.None));
		Directory.CreateDirectory(Path.Combine(remote, "pc-a", "db"));
		Assert.Equal(default, OldMirrors.Sweep(remote, _ => false, CancellationToken.None));
		Assert.True(Directory.Exists(Path.Combine(remote, "pc-a", "db")));
	}
}
