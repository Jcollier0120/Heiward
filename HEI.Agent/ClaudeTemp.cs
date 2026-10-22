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

using System.Text.Json;
using System.Text.RegularExpressions;

namespace HEI.Agent {
	/// <summary>
	/// Claude Code's temp folder, %TEMP%\claude: a folder per project, named after the folder its sessions work in as their
	/// transcripts' folder is (<see cref="ClaudeSessions.Slug"/>), holding a folder per session named by the session's id (its
	/// scratchpad, its tasks' output). Live sessions write in it every minute, so as a single Temp entry it was never untouched.
	/// Each session's folder is judged on its own instead, by its own newest change, and kept whatever its age while its session
	/// may still use it (<see cref="ClaudeKeeps"/>) or a program has a file in it open (<see cref="Held"/>). The rest of what's in
	/// it is judged as Temp's own entries are, and a project's folder goes once its sessions' have.
	/// </summary>
	static class ClaudeTemp {
		/// <summary>The developer item's kind; automatic cleanup takes it as temp files.</summary>
		public const string Kind = "claude";
		const string Folder = "claude";
		/// <summary>Added to a session's folder's name while it's renamed away (<see cref="Held"/>, <see cref="Remove"/>).</summary>
		const string Away = ".heiward";

		static readonly EnumerationOptions ListOptions = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };
		static readonly Regex SessionId = new("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		/// <summary>Claude Code's temp folder in the Temp folder <paramref name="temp"/>.</summary>
		public static string In(string temp) => Path.Combine(temp, Folder);

		/// <summary>Whether a Temp entry is Claude Code's temp folder, judged a session at a time rather than whole.</summary>
		public static bool IsIt(FileSystemInfo entry) => entry is DirectoryInfo && entry.Name.Equals(Folder, StringComparison.OrdinalIgnoreCase);

		static bool IsSession(FileSystemInfo e) => e is DirectoryInfo && e.Name.Length == 36 && SessionId.IsMatch(e.Name) && !IsLink(e);

		static bool IsLink(FileSystemInfo e) => (e.Attributes & FileAttributes.ReparsePoint) != 0;

		/// <param name="Idle">What may go: sessions' folders (<c>Session</c>) and other entries untouched for the whole time.</param>
		/// <param name="Kept">Sessions' folders that stay whatever their age: their session may still use them, or a program has a file in one open.</param>
		internal sealed record Found(List<(string Path, long Bytes, bool Session)> Idle, int Kept) {
			public int Sessions => Idle.Count(e => e.Session);
		}

		/// <summary>What in <paramref name="container"/> (Claude Code's temp folder) may go after <paramref name="days"/> days untouched.</summary>
		/// <param name="checkHeld">Rename each session's folder that may go away and back (<see cref="Held"/>); a cleanup that renames it away anyway doesn't.</param>
		internal static Found Scan(string container, int days, ClaudeKeeps keeps, bool checkHeld = true) {
			DateTime before = DateTime.UtcNow.AddDays(-days);
			var idle = new List<(string, long, bool)>();
			int kept = 0;
			foreach (FileSystemInfo e in Entries(container)) {
				List<FileSystemInfo> inside = e is DirectoryInfo && !IsLink(e) ? Entries(e.FullName) : [];
				// Not a project's folder (no session's folder in it): judged whole, as Temp's own entries are.
				if (!inside.Any(IsSession)) {
					if (Other(e, before, keeps) is { } old) idle.Add((old.Path, old.Bytes, false));
					continue;
				}
				foreach (FileSystemInfo s in inside) {
					if (!IsSession(s)) {
						if (Other(s, before, keeps) is { } old) idle.Add((old.Path, old.Bytes, false));
						continue;
					}
					// What keeps a session's folder is asked first: it costs no look inside.
					if (keeps.Why(s.Name, e.Name) != null) {
						kept++;
						continue;
					}
					var (bytes, newest) = DevScanner.Measure(s.FullName);
					if (newest >= before || s.LastWriteTimeUtc >= before) continue;
					if (checkHeld && Held(s.FullName)) {
						kept++;
						continue;
					}
					idle.Add((s.FullName, bytes, true));
				}
			}
			return new Found(idle, kept);
		}

		/// <summary>An entry that isn't a session's folder, judged as Temp's own are; kept while its name holds a kept session's id (cache-break-state-&lt;id&gt;.json).</summary>
		static (string Path, long Bytes)? Other(FileSystemInfo e, DateTime before, ClaudeKeeps keeps) =>
			SessionId.Matches(e.Name).Any(m => keeps.Why(m.Value) != null) ? null : DevScanner.Untouched(e, before);

		static List<FileSystemInfo> Entries(string folder) {
			try { return new DirectoryInfo(folder).EnumerateFileSystemInfos("*", ListOptions).ToList(); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
		}

		/// <summary>The developer item, under Temporary files: what may go, when it's a megabyte or more.</summary>
		internal static DevItem? Item(string temp, int days, ClaudeKeeps keeps) {
			string container = In(temp);
			Found found = Scan(container, days, keeps);
			long bytes = found.Idle.Sum(e => e.Bytes);
			if (bytes < 1 << 20) return null;
			int others = found.Idle.Count - found.Sessions;
			var facts = new List<string> { $"{found.Sessions:N0} session(s) idle for {days} days" };
			if (others > 0) facts.Add($"{others:N0} other item(s) untouched as long");
			if (found.Kept > 0) facts.Add($"{found.Kept:N0} kept: in use or resumable");
			return new DevItem(DevScanner.IdOf(DevScanner.Temp, container), Kind, "Claude Code sessions' temp files", container,
				found.Idle.Select(e => e.Path).ToList(), bytes, null, true, null, string.Join(" · ", facts));
		}

		/// <summary>
		/// Cleans what <paramref name="item"/> listed that may still go, every guard asked again now, then the project folders
		/// that leaves empty. <paramref name="keeps"/> null: Claude Code's sessions couldn't be read, and nothing goes.
		/// </summary>
		internal static CleanResult Clean(DevItem item, int days, ClaudeKeeps? keeps, FolderUse use) {
			if (keeps == null) return new CleanResult(0, 0, "Claude Code's sessions couldn't be read, so its temp files wait for the next check");
			var still = Scan(item.Location, days, keeps, checkHeld: false).Idle.ToDictionary(e => e.Path, e => e.Session, StringComparer.OrdinalIgnoreCase);
			var r = new SafeDelete.Result(0, 0);
			var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string p in item.Paths) {
				if (!still.TryGetValue(p, out bool session)) continue;
				if (session) {
					r = r.Add(Remove(p));
					projects.Add(Path.GetDirectoryName(p)!);
				}
				else r = r.Add(File.Exists(p) ? SafeDelete.File(p) : SafeDelete.Tree(p));
			}
			// Windows deletes a folder this way only while it's empty: one a session was started in meanwhile stays.
			foreach (string project in projects)
				try { Directory.Delete(project); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			return CleanResult.Of(r, item.Location, use);
		}

		/// <summary>
		/// Whether a program has a file in <paramref name="folder"/> open, or works in it. Windows won't rename a folder then,
		/// whatever the program let others do with its files (Node, which Claude Code runs on, lets them delete its open files,
		/// not rename their folder). So it's renamed away and straight back: one that can't be is held, and looked at again the
		/// next time.
		/// </summary>
		internal static bool Held(string folder) {
			string away = folder + Away;
			try { Directory.Move(folder, away); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
			for (int attempt = 1; ; attempt++) {
				try {
					Directory.Move(away, folder);
					return false;
				}
				catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 5) { Thread.Sleep(100); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					// Its name was taken meanwhile (its session came back). Under its other name it keeps the session's id, so it
					// stays as long as the session is kept.
					AgentPaths.AppendLog($"Claude Code's temp: a session's folder couldn't be renamed back ({e.Message})");
					return true;
				}
			}
		}

		/// <summary>
		/// Deletes a session's folder, renamed away first: what <see cref="Held"/> checks, and nothing can open a file in it by its
		/// path while it goes. Whatever can't be deleted goes back under its name.
		/// </summary>
		internal static SafeDelete.Result Remove(string folder) {
			string away = folder + Away;
			try { Directory.Move(folder, away); }
			catch (DirectoryNotFoundException) { return new SafeDelete.Result(0, 0); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return SafeDelete.Result.Failed(folder, e); }
			SafeDelete.Result r = SafeDelete.Tree(away);
			if (r.Left > 0)
				try { Directory.Move(away, folder); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			return r;
		}
	}

	/// <summary>
	/// The Claude Code sessions whose temp stays whatever its age, and why. A session that's running: Claude Code's
	/// sessions\&lt;pid&gt;.json while that process runs, the very one that wrote it (a number Windows gave another process
	/// since doesn't count). One that can still be resumed: its transcript, projects\&lt;project&gt;\&lt;id&gt;.jsonl, written in
	/// that time. One the Claude app lists as open, not archived. Read only.
	/// </summary>
	sealed class ClaudeKeeps {
		readonly Dictionary<string, string> sessions = new(StringComparer.OrdinalIgnoreCase);
		/// <summary>Project folders kept whole: an older Claude Code's running session, whose record doesn't say which session it is.</summary>
		readonly Dictionary<string, string> projects = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Why the session's temp stays; null when nothing keeps it. <paramref name="project"/>: the name of the project folder it's in.</summary>
		public string? Why(string sessionId, string? project = null) =>
			sessions.TryGetValue(sessionId, out string? why) ? why
			: project != null && projects.TryGetValue(project, out why) ? why
			: null;

		/// <summary>This PC's, as Claude Code and the Claude app keep them now; null when they can't be read, and then nothing goes.</summary>
		public static ClaudeKeeps? Now(int days) => Read(ClaudeSessions.DefaultHome, AppStores(), DateTime.UtcNow.AddDays(-days));

		/// <param name="claudeHome">Claude Code's own folder (<see cref="ClaudeSessions.DefaultHome"/>).</param>
		/// <param name="appStores">Where the Claude app keeps its records of Claude Code sessions (<see cref="AppStores"/>).</param>
		/// <param name="since">A session whose transcript was written since then can still be resumed.</param>
		internal static ClaudeKeeps? Read(string claudeHome, IEnumerable<string> appStores, DateTime since) {
			var keeps = new ClaudeKeeps();
			try {
				string running = Path.Combine(claudeHome, "sessions");
				if (Directory.Exists(running))
					foreach (string file in Directory.EnumerateFiles(running, "*.json"))
						keeps.Running(file);
				string transcripts = Path.Combine(claudeHome, "projects");
				if (Directory.Exists(transcripts))
					foreach (string project in Directory.EnumerateDirectories(transcripts))
						foreach (FileInfo t in new DirectoryInfo(project).EnumerateFiles("*.jsonl"))
							if (t.LastWriteTimeUtc >= since)
								keeps.sessions.TryAdd(Path.GetFileNameWithoutExtension(t.Name), $"Its session can be resumed: used {t.LastWriteTimeUtc.ToLocalTime():g}");
				var records = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = false, AttributesToSkip = 0 };
				foreach (string store in appStores)
					if (Directory.Exists(store))
						foreach (string file in Directory.EnumerateFiles(store, "local_*.json", records))
							keeps.Open(file);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
			return keeps;
		}

		/// <summary>
		/// Where the Claude app keeps its records of the Claude Code sessions it runs (claude-code-sessions\&lt;account&gt;\&lt;organization&gt;\local_&lt;id&gt;.json,
		/// each with the session's id and whether it's archived): its app data (%APPDATA%\Claude), the Store version's own copy of
		/// it, and where newer versions moved their data, %LOCALAPPDATA%\Claude-Data\&lt;name&gt; (leaving only a .user-data-left.&lt;name&gt;
		/// file in the old places).
		/// </summary>
		internal static IEnumerable<string> AppStores() {
			const string Records = "claude-code-sessions";
			string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			yield return Path.Combine(roaming, "Claude", Records);
			string packages = Path.Combine(local, "Packages");
			if (Directory.Exists(packages))
				foreach (string package in Directory.EnumerateDirectories(packages, "Claude_*"))
					yield return Path.Combine(package, "LocalCache", "Roaming", "Claude", Records);
			string moved = Path.Combine(local, "Claude-Data");
			if (Directory.Exists(moved))
				foreach (string data in Directory.EnumerateDirectories(moved))
					yield return Path.Combine(data, Records);
		}

		/// <summary>sessions\&lt;pid&gt;.json: its session stays while its process runs.</summary>
		void Running(string file) {
			if (Text(file) is not { } text) return; // gone: its session ended
			try {
				using var doc = JsonDocument.Parse(text);
				JsonElement s = doc.RootElement;
				if (!s.TryGetProperty("pid", out JsonElement pidValue) || !pidValue.TryGetInt32(out int pid)) return;
				if (ClaudeSessions.Running(pid, ClaudeSessions.ProcStart(s)) is not { } name) return;
				string why = $"Its session is running ({name}, process {pid})";
				if (s.TryGetProperty("sessionId", out JsonElement id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } sessionId)
					sessions.TryAdd(sessionId, why);
				// An older Claude Code doesn't say which session it is: every one of the folder it works in stays.
				else if (s.TryGetProperty("cwd", out JsonElement cwd) && cwd.ValueKind == JsonValueKind.String && cwd.GetString() is { Length: > 0 } dir)
					projects.TryAdd(ClaudeSessions.Slug(dir), why);
			}
			// Being written: whichever session it names stays.
			catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) {
				if (Field("sessionId", text) is { } sessionId) sessions.TryAdd(sessionId, "Its session may be running: its record was being written");
			}
		}

		/// <summary>The Claude app's local_&lt;id&gt;.json: its session stays while the app lists it, open or idle, until it's archived.</summary>
		void Open(string file) {
			if (Text(file) is not { } text) return; // gone: deleted in the app
			const string Why = "Its session is open in the Claude app";
			try {
				using var doc = JsonDocument.Parse(text);
				JsonElement r = doc.RootElement;
				if (r.TryGetProperty("isArchived", out JsonElement archived) && archived.ValueKind == JsonValueKind.True) return;
				if (r.TryGetProperty("cliSessionId", out JsonElement id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } sessionId)
					sessions.TryAdd(sessionId, Why);
			}
			catch (Exception e) when (e is JsonException or InvalidOperationException) {
				if (Field("cliSessionId", text) is { } sessionId) sessions.TryAdd(sessionId, Why);
			}
		}

		/// <summary>A record's text; null once it's gone. One being written is read again a moment later; one that still can't be is thrown.</summary>
		static string? Text(string file) {
			for (int attempt = 1; ; attempt++) {
				try { return File.ReadAllText(file); }
				catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
				catch (IOException) when (attempt < 3) { Thread.Sleep(50); }
			}
		}

		/// <summary>A session id a record names, read from its text when it isn't whole JSON yet.</summary>
		static string? Field(string name, string text) =>
			Regex.Match(text, "\"" + name + "\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"") is { Success: true } m ? m.Groups[1].Value : null;
	}
}
