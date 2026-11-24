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
/// The homes of the scans this PC ran for other PCs, looked after in the background: what Heiward 1.11.0 to 1.11.5 kept
/// of the PCs it scanned for (a stand-in of each of their files, never removed) goes a batch at a time, only Heiward's own,
/// never under a scan, and a sweep cut off partway picks up where it stopped; a PC's records are offered back to it and
/// kept until it takes them; a home with nothing to take back goes.
/// </summary>
[Collection(AgentHomeCollection.Name)] // RemoteHomes' batch and rest are process-wide
public sealed class RemoteHomesTests : IDisposable {
	readonly string dir = Directory.CreateTempSubdirectory("hei-remote-homes-").FullName;
	readonly string remote;
	readonly int batch = RemoteHomes.Batch;
	readonly TimeSpan rest = RemoteHomes.Rest;

	public RemoteHomesTests() {
		remote = Path.Combine(dir, "Heiward", "remote");
		RemoteHomes.Batch = 100;
		RemoteHomes.Rest = TimeSpan.Zero;
	}

	public void Dispose() {
		RemoteHomes.Batch = batch;
		RemoteHomes.Rest = rest;
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

	/// <summary>A scan's records in <paramref name="home"/>, as a scan keeps them; their paths.</summary>
	static string[] Records(string home) {
		string[] kept = [Path.Combine(home, "listing.tsv"), Path.Combine(home, "findings.json"), Path.Combine(home, "db", "ScannedFiles.db")];
		foreach (string k in kept) {
			Directory.CreateDirectory(Path.GetDirectoryName(k)!);
			File.WriteAllText(k, "kept");
		}
		return kept;
	}

	static string[] Files(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];

	static string[] Mirrors(string home) => Directory.Exists(home) ? Directory.GetDirectories(home, "mirror*") : [];

	[Fact]
	public void OldStandIns_GoABatchAtATime_OnlyHeiwardsOwn_AndASweepCutOffPicksUpWhereItStopped() {
		string pcA = Path.Combine(remote, "pc-a"), pcB = Path.Combine(remote, "pc-b"), pcC = Path.Combine(remote, "pc-c");
		Plant(Path.Combine(pcA, "mirror"), 1_200);
		File.WriteAllText(Path.Combine(pcA, "mirror.empty"), "");
		string[] kept = Records(pcA);
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
		var offered = new List<string>();
		RemoteHomes.Offered Away(string pc) {
			offered.Add(pc);
			return RemoteHomes.Offered.Away;
		}

		// Cut off after three batches (the page stopped, Heiward was updated).
		using var cut = new CancellationTokenSource();
		int rests = 0;
		RemoteHomes.Swept first = RemoteHomes.Sweep(remote, Scanning, cut.Token, hold: () => {
			if (++rests == 3) cut.Cancel();
			return false;
		}, offer: Away);
		Assert.Equal(3, rests);
		Assert.InRange(first.Files, 200, 300);
		Assert.False(Directory.Exists(Path.Combine(pcA, "mirror")));
		Assert.False(File.Exists(Path.Combine(pcA, "mirror.empty")));
		string aside = Assert.Single(Mirrors(pcA));
		Assert.StartsWith("mirror" + RemoteMirror.OldSuffix, Path.GetFileName(aside));
		Assert.Equal(1_200 - first.Files, Files(aside).Count(f => f.EndsWith(".jpg", StringComparison.Ordinal) && !f.Contains("Linked")));
		Assert.Empty(offered); // cut off before it got to its records

		// Picked up again: the rest go, and nothing else. Its records are offered back to pc-a, which can't be reached: they stay.
		RemoteHomes.Swept second = RemoteHomes.Sweep(remote, Scanning, CancellationToken.None, offer: Away);
		Assert.Equal(1_200, first.Files + second.Files);
		Assert.Equal(0, second.Left);
		Assert.Equal(2, second.Busy); // pc-b's and pc-c's left where they are
		Assert.Empty(Mirrors(pcA));
		Assert.All(kept, k => Assert.True(File.Exists(k), k));
		Assert.Equal(["pc-a"], offered);
		Assert.Equal(1, second.Waiting);
		Assert.Equal(new RemoteHomes.Waits("pc-a", "pc-a", "away"), Assert.Single(RemoteHomes.Waiting));
		Assert.True(File.Exists(Path.Combine(outside, "mine.jpg")));
		Assert.Equal(10, Files(Path.Combine(pcB, "mirror")).Length);
		Assert.Equal(10, Files(Path.Combine(pcC, "mirror")).Length);

		// Once their scans are over, theirs go too, and their homes with them: nothing in them to take back.
		cLock.Dispose();
		RemoteHomes.Swept third = RemoteHomes.Sweep(remote, _ => false, CancellationToken.None, offer: Away);
		Assert.Equal(20, third.Files);
		Assert.False(Directory.Exists(pcB));
		Assert.False(Directory.Exists(pcC));
		Assert.Equal(["pc-a", "pc-a"], offered);
		Assert.Equal(0, RemoteHomes.Sweep(remote, _ => false, CancellationToken.None, offer: Away).Files); // nothing left but pc-a's records
		Assert.Equal(["pc-a"], Directory.GetDirectories(remote).Select(Path.GetFileName));
	}

	[Fact]
	public void ARecordsHome_IsOfferedBackToItsPc_SaidOnThePageWhileItCant_AndGoesWhenThatPcLeftTheEstateLongAgo() {
		string pcA = Path.Combine(remote, "pc-a"), pcB = Path.Combine(remote, "pc-b"), pcC = Path.Combine(remote, "pc-c");
		Records(pcA);
		Records(pcB);
		File.WriteAllText(RemoteHomes.TargetFile(pcB), """{"id":"pc-b","name":"OFFICE"}""");
		Records(pcC);
		var answers = new Dictionary<string, RemoteHomes.Offered> {
			["pc-a"] = RemoteHomes.Offered.Taking, ["pc-b"] = RemoteHomes.Offered.Older, ["pc-c"] = RemoteHomes.Offered.Left,
		};
		RemoteHomes.Swept swept = RemoteHomes.Sweep(remote, _ => false, CancellationToken.None, offer: pc => answers[pc]);
		// pc-a takes them (and lets the home go itself); pc-b runs an older Heiward, said by its name; pc-c left the Estate
		// only just: theirs stay for now.
		Assert.Equal(2, swept.Waiting);
		Assert.Equal([new RemoteHomes.Waits("pc-b", "OFFICE", "older"), new RemoteHomes.Waits("pc-c", "pc-c", "away")], RemoteHomes.Waiting);
		Assert.True(Directory.Exists(pcA) && Directory.Exists(pcB) && Directory.Exists(pcC));

		// A month and more on, pc-c's go: it can't take them back.
		RemoteHomes.Sweep(remote, _ => false, CancellationToken.None, offer: pc => answers[pc], nowUtc: DateTime.UtcNow + RemoteHomes.KeptFor + TimeSpan.FromDays(1));
		Assert.False(Directory.Exists(pcC));
		Assert.True(Directory.Exists(pcA) && Directory.Exists(pcB));
		Assert.Empty(Directory.GetDirectories(remote, RemoteHomes.Gone + "*"));

		// A home let go (its PC took its records) goes whole, and only it.
		Assert.True(RemoteHomes.Retire(pcA));
		Assert.False(Directory.Exists(pcA));
		Assert.Single(Directory.GetDirectories(remote, RemoteHomes.Gone + "*"));
		RemoteHomes.Sweep(remote, _ => false, CancellationToken.None, offer: pc => answers[pc]);
		Assert.Equal(["pc-b"], Directory.GetDirectories(remote).Select(Path.GetFileName));
	}

	[Fact]
	public void NoRemoteScansHere_NothingToDo() {
		Assert.Equal(default, RemoteHomes.Sweep(remote, _ => false, CancellationToken.None));
		Directory.CreateDirectory(remote);
		Assert.Equal(default, RemoteHomes.Sweep(remote, _ => false, CancellationToken.None));
		Assert.Empty(RemoteHomes.Waiting);
	}
}
