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
using System.Text.Json.Serialization;

namespace HEI.Agent {
	/// <summary>Where the agent keeps its state: one folder, nothing inside the scanned libraries.</summary>
	static class AgentPaths {
		/// <summary>%LOCALAPPDATA%\Heiward, or HEIWARD_HOME.</summary>
		public static string Home {
			get {
				string? overridden = Environment.GetEnvironmentVariable("HEIWARD_HOME");
				return !string.IsNullOrWhiteSpace(overridden)
					? overridden
					: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heiward");
			}
		}
		public static string Config => Path.Combine(Home, "settings.json");
		public static string Report => Path.Combine(Home, "report.json");
		public static string Decisions => Path.Combine(Home, "decisions.json");
		public static string ScanStatus => Path.Combine(Home, "scan-status.json");
		public static string ScanLock => Path.Combine(Home, "scan.lock");
		public static string Database => Path.Combine(Home, "db");
		public static string Thumbnails => Path.Combine(Home, "thumbs");
		public static string Log => Path.Combine(Home, "heiward.log");
		/// <summary>Touched while the review page is open and showing (it polls): scans run at full speed then.</summary>
		public static string PageSeen => Path.Combine(Home, "page-seen");

		/// <summary>Writes a file atomically (temp file, then replace), so readers never see half of it.</summary>
		public static void WriteAtomic(string path, string contents) {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			string tmp = path + $".{Environment.ProcessId}.tmp";
			File.WriteAllText(tmp, contents);
			File.Move(tmp, path, overwrite: true);
		}

		public static void AppendLog(string line) {
			try {
				Directory.CreateDirectory(Home);
				File.AppendAllText(Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
			}
			catch { /* logging never breaks a scan */ }
		}
	}

	/// <summary>Several repositories shown as one project in Developer cleanup.</summary>
	sealed record DevProject(string Name, List<string> Repos) {
		/// <summary>
		/// What the page sent, made consistent: names trimmed and unique, each repository (a full path) in
		/// one project only, and a project needing two repositories (one is just the repository).
		/// </summary>
		public static List<DevProject> Normalize(IEnumerable<DevProject>? projects) {
			var result = new List<DevProject>();
			var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (DevProject p in projects ?? Enumerable.Empty<DevProject>()) {
				string name = (p.Name ?? "").Trim();
				if (name.Length is 0 or > 80 || result.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
				var repos = (p.Repos ?? new())
					.Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r))
					.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.Where(taken.Add)
					.ToList();
				if (repos.Count >= 2) result.Add(new DevProject(name, repos));
				else foreach (string r in repos) taken.Remove(r);
			}
			return result;
		}
	}

	/// <summary>The user's choices, in settings.json. Every field has a working default.</summary>
	sealed class AgentConfig {
		/// <summary>
		/// Scan every fixed drive, minus Windows', apps', games' and other programs' own folders
		/// (<see cref="ScanScope"/>). Off: only <see cref="Folders"/>.
		/// </summary>
		public bool ScanAllDrives { get; set; } = true;
		/// <summary>
		/// More folders to scan, subfolders included: e.g. a USB drive or a network share. Scanned even
		/// inside a folder the built-in rules leave out (the rules still apply below it).
		/// </summary>
		public List<string> Folders { get; set; } = new();
		/// <summary>
		/// Folders to leave out, besides the built-in ones: a full path (C:\Scans), a folder name at any
		/// depth (node_modules), or either with wildcards (*.lrdata, D:\Backups\*). Wins over
		/// <see cref="Folders"/>: a listed folder inside one is skipped, with a note.
		/// </summary>
		public List<string> ExcludeFolders { get; set; } = new();
		/// <summary>File types to leave out, e.g. ".heic" (HEIC decodes through FFmpeg, which is ~5x faster than Windows' codec).</summary>
		public List<string> ExcludeExtensions { get; set; } = new();
		/// <summary>auto (the NPU when there is one), cpu, or npu.</summary>
		public string AiDevice { get; set; } = "auto";
		/// <summary>Files decoded at once; 0 = automatic (<see cref="ParallelismFor"/>).</summary>
		public int Parallelism { get; set; }
		/// <summary>
		/// "auto": a scan the user starts (Scan now, <c>hei scan</c>), and a scheduled one while the review
		/// page is open, runs at full speed: every core but one, normal priority, no efficiency mode. Other
		/// scheduled scans run in the background. "background": every scan runs in the background, in
		/// Windows' efficiency mode at below-normal priority on half the cores (<see cref="ScanPace"/>).
		/// </summary>
		public string ScanSpeed { get; set; } = "auto";
		/// <summary>List what was cleaned up and kept on the review page's History. Off: nothing new is listed, and no file names are kept.</summary>
		public bool KeepHistory { get; set; } = true;
		/// <summary>
		/// Minutes between scheduled scans; 0 = no scheduled scans, only "Scan now". A rescan only looks at
		/// new and changed files, so an hourly scan with nothing new is a directory listing. Without an
		/// NPU the installer sets every 6 hours on AC power, or 0 if the user picks on demand.
		/// </summary>
		public int ScanEveryMinutes { get; set; } = 60;
		/// <summary>Let scheduled scans run on battery (they skip themselves in Battery Saver or below <see cref="MinBatteryPercent"/>).</summary>
		public bool ScanOnBattery { get; set; } = true;
		public int MinBatteryPercent { get; set; } = 30;
		/// <summary>Open the review page in the default browser once a day at sign-in, when something waits for review.</summary>
		public bool OpenPageAtSignIn { get; set; } = true;
		/// <summary>The review page's port on 127.0.0.1.</summary>
		public int Port { get; set; } = 18484;
		/// <summary>Minutes the review page stays up with nobody using it.</summary>
		public int ServerIdleMinutes { get; set; } = 60;
		/// <summary>A Windows notification when a scan finds something new.</summary>
		public bool Toast { get; set; } = true;
		/// <summary>
		/// Developer mode: also look for build outputs, worktrees, caches, emulators and temp files that
		/// tools recreate. "auto" checks once a day and shows the section when there's something to show;
		/// "off" never checks.
		/// </summary>
		public string DeveloperMode { get; set; } = "auto";
		/// <summary>A project untouched this many days has its build outputs ticked for cleaning.</summary>
		public int StaleProjectDays { get; set; } = 30;
		/// <summary>Temp files untouched this many days are ticked for cleaning.</summary>
		public int TempOlderThanDays { get; set; } = 7;

