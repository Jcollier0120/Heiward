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

using System.Globalization;

namespace HEI.Agent.Tests;

/// <summary>
/// "Scan now instead of at 18:00" on a drive's page: the next scheduled scan brought forward. Once the scan it starts has
/// gone through, that scheduled scan skips itself and the one after runs as usual; one that fails or is stopped skips
/// nothing. And each drive's "Last scanned".
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME is process-wide
public sealed class ScanBumpTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-bump-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public ScanBumpTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	const int Every = 360; // every 6 hours
	static readonly DateTime Slot = new(2026, 11, 12, 18, 0, 0, DateTimeKind.Utc); // the next scheduled scan
	static readonly DateTime Asked = Slot.AddHours(-4); // when "Scan now instead" was pressed

	/// <summary>The scan the bump started, ended: went through, stopped (130) or failed.</summary>
	static LastScan Ended(int? exitCode, Exception? error = null) => LastScan.Record(Asked, Asked.AddMinutes(20), exitCode, error);

	[Fact]
	public void ABumpThatWentThrough_SkipsTheNextScheduledScan_AndKeepsTheOneAfter() {
		Assert.NotNull(AgentScanner.AfterScan(Ended(0), Slot, Every));

		// Task Scheduler's run at 18:00, a few seconds late: skipped, and nothing is left to skip.
		string? skip = ScanBump.Take(Slot.AddSeconds(20), Every);
		Assert.NotNull(skip);
		Assert.Contains("brought forward", skip);
		Assert.Null(ScanBump.Load());
		// The one after, at midnight, runs as usual.
		Assert.Null(ScanBump.Take(Slot.AddHours(6).AddSeconds(5), Every));
	}

	[Fact]
	public void ABumpThatFailedOrWasStopped_SkipsNothing() {
		Assert.Null(AgentScanner.AfterScan(Ended(130), Slot, Every)); // Stop scan
		Assert.Null(AgentScanner.AfterScan(Ended(null, new OperationCanceledException()), Slot, Every)); // cancelled
		Assert.Null(AgentScanner.AfterScan(Ended(null, new IOException("disk gone")), Slot, Every)); // failed
		Assert.Null(AgentScanner.AfterScan(Ended(2), Slot, Every)); // none of its folders there

		Assert.Null(ScanBump.Load());
		Assert.Null(ScanBump.Take(Slot.AddSeconds(20), Every)); // the scheduled scan runs
	}

	[Fact]
	public void AFolderScan_NeverSpendsABump() {
		// Scan now on a folder reads one folder: the scheduled scan of every drive still runs.
		Assert.Null(AgentScanner.AfterScan(Ended(0), Slot, Every, folderScan: true));
		Assert.Null(ScanBump.Load());
		Assert.Null(ScanBump.Take(Slot.AddSeconds(20), Every));
	}

	[Fact]
	public void AScanNotBroughtForward_SkipsNothing() {
		Assert.Null(AgentScanner.AfterScan(Ended(0), bumpedSlotUtc: null, Every));
		Assert.Null(ScanBump.Load());
	}

	[Fact]
	public void TheSkip_IsDecidedWhereTheScheduledScanDecides_ForTheSlotAlone() {
		var bump = new ScanBump(Slot, Slot, Asked);
		Assert.Null(ScanWhenDue.BumpedSkip(null, Slot, Every));
		Assert.Null(ScanWhenDue.BumpedSkip(bump, Slot.AddMinutes(-30), Every)); // before it: a run of its own
		Assert.NotNull(ScanWhenDue.BumpedSkip(bump, Slot.AddMinutes(-1), Every)); // Task Scheduler a moment early
		Assert.NotNull(ScanWhenDue.BumpedSkip(bump, Slot, Every));
		Assert.NotNull(ScanWhenDue.BumpedSkip(bump, Slot.AddHours(1), Every)); // started late (the PC woke up)
		Assert.Null(ScanWhenDue.BumpedSkip(bump, Slot.AddHours(6), Every)); // the one after
		Assert.Null(ScanWhenDue.BumpedSkip(bump, Slot, 0)); // no scheduled scans: nothing to skip
	}

	[Fact]
	public void ABumpWhoseScheduledScanNeverCame_IsLetGo() {
		AgentScanner.AfterScan(Ended(0), Slot, Every);
		// The PC slept through 18:00 and midnight: the next run is the one at 06:00, which runs.
		Assert.Null(ScanBump.Take(Slot.AddHours(12).AddMinutes(3), Every));
		Assert.Null(ScanBump.Load());
	}

	[Fact]
	public void ARunBeforeTheSlot_LeavesTheBumpForIt() {
		AgentScanner.AfterScan(Ended(0), Slot, Every);
		Assert.Null(ScanBump.Take(Slot.AddHours(-1), Every));
		Assert.NotNull(ScanBump.Load());
		Assert.NotNull(ScanBump.Take(Slot.AddSeconds(5), Every));
	}

	[Fact]
	public void TwoBumpsBeforeTheFirstSlot_SkipBothScheduledScans_AndKeepTheOneAfter() {
		AgentScanner.AfterScan(Ended(0), Slot, Every);
		// The page now shows midnight as the next scheduled scan, and that one is brought forward too.
		AgentScanner.AfterScan(LastScan.Record(Asked.AddHours(1), Asked.AddHours(1.2), 0, null), Slot.AddHours(6), Every);

		Assert.NotNull(ScanBump.Take(Slot.AddSeconds(5), Every));
		Assert.NotNull(ScanBump.Load()); // midnight's still to skip
		Assert.NotNull(ScanBump.Take(Slot.AddHours(6).AddSeconds(5), Every));
		Assert.Null(ScanBump.Load());
		Assert.Null(ScanBump.Take(Slot.AddHours(12).AddSeconds(5), Every));
	}

	[Fact]
	public void TheNextRunHeiwardGives_IsTheOneAfterTheSkippedScan() {
		static string Local(DateTime utc) => utc.ToLocalTime().ToString(CultureInfo.CurrentCulture);
		var bump = new ScanBump(Slot, Slot, Asked);
		DateTime now = Asked.AddMinutes(30);

		Assert.Equal(Slot, RunTimes.Build(now, null, null, false, null, false, Every, Local(Slot)).NextRunAt);
		Assert.Equal(Slot.AddHours(6), RunTimes.Build(now, null, null, false, null, false, Every, Local(Slot), bump).NextRunAt);
		// A bump for a later scan leaves the next one as it is.
		var later = new ScanBump(Slot.AddHours(6), Slot.AddHours(6), Asked);
		Assert.Equal(Slot, RunTimes.Build(now, null, null, false, null, false, Every, Local(Slot), later).NextRunAt);
	}

	[Fact]
	public void BringingForward_NeedsAScheduledScanComing_TheOneThePageShowed() {
		Assert.Null(ReviewServer.BumpRefusal(Slot, Every, Slot));
		Assert.Null(ReviewServer.BumpRefusal(Slot.AddSeconds(59), Every, Slot)); // Task Scheduler's minutes
		Assert.Contains("no scheduled scans", ReviewServer.BumpRefusal(Slot, 0, null));
		Assert.Contains("No scheduled scan is coming", ReviewServer.BumpRefusal(Slot, Every, null)); // paused, or no scan task
		Assert.Contains("moved", ReviewServer.BumpRefusal(Slot, Every, Slot.AddHours(6))); // the page is out of date
		Assert.Contains("moved", ReviewServer.BumpRefusal(null, Every, Slot));
	}

	[Fact]
	public void EachDrive_KeepsWhenAScanLastReadIt() {
		Assert.Null(DriveScans.Load());
		DriveScans.Record([@"C:\", @"D:\Photos\"], Asked);
		DriveScans.Record([@"E:\"], Slot); // a later scan that read E:\ alone (C:\ and D:\Photos rested)

		var read = DriveScans.Load();
		Assert.Equal(Asked, DriveScans.Of(read, @"C:\"));
		Assert.Equal(Asked, DriveScans.Of(read, @"d:\photos"));
		Assert.Equal(Slot, DriveScans.Of(read, @"E:\"));
		Assert.Null(DriveScans.Of(read, @"F:\"));
		Assert.Equal(DateTimeKind.Utc, DriveScans.Of(read, @"C:\")!.Value.Kind);
	}

	[Fact]
	public void ADrivesCard_SaysWhenItWasLastScanned_NeverBeforeItsFirst() {
		string folder = RealTemp.Folder("hei-bump-root-");
		Directory.CreateDirectory(folder);
		try {
			var cfg = new AgentConfig { ScanAllDrives = false, Folders = [folder] };
			DriveCard card(IReadOnlyDictionary<string, DateTime>? read, ScanIndex? index = null) =>
				ExplorerView.Drives(cfg, index, new(), devBuild: false, read).Single(c => c.Root.Equals(folder, StringComparison.OrdinalIgnoreCase));

			Assert.Equal(Asked, card(new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { [folder] = Asked }).LastScanUtc);
			Assert.Null(card(new Dictionary<string, DateTime>()).LastScanUtc); // never
			// Before scans kept each drive's time: the last scan's, for a drive it listed.
			var index = new ScanIndex { ScannedAtUtc = Slot };
			index.Drives[ScanIndex.DriveOf(folder)] = new DriveScan(1, 1, 0, 0);
			Assert.Equal(Slot, card(null, index).LastScanUtc);
		}
		finally { Directory.Delete(folder, true); }
	}

	static string Asset(string name) {
		using Stream s = typeof(ReviewServer).Assembly.GetManifestResourceStream("wwwroot/" + name)!;
		return new StreamReader(s).ReadToEnd();
	}

	static string Function(string script, string name) {
		int start = script.IndexOf("function " + name + "(", StringComparison.Ordinal);
		Assert.True(start > 0, name);
		return script[start..script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal)];
	}

	[Fact]
	public void EveryDrivesPage_HasItsScan_TheLastAndNextScans_AndTheBump() {
		string script = Asset("app.js");
		// On a folder's page (of a drive scanned only when asked), the strip scans just that folder first (PageFolderMenuTests).
		Assert.Contains("if (d && (isDrive || d.onRequest)) frag.append(scanStrip(d, isDrive ? null : path));", Function(script, "renderContent"));
		string strip = Function(script, "scanStrip");
		Assert.Contains("'Scan this drive now'", strip); // auto or manual
		Assert.Contains("'Last scanned: never.'", strip);
		Assert.Contains("'Next scheduled scan: '", strip);
		Assert.Contains("'No scheduled scans: '", strip);
		Assert.Contains("d.scans === 'auto' && s.nextUtc", strip); // the bump: only with a scheduled scan coming
		Assert.Contains("'Scan now instead of at ' + time", strip);
		Assert.Contains("bumpScan(s.nextUtc)", strip);
		Assert.Contains("is skipped", strip);
		Assert.Contains("post('/api/scan/bump', { slotUtc })", Function(script, "bumpScan"));
	}
}
