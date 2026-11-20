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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HEI.Core;
using HEI.Core.AI;
using HEI.Core.FFTools.FFmpegNative;
using HEI.Core.Utils;

namespace HEI.Agent.Estate {
	/// <summary>
	/// What a scan for another PC found, as that PC pulls it (<c>GET /api/peer/findings</c>), version 1: its sets, with that
	/// PC's own paths. <see cref="Build"/>: the build that judged them, which must be that PC's own for it to show them
	/// (another build's rules judge sets otherwise, as a report of another build is set aside).
	/// </summary>
	sealed record RemoteFindings(int V, string Build, string Stamp, DateTime ScannedAtUtc, double DurationSec, int FilesScanned, string Device,
		List<ReportGroup> Groups, RemoteCost? Cost = null) {
		public const int Version = 1;
		/// <summary>In the home of the scans for that PC (<see cref="RemoteScanner.HomeFor"/>).</summary>
		public static string FilePath => Path.Combine(AgentPaths.Home, "findings.json");

		public static RemoteFindings? Load(string path) {
			try {
				return File.Exists(path) ? JsonSerializer.Deserialize<RemoteFindings>(File.ReadAllText(path), AgentConfig.Json) is { V: Version } f ? f : null : null;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return null;
			}
		}
	}

	/// <summary>What a scan for another PC cost: what it fetched, in how many asks, the most it held here at once, and how long it took.</summary>
	sealed record RemoteCost(long FetchedBytes, int Asks, long MostHeldBytes, double Seconds, int Failed);

	/// <summary>How a scan for another PC is getting on, for the PC it's for (pushed as the loan's <c>{"status":…}</c> lines).</summary>
	sealed record RemoteStatus(int Pid, DateTime StartedUtc, string Stage, int Done, int Total, long FetchedBytes, string? Waiting, bool Finished = false,
		int? Code = null, string? Error = null) {
		public static string FilePath(string home) => Path.Combine(home, "remote-status.json");

		public static RemoteStatus? Load(string home) {
			try {
				string path = FilePath(home);
				return File.Exists(path) ? JsonSerializer.Deserialize<RemoteStatus>(File.ReadAllText(path), AgentConfig.Json) : null;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return null;
			}
		}
	}

	/// <summary>
	/// <c>hei remote-scan --pc &lt;id&gt; --name &lt;name&gt;</c>: this PC scans another PC's photos and videos for it, over the
	/// Estate, in a process of its own whose HEIWARD_HOME is that PC's home here (<see cref="HomeFor"/>): its own database,
	/// report, content hashes and stand-ins, apart from this PC's own scans. Started by this PC's page when the other PC
	/// asks (<see cref="ScanForOthers"/>).
	/// <list type="number">
	/// <item>The other PC's listing, unless it's the same as at the last scan (<c>?have=</c>): then nothing is read at all.</item>
	/// <item>A stand-in for each file listed (<see cref="RemoteMirror"/>); files listed as at the last scan aren't read again.</item>
	/// <item>The engine as for this PC's own scans, its readers fetching what they read; at most <see cref="MostCores"/>
	/// cores' worth of the processor, in the background, the NPU for the AI where this PC has one.</item>
	/// <item>The report, its sets with the other PC's paths (<see cref="RemoteFindings"/>), for it to pull.</item>
	/// </list>
	/// </summary>
	static class RemoteScanner {
		/// <summary>The most of this PC's processor work for another PC takes: 8 cores' worth (of the X2's 18).</summary>
		public const int MostCores = 8;

		/// <summary>The home of the scans for <paramref name="pc"/> on this one (this PC's own home's <c>remote\&lt;pc&gt;</c>).</summary>
		public static string HomeFor(string pc) => Path.Combine(AgentPaths.Home, "remote", pc);

		static string MirrorIn(string home) => Path.Combine(home, "mirror");
		static string ListingIn(string home) => Path.Combine(home, "listing.json");

		/// <summary>The processor's share for work for another PC, in percent of the whole: <see cref="MostCores"/> cores' worth.</summary>
		public static double CpuCap(int processors) => Math.Min(100, MostCores * 100.0 / Math.Max(1, processors));

