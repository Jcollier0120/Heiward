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
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VDF.Agent {
	/// <summary>
	/// Something a developer's tools recreate when needed, and what cleaning it takes. <see cref="Blocked"/>
	/// says why it can't be cleaned right now (in use, uncommitted work); <see cref="Suggested"/> items
	/// start ticked on the page.
	/// </summary>
	sealed record DevItem(string Id, string Kind, string Name, string Location, List<string> Paths, long Bytes,
		DateTime? LastUsedUtc, bool Suggested, string? Blocked, string Detail, string? Repo = null);

	sealed record DevCategory(string Key, string Title, string Explain, List<DevItem> Items);

	/// <summary>The last developer-mode check (dev-report.json).</summary>
	sealed class DevReport {
		public DateTime ScannedAtUtc { get; set; }
		public double DurationSec { get; set; }
		public int StaleDays { get; set; }
		public List<DevCategory> Categories { get; set; } = new();
		/// <summary>Repositories with a remote, and their local branches merged into its default branch.</summary>
		public List<RepoBranches> Repositories { get; set; } = new();

		public static string FilePath => Path.Combine(AgentPaths.Home, "dev-report.json");
		static readonly object gate = new();

		public static DevReport? Load() {
			try {
				return File.Exists(FilePath) ? JsonSerializer.Deserialize<DevReport>(File.ReadAllText(FilePath), AgentConfig.Json) : null;
			}
			catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
				return null;
			}
		}

		public void Save() => AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(this, AgentConfig.Json));

		/// <summary>Replaces one repository's branch state in the saved report.</summary>
		public static void UpdateRepository(RepoBranches repo) {
			lock (gate) {
				DevReport? report = Load();
				if (report == null) return;
				int i = report.Repositories.FindIndex(r => r.Id == repo.Id);
				if (i >= 0) report.Repositories[i] = repo;
				report.Save();
			}
		}

		/// <summary>Replaces one item (or drops it, when <paramref name="replacement"/> is null) in the saved report.</summary>
		public static void Update(string id, DevItem? replacement) {
			lock (gate) {
				DevReport? report = Load();
				if (report == null) return;
				foreach (DevCategory c in report.Categories) {
					int i = c.Items.FindIndex(x => x.Id == id);
					if (i < 0) continue;
					if (replacement == null) c.Items.RemoveAt(i);
					else c.Items[i] = replacement;
				}
				report.Save();
			}
		}
	}

	/// <summary>
	/// Developer mode: finds what builds and tools leave behind. Nothing here guesses: a folder counts as
	/// a build output only next to the project file that makes it (bin/obj beside a .csproj, build beside
	/// build.gradle), caches are the tools' own documented folders, and links are never followed.
	/// </summary>
	static class DevScanner {
		public const string Projects = "projects", Worktrees = "worktrees", Caches = "caches", Android = "android", Temp = "temp";

		static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

		public static DevReport Run(AgentConfig cfg, CancellationToken ct = default) {
			var timer = Stopwatch.StartNew();
			var report = new DevReport { ScannedAtUtc = DateTime.UtcNow, StaleDays = cfg.StaleProjectDays };
			DateTime staleBefore = DateTime.UtcNow.AddDays(-cfg.StaleProjectDays);
			List<string> repos = FindRepositories(ScanScope.Roots(cfg), ScanScope.ExclusionRules(cfg), ct);

			report.Categories.Add(new DevCategory(Projects, "Build outputs and dependencies in your projects",
				"node_modules, bin and obj, Gradle build folders, target, Python virtual environments. The next install or build recreates them. " +
				$"Ticked in projects untouched for {cfg.StaleProjectDays} days.",
				repos.Where(r => Directory.Exists(Path.Combine(r, ".git")) || !Path.Exists(Path.Combine(r, ".git")))
					.Select(r => ProjectItem(r, staleBefore, ct)).Where(i => i != null).Select(i => i!)
					.OrderByDescending(i => i.Bytes).ToList()));

			report.Categories.Add(new DevCategory(Worktrees, "Git worktrees",
				"Extra working folders of a repository; AI coding agents leave many behind. Git removes one only when it has no uncommitted changes, " +
				"and it's offered only when its commits are pushed. The branch stays.",
				repos.SelectMany(r => WorktreeItems(r, staleBefore, ct)).OrderByDescending(i => i.Bytes).ToList()));

			report.Categories.Add(new DevCategory(Caches, "Package and build caches",
				"Downloads that Gradle, NuGet, npm and other tools keep. They're downloaded again when a build needs them, so the next build is slower. " +
				"Never ticked for you.",
				CacheItems().OrderByDescending(i => i.Bytes).ToList()));

			report.Categories.Add(new DevCategory(Android, "Android emulators",
				"Emulator system images that no emulator uses (ticked), and emulators you haven't started in a while. Deleting an emulator deletes the apps and data inside it.",
				AndroidItems(staleBefore).OrderByDescending(i => i.Bytes).ToList()));

			report.Categories.Add(new DevCategory(Temp, "Temporary files and crash dumps",
				$"Files in your Temp folder untouched for {cfg.TempOlderThanDays} days, and crash dumps apps left behind.",
				TempItems(cfg.TempOlderThanDays).ToList()));

			report.Categories.RemoveAll(c => c.Items.Count == 0);
			// Main checkouts only: a repository's worktrees share its branches.
			report.Repositories = repos.Where(r => Directory.Exists(Path.Combine(r, ".git")))
				.Select(BranchPruner.Inspect).Where(r => r != null).Select(r => r!)
				.OrderByDescending(r => r.Merged.Count).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
			report.DurationSec = Math.Round(timer.Elapsed.TotalSeconds, 1);
			return report;
		}

		// ------------------------------------------------------------------ projects

		/// <summary>Code repositories below the roots, by the scan's own rules (a repository isn't descended into).</summary>
		internal static List<string> FindRepositories(IEnumerable<string> roots, IReadOnlyList<ScanScope.Rule> rules, CancellationToken ct) {
			var repos = new List<string>();
			var queue = new Queue<string>();
			foreach (string root in roots) {
				if (IsRepository(root)) repos.Add(root.TrimEnd(Path.DirectorySeparatorChar));
				else queue.Enqueue(root);
			}
			while (queue.Count > 0 && !ct.IsCancellationRequested) {
				string dir = queue.Dequeue();
				IEnumerable<DirectoryInfo> subfolders;
				try { subfolders = new DirectoryInfo(dir).EnumerateDirectories("*", ListOptions).ToList(); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
				foreach (DirectoryInfo d in subfolders) {
					string? reason = ScanScope.ExemptReason(d, rules);
					if (reason == ScanScope.RepositoryReason) repos.Add(d.FullName);
					else if (reason == null) queue.Enqueue(d.FullName);
				}
			}
			// A repository listed in folders is a root of its own, and the drive's walk finds it too.
			return repos.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		}

		static bool IsRepository(string folder) => ScanScope.RepositoryMarkers.Any(m => Path.Exists(Path.Combine(folder, m)));

		static DevItem? ProjectItem(string repo, DateTime staleBefore, CancellationToken ct) {
			var outputs = FindBuildOutputs(repo, ct);
			if (outputs.Count == 0) return null;
			long bytes = outputs.Sum(o => Measure(o.Path).Bytes);
			if (bytes < 1 << 20) return null; // under 1 MB: not worth a row
			DateTime lastUsed = LastUsed(repo, GitDirOf(repo));
			bool stale = lastUsed < staleBefore;
			string detail = string.Join(", ", outputs.GroupBy(o => o.Label).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));
			return new DevItem(IdOf(Projects, repo), Projects, Path.GetFileName(repo), repo, outputs.Select(o => o.Path).ToList(), bytes,
				lastUsed == DateTime.MinValue ? null : lastUsed, stale, null, detail);
		}

		/// <summary>Build outputs inside a repository, each recognised by the project file beside it. Nested repositories are their own.</summary>
		internal static List<(string Path, string Label)> FindBuildOutputs(string repo, CancellationToken ct = default) {
			var found = new List<(string, string)>();
			var stack = new Stack<(string Dir, int Depth)>();
			stack.Push((repo, 0));
			while (stack.Count > 0 && !ct.IsCancellationRequested) {
				var (dir, depth) = stack.Pop();
				DirectoryInfo[] subfolders;
				try { subfolders = new DirectoryInfo(dir).GetDirectories("*", ListOptions); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
				foreach (DirectoryInfo d in subfolders) {
					if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
					if (BuildOutputLabel(d) is { } label) {
						found.Add((d.FullName, label));
						continue;
					}
					if (depth >= 6 || d.Name is ".git" or ".hg" or ".svn" or ".vs" or ".idea" || IsRepository(d.FullName)) continue;
					stack.Push((d.FullName, depth + 1));
				}
			}
			return found;
		}

		/// <summary>What a folder is, when a build tool made it: judged by the project file in the folder above.</summary>
		internal static string? BuildOutputLabel(DirectoryInfo folder) {
			string? parent = folder.Parent?.FullName;
			if (parent == null) return null;
			bool Beside(params string[] patterns) {
				try { return patterns.Any(p => Directory.EnumerateFiles(parent, p).Any()); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
			}
			return folder.Name.ToLowerInvariant() switch {
				"node_modules" => Beside("package.json") ? "node_modules" : null,
				"bin" or "obj" => Beside("*.csproj", "*.fsproj", "*.vbproj") ? "bin/obj" : null,
				"build" or ".cxx" => Beside("build.gradle", "build.gradle.kts") ? "Gradle build" : null,
				".gradle" => Beside("settings.gradle", "settings.gradle.kts", "build.gradle", "build.gradle.kts", "gradlew") ? "Gradle build" : null,
				"target" => Beside("Cargo.toml", "pom.xml") ? "target" : null,
				".venv" or "venv" => File.Exists(Path.Combine(folder.FullName, "pyvenv.cfg")) ? "Python venv" : null,
				".next" => Beside("next.config.js", "next.config.mjs", "next.config.ts") ? ".next" : null,
				_ => null,
			};
		}

		/// <summary>
		/// When the project was last worked on: git's own files (every commit, checkout, add, fetch touches
		/// one) and the entries at the project's top level, build outputs aside.
		/// </summary>
		internal static DateTime LastUsed(string folder, string? gitDir) {
			var times = new List<DateTime>();
			if (gitDir != null)
				foreach (string f in new[] { "index", "HEAD", "FETCH_HEAD", "ORIG_HEAD", Path.Combine("logs", "HEAD") }) {
					string p = Path.Combine(gitDir, f);
					if (File.Exists(p)) times.Add(File.GetLastWriteTimeUtc(p));
				}
			try {
				foreach (FileSystemInfo e in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", ListOptions))
					if (e.Name is not ("node_modules" or "bin" or "obj" or "build" or ".gradle" or "target" or ".venv" or "venv" or ".next"))
						times.Add(e.LastWriteTimeUtc);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			return times.Count > 0 ? times.Max() : DateTime.MinValue;
		}

		/// <summary>The repository's git folder: .git itself, or where a worktree's .git file points.</summary>
		internal static string? GitDirOf(string repo) {
			string dotGit = Path.Combine(repo, ".git");
			if (Directory.Exists(dotGit)) return dotGit;
			try {
				if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is { } text && text.StartsWith("gitdir:", StringComparison.Ordinal)) {
					string target = text["gitdir:".Length..].Trim();
					return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(repo, target));
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			return null;
		}

		// ------------------------------------------------------------------ worktrees

		static IEnumerable<DevItem> WorktreeItems(string repo, DateTime staleBefore, CancellationToken ct) {
			string meta = Path.Combine(repo, ".git", "worktrees");
			if (!Directory.Exists(meta)) yield break;
			foreach (string w in Directory.EnumerateDirectories(meta)) {
				if (ct.IsCancellationRequested) yield break;
				string? path = WorktreePath(w);
				if (path == null || !Directory.Exists(path)) continue; // a deleted worktree's record takes no space
				long bytes = Measure(path).Bytes;
				DateTime lastUsed = LastUsed(path, w);
				string branch = BranchOf(w) ?? "detached";
				string? blocked = File.Exists(Path.Combine(w, "locked")) ? "Locked in git"
					: ToolHome(path) is { } tool ? $"Kept for the tool that made it ({tool})"
					: ScheduledTasksText.Value.Contains(path, StringComparison.OrdinalIgnoreCase) ? "A scheduled task uses it"
					: WorktreeBlocker(path);
				yield return new DevItem(IdOf(Worktrees, path), Worktrees, Path.GetFileName(path), path, new() { path }, bytes,
					lastUsed == DateTime.MinValue ? null : lastUsed, lastUsed < staleBefore && blocked == null, blocked,
					$"branch {branch} · worktree of {Path.GetFileName(repo)}", repo);
			}
		}

		/// <summary>
		/// The tool whose home holds <paramref name="path"/>: a dot-folder of the user profile
		/// (~\.npu-agent\checkouts\...) or app data. A tool keeps a worktree there for its own use (npu-agent's
		/// jobs read theirs), so removing it would break the tool. Worktrees under a project's own folder
		/// (repo\.claude\worktrees) are the user's.
		/// </summary>
		internal static string? ToolHome(string path) {
			foreach (string home in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) })
				if (!string.IsNullOrEmpty(home) && path.StartsWith(home.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
					return "app data";
			string profile = Home.TrimEnd('\\') + "\\";
			if (!path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)) return null;
			string first = path[profile.Length..].Split('\\')[0];
			return first.StartsWith('.') ? first : null;
		}

		/// <summary>Every scheduled task's command and start folder, read once: a worktree a task runs in stays.</summary>
		static readonly Lazy<string> ScheduledTasksText = new(() => {
			try {
				var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"), "/Query /FO CSV /V") {
					UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
				};
				using var p = Process.Start(psi)!;
				var stderr = p.StandardError.ReadToEndAsync();
				string output = p.StandardOutput.ReadToEnd();
				p.WaitForExit(30_000);
				return output;
			}
			catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { return ""; }
		});

		static string? WorktreePath(string worktreeMeta) {
			try {
				string gitdir = File.ReadAllText(Path.Combine(worktreeMeta, "gitdir")).Trim(); // <worktree>\.git
				return Path.GetDirectoryName(Path.GetFullPath(gitdir));
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
		}

		static string? BranchOf(string gitDir) {
			try {
				string head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
				return head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? head["ref: refs/heads/".Length..] : null;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
		}

		/// <summary>Why git shouldn't remove the worktree: uncommitted changes, or commits that exist nowhere else.</summary>
		internal static string? WorktreeBlocker(string worktree) {
			if (Git.Exe == null) return "git not found";
			var (code, status) = Git.Run(worktree, "status", "--porcelain");
			if (code != 0) return "git couldn't read it";
			if (status.Trim().Length > 0) return "Uncommitted changes";
			var (upstreamCode, _) = Git.Run(worktree, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
			if (upstreamCode == 0) {
				var (c, ahead) = Git.Run(worktree, "rev-list", "--count", "@{u}..HEAD");
				return c == 0 && int.TryParse(ahead.Trim(), out int n) && n > 0 ? $"{n} commit(s) not pushed" : null;
			}
			// No upstream: fine only if a remote branch already has this commit.
			var (rc, remotes) = Git.Run(worktree, "branch", "-r", "--contains", "HEAD");
			return rc == 0 && remotes.Trim().Length > 0 ? null : "Commits not pushed";
		}

		// ------------------------------------------------------------------ caches

		/// <summary>Tools' own cache folders; blocked while a process that uses them runs.</summary>
		static readonly (string Name, string[] Paths, string[] Processes)[] CacheDefs = {
			("Gradle", new[] { @"~\.gradle\caches", @"~\.gradle\wrapper\dists", @"~\.gradle\daemon" }, new[] { "java", "javaw", "gradle", "studio64" }),
			("NuGet", new[] { @"~\.nuget\packages", @"%L\NuGet\v3-cache", @"%L\NuGet\plugins-cache" }, new[] { "dotnet", "MSBuild", "devenv", "VBCSCompiler" }),
			("npm", new[] { @"%L\npm-cache" }, Array.Empty<string>()),
			("Yarn", new[] { @"%L\Yarn\Cache" }, Array.Empty<string>()),
			("pnpm", new[] { @"%L\pnpm\store" }, Array.Empty<string>()),
			("pip", new[] { @"%L\pip\Cache" }, Array.Empty<string>()),
			("Maven", new[] { @"~\.m2\repository" }, new[] { "java", "javaw", "mvn" }),
			("Cargo", new[] { @"~\.cargo\registry\cache", @"~\.cargo\registry\src", @"~\.cargo\git\checkouts" }, new[] { "cargo", "rustc" }),
			("Go", new[] { @"%L\go-build" }, new[] { "go" }),
		};

		static string Expand(string p) => p.Replace("~", Home).Replace("%L", Local);

		static IEnumerable<DevItem> CacheItems() {
			foreach (var (name, paths, processes) in CacheDefs) {
				var existing = paths.Select(Expand).Where(Directory.Exists).ToList();
				if (existing.Count == 0) continue;
				var sizes = existing.Select(Measure).ToList();
				long bytes = sizes.Sum(s => s.Bytes);
				if (bytes < 1 << 20) continue;
				yield return new DevItem(IdOf(Caches, name), Caches, name, existing[0], existing, bytes, sizes.Max(s => s.NewestUtc), false,
					CacheBlocker(processes), existing.Count > 1 ? $"{existing.Count} folders" : "");
			}
		}

		internal static string? CacheBlocker(string[] processes) {
			foreach (string p in processes)
				if (Process.GetProcessesByName(p).Length > 0)
					return $"In use ({p} is running)";
			return null;
		}

		// ------------------------------------------------------------------ Android

		static string? AndroidSdk() =>
			new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"), Path.Combine(Local, "Android", "Sdk") }
				.FirstOrDefault(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));

		static string AvdHome() => Environment.GetEnvironmentVariable("ANDROID_AVD_HOME") is { Length: > 0 } h ? h : Path.Combine(Home, ".android", "avd");

		internal static bool EmulatorRunning() =>
			Process.GetProcesses().Any(p => { try { return p.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("emulator", StringComparison.OrdinalIgnoreCase); } catch { return false; } });

		static IEnumerable<DevItem> AndroidItems(DateTime staleBefore) {
			var usedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string avdHome = AvdHome();
			string? blocked = EmulatorRunning() ? "An emulator is running" : null;
			if (Directory.Exists(avdHome)) {
				foreach (string ini in Directory.EnumerateFiles(avdHome, "*.ini")) {
					string? avdDir = IniValue(ini, "path");
					if (avdDir == null || !Directory.Exists(avdDir)) continue;
					string? sysdir = IniValue(Path.Combine(avdDir, "config.ini"), "image.sysdir.1");
					if (sysdir != null) usedImages.Add(sysdir.Replace('/', '\\').TrimEnd('\\'));
					var (bytes, newest) = Measure(avdDir);
					string name = Path.GetFileNameWithoutExtension(ini);
					yield return new DevItem(IdOf(Android, avdDir), "avd", name.Replace('_', ' '), avdDir, new() { avdDir, ini }, bytes, newest,
						false, blocked, "Emulator" + (sysdir != null ? " · " + sysdir.Replace('\\', '/').TrimEnd('/') : ""));
				}
			}
			string? sdk = AndroidSdk();
			string images = sdk == null ? "" : Path.Combine(sdk, "system-images");
			if (!Directory.Exists(images)) yield break;
			foreach (string api in Directory.EnumerateDirectories(images))
				foreach (string tag in Directory.EnumerateDirectories(api))
					foreach (string abi in Directory.EnumerateDirectories(tag)) {
						string rel = Path.GetRelativePath(sdk!, abi).TrimEnd('\\');
						if (usedImages.Contains(rel)) continue;
						var (bytes, newest) = Measure(abi);
						yield return new DevItem(IdOf(Android, abi), "sysimage", $"{Path.GetFileName(api)} · {Path.GetFileName(tag)} · {Path.GetFileName(abi)}",
							abi, new() { abi }, bytes, newest, blocked == null, blocked, "System image no emulator uses (the SDK Manager downloads it again)");
					}
		}

		static string? IniValue(string file, string key) {
			try {
				foreach (string line in File.ReadLines(file)) {
					int eq = line.IndexOf('=');
					if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
						return line[(eq + 1)..].Trim();
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			return null;
		}

		// ------------------------------------------------------------------ temp and crash dumps

		static IEnumerable<DevItem> TempItems(int olderThanDays) {
			string temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
			var old = OldTempEntries(temp, olderThanDays);
			long bytes = old.Sum(e => e.Bytes);
			if (bytes >= 1 << 20)
				yield return new DevItem(IdOf(Temp, temp), "temp", "Temp folder", temp, old.Select(e => e.Path).ToList(), bytes, null, true, null,
					$"{old.Count:N0} item(s) untouched for {olderThanDays} days");
			string dumps = Path.Combine(Local, "CrashDumps");
			if (Directory.Exists(dumps)) {
				var files = new DirectoryInfo(dumps).EnumerateFiles("*.dmp").ToList();
				long dumpBytes = files.Sum(f => f.Length);
				if (dumpBytes > 0)
					yield return new DevItem(IdOf(Temp, dumps), "dumps", "Crash dumps", dumps, files.Select(f => f.FullName).ToList(), dumpBytes,
						files.Max(f => f.LastWriteTimeUtc), true, null, $"{files.Count} dump file(s)");
			}
		}

		/// <summary>Top-level Temp entries with nothing inside changed for <paramref name="days"/> days.</summary>
		internal static List<(string Path, long Bytes)> OldTempEntries(string temp, int days) {
			DateTime before = DateTime.UtcNow.AddDays(-days);
			var old = new List<(string, long)>();
			IEnumerable<FileSystemInfo> entries;
			try { entries = new DirectoryInfo(temp).EnumerateFileSystemInfos("*", ListOptions).ToList(); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return old; }
			foreach (FileSystemInfo e in entries) {
				if (e is FileInfo f) {
					if (f.LastWriteTimeUtc < before) old.Add((f.FullName, f.Length));
				}
				else if ((e.Attributes & FileAttributes.ReparsePoint) == 0) {
					var (bytes, newest) = Measure(e.FullName);
					if (newest < before && e.LastWriteTimeUtc < before) old.Add((e.FullName, bytes));
				}
			}
			return old;
		}

		// ------------------------------------------------------------------ helpers

		static readonly EnumerationOptions ListOptions = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

		/// <summary>Bytes and the newest change below a folder, never following links (pnpm's node_modules are full of them).</summary>
		internal static (long Bytes, DateTime NewestUtc) Measure(string path) {
			long bytes = 0;
			DateTime newest = DateTime.MinValue;
			try {
				var files = new FileSystemEnumerable<(long, DateTime)>(path,
					(ref FileSystemEntry e) => (e.Length, e.LastWriteTimeUtc.UtcDateTime),
					new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }) {
					ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
					ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
				};
				foreach (var (length, written) in files) {
					bytes += length;
					if (written > newest) newest = written;
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			return (bytes, newest);
		}

		static string IdOf(string kind, string key) =>
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + key.ToLowerInvariant())))[..16].ToLowerInvariant();
	}

	/// <summary>A repository's local branches, and those already merged into its remote's default branch.</summary>
	sealed record RepoBranches(string Id, string Name, string Path, string? Default, int LocalBranches, List<string> Merged,
		List<string> CheckedOut, string? Note);

	sealed record PruneKept(string Branch, string Reason);
	sealed record PruneResult(List<string> Deleted, List<PruneKept> Kept, bool Fetched, string? Error);

	/// <summary>
	/// Deletes local branches that are merged into the remote's default branch (origin/main or origin/master),
	/// the tidy-up developers do with a PowerShell one-liner, with the guards spelled out:
	/// <list type="bullet">
	/// <item>it fetches first (fetch --prune), so "merged" means merged on the remote; if the fetch fails, the
	/// last fetched state is used, which only ever finds fewer branches;</item>
	/// <item>only branches whose every commit is in the default branch (git branch --merged);</item>
	/// <item>never main, master, develop, dev, trunk, the default branch, or a branch checked out in any worktree;</item>
	/// <item>git branch -d first; -D only when git objects that the branch isn't merged into the current branch,
	/// after checking again that it is an ancestor of the remote default.</item>
	/// </list>
	/// Remote branches are never touched.
	/// </summary>
	static class BranchPruner {
		static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase) { "main", "master", "develop", "dev", "trunk" };

		/// <summary>From what's already fetched: no network.</summary>
		public static RepoBranches? Inspect(string repo) {
			if (Git.Exe == null) return null;
			string name = System.IO.Path.GetFileName(repo.TrimEnd('\\'));
			string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("branches|" + repo.ToLowerInvariant())))[..16].ToLowerInvariant();
			var (rc, remotes) = Git.Run(repo, "remote");
			var remoteList = remotes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
			if (rc != 0 || remoteList.Count == 0) return null; // no remote: nothing is "merged on the remote"
			string remote = remoteList.Contains("origin") ? "origin" : remoteList[0];
			string? defaultRef = DefaultBranch(repo, remote);
			var (_, all) = Git.Run(repo, "branch", "--format=%(refname:short)");
			int local = Lines(all).Count;
			if (defaultRef == null)
				return new RepoBranches(id, name, repo, null, local, new(), new(), $"No {remote}/main or {remote}/master to compare with");
			var (mc, mergedText) = Git.Run(repo, "branch", "--format=%(refname:short)", "--merged", defaultRef);
			if (mc != 0) return new RepoBranches(id, name, repo, defaultRef, local, new(), new(), "git couldn't list merged branches");
			string defaultShort = defaultRef[(defaultRef.IndexOf('/') + 1)..];
			var checkedOut = CheckedOutBranches(repo);
			var merged = Lines(mergedText).Where(b => !Protected.Contains(b) && !b.Equals(defaultShort, StringComparison.OrdinalIgnoreCase)).ToList();
			return new RepoBranches(id, name, repo, defaultRef, local,
				merged.Where(b => !checkedOut.Contains(b)).ToList(), merged.Where(checkedOut.Contains).ToList(), null);
		}

		public static PruneResult Prune(string repo) {
			if (Git.Exe == null) return new PruneResult(new(), new(), false, "git not found");
			var (_, remotes) = Git.Run(repo, "remote");
			var remoteList = Lines(remotes);
			if (remoteList.Count == 0) return new PruneResult(new(), new(), false, "The repository has no remote");
			string remote = remoteList.Contains("origin") ? "origin" : remoteList[0];
			bool fetched = Git.Run(repo, "fetch", "--prune", "--quiet", remote).Code == 0;
			RepoBranches? state = Inspect(repo);
			if (state?.Default == null) return new PruneResult(new(), new(), fetched, state?.Note ?? "Nothing to compare with");
			var deleted = new List<string>();
			var kept = state.CheckedOut.Select(b => new PruneKept(b, "checked out in a worktree")).ToList();
			foreach (string branch in state.Merged) {
				var (code, output) = Git.Run(repo, "branch", "-d", "--", branch);
				if (code != 0 && output.Contains("not fully merged", StringComparison.OrdinalIgnoreCase) &&
					Git.Run(repo, "merge-base", "--is-ancestor", "refs/heads/" + branch, state.Default).Code == 0)
					(code, output) = Git.Run(repo, "branch", "-D", "--", branch); // merged on the remote, just not into this checkout
				if (code == 0) deleted.Add(branch);
				else kept.Add(new PruneKept(branch, FirstLine(output)));
			}
			return new PruneResult(deleted, kept, fetched, null);
		}

		static string? DefaultBranch(string repo, string remote) {
			var (code, head) = Git.Run(repo, "symbolic-ref", "--quiet", "--short", $"refs/remotes/{remote}/HEAD");
			if (code == 0 && head.Trim().Length > 0) return head.Trim();
			foreach (string b in new[] { "main", "master" })
				if (Git.Run(repo, "rev-parse", "--verify", "--quiet", $"refs/remotes/{remote}/{b}").Code == 0)
					return $"{remote}/{b}";
			return null;
		}

		static HashSet<string> CheckedOutBranches(string repo) {
			var (_, text) = Git.Run(repo, "worktree", "list", "--porcelain");
			return Lines(text).Where(l => l.StartsWith("branch refs/heads/", StringComparison.Ordinal))
				.Select(l => l["branch refs/heads/".Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
		}

		static List<string> Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
		static string FirstLine(string text) => Lines(text).FirstOrDefault() ?? "git refused";
	}

	/// <summary>One developer-mode check at a time (dev-scan.lock), from the scheduled scan or the page.</summary>
	static class DevScan {
		static string LockPath => Path.Combine(AgentPaths.Home, "dev-scan.lock");

		/// <summary>After a scheduled scan: at most once a day, so the caches aren't measured every hour.</summary>
		public static bool Due(AgentConfig cfg) =>
			cfg.DeveloperModeOn && (DevReport.Load() is not { } last || DateTime.UtcNow - last.ScannedAtUtc > TimeSpan.FromHours(20));

		public static DevReport? RunAndSave(AgentConfig cfg, CancellationToken ct = default) {
			Directory.CreateDirectory(AgentPaths.Home);
			FileStream? held;
			try { held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
			catch (IOException) { return null; } // another check is running
			using (held) {
				DevReport report = DevScanner.Run(cfg, ct);
				if (!ct.IsCancellationRequested) report.Save();
				AgentPaths.AppendLog($"developer check: {report.Categories.Sum(c => c.Items.Count)} item(s), " +
					$"{Format.Bytes(report.Categories.SelectMany(c => c.Items).Where(i => i.Suggested).Sum(i => i.Bytes))} ticked, in {report.DurationSec:N0} s");
				return report;
			}
		}

		public static bool IsRunning() {
			if (!File.Exists(LockPath)) return false;
			try {
				using var probe = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
				return false;
			}
			catch (IOException) { return true; }
		}
	}

	/// <summary>git, when it's installed.</summary>
	static class Git {
		public static readonly string? Exe = Find();

		static string? Find() {
			foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
				try {
					string p = Path.Combine(dir.Trim(), "git.exe");
					if (File.Exists(p)) return p;
				}
				catch (ArgumentException) { }
			}
			string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe");
			return File.Exists(pf) ? pf : null;
		}

		public static (int Code, string Output) Run(string workingDir, params string[] args) {
			if (Exe == null) return (-1, "");
			var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
			psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // a fetch that needs a password fails instead of waiting forever
			psi.ArgumentList.Add("-C");
			psi.ArgumentList.Add(workingDir);
			foreach (string a in args) psi.ArgumentList.Add(a);
			try {
				using var p = Process.Start(psi)!;
				var stderr = p.StandardError.ReadToEndAsync();
				string output = p.StandardOutput.ReadToEnd();
				if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } return (-1, ""); }
				return (p.ExitCode, output + stderr.Result);
			}
			catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) {
				return (-1, "");
			}
		}
	}

	sealed record CleanResult(long FreedBytes, int LeftInUse, string? Error);

	/// <summary>
	/// Cleans one developer-mode item, re-checking it first. Deletion is permanent (tools recreate all of
	/// it), removes links without touching what they point to, and leaves files in use alone.
	/// </summary>
	static class DevCleaner {
		public static CleanResult Clean(DevItem item, AgentConfig cfg) {
			switch (item.Kind) {
				case DevScanner.Projects: {
					long freed = 0;
					int left = 0;
					foreach (string p in item.Paths) {
						var dir = new DirectoryInfo(p);
						if (!dir.Exists || DevScanner.BuildOutputLabel(dir) == null) continue; // no longer what it was
						var r = SafeDelete.Tree(p);
						freed += r.Bytes;
						left += r.Left;
					}
					return new CleanResult(freed, left, null);
				}
				case DevScanner.Worktrees: {
					if (item.Repo == null) return new CleanResult(0, 0, "Unknown repository");
					if (DevScanner.WorktreeBlocker(item.Location) is { } why) return new CleanResult(0, 0, why);
					long before = DevScanner.Measure(item.Location).Bytes;
					// Without --force git refuses a worktree with changes, a second guard behind the check above.
					var (code, output) = Git.Run(item.Repo, "worktree", "remove", item.Location);
					return code == 0 ? new CleanResult(before, 0, null) : new CleanResult(0, 0, "git: " + output.Trim());
				}
				case DevScanner.Caches: {
					var def = item.Name;
					string? blocked = DevScanner.CacheBlocker(ProcessesFor(def));
					if (blocked != null) return new CleanResult(0, 0, blocked);
					long freed = 0;
					int left = 0;
					foreach (string p in item.Paths.Where(Directory.Exists)) {
						var r = SafeDelete.Tree(p, keepRoot: true);
						freed += r.Bytes;
						left += r.Left;
					}
					return new CleanResult(freed, left, null);
				}
				case "avd":
				case "sysimage": {
					if (DevScanner.EmulatorRunning()) return new CleanResult(0, 0, "An emulator is running");
					long freed = 0;
					int left = 0;
					foreach (string p in item.Paths) {
						var r = File.Exists(p) ? SafeDelete.File(p) : SafeDelete.Tree(p);
						freed += r.Bytes;
						left += r.Left;
					}
					return new CleanResult(freed, left, null);
				}
				case "temp": {
					// Only entries still untouched for the whole period, as they were when listed.
					var still = DevScanner.OldTempEntries(item.Location, cfg.TempOlderThanDays).Select(e => e.Path)
						.ToHashSet(StringComparer.OrdinalIgnoreCase);
					long freed = 0;
					int left = 0;
					foreach (string p in item.Paths.Where(still.Contains)) {
						var r = File.Exists(p) ? SafeDelete.File(p) : SafeDelete.Tree(p);
						freed += r.Bytes;
						left += r.Left;
					}
					return new CleanResult(freed, left, null);
				}
				case "dumps": {
					long freed = 0;
					int left = 0;
					foreach (string p in item.Paths.Where(p => p.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))) {
						var r = SafeDelete.File(p);
						freed += r.Bytes;
						left += r.Left;
					}
					return new CleanResult(freed, left, null);
				}
				default:
					return new CleanResult(0, 0, "Unknown item");
			}
		}

		static string[] ProcessesFor(string cacheName) => cacheName switch {
			"Gradle" => new[] { "java", "javaw", "gradle", "studio64" },
			"NuGet" => new[] { "dotnet", "MSBuild", "devenv", "VBCSCompiler" },
			"Maven" => new[] { "java", "javaw", "mvn" },
			"Cargo" => new[] { "cargo", "rustc" },
			"Go" => new[] { "go" },
			_ => Array.Empty<string>(),
		};
	}

	/// <summary>Permanent deletion that removes links (junctions, symbolic links) without following them.</summary>
	static class SafeDelete {
		public readonly record struct Result(long Bytes, int Left);

		public static Result File(string path) {
			try {
				var f = new FileInfo(path);
				if (!f.Exists) return new Result(0, 0);
				long length = (f.Attributes & FileAttributes.ReparsePoint) != 0 ? 0 : f.Length;
				if (f.IsReadOnly) f.IsReadOnly = false;
				f.Delete();
				return new Result(length, 0);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return new Result(0, 1);
			}
		}

		/// <param name="keepRoot">Empty the folder but keep it (a tool's cache folder).</param>
		public static Result Tree(string path, bool keepRoot = false) {
			var root = new DirectoryInfo(path);
			if (!root.Exists) return new Result(0, 0);
			if (root.Parent == null) throw new InvalidOperationException("Refusing to delete a drive root");
			if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
				return RemoveLink(root);
			long bytes = 0;
			int left = 0;
			IEnumerable<FileSystemInfo> entries;
			try { entries = root.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new Result(0, 1); }
			foreach (FileSystemInfo e in entries) {
				Result r = e switch {
					DirectoryInfo d when (d.Attributes & FileAttributes.ReparsePoint) != 0 => RemoveLink(d),
					DirectoryInfo d => Tree(d.FullName),
					_ => File(e.FullName),
				};
				bytes += r.Bytes;
				left += r.Left;
			}
			if (!keepRoot && left == 0) {
				try {
					if ((root.Attributes & FileAttributes.ReadOnly) != 0) root.Attributes &= ~FileAttributes.ReadOnly;
					root.Delete(recursive: false);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left++; }
			}
			return new Result(bytes, left);
		}

		/// <summary>Removes the link itself; the folder it points to is untouched.</summary>
		static Result RemoveLink(DirectoryInfo link) {
			try {
				link.Delete(recursive: false);
				return new Result(0, 0);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return new Result(0, 1);
			}
		}
	}
}
