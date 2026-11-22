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
/// An update under a scan for another PC (1.11.2 on WEASELDEV-01, 2026-11-22): the installer stopped the page with its
/// tree and waited only for the page, so the scan's process, still ending, held hei.exe; the copy failed, the install
/// exited 1 and nothing started Heiward again, and the scan's 12¾ hours went. Now every process from the install folder
/// is waited for, the copy tries again while the exe is held, a failed update starts the copy that was there again, and an
/// update waits while a scan for another PC, or another's of this one, runs.
/// </summary>
public sealed class UpdateKeepsWorkTests : IDisposable {
	readonly string folder = Directory.CreateTempSubdirectory("hei-update-").FullName;

	public void Dispose() {
		Installer.StopProcessesIn(folder, within: TimeSpan.FromSeconds(10));
		try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	/// <summary>A copy of a Windows program in the test's folder, so its processes run from there as the installed copy's do.</summary>
	string Program(string name) {
		string to = Path.Combine(folder, name);
		File.Copy(Path.Combine(Environment.SystemDirectory, name), to);
		return to;
	}

	static Process? RunningFrom(string exe) {
		foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) {
			try {
				if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return p;
			}
			catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
			p.Dispose();
		}
		return null;
	}

	[Fact]
	public void AChildItsParentsTreeTook_IsWaitedFor_SoItsExeCanBeReplacedAtOnce() {
		string cmd = Program("cmd.exe"), ping = Program("PING.EXE");
		// The page and the scan it started for another PC: a parent and its child, both run from the folder.
		using var parent = Process.Start(new ProcessStartInfo(cmd, $"/c \"\"{ping}\" -n 60 127.0.0.1\"") { UseShellExecute = false, CreateNoWindow = true })!;
		Process? child = null;
		Assert.True(SpinWait.SpinUntil(() => (child = RunningFrom(ping)) != null, 10_000), "the child didn't start");
		using (child) {
			Assert.Equal(0, Installer.StopProcessesIn(folder, within: TimeSpan.FromSeconds(20)));
			Assert.True(parent.HasExited);
			Assert.True(child!.HasExited);
		}
		// Nothing holds it now: the copy over it goes at the first try.
		File.Copy(cmd, ping, overwrite: true);
	}

