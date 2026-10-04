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

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent {
	/// <summary>A running process and the folder it works in (its current directory).</summary>
	readonly record struct WorkingFolder(int Pid, string Name, string Folder);

	/// <summary>
	/// Whether a folder (a worktree, usually) is in use right now, and by what: a running process works in it (Windows
	/// won't remove a folder a process works in, nor anything above it), a live Claude Code session works in it, or a
	/// Claude Code session used it lately and may come back to it. One per developer check or cleanup: the processes are
	/// read once, when first asked.
	/// </summary>
	sealed class FolderUse {
		/// <summary>A Claude Code session that wrote to its transcript this recently may come back: its worktree waits.</summary>
		public static readonly TimeSpan RecentSession = TimeSpan.FromHours(24);

		readonly DateTime nowUtc;
		readonly TimeSpan recent;
		readonly string claudeHome;
		readonly Lazy<List<WorkingFolder>> processes;

		/// <param name="claudeHome">Claude Code's own folder; by default CLAUDE_CONFIG_DIR, or %USERPROFILE%\.claude.</param>
		/// <param name="processes">The running processes' working folders; by default read from Windows (<see cref="ProcessFolders.All"/>).</param>
		public FolderUse(DateTime nowUtc, TimeSpan? recent = null, string? claudeHome = null, Func<List<WorkingFolder>>? processes = null) {
			this.nowUtc = nowUtc;
			this.recent = recent ?? RecentSession;
			this.claudeHome = claudeHome ?? ClaudeSessions.DefaultHome;
			this.processes = new(processes ?? ProcessFolders.All);
		}

		/// <summary>Why <paramref name="folder"/> is in use, for the page and the log; null when nothing is known to use it.</summary>
		public string? Why(string folder) {
			if (ClaudeSessions.LiveIn(claudeHome, folder) is { } session)
				return $"In use: a Claude Code session works in it ({session.Name}, process {session.Pid})";
			if (WorkingIn(folder).FirstOrDefault() is { Name: not null } p)
				return $"In use: {p.Name} (process {p.Pid}) works in it";
			if (ClaudeSessions.LastUsedUtc(claudeHome, folder) is { } used && nowUtc - used < recent)
				return $"A Claude Code session used it {Ago(nowUtc - used)}";
			return null;
		}

		/// <summary>The running processes that work in <paramref name="folder"/> or below it.</summary>
		public List<WorkingFolder> WorkingIn(string folder) => processes.Value.Where(p => ListingPlan.IsUnder(p.Folder, folder)).ToList();

		/// <summary>
		/// What holds what a deletion left behind in <paramref name="root"/>: processes working in it, and those with
		/// one of <paramref name="left"/> open (the first few). Empty when nothing is known.
		/// </summary>
		public List<string> Holders(string root, IReadOnlyCollection<string> left) {
			var holders = WorkingIn(root).Select(p => $"{p.Name} (process {p.Pid}) works in it").ToList();
			holders.AddRange(FileLockers.Of(left.Where(File.Exists).Take(10).ToList()).Select(p => $"{p} has a file open"));
			return holders.Distinct().ToList();
		}

		static string Ago(TimeSpan t) =>
			t.TotalMinutes < 2 ? "a minute ago" :
			t.TotalHours < 2 ? $"{(int)t.TotalMinutes} minutes ago" :
			$"{(int)t.TotalHours} hours ago";
	}

	/// <summary>
	/// Claude Code's sessions, as it keeps them in its own folder (%USERPROFILE%\.claude): sessions\&lt;pid&gt;.json for each
	/// session running now, with its working folder ("cwd"), and projects\&lt;folder as a name&gt;\*.jsonl, each session's
	/// transcript, written as it works. Read only.
	/// </summary>
	static class ClaudeSessions {
		public static string DefaultHome =>
			Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

		/// <summary>Claude Code names a folder's transcripts after its path, every character but a letter or a digit a '-'.</summary>
		internal static string Slug(string folder) => Regex.Replace(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), "[^a-zA-Z0-9]", "-");

		/// <summary>Claude Code cuts a name longer than this and adds a hash of the whole path.</summary>
		const int LongestSlug = 200;

		/// <summary>A live session (its process still running, the same process that wrote the file) that works in <paramref name="folder"/> or below it.</summary>
		public static WorkingFolder? LiveIn(string home, string folder) {
			string sessions = Path.Combine(home, "sessions");
			IEnumerable<string> files;
			try { files = Directory.Exists(sessions) ? Directory.EnumerateFiles(sessions, "*.json").ToList() : []; }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
			foreach (string file in files) {
				try {
					using var doc = JsonDocument.Parse(File.ReadAllText(file));
					JsonElement s = doc.RootElement;
					if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("cwd", out JsonElement cwd) || cwd.GetString() is not { Length: > 0 } dir) continue;
					if (!s.TryGetProperty("pid", out JsonElement pidValue) || !pidValue.TryGetInt32(out int pid)) continue;
					if (!ListingPlan.IsUnder(dir, folder)) continue;
					string? procStart = s.TryGetProperty("procStart", out JsonElement ps) ? ps.ValueKind == JsonValueKind.String ? ps.GetString() : ps.ToString() : null;
					if (Running(pid, procStart) is { } name) return new WorkingFolder(pid, name, dir);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
			}
			return null;
		}

		/// <summary>
		/// The process's name while it runs; null once it's gone, or when its number now belongs to a process started
		/// later than the session's (<paramref name="procStart"/>, a FILETIME, when the file has it).
		/// </summary>
		static string? Running(int pid, string? procStart) {
			try {
				using Process p = Process.GetProcessById(pid);
				if (p.HasExited) return null;
				if (long.TryParse(procStart, out long started)) {
					try {
						if (p.StartTime.ToFileTimeUtc() != started) return null;
					}
					catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { } // can't tell: it counts
				}
				return p.ProcessName;
			}
			catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) {
				return null;
			}
		}

		/// <summary>When a Claude Code session last wrote a transcript for <paramref name="folder"/>; null when none has.</summary>
		public static DateTime? LastUsedUtc(string home, string folder) {
			string projects = Path.Combine(home, "projects");
			string slug = Slug(folder);
			try {
				if (!Directory.Exists(projects)) return null;
				IEnumerable<string> dirs = slug.Length <= LongestSlug
					? new[] { Path.Combine(projects, slug) }.Where(Directory.Exists)
					: Directory.EnumerateDirectories(projects, slug[..LongestSlug] + "-*");
				DateTime? newest = null;
				foreach (string dir in dirs)
					foreach (string transcript in Directory.EnumerateFiles(dir, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 })) {
						DateTime written = File.GetLastWriteTimeUtc(transcript);
						if (newest == null || written > newest) newest = written;
					}
				return newest;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return null;
			}
		}
	}

	/// <summary>Each running process's working folder, read from its process parameters (as Process Explorer shows it).</summary>
	static class ProcessFolders {
		/// <summary>Every process this user may read; those it may not (another account's, protected ones) are left out.</summary>
		public static List<WorkingFolder> All() {
			var found = new List<WorkingFolder>();
			if (!Environment.Is64BitProcess) return found; // the layouts read below are the 64-bit ones
			foreach (Process p in Process.GetProcesses()) {
				using (p) {
					try {
						if (p.Id == 0 || p.Id == 4) continue; // Idle and System
						if (CurrentDirectoryOf(p.Id) is { Length: > 0 } dir) found.Add(new WorkingFolder(p.Id, p.ProcessName, dir));
					}
					catch (Exception e) when (e is InvalidOperationException or Win32Exception) { } // gone meanwhile
				}
			}
			return found;
		}

		const uint QueryInformation = 0x0400, QueryLimitedInformation = 0x1000, VmRead = 0x0010;
		const int ProcessBasicInformation = 0, ProcessWow64Information = 26;

		/// <summary>The process's current directory, without its trailing separator; null when it can't be read.</summary>
		internal static string? CurrentDirectoryOf(int pid) {
			using SafeProcessHandle h = OpenProcess(QueryInformation | VmRead, false, pid) is { IsInvalid: false } full ? full : OpenProcess(QueryLimitedInformation | VmRead, false, pid);
			if (h.IsInvalid) return null;
			// A 32-bit process (WOW64) keeps its own parameters, and changes its folder in those.
			if (NtQueryInformationProcess(h, ProcessWow64Information, out IntPtr peb32, IntPtr.Size, out _) == 0 && peb32 != IntPtr.Zero) {
				if (ReadUInt32(h, peb32 + 0x10) is not { } parameters32 || parameters32 == 0) return null;
				return ReadUnicodeString32(h, (IntPtr)(long)parameters32 + 0x24);
			}
			var info = new BasicInformation();
			if (NtQueryInformationProcess(h, ProcessBasicInformation, ref info, Marshal.SizeOf<BasicInformation>(), out _) != 0 || info.PebBaseAddress == IntPtr.Zero) return null;
			if (ReadPointer(h, info.PebBaseAddress + 0x20) is not { } parameters || parameters == IntPtr.Zero) return null;
			return ReadUnicodeString64(h, parameters + 0x38); // RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath
		}

		static string? Trimmed(string? dir) => dir == null ? null : Path.TrimEndingDirectorySeparator(dir);

		static string? ReadUnicodeString64(SafeProcessHandle h, IntPtr at) {
			byte[]? s = Read(h, at, 16);
			if (s == null) return null;
			int length = BitConverter.ToUInt16(s, 0);
			var buffer = (IntPtr)BitConverter.ToInt64(s, 8);
			return length == 0 || buffer == IntPtr.Zero || length > 0x10000 ? null : Trimmed(Read(h, buffer, length) is { } text ? Encoding.Unicode.GetString(text) : null);
		}

		static string? ReadUnicodeString32(SafeProcessHandle h, IntPtr at) {
			byte[]? s = Read(h, at, 8);
			if (s == null) return null;
			int length = BitConverter.ToUInt16(s, 0);
			var buffer = (IntPtr)(long)BitConverter.ToUInt32(s, 4);
			return length == 0 || buffer == IntPtr.Zero || length > 0x10000 ? null : Trimmed(Read(h, buffer, length) is { } text ? Encoding.Unicode.GetString(text) : null);
		}

		static IntPtr? ReadPointer(SafeProcessHandle h, IntPtr at) => Read(h, at, 8) is { } b ? (IntPtr)BitConverter.ToInt64(b, 0) : null;
		static uint? ReadUInt32(SafeProcessHandle h, IntPtr at) => Read(h, at, 4) is { } b ? BitConverter.ToUInt32(b, 0) : null;

		static byte[]? Read(SafeProcessHandle h, IntPtr at, int size) {
			var buffer = new byte[size];
			return ReadProcessMemory(h, at, buffer, size, out IntPtr read) && read == size ? buffer : null;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct BasicInformation {
			public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

		[DllImport("ntdll.dll")]
		static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, ref BasicInformation info, int length, out int returned);

		[DllImport("ntdll.dll")]
		static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, out IntPtr info, int length, out int returned);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, [Out] byte[] buffer, nint size, out IntPtr read);
	}

	/// <summary>Which processes have a file open, as Windows' Restart Manager knows (what an installer asks before replacing a file).</summary>
	static class FileLockers {
		/// <summary>The names of the processes holding any of <paramref name="files"/> ("Code (process 1234)"); empty when none or unknown.</summary>
		public static List<string> Of(IReadOnlyList<string> files) {
			var names = new List<string>();
			if (files.Count == 0) return names;
			var key = new StringBuilder(33); // CCH_RM_SESSION_KEY + 1
			if (RmStartSession(out uint session, 0, key) != 0) return names;
			try {
				string[] plain = files.Select(f => f.StartsWith(@"\\?\", StringComparison.Ordinal) && !f.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? f[4..] : f).ToArray();
				if (RmRegisterResources(session, (uint)plain.Length, plain, 0, IntPtr.Zero, 0, null) != 0) return names;
				uint count = 0, reasons = 0;
				int rc = RmGetList(session, out uint needed, ref count, null, ref reasons);
				if (rc != MoreData || needed == 0) return names;
				var info = new ProcessInfo[needed];
				count = needed;
				if (RmGetList(session, out _, ref count, info, ref reasons) != 0) return names;
				foreach (ProcessInfo p in info.Take((int)count)) {
					string name = p.AppName;
					try {
						using Process process = Process.GetProcessById(p.Process.ProcessId);
						name = process.ProcessName;
					}
					catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
					names.Add($"{name} (process {p.Process.ProcessId})");
				}
			}
			finally {
				RmEndSession(session);
			}
			return names.Distinct().ToList();
		}

		const int MoreData = 234;

		[StructLayout(LayoutKind.Sequential)]
		struct UniqueProcess {
			public int ProcessId;
			public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct ProcessInfo {
			public UniqueProcess Process;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
			public int ApplicationType;
			public uint AppStatus;
			public uint SessionId;
			[MarshalAs(UnmanagedType.Bool)] public bool Restartable;
		}

		[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
		static extern int RmStartSession(out uint session, int flags, StringBuilder key);

		[DllImport("rstrtmgr.dll")]
		static extern int RmEndSession(uint session);

		[DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
		static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint applications, IntPtr uniqueProcesses, uint services, string[]? serviceNames);

		[DllImport("rstrtmgr.dll")]
		static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? info, ref uint rebootReasons);
	}
}
