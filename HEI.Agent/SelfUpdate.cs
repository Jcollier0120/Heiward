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
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace HEI.Agent {
	/// <summary>What the last look for an update found (update.json), for <c>hei update</c> and the log.</summary>
	/// <param name="Latest">The newest release's version, when the look got that far.</param>
	/// <param name="Outcome">"up to date", "updating to 1.9.10", or why it couldn't look or install.</param>
	sealed record UpdateCheck(DateTime CheckedAtUtc, string Current, string? Latest, string Outcome);

	/// <summary>
	/// The copy installed from GitHub keeps itself up to date from the same releases Manor installs Heiward from: once a day
	/// the review page's process looks at the newest release on GitHub, and when it's newer, downloads this PC's exe,
	/// checks it against the release's SHA256SUMS.txt, and runs its installer as Manor does (<c>install --yes
	/// --no-browser</c>), with <c>--keep-settings</c> so where the AI runs, the schedule and the graphics card stay. The
	/// installer stops this copy (this process among them), installs over it, and starts the page and a scan again.
	/// <para>
	/// Never while a scan runs, and never for a copy something else updates: the Store version (the Store), one Manor
	/// employs (Manor's rounds), a development build, or an exe run from anywhere but the install folder. Settings'
	/// <see cref="AgentConfig.AutoUpdate"/> turns it off.
	/// </para>
	/// </summary>
	static class SelfUpdate {
		const string Repo = "Jcollier0120/Heiward";
		/// <summary>The first look, after the page starts: long enough for a sign-in's own work to settle.</summary>
		static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(10);
		static readonly TimeSpan Every = TimeSpan.FromHours(24);
		/// <summary>While a scan runs, look again this often, for at most <see cref="MostWait"/>; then tomorrow.</summary>
		static readonly TimeSpan BusyRetry = TimeSpan.FromMinutes(5), MostWait = TimeSpan.FromHours(6);

		/// <summary>GitHub's API: HEIWARD_RELEASES_API points it elsewhere (tests, a mirror).</summary>
		static string Api => (Environment.GetEnvironmentVariable("HEIWARD_RELEASES_API") is { Length: > 0 } api ? api : "https://api.github.com").TrimEnd('/');

		public static string Folder => Path.Combine(AgentPaths.InstalledHome, "updates");
		static string StateFile => Path.Combine(AgentPaths.InstalledHome, "update.json");

		/// <summary>This build's version: 1.9.9.</summary>
		public static string Current => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

		/// <summary>Why this copy doesn't update itself, in a sentence; null when it does.</summary>
		internal static string? WhyNot(bool autoUpdate, bool packaged, bool devBuild, bool runningInstalled, bool manorEmploys) =>
			packaged ? "The Microsoft Store keeps it up to date."
			: manorEmploys ? "Manor keeps it up to date."
			: devBuild ? "A development build doesn't update itself."
			: !runningInstalled ? "Only the installed copy updates itself."
			: !autoUpdate ? "Updating by itself is off in Settings."
			: null;

		static string? WhyNot(AgentConfig cfg) =>
			WhyNot(cfg.AutoUpdate, StorePackage.IsPackaged, DevBuild.Current, Installer.RunningInstalled, ManorEmploys(Manor.Folder));

		/// <summary>
		/// Settings' About: whether this copy may update itself (<c>switch</c>: the Store, Manor and a development build
		/// update it otherwise, and then there's no switch), the switch, and the last look.
		/// </summary>
		public static object View(AgentConfig cfg) {
			bool other = StorePackage.IsPackaged || DevBuild.Current || !Installer.RunningInstalled || ManorEmploys(Manor.Folder);
			UpdateCheck? last = Last();
			return new {
				@switch = !other,
				on = cfg.AutoUpdate,
				by = StorePackage.IsPackaged ? "store" : ManorEmploys(Manor.Folder) ? "manor" : null,
				last = last == null ? null : new { checkedAtUtc = last.CheckedAtUtc, last.Latest, last.Outcome },
			};
		}

		/// <summary>Manor is installed here, and employs Heiward: its agents.json lists "heiward". Manor's rounds update it then.</summary>
		internal static bool ManorEmploys(string? manorFolder) {
			if (manorFolder == null || Manor.Load(manorFolder) == null) return false;
			try {
				using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(manorFolder, "agents.json")));
				return doc.RootElement.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array &&
					agents.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == "heiward");
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return false;
			}
		}

		/// <summary>True when <paramref name="candidate"/> (1.10.0, v1.10.0) is a later version than <paramref name="current"/>.</summary>
		internal static bool Newer(string candidate, string current) =>
			Version.TryParse(candidate.TrimStart('v', 'V').Split('-', '+')[0], out var a) && Version.TryParse(current.Split('-', '+')[0], out var b) && a > b;

		/// <summary>The release's exe for this PC: Heiward-1.9.10-arm64.exe or Heiward-1.9.10-x64.exe.</summary>
		internal static string AssetName(string version, bool arm64) => $"Heiward-{version}-{(arm64 ? "arm64" : "x64")}.exe";

		/// <summary>SHA256SUMS.txt's lines ("&lt;hex&gt;  &lt;name&gt;", or "&lt;hex&gt; *&lt;name&gt;") by file name, the hex in lower case.</summary>
		internal static Dictionary<string, string> ParseSums(string text) {
			var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (string raw in text.Split('\n')) {
				string line = raw.Trim();
				int space = line.IndexOf(' ');
				if (space != 64) continue;
				string hex = line[..64], name = line[space..].Trim().TrimStart('*');
				if (name.Length > 0 && hex.All(Uri.IsHexDigit)) sums[name] = hex.ToLowerInvariant();
			}
			return sums;
		}

		/// <summary>The last look's outcome, or null before the first.</summary>
		public static UpdateCheck? Last() {
			try {
				return File.Exists(StateFile) ? JsonSerializer.Deserialize<UpdateCheck>(File.ReadAllText(StateFile), AgentConfig.Json) : null;
			}
			catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
				return null;
			}
		}

		static void Record(string? latest, string outcome) {
			try { AgentPaths.WriteAtomic(StateFile, JsonSerializer.Serialize(new UpdateCheck(DateTime.UtcNow, Current, latest, outcome), AgentConfig.Json)); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			AgentPaths.AppendLog($"update: {outcome}");
		}

		/// <summary>The review page's daily look, from <see cref="ReviewServer"/>, until the page closes.</summary>
		public static async Task RunAsync(Func<bool> scanBusy, CancellationToken ct) {
			try {
				await Task.Delay(FirstLook, ct);
				while (!ct.IsCancellationRequested) {
					await LookAsync(scanBusy, install: true, ct);
					await Task.Delay(Every, ct);
				}
			}
			catch (OperationCanceledException) { }
		}

		/// <summary>
		/// Looks for a newer release, and with <paramref name="install"/> installs it once no scan runs. Returns what it found,
		/// in a sentence. Never throws for a network or disk problem: that's the outcome.
		/// </summary>
		public static async Task<string> LookAsync(Func<bool> scanBusy, bool install, CancellationToken ct) {
			AgentConfig cfg = AgentConfig.Load();
			if (WhyNot(cfg) is string why) return why;
			string? latest = null;
			try {
				using var http = Client();
				var (version, assets) = await LatestAsync(http, ct);
				latest = version;
				if (!Newer(version, Current)) {
					Record(version, "up to date");
					return $"Heiward {Current} is up to date.";
				}
				if (!install) {
					Record(version, $"{version} is out");
					return $"Heiward {version} is out (this is {Current}).";
				}
				bool arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
				string name = AssetName(version, arm64);
				if (!assets.TryGetValue(name, out string? exeUrl)) throw new InvalidOperationException($"the release {version} has no {name}");
				if (!assets.TryGetValue("SHA256SUMS.txt", out string? sumsUrl)) throw new InvalidOperationException($"the release {version} has no SHA256SUMS.txt");
				if (!ParseSums(await http.GetStringAsync(sumsUrl, ct)).TryGetValue(name, out string? sum)) throw new InvalidOperationException($"SHA256SUMS.txt has no line for {name}");

				// Not in the middle of a scan: its process would be stopped by the installer, and the scan lost.
				for (var waited = TimeSpan.Zero; scanBusy(); waited += BusyRetry) {
					if (waited >= MostWait) {
						Record(version, $"{version} waits: a scan ran for hours");
						return $"Heiward {version} waits until no scan runs.";
					}
					await Task.Delay(BusyRetry, ct);
				}

				string exe = await DownloadAsync(http, exeUrl, name, sum, ct);
				Record(version, $"updating from {Current} to {version}");
				StartInstaller(exe);
				return $"Updating Heiward from {Current} to {version}.";
			}
			catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested) {
				Record(latest, $"couldn't update: {e.Message}");
				return $"Heiward couldn't look for an update: {e.Message}";
			}
		}

		static HttpClient Client() {
			var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
			http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Heiward", Current));
			return http;
		}

		/// <summary>The newest release (GitHub's latest: never a draft or a pre-release): its version, and its files' download addresses by name.</summary>
		static async Task<(string Version, Dictionary<string, string> Assets)> LatestAsync(HttpClient http, CancellationToken ct) {
			using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/repos/{Repo}/releases/latest");
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
			using var response = await http.SendAsync(request, ct);
			response.EnsureSuccessStatusCode();
			using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
			string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException("the latest release has no tag");
			var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
				if (a.GetProperty("name").GetString() is string n && a.GetProperty("browser_download_url").GetString() is string u) assets[n] = u;
			return (tag.TrimStart('v', 'V'), assets);
		}

		/// <summary>Downloads the exe into <see cref="Folder"/> and checks its SHA-256; a file that doesn't match is deleted.</summary>
		static async Task<string> DownloadAsync(HttpClient http, string url, string name, string sum, CancellationToken ct) {
			Directory.CreateDirectory(Folder);
			// Earlier downloads go: each is ~50 MB, and only the newest is ever installed.
			foreach (string old in Directory.EnumerateFiles(Folder, "Heiward-*.exe*"))
				try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
			string path = Path.Combine(Folder, name), partial = path + ".partial";
			await using (var body = await http.GetStreamAsync(url, ct))
			await using (var file = File.Create(partial))
				await body.CopyToAsync(file, ct);
			string got;
			await using (var file = File.OpenRead(partial))
				got = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
			if (got != sum) {
				File.Delete(partial);
				throw new InvalidOperationException($"{name} didn't match the release's SHA-256, so it wasn't installed");
			}
			File.Move(partial, path, overwrite: true);
			return path;
		}

		/// <summary>The new exe's installer, on its own: it stops this process, installs over the installed copy, and starts the page again.</summary>
		static void StartInstaller(string exe) {
			var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, WorkingDirectory = Folder };
			foreach (string a in new[] { "install", "--yes", "--no-browser", "--keep-settings" }) psi.ArgumentList.Add(a);
			Installer.KeepStdHandlesToSelf();
			using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(exe)} didn't start");
			// Nobody to answer a question: its input is closed, as for any unattended install.
			p.StandardInput.Close();
		}
	}
}