	[Fact]
	public void AProcessRunFromThroughAJunction_IsTheFoldersToo() {
		// Windows names a process's exe by its real path; the install folder may be written through a junction.
		string real = Directory.CreateDirectory(Path.Combine(folder, "real")).FullName, link = Path.Combine(folder, "link");
		using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!) {
			mklink.StandardOutput.ReadToEnd();
			mklink.WaitForExit();
			Assert.Equal(0, mklink.ExitCode);
		}
		// As Windows names them: by the real path (1), and as started (2); 3 is elsewhere.
		(int, string?)[] running = [(1, Path.Combine(RealPath.Of(real), "hei.exe")), (2, Path.Combine(link, "hei.exe")), (3, Path.Combine(folder, "other", "hei.exe"))];
		Assert.Equal([1, 2], Installer.RunningFrom(running, link, self: 0).Select(p => p.Id));
		Directory.Delete(link);
	}

	[Fact]
	public void AnExeStillHeld_IsCopiedOnceItsLetGo_AndWhatHoldsItIsStoppedAgainMeanwhile() {
		string from = Path.Combine(folder, "new.exe"), to = Path.Combine(folder, "hei.exe");
		File.WriteAllText(from, "the new one");
		File.WriteAllText(to, "the old one");
		var held = new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.None);
		int stopped = 0;
		var said = new List<string>();
		using var letGo = new Timer(_ => held.Dispose(), null, 1500, Timeout.Infinite);
		Installer.CopyOver(from, to, () => stopped++, TimeSpan.FromSeconds(15), said.Add);
		Assert.Equal("the new one", File.ReadAllText(to));
		Assert.True(stopped > 0);
		Assert.Contains(said, l => l.Contains("still in use"));
		Assert.Contains(said, l => l.Contains("copied on try"));
	}

	[Fact]
	public void AnyOtherFailureOfTheCopy_IsThrownAtOnce() {
		int stopped = 0;
		var timer = Stopwatch.StartNew();
		Assert.Throws<FileNotFoundException>(() => Installer.CopyOver(Path.Combine(folder, "missing.exe"), Path.Combine(folder, "hei.exe"), () => stopped++, TimeSpan.FromSeconds(15)));
		Assert.Equal(0, stopped);
		Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void AnExeHeldPastTheWait_FailsAsBefore() {
		string from = Path.Combine(folder, "new.exe"), to = Path.Combine(folder, "hei.exe");
		File.WriteAllText(from, "the new one");
		File.WriteAllText(to, "the old one");
		using var held = new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.None);
		var e = Assert.ThrowsAny<IOException>(() => Installer.CopyOver(from, to, () => { }, TimeSpan.FromSeconds(1)));
		Assert.True(Installer.InUse(e));
	}

	[Theory]
	// An update: the exe was there before this copy replaced it.
	[InlineData(false, false, false, true, true)]
	// The installed copy's setup, run by hand.
	[InlineData(true, false, false, false, true)]
	// An update's second half, from the installed copy: its first half starts it again.
	[InlineData(true, false, true, false, false)]
	// A new install: nothing was here to start again.
	[InlineData(false, false, false, false, false)]
	// The Store version: the Store keeps it.
	[InlineData(true, true, false, true, false)]
	public void AFailedInstall_StartsTheCopyThatWasThereAgain(bool runningInstalled, bool packaged, bool fromSetup, bool wasHere, bool startsAgain) =>
		Assert.Equal(startsAgain, Installer.StartsAgainOnFailure(runningInstalled, packaged, fromSetup, wasHere));

	[Fact]
	public void AScanForAnotherPC_StopsAsAsked_ByItsStopFile_SoItKeepsWhatItChecked() {
		string stop = RemoteScanner.StopFile(folder);
		int said = 0;
		using var told = new RemoteScanner.StopWhenAsked(stop, CancellationToken.None, () => said++, TimeSpan.FromMilliseconds(50));
		Thread.Sleep(300);
		Assert.False(told.Token.IsCancellationRequested);
		File.WriteAllText(stop, ""); // the page's EndAndWait
		Assert.True(SpinWait.SpinUntil(() => told.Token.IsCancellationRequested, 5_000));
		Thread.Sleep(200);
		Assert.Equal(1, said); // said once
	}

	[Fact]
	public void AScanForAnotherPC_StopsToo_WhenItsOwnProcessIsAskedTo() {
		using var process = new CancellationTokenSource();
		using var told = new RemoteScanner.StopWhenAsked(RemoteScanner.StopFile(folder), process.Token);
		process.Cancel();
		Assert.True(told.Token.IsCancellationRequested);
	}

	static readonly DateTime Now = new(2026, 11, 22, 15, 0, 0, DateTimeKind.Utc);

	[Fact]
	public void AScanForAnotherPC_OrAnothersOfThisOne_HoldsTheUpdate_ForADayAtMost() {
		Assert.Null(ScanForOthers.Holding(null, null, Now));
		Assert.Equal("scanning GAMING-010's photos and videos for it", ScanForOthers.Holding(("GAMING-010", Now.AddHours(-13)), null, Now));
		Assert.Equal("WEASELDEV-01 is scanning this PC's photos and videos", ScanForOthers.Holding(null, ("WEASELDEV-01", Now.AddMinutes(-5)), Now));
		// A day on, the update goes in: the scan picks up where it stopped.
		Assert.Null(ScanForOthers.Holding(("GAMING-010", Now - ScanForOthers.MostHold), null, Now));
		Assert.Null(ScanForOthers.Holding(null, ("WEASELDEV-01", Now.AddHours(-25)), Now));
	}

	[Fact]
	public void TheOtherPCsUpdate_IsSaidInPlainWords_OnThisPCsCard() {
		Assert.Equal("Heiward on WEASELDEV-01 is being updated, so its scan of this PC stopped.",
			ScanForOthers.EndedWords("WEASELDEV-01", $"WEASELDEV-01 stopped lending its NPU: {ScanForOthers.Updated}."));
		Assert.Equal("WEASELDEV-01 stopped answering.", ScanForOthers.EndedWords("WEASELDEV-01", "WEASELDEV-01 stopped answering."));
	}

	string Kept(string json) {
		string file = Path.Combine(folder, "remote-asked.json");
		File.WriteAllText(file, json);
		return file;
	}

	[Fact]
	public void AScanKeptAsUnderWay_ComesBackStopped_ForScanAgain() {
		string file = Kept("""{"pc":"pc-1","name":"WEASELDEV-01","startedUtc":"2026-11-22T02:49:00Z","stage":"Checking its files","done":654941,"total":671751,"fetched":131900000000,"finished":false,"problem":null,"sets":null,"begun":true,"lenderBuild":"1.11.2"}""");
		ScanForOthers.Asked a = ScanForOthers.LoadAsked(file, Now)!;
		Assert.True(a.Finished);
		Assert.True(a.Begun);
		Assert.Equal("Heiward on this PC stopped while it was scanned.", a.Problem);
		Assert.Equal((654_941, 671_751), (a.Done, a.Total));
		Assert.Equal("1.11.2", a.LenderBuild);
		Assert.Equal("WEASELDEV-01", a.Lender.Name);
	}

	[Fact]
	public void AScanThatEnded_ComesBackAsItEnded_ForAWeek() {
		string json = """{"pc":"pc-1","name":"WEASELDEV-01","startedUtc":"2026-11-22T02:49:00Z","stage":"Checking its files","done":654941,"total":671751,"fetched":0,"finished":true,"problem":"WEASELDEV-01 stopped answering.","sets":null,"begun":true,"lenderBuild":"1.11.2"}""";
		Assert.Equal("WEASELDEV-01 stopped answering.", ScanForOthers.LoadAsked(Kept(json), Now)!.Problem);
		Assert.Null(ScanForOthers.LoadAsked(Kept(json), Now.AddDays(8)));
		Assert.Null(ScanForOthers.LoadAsked(Kept("not json"), Now));
		Assert.Null(ScanForOthers.LoadAsked(Path.Combine(folder, "none.json"), Now));
	}
}
