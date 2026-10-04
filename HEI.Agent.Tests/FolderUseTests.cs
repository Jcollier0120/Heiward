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

namespace HEI.Agent.Tests;

/// <summary>
/// A folder in use: a Claude Code session's files, as Claude Code writes them (a stand-in Claude folder here, never the
/// real one), and a real process working in a folder.
/// </summary>
public sealed class FolderUseTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "hei-use-" + Guid.NewGuid().ToString("N"));
	readonly string claude, worktree;
	static readonly DateTime Now = DateTime.UtcNow;

	public FolderUseTests() {
		claude = Path.Combine(root, ".claude");
		worktree = Path.Combine(root, "repo", ".claude", "worktrees", "cranky-allen-d2b163");
		Directory.CreateDirectory(worktree);
	}

	public void Dispose() {
		try { Directory.Delete(root, true); } catch { }
	}

	FolderUse Use(List<WorkingFolder>? processes = null) => new(Now, claudeHome: claude, processes: () => processes ?? new());

	/// <summary>A transcript of a session in <paramref name="folder"/>, last written <paramref name="ago"/> ago.</summary>
	void Transcript(string folder, TimeSpan ago) {
		string dir = Path.Combine(claude, "projects", ClaudeSessions.Slug(folder));
		Directory.CreateDirectory(dir);
		string file = Path.Combine(dir, Guid.NewGuid() + ".jsonl");
		File.WriteAllText(file, "{\"type\":\"user\",\"cwd\":" + System.Text.Json.JsonSerializer.Serialize(folder) + "}\n");
		File.SetLastWriteTimeUtc(file, Now - ago);
	}

	/// <summary>sessions\&lt;pid&gt;.json, as Claude Code writes it for a session running now.</summary>
	void Session(int pid, string cwd, string? procStart) {
		string dir = Path.Combine(claude, "sessions");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, pid + ".json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> {
			["pid"] = pid, ["sessionId"] = Guid.NewGuid().ToString(), ["cwd"] = cwd, ["startedAt"] = 1791140705196, ["procStart"] = procStart,
			["version"] = "2.1.286", ["kind"] = "interactive", ["status"] = "idle",
		}));
		// Beside each, Claude Code keeps a key file: not a session.
		File.WriteAllText(Path.Combine(dir, pid + ".0123abcd.key"), "{\"peerToken\":\"x\"}");
	}

	static string StartOf(Process p) => p.StartTime.ToFileTimeUtc().ToString();

	[Theory]
	[InlineData(@"C:\Projects\Heiward\.claude\worktrees\cranky-allen-d2b163", "C--Projects-Heiward--claude-worktrees-cranky-allen-d2b163")]
	[InlineData(@"C:\worktrees\heiward-efficiency\", "C--worktrees-heiward-efficiency")]
	[InlineData(@"C:\Users\me\My Project_1", "C--Users-me-My-Project-1")]
	public void ClaudeCode_NamesAFoldersTranscriptsAfterItsPath(string folder, string slug) => Assert.Equal(slug, ClaudeSessions.Slug(folder));

	[Fact]
	public void NothingUsesAFolder_NoTranscriptNoSessionNoProcess() {
		Assert.Null(Use().Why(worktree));
		Transcript(Path.Combine(root, "repo"), TimeSpan.FromMinutes(5)); // the main checkout's session: another folder
		Assert.Null(Use().Why(worktree));
	}

	[Fact]
	public void ATranscriptWrittenInTheLastDay_KeepsTheWorktree() {
		Transcript(worktree, TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
		Assert.Equal("A Claude Code session used it 3 hours ago", Use().Why(worktree));
	}

	[Fact]
	public void AnOlderTranscript_DoesNot() {
		Transcript(worktree, TimeSpan.FromHours(30));
		Assert.Null(Use().Why(worktree));
		Assert.NotNull(new FolderUse(Now, TimeSpan.FromHours(48), claude, () => new()).Why(worktree)); // a longer wait, when asked for
	}

	[Fact]
	public void ALiveSessionWorkingInIt_OrBelowIt_KeepsTheWorktree() {
		using Process me = Process.GetCurrentProcess();
		Session(me.Id, Path.Combine(worktree, "HEI.Agent"), StartOf(me));
		string? why = Use().Why(worktree);
		Assert.Equal($"In use: a Claude Code session works in it ({me.ProcessName}, process {me.Id})", why);
		Assert.Null(Use().Why(Path.Combine(root, "elsewhere")));
	}

	[Fact]
	public void ASessionFileLeftBehind_DoesNot() {
		// Its process ended: Claude Code exited without removing the file.
		using var ended = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
		ended.WaitForExit();
		Session(ended.Id, worktree, "134356143046665905");
		Assert.Null(Use().Why(worktree));

		// Its number now belongs to another process, started later (this one).
		using Process me = Process.GetCurrentProcess();
		Session(me.Id, worktree, "134356143046665905");
		Assert.Null(Use().Why(worktree));
	}

	[Fact]
	public void AProcessWorkingInIt_KeepsIt_AndIsNamed() {
		var use = Use(new() { new WorkingFolder(4242, "Code", Path.Combine(worktree, "src")), new WorkingFolder(7, "explorer", root) });
		Assert.Equal("In use: Code (process 4242) works in it", use.Why(worktree));
		Assert.Equal(new[] { "Code (process 4242) works in it" }, use.Holders(worktree, Array.Empty<string>()));
	}

	[Fact]
	public void ARealProcessesWorkingFolder_IsRead() {
		if (!Environment.Is64BitProcess) return;
		using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") {
			WorkingDirectory = worktree, CreateNoWindow = true, UseShellExecute = false,
		})!;
		try {
			Assert.Equal(worktree, ProcessFolders.CurrentDirectoryOf(p.Id), StringComparer.OrdinalIgnoreCase);
			string? why = new FolderUse(Now, claudeHome: claude).Why(worktree);
			Assert.NotNull(why);
			Assert.StartsWith("In use: ", why);
			Assert.Contains("works in it", why);
		}
		finally {
			try { p.Kill(entireProcessTree: true); } catch { }
			p.WaitForExit();
		}
	}
}