		public static async Task<int> RunAsync(string pc, string name, CancellationToken ct) {
			string home = AgentPaths.Home;
			Directory.CreateDirectory(home);
			using FileStream? scanLock = AgentScanner.TryLock();
			if (scanLock == null) {
				Console.Error.WriteLine("A scan for that PC is already running.");
				return 1;
			}
			var cfg = AgentConfig.Load();
			var started = DateTime.UtcNow;
			var timer = Stopwatch.StartNew();
			string stage = "Asking for its list of files";
			int done = 0, total = 0;
			string? waiting = null;
			EstateBytes? bytes = null;
			object gate = new();
			bool ended = false;
			string? last = null;
			// Read by the page here every 2 s (RemoteJob), and sent to the other PC for its page: written as it changes, and
			// never again once it's finished.
			void Write(bool finished = false, int? code = null, string? error = null) {
				lock (gate) {
					if (ended) return;
					ended = finished;
					string json = JsonSerializer.Serialize(
						new RemoteStatus(Environment.ProcessId, started, stage, done, total, bytes?.Fetched ?? 0, waiting, finished, code, error), AgentConfig.Json);
					if (json == last) return;
					try {
						AgentPaths.WriteAtomic(RemoteStatus.FilePath(home), json);
						last = json;
					}
					catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
				}
			}
			Write();
			using var ticker = new Timer(_ => Write(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
			try {
				int code = await ScanAsync(pc, name, home, cfg, started, timer, ct, s => stage = s, (d, t) => (done, total) = (d, t), w => waiting = w, b => bytes = b);
				stage = code == 0 ? "Done" : stage;
				Write(finished: true, code: code);
				return code;
			}
			catch (OperationCanceledException) {
				AgentPaths.AppendLog($"scan for {name} stopped");
				Write(finished: true, code: 130, error: "stopped");
				return 130;
			}
			catch (Exception e) {
				AgentPaths.AppendLog($"scan for {name} failed: {e}");
				Write(finished: true, code: 1, error: e.Message);
				return 1;
			}
		}

		static async Task<int> ScanAsync(string pc, string name, string home, AgentConfig cfg, DateTime started, Stopwatch timer, CancellationToken ct,
			Action<string> stage, Action<int, int> progress, Action<string?> waiting, Action<EstateBytes> fetcher) {
			// 1. Its listing, or word that it's as it was.
			RemoteListing? kept = LoadListing(ListingIn(home));
			bool keptHolds = kept != null && Report.Load() != null && File.Exists(RemoteFindings.FilePath);
			string route = "scan/listing" + (keptHolds ? "?have=" + Uri.EscapeDataString(kept!.Stamp) : "");
			RemoteListing listing;
			var (status, json) = await EstateLink.GetJsonAsync(EstateLink.PeerPath(pc, route), TimeSpan.FromMinutes(5), ct);
			using (json) {
				if (status != 200 || json?.RootElement is not { ValueKind: JsonValueKind.Object } root) {
					AgentPaths.AppendLog($"scan for {name}: its list of files didn't come ({status})");
					stage("Its list of files didn't come");
					return 3;
				}
				if (root.TryGetProperty("same", out JsonElement same) && same.ValueKind == JsonValueKind.True) {
					AgentPaths.AppendLog($"scan for {name}: nothing new there since the last scan, nothing read");
					return 0;
				}
				if (RemoteListing.Parse(root) is not { } parsed) {
					AgentPaths.AppendLog($"scan for {name}: its list of files isn't one this build reads");
					stage("Its Heiward is another version: update Heiward on both PCs");
					return 3;
				}
				listing = parsed;
			}
			int listed = listing.Roots.Sum(r => r.Files.Count);
			// 2. The stand-ins.
			string mirrorDir = MirrorIn(home);
			if (!RemoteMirror.CanHold(mirrorDir)) {
				AgentPaths.AppendLog($"scan for {name}: Heiward's folder is on a drive that can't hold the stand-ins (not NTFS)");
				stage("Heiward's folder on this PC is on a drive that can't hold the other PC's files");
				return 4;
			}
			var bytes = new EstateBytes(pc, waiting);
			fetcher(bytes);
			using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
			using var mirror = new RemoteMirror(mirrorDir, bytes) { Stopping = stopping.Token };
			stage("Making room for its files");
			var (added, changed, gone) = mirror.Sync(listing);

			// 3. The engine, as for this PC's own scans, reading through the stand-ins.
			var notes = new List<string>();
			var settings = new Settings {
				IncludeImages = true,
				IncludeSubDirectories = true,
				UseAiMatching = AiComponents.IsReady,
				AiDevice = Enum.TryParse(cfg.AiDevice, ignoreCase: true, out AiDevice d) ? d : AiDevice.Auto,
				Gpu = cfg.Gpu,
				ListingProvesExistence = true,
				ForgetUnlisted = true,
				UseWindowsImageDecoder = true,
				MaxDegreeOfParallelism = Math.Min(MostCores, cfg.ParallelismFor(true)),
				CustomDatabaseFolder = AgentPaths.Database,
				UseNativeFfmpegBinding = ScanEngine.NativeFFmpegExists,
				// A clip of a longer video is found by its soundtrack and every frame: all of both videos fetched. Not over the link.
				EnablePartialClipDetection = false,
				EnableAiPartialDetection = false,
			};
			Directory.CreateDirectory(AgentPaths.Database);
			var roots = new Dictionary<string, ListedRoot>(StringComparer.OrdinalIgnoreCase);
			foreach (ListedRoot r in listing.Roots) {
				string standIn = Path.TrimEndingDirectorySeparator(RemotePath.Mirrored(mirrorDir, r.Root));
				roots[standIn] = r;
				settings.IncludeList.Add(standIn);
			}
			if (!settings.UseNativeFfmpegBinding) {
				// Without FFmpeg's libraries in this process, a video could only be read by the FFmpeg program, by its path: all of it fetched.
				foreach (string e in FileUtils.VideoExtensions) settings.ExcludedExtensions.Add(e);
				notes.Add("Videos were left out: FFmpeg's libraries don't load on this PC.");
			}
			if (!settings.UseAiMatching) notes.Add("AI matching is off on this PC: run 'hei setup' there.");
			// Files listed as at the last scan aren't listed again: the engine knows them as they are.
			Dictionary<string, ListedFile>? before = keptHolds ? kept!.Roots.SelectMany(r => r.Files).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) : null;
			int unchangedFiles = 0;
			ScanEngine.RootListing? ListFor(string root) {
				if (!roots.TryGetValue(Path.TrimEndingDirectorySeparator(root), out ListedRoot? r)) return null;
				var files = new List<FileInfo>();
				var unchanged = new List<string>();
				foreach (ListedFile f in r.Files) {
					string p = RemotePath.Mirrored(mirrorDir, f.Path);
					if (before != null && before.TryGetValue(f.Path, out ListedFile? was) && was == f) unchanged.Add(p);
					else files.Add(new FileInfo(p));
				}
				Interlocked.Add(ref unchangedFiles, unchanged.Count);
				return new ScanEngine.RootListing(files, unchanged);
			}

			Power.SetPace(false, CpuCap(Environment.ProcessorCount));
			AgentPaths.AppendLog($"scan for {name} started: {listed:N0} files listed there ({added:N0} new, {changed:N0} changed, {gone:N0} gone since the last scan), " +
				$"{settings.MaxDegreeOfParallelism} at once, at most {CpuCap(Environment.ProcessorCount):0.#}% of the processor");
			if (settings.UseAiMatching) await NpuCheck.EnsureAsync(settings.AiDevice, AgentPaths.AppendLog, ct);

			// What sorting the sets reads of a photo, taken while the scan has all of it here: its hash (for plain copies) and
			// whether it has a capture date (for which to keep). Without, every photo in a set was fetched a second time.
			var hashes = ReportBuilder.ContentHashes.Load();
			var dates = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			mirror.WhileHeld = path => {
				dates[path] = ExifReader.TryGetDateTaken(path, out _);
				var file = new FileInfo(path);
				using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
				hashes.Keep(path, file.Length, file.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(fs)));
			};
			RemoteMedia.Use(mirror.IsRemote, mirror.Open);
			RemoteFiles.Fetch = mirror.Fetch;
			var engine = new ScanEngine { Settings = settings, ListRoot = ListFor, BeforeRead = mirror.Before, AfterRead = mirror.After };
			engine.Progress += (_, e) => progress(e.CurrentPosition, e.MaxPosition);
			stage("Checking its files");
			// The other PC ended its scan, or Castellan stopped: nothing more can be fetched.
			using var over = new Timer(_ => { if (mirror.Over) stopping.Cancel(); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
			// A game here: the scan pauses until it's gone (two checks in a row either way).
			using var games = new ThreeDWatch();
			int busy = 0, free = 0;
			bool paused = false;
			using var gameTimer = new Timer(_ => {
				try {
					if (games.Busy() != null) { free = 0; if (++busy >= 2 && !paused) { paused = true; engine.Pause(); stage("Paused while a game runs on this PC"); } }
					else { busy = 0; if (paused && ++free >= 2) { paused = false; engine.Resume(); stage("Checking its files"); } }
				}
				catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }
			}, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
			List<ReportGroup> groups;
			string device;
			int files;
			// The other PC ended it (its Stop), or this PC stopped it: nothing is saved as found, and what was read stays in the
			// database for the next scan, but for the files that couldn't be read.
			int Stopped() {
				if (!mirror.Failed.IsEmpty) DatabaseUtils.Database.RemoveWhere(e => mirror.Failed.ContainsKey(e.Path));
				DatabaseUtils.SaveDatabase();
				stage("Stopped");
				AgentPaths.AppendLog($"scan for {name} stopped{(mirror.Over ? " by that PC" : "")}, {Format.Bytes(bytes.Fetched)} fetched: what was read is kept for its next scan");
				return 130;
			}
			try {
				await AgentScanner.RunEngineAsync(engine, () => stage("Comparing"), stopping.Token);
				files = engine.FoundMedia().Count;
				device = !settings.UseAiMatching ? "off" : engine.AiDeviceUsed ?? NpuComponents.DeviceFor(settings.AiDevice);
				string? cacheKey = engine.AiDeviceUsed != null ? engine.AiCacheKeyUsed : NpuComponents.CacheKeyFor(settings.AiDevice);
				var fingerprints = new ScanFingerprints(cacheKey, settings.UseAiMatching, stopping.Token) { TextDevice = settings.AiDevice };
				stage(AgentScanner.SortingSets);
				try {
					groups = ReportBuilder.Build(engine.Duplicates, fingerprints, hashes, null, null, progress, stopping.Token, dates);
				}
				finally {
					if (fingerprints.FinishReading() is { } read) AgentPaths.AppendLog(read);
				}
				try { hashes.Save(); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AgentPaths.AppendLog("saving the content hashes failed: " + e.Message); }
			}
			catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
				return Stopped();
			}
			finally {
				RemoteFiles.Fetch = null;
				RemoteMedia.Stop();
				mirror.EmptyAll();
			}
			// Sorting the sets stops between rounds without saying so: what it made of part of them isn't what was found.
			if (stopping.IsCancellationRequested) return Stopped();
			stage("Saving what it found");
			var report = new Report(Report.CurrentVersion, started, timer.Elapsed.TotalSeconds, device, files, settings.IncludeList.ToList(), [], notes, groups, AppBuild.Current);
			report.Save();
			// Files whose bytes didn't come wait for the next scan: out of the database, and of the listing kept, so it reads them then.
			if (!mirror.Failed.IsEmpty) {
				DatabaseUtils.Database.RemoveWhere(e => mirror.Failed.ContainsKey(e.Path));
				DatabaseUtils.SaveDatabase();
			}
			var keep = listing with {
				Roots = listing.Roots.Select(r => r with {
					Files = r.Files.Where(f => !mirror.Failed.ContainsKey(RemotePath.Mirrored(mirrorDir, f.Path))).ToList(),
				}).ToList(),
			};
			if (!mirror.Failed.IsEmpty) keep = keep with { Stamp = "" }; // asked whole next time
			File.WriteAllBytes(ListingIn(home), keep.ToJson());

			var cost = new RemoteCost(bytes.Fetched, bytes.Asks, mirror.MostHeld, timer.Elapsed.TotalSeconds, mirror.Failed.Count);
			var findings = new RemoteFindings(RemoteFindings.Version, AppBuild.Current, listing.Stamp, started, timer.Elapsed.TotalSeconds, files, device,
				Mapped(groups, mirrorDir), cost);
			AgentPaths.WriteAtomic(RemoteFindings.FilePath, JsonSerializer.Serialize(findings, AgentConfig.Json));
			AgentPaths.AppendLog($"scan for {name} done: {files:N0} files ({unchangedFiles:N0} as at the last scan), {groups.Count} set(s); " +
				$"{Format.Bytes(bytes.Fetched)} fetched in {bytes.Asks:N0} asks, at most {Format.Bytes(mirror.MostHeld)} held here at once, " +
				$"{mirror.Failed.Count:N0} not fetched; {timer.Elapsed.TotalSeconds:N0} s, AI on {device}");
			foreach (var (path, why) in mirror.Failed.Take(20)) AgentPaths.AppendLog($"  not fetched: {mirror.Original(path)} ({why})");
			return 0;
		}

		/// <summary>The sets with the other PC's own paths, keyed as that PC's own scan would key them; a set with a file outside the stand-ins is left out.</summary>
		internal static List<ReportGroup> Mapped(IEnumerable<ReportGroup> groups, string mirrorDir) {
			var mapped = new List<ReportGroup>();
			foreach (ReportGroup g in groups) {
				var items = new List<ReportItem>(g.Items.Count);
				foreach (ReportItem i in g.Items) {
					if (RemotePath.Original(mirrorDir, i.Path) is not string p) break;
					items.Add(i with { Path = p, Folder = Path.GetDirectoryName(p) ?? "", Synced = false });
				}
				if (items.Count != g.Items.Count || RemotePath.Original(mirrorDir, g.KeepPath) is not string keep) continue;
				mapped.Add(g with { Key = ReportBuilder.GroupKey(items.Select(i => i.Path)), KeepPath = keep, Items = items });
			}
			return mapped;
		}

		static RemoteListing? LoadListing(string path) {
			try {
				if (!File.Exists(path)) return null;
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
				return RemoteListing.Parse(doc.RootElement);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return null;
			}
		}
	}
}
