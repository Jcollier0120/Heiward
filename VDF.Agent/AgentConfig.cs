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

namespace VDF.Agent {
	/// <summary>Where the agent keeps its state: one folder, nothing inside the scanned libraries.</summary>
	static class AgentPaths {
		/// <summary>%LOCALAPPDATA%\VDF Agent, or VDF_AGENT_HOME.</summary>
		public static string Home {
			get {
				string? overridden = Environment.GetEnvironmentVariable("VDF_AGENT_HOME");
				return !string.IsNullOrWhiteSpace(overridden)
					? overridden
					: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VDF Agent");
			}
		}
		public static string Config => Path.Combine(Home, "agent.json");
		public static string Report => Path.Combine(Home, "report.json");
		public static string Decisions => Path.Combine(Home, "decisions.json");
		public static string ScanStatus => Path.Combine(Home, "scan-status.json");
		public static string ScanLock => Path.Combine(Home, "scan.lock");
		public static string Database => Path.Combine(Home, "db");
		public static string Thumbnails => Path.Combine(Home, "thumbs");
		public static string Log => Path.Combine(Home, "agent.log");

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

	/// <summary>The user's choices, in agent.json. Every field has a working default.</summary>
	sealed class AgentConfig {
		/// <summary>Folders to scan (subfolders included).</summary>
		public List<string> Folders { get; set; } = DefaultFolders();
		/// <summary>Folders (or wildcard names) to leave out.</summary>
		public List<string> ExcludeFolders { get; set; } = new();
		/// <summary>File types to leave out, e.g. ".heic" (HEIC decodes through FFmpeg, which is ~5x faster than Windows' codec).</summary>
		public List<string> ExcludeExtensions { get; set; } = new();
		/// <summary>auto (the NPU when there is one), cpu, or npu.</summary>
		public string AiDevice { get; set; } = "auto";
		/// <summary>Files decoded at once; 0 = half the logical processors (at least 2).</summary>
		public int Parallelism { get; set; }
		/// <summary>
		/// Minutes between scheduled scans. A rescan only looks at new and changed files, so an hourly
		/// scan with nothing new is a directory listing. PCs without an NPU get a daily scan at install.
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

		[JsonIgnore]
		public int EffectiveParallelism => Parallelism > 0 ? Parallelism : Math.Max(2, Environment.ProcessorCount / 2);

		static List<string> DefaultFolders() {
			var folders = new List<string>();
			foreach (var f in new[] { Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Desktop }) {
				string path = Environment.GetFolderPath(f);
				if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !folders.Contains(path, StringComparer.OrdinalIgnoreCase))
					folders.Add(path);
			}
			string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			// iCloud for Windows keeps the photo library outside Pictures (older versions under it).
			// Cloud-only files in it are skipped, never downloaded.
			foreach (string icloud in new[] { Path.Combine(profile, "iCloudPhotos", "Photos"), Path.Combine(profile, "Pictures", "iCloud Photos", "Photos") })
				if (Directory.Exists(icloud) && !folders.Any(f => icloud.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
					folders.Add(icloud);
			string downloads = Path.Combine(profile, "Downloads");
			if (Directory.Exists(downloads))
				folders.Add(downloads);
			return folders;
		}

		internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

		public static AgentConfig Load() {
			try {
				if (File.Exists(AgentPaths.Config))
					return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(AgentPaths.Config), Json) ?? new AgentConfig();
			}
			catch (Exception e) {
				AgentPaths.AppendLog($"agent.json unreadable, using defaults: {e.Message}");
			}
			return new AgentConfig();
		}

		public void Save() => AgentPaths.WriteAtomic(AgentPaths.Config, JsonSerializer.Serialize(this, Json));
	}
}
