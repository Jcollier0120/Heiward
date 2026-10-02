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
	/// <summary>
	/// A development build: a hei.exe built in a checkout, which a folder above it marks with .git (a folder, or
	/// the file a git worktree has). It keeps its own data folder and port (<see cref="AgentPaths.Separate"/>), so it
	/// runs beside the installed Heiward without touching it, and it has no scheduled tasks. The installed copy,
	/// the Store version and a download are never one.
	/// </summary>
	static class DevBuild {
		/// <summary>Folders looked at above the exe's own: a published build is bin\Release\net10.0-windows\win-arm64\publish, five deep.</summary>
		const int Levels = 8;

		/// <summary>Set by tests; null: what <see cref="IsCheckout"/> says of this exe's folder.</summary>
		internal static bool? Override;

		static readonly Lazy<bool> current = new(() => !Installer.RunningInstalled && IsCheckout(AppContext.BaseDirectory));

		public static bool Current => Override ?? current.Value;

		/// <summary><paramref name="folder"/>, or one of the <see cref="Levels"/> folders above it, holds .git.</summary>
		internal static bool IsCheckout(string folder) {
			try {
				var dir = new DirectoryInfo(Path.GetFullPath(folder));
				for (int i = 0; dir != null && i <= Levels; i++, dir = dir.Parent) {
					string git = Path.Combine(dir.FullName, ".git");
					if (Directory.Exists(git) || File.Exists(git)) return true;
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { }
			return false;
		}
	}

	/// <summary>Where the agent keeps its state: one folder, nothing inside the scanned libraries.</summary>
	static class AgentPaths {
		/// <summary>The installed copy's review page port; a development build's is 10000 more.</summary>
		public const int InstalledPort = 18484, DevPort = InstalledPort + 10000;

		static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

		static string? HomeOverride {
			get {
				string? overridden = Environment.GetEnvironmentVariable("HEIWARD_HOME");
				return string.IsNullOrWhiteSpace(overridden) ? null : overridden;
			}
		}

		/// <summary>Install and uninstall act on the installed copy, whichever exe runs them (<see cref="ActAsInstalled"/>).</summary>
		internal static bool ActingAsInstalled { get; set; }

		/// <summary>From here on this process uses the installed copy's data folder and port, even in a development build.</summary>
		public static void ActAsInstalled() => ActingAsInstalled = true;

		/// <summary>
		/// A development build on its own data folder (%LOCALAPPDATA%\Heiward-dev) and port (<see cref="DevPort"/>).
		/// Not with HEIWARD_HOME set, which overrides it as before, nor while it installs or uninstalls.
		/// </summary>
		public static bool Separate => DevBuild.Current && !ActingAsInstalled && HomeOverride == null;

		/// <summary>The installed copy's: %LOCALAPPDATA%\Heiward, or HEIWARD_HOME.</summary>
		public static string InstalledHome => HomeOverride ?? Path.Combine(LocalAppData, "Heiward");

		/// <summary><see cref="InstalledHome"/>, or a development build's %LOCALAPPDATA%\Heiward-dev (<see cref="Separate"/>).</summary>
		public static string Home => Separate ? Path.Combine(LocalAppData, "Heiward-dev") : InstalledHome;

		/// <summary>The review page's port when settings.json sets none.</summary>
		public static int DefaultPort => Separate ? DevPort : InstalledPort;

		public static string Config => Path.Combine(Home, "settings.json");
		public static string Report => Path.Combine(Home, "report.json");
		public static string Decisions => Path.Combine(Home, "decisions.json");
		public static string ScanStatus => Path.Combine(Home, "scan-status.json");
		/// <summary>When the last scan started, finished or not: opening the page doesn't start another before it's due.</summary>
		public static string ScanStarted => Path.Combine(Home, "scan-started.txt");
		public static string ScanLock => Path.Combine(Home, "scan.lock");
		public static string Database => Path.Combine(Home, "db");
		public static string Thumbnails => Path.Combine(Home, "thumbs");
		public static string Log => Path.Combine(Home, "heiward.log");
		/// <summary>Scheduled scans are paused (<see cref="AgentPause"/>).</summary>
		public static string Paused => Path.Combine(Home, "paused.json");
		/// <summary>The user asked the running scan to stop (<see cref="ScanStop"/>).</summary>
		public static string StopScan => Path.Combine(Home, "stop-scan.txt");
		/// <summary>
		/// The Store version's setup is done. In the package's storage, which Windows removes with the app:
		/// set up again after a reinstall, as its scheduled tasks deleted themselves.
		/// </summary>
		public static string StoreSetUp => Path.Combine(StorePackage.Storage, "setup.txt");
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

	/// <summary>Several repositories shown as one project in the Developer area.</summary>
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
		/// <summary>
		/// Drives scanned only when you ask, from the drive's own page ("D:\"): scheduled scans and the home
		/// page's Scan now leave them alone, not reading them at all, so an archive disk can sleep. Their
		/// photos and videos as their last scan found them still count: their sets stay listed, and a copy
		/// of one elsewhere is still found.
		/// </summary>
		public List<string> OnRequestDrives { get; set; } = new();

		/// <summary>The path is on one of <see cref="OnRequestDrives"/>.</summary>
		public bool IsOnRequest(string path) => OnRequestDrives.Count > 0 && DriveOf(path) is { } drive &&
			OnRequestDrives.Any(d => string.Equals(DriveOf(d), drive, StringComparison.OrdinalIgnoreCase));

		/// <summary>A path's drive as these settings name it: "D:\".</summary>
		public static string? DriveOf(string path) {
			try { return Path.GetPathRoot(Path.GetFullPath(path))?.ToUpperInvariant(); }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
		}
		/// <summary>File types to leave out, e.g. ".heic" (HEIC decodes through FFmpeg, which is ~5x faster than Windows' codec).</summary>
		public List<string> ExcludeExtensions { get; set; } = new();
		/// <summary>auto (the NPU when there is one), cpu, or npu.</summary>
		public string AiDevice { get; set; } = "auto";
		/// <summary>
		/// The graphics card for GPU work, by name (<see cref="HEI.Core.Utils.GpuAdapter.Key"/>), on a PC with more
		/// than one: AI matching on the GPU, and decoding videos and iPhone photos. Empty: Windows' default, the
		/// card driving the main display. Set by the installer and the page's Settings; a scan reads it at its start.
		/// </summary>
		public string Gpu { get; set; } = "";
		/// <summary>Files decoded at once; 0 = automatic (<see cref="ParallelismFor"/>).</summary>
		public int Parallelism { get; set; }
		/// <summary>The most of the processor a background scan uses, in percent; 0 = automatic (<see cref="BackgroundCpuCap"/>).</summary>
		public int BackgroundCpuPercent { get; set; }
		/// <summary>
		/// "auto": a scan the user starts (Scan now, <c>hei scan</c>), and a scheduled one while the review
		/// page is open, runs at full speed: every core but one, normal priority, no efficiency mode. Other
		/// scheduled scans run in the background. "background": every scan runs in the background, in
		/// Windows' efficiency mode at below-normal priority, under a cap on the processor (<see cref="ScanPace"/>, <see cref="BackgroundCpuCap"/>).
		/// "full": every scan runs at full speed, scheduled ones too.
		/// </summary>
		public string ScanSpeed { get; set; } = "auto";
		/// <summary>
		/// Use more memory to scan faster: in the background the GPU's video decoder takes up to one video
		/// per 4 GB of RAM at once (8 at most), about half a gigabyte each for a 4K phone video. Off: 2 at
		/// once. While a game or another 3D program runs, scans use less whatever this says (<see cref="ThreeDWatch"/>).
		/// </summary>
		public bool MoreMemory { get; set; } = true;
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
		/// <summary>The review page's port on 127.0.0.1: 18484, a development build's 28484 (<see cref="AgentPaths.DefaultPort"/>).</summary>
		public int Port { get; set; } = AgentPaths.DefaultPort;
		/// <summary>Minutes the review page stays up with nobody using it.</summary>
		public int ServerIdleMinutes { get; set; } = 60;
		/// <summary>A Windows notification when a scan finds something new.</summary>
		public bool Toast { get; set; } = true;
		/// <summary>
		/// Developer mode: also look for build outputs, worktrees, caches, emulators and temp files that
		/// tools recreate, once a day, and show them on the page. "on" or "off", the switch in the page's
		/// Settings. Off, the page shows nothing of it and nothing is checked. Settings files from before
		/// the switch say "auto", read as on only where automatic cleanup of developer leftovers is on, so
		/// that keeps working (<see cref="Load"/>).
		/// </summary>
		public string DeveloperMode { get; set; } = "off";
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
		public bool DeveloperModeOn => string.Equals(DeveloperMode, "on", StringComparison.OrdinalIgnoreCase);

		[JsonIgnore]
		public bool AlwaysInBackground => string.Equals(ScanSpeed, "background", StringComparison.OrdinalIgnoreCase);

		[JsonIgnore]
		public bool AlwaysFullSpeed => string.Equals(ScanSpeed, "full", StringComparison.OrdinalIgnoreCase);

		/// <summary>The values <see cref="ScanSpeed"/> takes.</summary>
		public static readonly string[] ScanSpeeds = { "auto", "background", "full" };

		/// <summary>Files decoded at once: <see cref="Parallelism"/> when set; else every core but one at full speed, half of them in the background (at least 2).</summary>
		public int ParallelismFor(bool fullSpeed) =>
			Parallelism > 0 ? Parallelism : Math.Max(2, fullSpeed ? Environment.ProcessorCount - 1 : Environment.ProcessorCount / 2);

		/// <summary>
		/// The most of the whole processor a background scan uses, in percent: <see cref="BackgroundCpuPercent"/> when
		/// set; else a quarter of it, and at most two cores' worth, so a big processor isn't kept busy either.
		/// Efficiency mode and a low priority alone left an idle PC's processor to the scan (80% of an older
		/// desktop's): the priority only makes it give way to other work, and the efficiency cores and low
		/// clocks of EcoQoS aren't there on every processor.
		/// </summary>
		public double BackgroundCpuCap(int processors) =>
			BackgroundCpuPercent is > 0 and <= 100 ? BackgroundCpuPercent : Math.Min(25, 200.0 / Math.Max(1, processors));

		internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

		public static AgentConfig Load() {
			try {
				if (File.Exists(AgentPaths.Config))
					return FromJson(File.ReadAllText(AgentPaths.Config));
			}
			catch (Exception e) {
				AgentPaths.AppendLog($"settings.json unreadable, using defaults: {e.Message}");
			}
			return new AgentConfig();
		}

		internal static AgentConfig FromJson(string json) {
			AgentConfig cfg = JsonSerializer.Deserialize<AgentConfig>(json, Json) ?? new AgentConfig();
			// Before the switch, "auto" checked every PC for developer leftovers. It stays on where it
			// cleans them up by itself; elsewhere it's off until it's turned on.
			if (string.Equals(cfg.DeveloperMode, "auto", StringComparison.OrdinalIgnoreCase))
				cfg.DeveloperMode = cfg.AutoClean.Developer ? "on" : "off";
			// A development build never takes the installed copy's port, as from a settings.json copied over
			// from it: any other port it sets stands.
			if (AgentPaths.Separate && cfg.Port == AgentPaths.InstalledPort) cfg.Port = AgentPaths.DevPort;
			return cfg;
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
