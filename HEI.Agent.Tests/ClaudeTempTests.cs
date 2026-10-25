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
using System.Text.Json;

namespace HEI.Agent.Tests;

/// <summary>
/// Claude Code's temp, a session's folder at a time, and what keeps one whatever its age. A stand-in Temp folder, Claude
/// Code folder and Claude app records here, never the real ones.
/// </summary>
public sealed class ClaudeTempTests : IDisposable {
	readonly string root = RealTemp.Folder("hei-claude-");
	readonly string temp, container, home, app;
	const string Project = "C--Projects-Heiward";
	static readonly DateTime Long = DateTime.UtcNow.AddDays(-30);

	public ClaudeTempTests() {
		temp = Path.Combine(root, "Temp");
		container = Path.Combine(temp, "claude");
		home = Path.Combine(root, ".claude");
		app = Path.Combine(root, "Claude-Data", "claude_x", "claude-code-sessions");
		Directory.CreateDirectory(container);
	}

	public void Dispose() {
		try { Directory.Delete(root, true); } catch { }
	}

	/// <summary>A session's folder, as Claude Code makes it (scratchpad, tasks), with nothing changed in it since <paramref name="written"/>.</summary>
	string Session(DateTime written, string project = Project, string? id = null, int bytes = 100) {
		string folder = Path.Combine(container, project, id ?? Guid.NewGuid().ToString());
		string note = Path.Combine(folder, "scratchpad", "notes.md");
		Directory.CreateDirectory(Path.GetDirectoryName(note)!);
		File.WriteAllBytes(note, new byte[bytes]);
		Directory.CreateDirectory(Path.Combine(folder, "tasks"));
		File.SetLastWriteTimeUtc(note, written);
		foreach (string d in new[] { Path.Combine(folder, "scratchpad"), Path.Combine(folder, "tasks"), folder })
			Directory.SetLastWriteTimeUtc(d, written);
		return folder;
	}

	static string IdOf(string folder) => Path.GetFileName(folder);

	ClaudeKeeps Keeps(int days = 7) => ClaudeKeeps.Read(home, [app], DateTime.UtcNow.AddDays(-days))!;

	ClaudeTemp.Found Scan() => ClaudeTemp.Scan(container, 7, Keeps());

