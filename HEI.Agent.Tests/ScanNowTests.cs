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
/// Scan now on a folder, and Scan this drive now, scan now. The owner, 2026-11-17, on a new install whose first scan waited:
/// they right-clicked Steam on E:\ and chose Scan now, and Heiward "scheduled a scan almost 3 hours out instead". The folder's
/// scan had run, and read nothing in a second (a Steam folder holds its game library, steamapps, which scans leave out). But
/// it ended the first scan's wait, so the first scan of every drive became the next scheduled one, at 20:13. The page then said
/// "Last scanned: never. Next scheduled scan: today 08:13 PM" beside "Not scanned yet" and "Last scan took under a second", and
/// nothing about what the scan had done.
/// </summary>
[Collection(AgentHomeCollection.Name)]
public sealed class ScanNowTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-scan-now-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public ScanNowTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "home"));
		Directory.CreateDirectory(AgentPaths.Home);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	static readonly DateTime Now = new(2026, 11, 17, 22, 15, 27, DateTimeKind.Utc);

	/// <summary>A Steam library folder, as a second drive has one: its game library, and Steam's own files beside it.</summary>
	string SteamFolder() {
		string steam = Path.Combine(dir, "Steam");
		Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Some Game"));
		File.WriteAllBytes(Path.Combine(steam, "steamapps", "common", "Some Game", "cover.png"), new byte[64]);
		File.WriteAllText(Path.Combine(steam, "libraryfolder.vdf"), "\"libraryfolder\" { }");
		return steam;
	}

	// ---- The first scan still waits, and isn't put off to the schedule

	[Fact]
	public void ScanNowOnAFolder_OrOnADrive_LeavesTheFirstScanWaiting_ScanNowOnEveryDriveAnswersIt() {
		Assert.False(FirstScanWait.AnsweredBy(new[] { @"E:\Steam" }, null));
		Assert.False(FirstScanWait.AnsweredBy(null, new[] { @"E:\" }));
		Assert.True(FirstScanWait.AnsweredBy(null, null));
		Assert.True(FirstScanWait.AnsweredBy(Array.Empty<string>(), Array.Empty<string>()));
	}

	[Fact]
	public void AfterScanThisDriveNow_TheFirstScanStillWaits_UntilEveryDriveHasBeenRead() {
		FirstScanWait.Begin();
		var drive = ScanAsk.For(null, new[] { @"E:\" });
		// E:\ read whole; C:\ never: the first scan still has C:\ to read.
		DriveScans.Record(new[] { @"E:\" }, Now);
		AgentScanner.AfterScopedScan(drive, new[] { @"C:\", @"E:\" });
		Assert.True(FirstScanWait.Waiting);
		// A folder's scan never reads a drive whole: it changes nothing here.
		AgentScanner.AfterScopedScan(ScanAsk.For(new[] { @"C:\Photos" }, null), new[] { @"C:\", @"E:\" });
		Assert.True(FirstScanWait.Waiting);
		// Scan this drive now on C:\ too: every drive has been read, and there's nothing left for a first scan to read.
		DriveScans.Record(new[] { @"C:\" }, Now.AddMinutes(5));
		AgentScanner.AfterScopedScan(drive, new[] { @"C:\", @"E:\" });
		Assert.False(FirstScanWait.Waiting);
	}

	[Fact]
	public void FirstScanDone_OnlyOnceEveryRootHasALastScanned() {
		var read = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { [@"E:\"] = Now };
		Assert.False(AgentScanner.FirstScanDone(new[] { @"C:\", @"E:\" }, read));
		Assert.True(AgentScanner.FirstScanDone(new[] { @"E:\" }, read));
		Assert.False(AgentScanner.FirstScanDone(Array.Empty<string>(), read)); // nothing to read isn't a first scan done
		Assert.False(AgentScanner.FirstScanDone(new[] { @"E:\" }, null));
	}

	// ---- "Last scan took under a second" of a drive never scanned

	[Fact]
	public void AFoldersScan_TimesNoDrive_AndADriveNeverReadWholeHasNoTime() {
		// The owner's: the first scan ever, Scan now on E:\Steam. It read nothing on E:\ whole, nor on any other drive.
		var index = ScanIndex.Build(Now, new[] { @"C:\", @"E:\" }, Array.Empty<(string, long)>(),
			new Dictionary<string, TimeSpan> { [@"E:\"] = TimeSpan.FromSeconds(0.4) }, new Dictionary<string, TimeSpan>(), whole: Array.Empty<string>());
		Assert.Empty(index.Drives); // before: E:\ and C:\ each "took under a second"
		Assert.Equal(new[] { @"C:\", @"E:\" }, index.Roots);
	}

	[Fact]
	public void ADriveThisScanDidntReadWhole_KeepsItsLastWholeScansTime_WithWhatsOnItNow() {
		var before = ScanIndex.Build(Now.AddHours(-6), new[] { @"C:\", @"E:\" }, new[] { (@"C:\a.jpg", 10L), (@"E:\Photos\b.jpg", 20L) },
			new Dictionary<string, TimeSpan> { [@"C:\"] = TimeSpan.FromSeconds(12), [@"E:\"] = TimeSpan.FromMinutes(3) },
			new Dictionary<string, TimeSpan> { [@"C:\"] = TimeSpan.FromSeconds(30), [@"E:\"] = TimeSpan.FromMinutes(20) });
		Assert.Equal(new DriveScan(180, 1200, 1, 20), before.Drives[@"E:\"]);

		// Scan this drive now on C:\: C:\ is timed again; E:\ keeps its scan's time, its files as they stand.
		var after = ScanIndex.Build(Now, new[] { @"C:\", @"E:\" }, new[] { (@"C:\a.jpg", 10L), (@"E:\Photos\b.jpg", 20L), (@"E:\Photos\c.jpg", 5L) },
			new Dictionary<string, TimeSpan> { [@"C:\"] = TimeSpan.FromSeconds(2) }, new Dictionary<string, TimeSpan> { [@"C:\"] = TimeSpan.FromSeconds(1) },
			whole: new[] { @"C:\" }, previous: before);
		Assert.Equal(new DriveScan(2, 1, 1, 10), after.Drives[@"C:\"]);
		Assert.Equal(new DriveScan(180, 1200, 2, 25), after.Drives[@"E:\"]);
		// A scan of every drive (whole: null) times every one, as before.
		var all = ScanIndex.Build(Now, new[] { @"C:\" }, Array.Empty<(string, long)>(), new Dictionary<string, TimeSpan>(), new Dictionary<string, TimeSpan>());
		Assert.Equal(new DriveScan(0, 0, 0, 0), all.Drives[@"C:\"]);
	}

	// ---- What the scan found, for the page that started it

	[Fact]
	public void ScanNowOnASteamFolder_FindsNothing_AndSaysItsGameLibraryIsLeftOut() {
		string steam = SteamFolder();
		var rules = ScanScope.ExclusionRules(new AgentConfig { ScanAllDrives = false });
		var ask = ScanAsk.For(new[] { steam }, null);
		Assert.Equal(ScanAsk.Folder, ask.Scope);
		Assert.Equal(new[] { steam }, ask.Paths);
		// A photo elsewhere on the drive isn't in what it was asked for.
		ask.Ran(new[] { (Path.Combine(dir, "Photos", "a.jpg"), 10L) }, new[] { steam }, rules);
		Assert.Equal(0, ask.Found);
		Assert.Equal(new[] { new LeftOutFolder("steamapps", "Game library") }, ask.LeftOut);
	}

	[Fact]
	public void ScanNowOnAFolder_ThatFoundSome_CountsThem_AndNamesNothingLeftOut() {
		string steam = SteamFolder();
		string shot = Path.Combine(steam, "screenshots", "shot.png");
		var ask = ScanAsk.For(new[] { steam }, null);
		ask.Ran(new[] { (shot, 10L), (Path.Combine(dir, "Other", "b.jpg"), 10L) }, new[] { steam }, ScanScope.ExclusionRules(new AgentConfig()));
		Assert.Equal(1, ask.Found);
		Assert.Null(ask.LeftOut);
		// Scan this drive now: what's on the drive it read; Scan now (every drive): everything.
		var drive = ScanAsk.For(null, new[] { dir });
		drive.Ran(new[] { (shot, 10L), (@"Q:\elsewhere.jpg", 1L) }, new[] { dir }, []);
		Assert.Equal((ScanAsk.Drive, (int?)1), (drive.Scope, drive.Found));
		Assert.Null(drive.LeftOut);
		var every = ScanAsk.For(null, null);
		every.Ran(new[] { (shot, 10L), (@"Q:\elsewhere.jpg", 1L) }, null, []);
		Assert.Equal(((string?)null, (int?)2), (every.Scope, every.Found));
	}

	[Fact]
	public void LeftOutIn_NamesAtMostAFew() {
		string many = Path.Combine(dir, "Many");
		foreach (string name in new[] { "node_modules", ".git", "steamapps", "SteamLibrary", "Epic Games", "GOG Games", "Photos" })
			Directory.CreateDirectory(Path.Combine(many, name));
		var left = ScanAsk.LeftOutIn(new[] { many }, ScanScope.ExclusionRules(new AgentConfig()))!;
		Assert.Equal(ScanAsk.MaxLeftOut, left.Count);
		Assert.DoesNotContain(left, f => f.Name == "Photos");
		Assert.Null(ScanAsk.LeftOutIn(new[] { Path.Combine(dir, "no such folder") }, []));
	}

	[Fact]
	public void TheLastScan_KeepsWhatItWasAskedFor_AndWhatItFound() {
		var ask = new ScanAsk { Scope = ScanAsk.Folder, Paths = [@"E:\Steam"], Found = 0, LeftOut = [new("steamapps", "Game library")] };
		LastScan.Record(Now, Now.AddSeconds(1), 0, null, ask);
		LastScan read = LastScan.Load()!;
		Assert.True(read.Ok);
		Assert.Equal(("folder", @"E:\Steam", (int?)0, "steamapps", (string?)null), (read.Scope, read.Asked!.Single(), read.Found, read.LeftOut!.Single().Name, read.Why));
		// One of every drive, as before: no scope, nothing asked.
		LastScan.Record(Now, Now.AddMinutes(9), 0, null);
		Assert.Equal(((string?)null, (List<string>?)null), (LastScan.Load()!.Scope, LastScan.Load()!.Asked));
	}

	/// <summary>A folder that isn't there now: the scan reads nothing, and says why rather than nothing at all.</summary>
	[Fact]
	public async Task ScanNowOnAFolderGone_ReadsNothing_AndSaysWhy() {
		var cfg = new AgentConfig { ScanAllDrives = false, Folders = new() { dir } };
		string gone = Path.Combine(dir, "Steam");
		Assert.Equal(1, await AgentScanner.RunAsync(cfg, notify: false, scheduled: false, CancellationToken.None, folders: new[] { gone }));
		LastScan last = LastScan.Load()!;
		Assert.Equal((false, (int?)1, "folder", gone), (last.Ok, last.ExitCode, last.Scope, last.Asked!.Single()));
		Assert.Equal("that folder isn't there now, or scans leave it out", last.Why);
		Assert.Null(ScanBump.Load()); // nothing brought forward, and nothing put off
	}

	[Fact]
	public async Task ScanThisDriveNow_OnADriveNotScanned_ReadsNothing_AndSaysWhy() {
		var cfg = new AgentConfig { ScanAllDrives = false, Folders = new() { dir } };
		Assert.Equal(1, await AgentScanner.RunAsync(cfg, notify: false, scheduled: false, CancellationToken.None, only: new[] { @"Q:\" }));
		LastScan last = LastScan.Load()!;
		Assert.Equal((false, "drive", "Heiward doesn't scan that drive now"), (last.Ok, last.Scope, last.Why));
	}

	// ---- When another scan runs

	[Fact]
	public void ScanNowRefused_WhileAnotherRuns_SaysWhyAndWhen() {
		Assert.Equal("Another scan is running now, so this folder wasn't scanned. Once that scan has finished, scan this folder again.",
			ReviewServer.BusyText("this folder"));
		Assert.Contains("this drive wasn't scanned", ReviewServer.BusyText("this drive"));
	}
}