		/// <summary>Repositories the user bundled into one project on the review page (each in at most one).</summary>
		public List<DevProject> DevProjects { get; set; } = new();

		/// <summary>What Heiward may clean up by itself. Off until the user turns it on (on the review page).</summary>
		public AutoCleanConfig AutoClean { get => autoClean; set => autoClean = value ?? new(); }
		AutoCleanConfig autoClean = new();

		[JsonIgnore]
		public bool DeveloperModeOn => !string.Equals(DeveloperMode, "off", StringComparison.OrdinalIgnoreCase);

		[JsonIgnore]
		public bool AlwaysInBackground => string.Equals(ScanSpeed, "background", StringComparison.OrdinalIgnoreCase);

		/// <summary>Files decoded at once: <see cref="Parallelism"/> when set; else every core but one at full speed, half of them in the background (at least 2).</summary>
		public int ParallelismFor(bool fullSpeed) =>
			Parallelism > 0 ? Parallelism : Math.Max(2, fullSpeed ? Environment.ProcessorCount - 1 : Environment.ProcessorCount / 2);

		internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

		public static AgentConfig Load() {
			try {
				if (File.Exists(AgentPaths.Config))
					return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(AgentPaths.Config), Json) ?? new AgentConfig();
			}
			catch (Exception e) {
				AgentPaths.AppendLog($"settings.json unreadable, using defaults: {e.Message}");
			}
			return new AgentConfig();
		}

		public void Save() => AgentPaths.WriteAtomic(AgentPaths.Config, JsonSerializer.Serialize(this, Json));
	}

	/// <summary>
	/// Automatic cleanup, for once the user trusts what the page suggests: after a scan, Heiward cleans
	/// what the page ticks for them, once it has been listed for <see cref="AfterDays"/> days (<see cref="AutoCleaner"/>).
	/// </summary>
	sealed class AutoCleanConfig {
		/// <summary>Plain copies of photos, and byte-identical videos, go to the Recycle Bin.</summary>
		public bool Duplicates { get; set; }
		/// <summary>Developer leftovers of the kinds in <see cref="DeveloperKinds"/> are deleted (tools recreate them).</summary>
		public bool Developer { get; set; }
		/// <summary>Some of <see cref="AutoCleaner.DeveloperKinds"/>: branches, temp, buildOutputs, worktrees, systemImages.</summary>
		public List<string> DeveloperKinds { get => kinds; set => kinds = value ?? new(); }
		List<string> kinds = AutoCleaner.DeveloperKinds.ToList();
		/// <summary>Days something is listed before it's cleaned: time to see it coming and say "leave it". 0 = at the next scan.</summary>
		public int AfterDays { get; set; } = 3;

		public const int MaxAfterDays = 90;

		/// <summary>What the page sent, made valid: known kinds only, each once, and days within 0–<see cref="MaxAfterDays"/>.</summary>
		public AutoCleanConfig Normalized() => new() {
			Duplicates = Duplicates,
			Developer = Developer,
			DeveloperKinds = AutoCleaner.DeveloperKinds.Where(k => DeveloperKinds.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList(),
			AfterDays = Math.Clamp(AfterDays, 0, MaxAfterDays),
		};
	}
}