	/// <summary>sessions\&lt;pid&gt;.json, as Claude Code writes it while a session runs.</summary>
	void Running(int pid, string? procStart, string sessionId) {
		string dir = Path.Combine(home, "sessions");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, pid + ".json"), JsonSerializer.Serialize(new Dictionary<string, object?> {
			["pid"] = pid, ["sessionId"] = sessionId, ["cwd"] = @"C:\Projects\Heiward", ["startedAt"] = 1792707158947, ["procStart"] = procStart,
			["version"] = "2.1.308", ["kind"] = "interactive", ["entrypoint"] = "claude-desktop",
		}));
		// Beside each, Claude Code keeps a key file: not a session.
		File.WriteAllText(Path.Combine(dir, pid + ".0123abcd.key"), "{\"peerToken\":\"x\",\"procStartFt\":\"1\"}");
	}

	/// <summary>A session's transcript, last written at <paramref name="written"/>.</summary>
	void Transcript(string sessionId, DateTime written) {
		string file = Path.Combine(home, "projects", Project, sessionId + ".jsonl");
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, "{\"type\":\"user\",\"cwd\":\"C:\\\\Projects\\\\Heiward\"}\n");
		File.SetLastWriteTimeUtc(file, written);
	}

	/// <summary>The Claude app's record of a session it runs, as it keeps them in Claude-Data.</summary>
	void InTheApp(string sessionId, bool archived) {
		string dir = Path.Combine(app, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
		Directory.CreateDirectory(dir);
		string local = "local_" + Guid.NewGuid();
		File.WriteAllText(Path.Combine(dir, local + ".json"), JsonSerializer.Serialize(new Dictionary<string, object?> {
			["sessionId"] = local, ["cliSessionId"] = sessionId, ["cwd"] = @"C:\Projects\Heiward", ["isArchived"] = archived, ["title"] = "A session",
		}));
	}

	static string StartOf(Process p) => p.StartTime.ToFileTimeUtc().ToString();

	[Fact]
	public void AnOldIdleSession_IsTaken_AndItsProjectFolderGoesOnceEmpty() {
		string idle = Session(Long, bytes: 2 << 20);
		string fresh = Session(DateTime.UtcNow, project: "C--worktrees-other");
		string stray = Path.Combine(container, "build.log"); // Claude Code's own, beside the projects
		File.WriteAllText(stray, "x");
		File.SetLastWriteTimeUtc(stray, Long);

		ClaudeTemp.Found found = Scan();
		Assert.Equal(new[] { (idle, (long)(2 << 20), true), (stray, 1L, false) }, found.Idle.OrderBy(e => e.Path.Length).Reverse());
		Assert.Equal(1, found.Sessions);

		DevItem item = ClaudeTemp.Item(temp, 7, Keeps())!;
		Assert.Equal("Claude Code sessions' temp files", item.Name);
		Assert.Equal(container, item.Location);
		Assert.Equal("1 session(s) idle for 7 days · 1 other item(s) untouched as long", item.Detail);
		Assert.True(item.Suggested);
		Assert.Equal(AutoCleaner.Temp, AutoCleaner.KindOf(item)); // automatic cleanup takes it as temp files

		CleanResult r = ClaudeTemp.Clean(item, 7, Keeps(), new FolderUse(DateTime.UtcNow, claudeHome: home, processes: () => new()));
		Assert.Null(r.Error);
		Assert.Equal(0, r.LeftInUse);
		Assert.Equal((2 << 20) + 1, r.FreedBytes);
		Assert.Equal("1 ended session's folder and 1 other item", r.Went);
		Assert.False(Directory.Exists(Path.Combine(container, Project)), "the emptied project folder stayed");
		Assert.True(Directory.Exists(fresh));
		Assert.True(Directory.Exists(container));
	}

	/// <summary>By default an ended session's folder goes once it's been idle a day, and its transcript as long: that's when Claude Code's temp filled a drive.</summary>
	[Fact]
	public void AnEndedSession_GoesAfterADayIdle_ByDefault() {
		int days = new AgentConfig().TempUntouchedDays;
		string ended = Session(DateTime.UtcNow.AddDays(-2), bytes: 2 << 20);
		Transcript(IdOf(ended), DateTime.UtcNow.AddDays(-2));
		string lastHour = Session(DateTime.UtcNow.AddHours(-1));
		string resumed = Session(DateTime.UtcNow.AddDays(-2));
		Transcript(IdOf(resumed), DateTime.UtcNow.AddHours(-3)); // resumed this morning: its scratchpad may matter to it again
		ClaudeTemp.Found found = ClaudeTemp.Scan(container, days, Keeps(days));
		Assert.Equal(new[] { ended }, found.Idle.Select(e => e.Path));
		Assert.Equal(1, found.Kept);
		Assert.True(Directory.Exists(lastHour));
		Assert.Equal("1 session(s) idle for a day · 1 kept: in use or resumable", ClaudeTemp.Item(temp, days, Keeps(days)) is { } item ? item.Detail : "too small to list");
	}

	[Fact]
	public void TheTempFoldersOwnScan_LeavesClaudeCodesFolderToItsSessions() {
		Session(Long);
		Directory.SetLastWriteTimeUtc(container, Long);
		string old = Path.Combine(temp, "old.tmp");
		File.WriteAllText(old, "x");
		File.SetLastWriteTimeUtc(old, Long);
		// Old throughout, and still not one entry: a session's folder is judged by itself.
		Assert.Equal(new[] { old }, DevScanner.OldTempEntries(temp, 7).Select(e => e.Path));
	}

	[Fact]
	public void ARunningSession_KeepsItsFolder_WhateverItsAge() {
		string folder = Session(Long);
		using Process me = Process.GetCurrentProcess();
		Running(me.Id, StartOf(me), IdOf(folder));
		Assert.Equal($"Its session is running ({me.ProcessName}, process {me.Id})", Keeps().Why(IdOf(folder)));
		ClaudeTemp.Found found = Scan();
		Assert.Empty(found.Idle);
		Assert.Equal(1, found.Kept);
		Assert.True(Directory.Exists(folder));
	}

	[Fact]
	public void ASessionFileWhosePidAnotherProcessHasNow_DoesntKeepIt() {
		string folder = Session(Long);
		// Its number now belongs to another process, started later (this one).
		using Process me = Process.GetCurrentProcess();
		Running(me.Id, (me.StartTime.ToFileTimeUtc() - TimeSpan.FromDays(3).Ticks).ToString(), IdOf(folder));
		Assert.Null(Keeps().Why(IdOf(folder)));
		Assert.Equal(new[] { folder }, Scan().Idle.Select(e => e.Path));

		// And one whose process ended.
		using var ended = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
		ended.WaitForExit();
		Running(ended.Id, "134356143046665905", IdOf(folder));
		Assert.Null(Keeps().Why(IdOf(folder)));
	}

	[Fact]
	public void ASessionThatCanStillBeResumed_KeepsItsFolder() {
		string recent = Session(Long);
		Transcript(IdOf(recent), DateTime.UtcNow.AddDays(-2));
		string stale = Session(Long);
		Transcript(IdOf(stale), DateTime.UtcNow.AddDays(-10));
		Assert.StartsWith("Its session can be resumed", Keeps().Why(IdOf(recent)));
		Assert.Null(Keeps().Why(IdOf(stale)));
		ClaudeTemp.Found found = Scan();
		Assert.Equal(new[] { stale }, found.Idle.Select(e => e.Path));
		Assert.Equal(1, found.Kept);
		Assert.Empty(ClaudeTemp.Scan(container, 30, Keeps(30)).Idle); // a longer wait, when asked for
	}

	[Fact]
	public void ASessionTheClaudeAppListsAsOpen_KeepsItsFolder_AnArchivedOneDoesnt() {
		string open = Session(Long);
		InTheApp(IdOf(open), archived: false);
		string archived = Session(Long);
		InTheApp(IdOf(archived), archived: true);
		Assert.Equal("Its session is open in the Claude app", Keeps().Why(IdOf(open)));
		Assert.Null(Keeps().Why(IdOf(archived)));
		Assert.Equal(new[] { archived }, Scan().Idle.Select(e => e.Path));
	}

	[Fact]
	public void RecordsBeingWritten_StillKeepTheirSessions() {
		string running = Session(Long), open = Session(Long);
		Directory.CreateDirectory(Path.Combine(home, "sessions"));
		File.WriteAllText(Path.Combine(home, "sessions", "4242.json"), "{\"pid\":4242,\"sessionId\":\"" + IdOf(running) + "\",\"cwd\":\"C:\\\\Pro");
		Directory.CreateDirectory(app);
		File.WriteAllText(Path.Combine(app, "local_1.json"), "{\"sessionId\":\"local_1\",\"cliSessionId\":\"" + IdOf(open) + "\",\"isArch");
		Assert.NotNull(Keeps().Why(IdOf(running)));
		Assert.NotNull(Keeps().Why(IdOf(open)));
		Assert.Empty(Scan().Idle);
	}

	[Fact]
	public void AFolderAProgramHasAFileOpenIn_IsHeld_AndLookedAtAgainNextTime() {
		string folder = Session(Long);
		string note = Path.Combine(folder, "scratchpad", "notes.md");
		DateTime written = File.GetLastWriteTimeUtc(note);
		// As Node opens files: others may read, write and even delete it, but its folder can't be renamed.
		using (new FileStream(note, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
			Assert.True(ClaudeTemp.Held(folder));
			ClaudeTemp.Found found = Scan();
			Assert.Empty(found.Idle);
			Assert.Equal(1, found.Kept);
			// A cleanup leaves it whole, untouched, as held: not a failure.
			SafeDelete.Result r = SafeDelete.Away(folder);
			Assert.Equal(0, r.Left);
			Assert.Equal(1, r.Held);
			Assert.Equal(0, r.Bytes);
		}
		Assert.True(File.Exists(note), "the file held open went");
		Assert.Equal(written, File.GetLastWriteTimeUtc(note)); // the check renamed it away and back, and changed nothing
		Assert.False(ClaudeTemp.Held(folder));
		Assert.Equal(new[] { folder }, Scan().Idle.Select(e => e.Path));
	}

	[Fact]
	public void AProcessWorkingInAFolder_HoldsIt() {
		string folder = Session(Long);
		using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c echo ready& ping -n 30 127.0.0.1 >nul") {
			WorkingDirectory = Path.Combine(folder, "scratchpad"), CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
		})!;
		try {
			// A new process opens its working folder as it starts, after Windows has written which it is: once it runs a command, it has.
			Assert.Equal("ready", p.StandardOutput.ReadLine());
			Assert.True(ClaudeTemp.Held(folder));
		}
		finally {
			p.Kill(entireProcessTree: true); // ping too, which works in the folder as well
			p.WaitForExit();
		}
	}

	[Fact]
	public void OtherEntries_AreJudgedAsTempsOwn_AndOneNamingAKeptSession_Stays() {
		string session = Session(DateTime.UtcNow);
		using Process me = Process.GetCurrentProcess();
		Running(me.Id, StartOf(me), IdOf(session));
		string itsState = Path.Combine(container, $"cache-break-state-{IdOf(session)}.json");
		string another = Path.Combine(container, $"cache-break-state-{Guid.NewGuid()}.json");
		string probe = Path.Combine(container, "fswatch-probe-0X86Bl", "probe.txt");
		Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
		foreach (string f in new[] { itsState, another, probe }) {
			File.WriteAllText(f, "{}");
			File.SetLastWriteTimeUtc(f, Long);
		}
		Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(probe)!, Long);
		Assert.Equal(new[] { another, Path.GetDirectoryName(probe)! }.Order(), Scan().Idle.Select(e => e.Path).Order());
	}

	[Fact]
	public void ACleanup_AsksEveryGuardAgain() {
		string folder = Session(Long, bytes: 2 << 20);
		DevItem item = ClaudeTemp.Item(temp, 7, Keeps())!;
		// Resumed since the check listed it.
		Transcript(IdOf(folder), DateTime.UtcNow);
		CleanResult r = ClaudeTemp.Clean(item, 7, Keeps(), new FolderUse(DateTime.UtcNow, claudeHome: home, processes: () => new()));
		Assert.Equal(0, r.FreedBytes);
		Assert.True(Directory.Exists(folder));
		// And when Claude Code's sessions can't be read, nothing goes.
		Assert.NotNull(ClaudeTemp.Clean(item, 7, null, new FolderUse(DateTime.UtcNow, claudeHome: home, processes: () => new())).Error);
		Assert.True(Directory.Exists(folder));
	}
}
