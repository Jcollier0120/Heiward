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
using HEI.Core.FFTools;
using HEI.Core.FFTools.FFmpegNative;
using HEI.Core.Utils;

namespace HEI.Agent.Estate {
	/// <summary>
	/// What a scan for another PC found, as that PC pulls it (<c>GET /api/peer/findings</c>), version 1: its sets, with that
	/// PC's own paths. <see cref="Build"/>: the build that judged them, which must be that PC's own for it to show them
	/// (another build's rules judge sets otherwise, as a report of another build is set aside).
	/// <see cref="ScannedAtUtc"/>: when the scan began, which also says which scan they're from (the same again: nothing new);
	/// <see cref="FinishedUtc"/>: when it ended (an older Heiward leaves it out, and reads past it).
	/// </summary>
	sealed record RemoteFindings(int V, string Build, string Stamp, DateTime ScannedAtUtc, double DurationSec, int FilesScanned, string Device,
		List<ReportGroup> Groups, RemoteCost? Cost = null, DateTime? FinishedUtc = null) {
		public const int Version = 1;

		/// <summary>When the scan ended: as said, or, from an older PC, when it began and how long it took.</summary>
		public DateTime EndedUtc => FinishedUtc ?? ScannedAtUtc.AddSeconds(Math.Max(0, DurationSec));
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
	/// <item>The other PC's listing, a page at a time (<see cref="ListingPages"/>), unless it's the same as at the last
	/// scan (<c>?have=</c>): then nothing is read at all.</item>
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
		/// <summary>
		/// Written by this PC's page when the scan is to stop (the other PC ended it, or Heiward here stops or is updated): the
		/// scan stops as if asked, keeping what it checked for the next scan to pick up from, rather than its process being ended.
		/// </summary>
		public static string StopFile(string home) => Path.Combine(home, "stop");
		/// <summary>The listing as at the last scan (<see cref="ListingPages.Save"/>), for the next.</summary>
		static string ListingIn(string home) => Path.Combine(home, "listing.tsv");
		static string DatesIn(string home) => Path.Combine(home, "dates.json");

		/// <summary>Whether a photo there has a capture date, read while its bytes were here, with its size and time then.</summary>
		internal sealed record DateKept(long Size, long ModifiedTicks, bool Has);

		/// <summary>A token cancelled once the stop file is there (looked for every <c>every</c>), or the one it's linked to is.</summary>
		internal sealed class StopWhenAsked : IDisposable {
			readonly CancellationTokenSource told;
			readonly Timer look;

			public StopWhenAsked(string stopFile, CancellationToken ct, Action? said = null, TimeSpan? every = null) {
				told = CancellationTokenSource.CreateLinkedTokenSource(ct);
				TimeSpan often = every ?? TimeSpan.FromSeconds(1);
				look = new Timer(_ => {
					try {
						if (told.IsCancellationRequested || !File.Exists(stopFile)) return;
						said?.Invoke();
						told.Cancel();
					}
					catch (ObjectDisposedException) { }
				}, null, often, often);
			}

			public CancellationToken Token => told.Token;

			public void Dispose() {
				look.Dispose();
				told.Dispose();
			}
		}

		/// <summary>How long a stopped scan waits for the engine's own last saves (its database, the AI's embeddings).</summary>
		static readonly TimeSpan EngineStopWait = TimeSpan.FromSeconds(20);

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
			// The page asks this scan to stop by its stop file: it stops as its Stop would.
			using var told = new StopWhenAsked(StopFile(home), ct, () => AgentPaths.AppendLog($"scan for {name}: this PC's page asked it to stop"));
			ct = told.Token;
			try {
				int code = await ScanAsync(pc, name, home, cfg, started, timer, ct, s => stage = s, (d, t) => (done, total) = (d, t), w => waiting = w, b => bytes = b);
				stage = code == 0 ? "Done" : stage;
				Write(finished: true, code: code);
				return code;
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				AgentPaths.AppendLog($"scan for {name} stopped");
				Write(finished: true, code: 130, error: "stopped");
				return 130;
			}
			catch (Exception e) {
				AgentPaths.AppendLog($"scan for {name} failed: {e}");
				Write(finished: true, code: 1, error: Plain(e));
				return 1;
			}
		}

		/// <summary>
		/// Why a scan for another PC failed, in words for that PC's page, where it shows (this PC is "the other
		/// PC" there, so it's named): never an exception's own message, but for this PC's disk's, which Windows words.
		/// </summary>
		internal static string Plain(Exception e) {
			string me = Environment.MachineName;
			return e switch {
				EstateMissing => $"Castellan isn't running on {me}",
				HttpRequestException or OperationCanceledException or IOException { InnerException: System.Net.Sockets.SocketException } or HttpIOException =>
					$"The link between {me} and this PC broke off. Try again; Castellan must be running on both PCs",
				InvalidDataException or JsonException => $"This PC sent something Heiward on {me} doesn't read: update Heiward on both PCs",
				IOException or UnauthorizedAccessException => $"{me} couldn't use its own disk for the scan: {e.Message.TrimEnd('.')}",
				_ => $"Something went wrong on {me}: Heiward's log there says what",
			};
		}

		static async Task<int> ScanAsync(string pc, string name, string home, AgentConfig cfg, DateTime started, Stopwatch timer, CancellationToken ct,
			Action<string> stage, Action<int, int> progress, Action<string?> waiting, Action<EstateBytes> fetcher) {
			// 1. Its listing, a page at a time, or word that it's as it was. (Version 1's listing.json, read whole, goes: the
			// first scan after it asks for all of the listing again.)
			TryDelete(Path.Combine(home, "listing.json"));
			RemoteListing? kept = ListingPages.Load(ListingIn(home));
			bool keptHolds = kept != null && Report.Load() != null && File.Exists(RemoteFindings.FilePath);
			var listingTimer = Stopwatch.StartNew();
			ListingPages.Fetched fetched = await ListingPages.FetchAsync(pc, name, keptHolds ? kept!.Stamp : null, progress, ct);
			if (fetched.Problem is string problem) {
				AgentPaths.AppendLog($"scan for {name}: {problem}");
				stage(problem);
				return 3;
			}
			if (fetched.Same) {
				AgentPaths.AppendLog($"scan for {name}: nothing new there since the last scan, nothing read");
				return 0;
			}
			RemoteListing listing = fetched.Listing!;
			int listed = listing.Roots.Sum(r => r.Files.Count);
			AgentPaths.AppendLog($"scan for {name}: its list of {listed:N0} files came in {listingTimer.Elapsed.TotalSeconds:N1} s");
			// 2. The stand-ins.
			string mirrorDir = MirrorIn(home);
			if (!RemoteMirror.CanHold(mirrorDir)) {
				AgentPaths.AppendLog($"scan for {name}: Heiward's folder is on a drive that can't hold the stand-ins (not NTFS)");
				stage($"Heiward's folder on {Environment.MachineName} is on a drive that can't hold this PC's files");
				return 4;
			}
			// What the scan waits for, for the other PC's page: a game there (the link), or this PC's NPU giving way.
			object waits = new();
			string? onLink = null;
			bool givingWay = false;
			void Say() => waiting(givingWay ? "npu" : onLink);
			var timings = new RemoteTimings();
			var bytes = new EstateBytes(pc, w => { lock (waits) { onLink = w; Say(); } }, timings);
			fetcher(bytes);
			using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
			// This PC's NPU lent to that PC as background work, on the PC-wide record (the kit's LENDING.md): while a call is
			// captioned for another PC, or work someone waits on is lent, the scan takes no new NPU turn, and goes on after.
			using var lending = NpuLending.Join(NpuLending.Background, "heiward", name, $"scanning {name}'s photos and videos for it", stopping.Token, giving => {
				lock (waits) { givingWay = giving; Say(); }
				AgentPaths.AppendLog(giving ? $"scan for {name}: this PC's NPU is lent to something more urgent, so the scan waits for it"
					: $"scan for {name}: the NPU is free of it again");
			});
			using var mirror = new RemoteMirror(mirrorDir, bytes) { Stopping = stopping.Token, Timings = timings };
			// The card counts the stand-ins as they're made, from none (not the listing's count, done).
			progress(0, listed);
			stage("Making room for its files");
			var roomTimer = Stopwatch.StartNew();
			var (added, changed, gone) = mirror.Sync(listing, progress);
			AgentPaths.AppendLog($"scan for {name}: room made for its {listed:N0} files in {roomTimer.Elapsed.TotalSeconds:N1} s");

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
			kept = null; // its files are in before now
			int unchangedFiles = before == null ? 0
				: listing.Roots.SelectMany(r => r.Files).Count(f => before.TryGetValue(f.Path, out ListedFile? was) && was == f);
			ScanEngine.RootListing? ListFor(string root) {
				if (!roots.TryGetValue(Path.TrimEndingDirectorySeparator(root), out ListedRoot? r)) return null;
				var files = new List<FileInfo>();
				var unchanged = new List<string>();
				foreach (ListedFile f in r.Files) {
					string p = RemotePath.Mirrored(mirrorDir, f.Path);
					if (before != null && before.TryGetValue(f.Path, out ListedFile? was) && was == f) unchanged.Add(p);
					else files.Add(new FileInfo(p));
				}
				return new ScanEngine.RootListing(files, unchanged);
			}

			// The cap is a share of the whole PC, so of all its processors: 8 cores' worth of the X2's 18 is 44%.
			int processors = Power.MachineProcessors;
			Power.SetPace(false, CpuCap(processors));
			AgentPaths.AppendLog($"scan for {name} started: {listed:N0} files listed there ({added:N0} new, {changed:N0} changed, {gone:N0} gone since the last scan), " +
				$"{settings.MaxDegreeOfParallelism} at once, at most {CpuCap(processors):0.#}% of the processor ({Math.Min(MostCores, processors)} of its {processors} cores' worth), {RemoteMirror.AheadAsksDefault} asks under way at once for the files just ahead");
			if (settings.UseAiMatching) await NpuCheck.EnsureAsync(settings.AiDevice, AgentPaths.AppendLog, ct);

			// What sorting the sets reads of a photo, taken while the scan has all of it here: its hash (for plain copies) and
			// whether it has a capture date (for which to keep). Without, every photo in a set was fetched a second time. Both are
			// kept for the next scan, for photos as they were: the hashes as this PC's own scans keep theirs, the dates here.
			var hashes = ReportBuilder.ContentHashes.Load();
			var byStandIn = listing.Roots.SelectMany(r => r.Files)
				.ToDictionary(f => RemotePath.Mirrored(mirrorDir, f.Path), StringComparer.OrdinalIgnoreCase);
			var dates = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			foreach (var (path, k) in ReadDates(DatesIn(home)))
				if (byStandIn.TryGetValue(path, out ListedFile? f) && f.Size == k.Size && f.ModifiedTicks == k.ModifiedTicks) dates[path] = k.Has;
			void KeepTaken(bool sorted) {
				try {
					if (sorted) hashes.Save();
					else hashes.SaveTaken();
					var keepDates = new Dictionary<string, DateKept>(StringComparer.OrdinalIgnoreCase);
					foreach (var (path, has) in dates)
						if (byStandIn.TryGetValue(path, out ListedFile? f) && !mirror.Failed.ContainsKey(path)) keepDates[path] = new DateKept(f.Size, f.ModifiedTicks, has);
					AgentPaths.WriteAtomic(DatesIn(home), JsonSerializer.Serialize(keepDates, AgentConfig.Json));
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AgentPaths.AppendLog("saving what was read of its photos failed: " + e.Message); }
			}
			mirror.WhileHeld = path => {
				dates[path] = ExifReader.TryGetDateTaken(path, out _);
				var file = new FileInfo(path);
				using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
				hashes.Keep(path, file.Length, file.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(fs)));
			};
			RemoteMedia.Use(mirror.IsRemote, mirror.Open);
			RemoteFiles.Fetch = mirror.Fetch;
			// A video whose keyframes are too far apart to sample is left out, said by name here, not under its stand-in's path.
			FfmpegEngine.VideoLeftOut = (path, why) => AgentPaths.AppendLog($"scan for {name}: left out {mirror.Original(path)}: {why}");
			// Planned: the files it reads next are fetched just ahead of it (RemoteMirror.Plan), several asks at once.
			var engine = new ScanEngine { Settings = settings, ListRoot = ListFor, BeforeRead = mirror.Before, AfterRead = mirror.After, Planned = mirror.Plan };
			// A scan picking up where one stopped has the photos' fingerprints, and maybe not their embeddings: those photos are read too.
			mirror.WantsEmbedding = engine.WantsPhotoEmbedding;
			int checkedNow = 0, checkedOf = -1;
			bool checking = true;
			engine.Progress += (_, e) => {
				// The files checked of all to check: the first count's whole, not a step's of its own on the way (its own 1 of 1).
				if (checking && (checkedOf < 0 || e.MaxPosition == checkedOf)) (checkedNow, checkedOf) = (e.CurrentPosition, e.MaxPosition);
				progress(e.CurrentPosition, e.MaxPosition);
			};
			// Where the time goes, in the log: a summary every 1,000 files checked, or every 5 minutes while some are.
			int summarizedAt = 0, summarizing = 0;
			long summarizedTicks = Stopwatch.GetTimestamp();
			ScanEngine.ReadCost costsBefore = default;
			RemoteTimings.Target? targetBefore = await TargetTimingAsync(pc, ct);
			async Task SummarizeAsync() {
				if (Interlocked.Exchange(ref summarizing, 1) == 1) return;
				try {
					int now = checkedNow, before = summarizedAt;
					summarizedAt = now;
					summarizedTicks = Stopwatch.GetTimestamp();
					ScanEngine.ReadCost costs = engine.ReadCosts, step = costs.Minus(costsBefore);
					costsBefore = costs;
					RemoteTimings.Target? target = await TargetTimingAsync(pc, stopping.Token);
					RemoteTimings.Target? since = target?.Minus(targetBefore);
					targetBefore = target ?? targetBefore;
					AgentPaths.AppendLog(timings.Summary(name, before, now, Math.Max(checkedOf, now), Steps(step), since));
				}
				catch (Exception e) when (e is OperationCanceledException or IOException or HttpRequestException or JsonException) { }
				finally { Volatile.Write(ref summarizing, 0); }
			}
			using var summaries = new Timer(_ => {
				int now = checkedNow;
				if (now - summarizedAt >= 1_000 || now > summarizedAt && Stopwatch.GetElapsedTime(summarizedTicks).TotalMinutes >= 5) _ = SummarizeAsync();
			}, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
			progress(0, listed);
			stage("Checking its files");
			var checkTimer = Stopwatch.StartNew();
			// The other PC ended its scan, or Castellan stopped: nothing more can be fetched.
			using var over = new Timer(_ => { if (mirror.Over) stopping.Cancel(); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
			// A game here: the scan pauses until it's gone (two checks in a row either way).
			using var games = new ThreeDWatch();
			int busy = 0, free = 0;
			bool paused = false;
			using var gameTimer = new Timer(_ => {
				try {
					if (games.Busy() != null) { free = 0; if (++busy >= 2 && !paused) { paused = true; engine.Pause(); stage($"Paused while a game runs on {Environment.MachineName}"); } }
					else { busy = 0; if (paused && ++free >= 2) { paused = false; engine.Resume(); stage("Checking its files"); } }
				}
				catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }
			}, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
			List<ReportGroup> groups;
			string device;
			int files;
			// The other PC ended it (its Stop), or this PC stopped it: nothing is saved as found, and what was read stays in the
			// database for the next scan, but for the files that couldn't be read, and so do its photos' hashes and dates.
			int Stopped() {
				// The engine saves its database and the AI's embeddings as it stops: this process ends only once it has.
				for (var waited = Stopwatch.StartNew(); engine.IsScanning && waited.Elapsed < EngineStopWait;) Thread.Sleep(100);
				if (!mirror.Failed.IsEmpty) DatabaseUtils.Database.RemoveWhere(e => mirror.Failed.ContainsKey(e.Path));
				DatabaseUtils.SaveDatabase();
				KeepTaken(sorted: false);
				stage("Stopped");
				AgentPaths.AppendLog($"scan for {name} stopped{(mirror.Over ? " by that PC" : "")}, {Format.Bytes(bytes.Fetched)} fetched: what was read is kept for its next scan");
				return 130;
			}
			try {
				await AgentScanner.RunEngineAsync(engine, () => {
					checking = false; // the compare's own progress from here
					checkedNow = Math.Max(checkedNow, checkedOf);
					summaries.Change(Timeout.Infinite, Timeout.Infinite);
					AgentPaths.AppendLog($"scan for {name}: its {checkedNow:N0} files checked in {checkTimer.Elapsed.TotalSeconds:N0} s, {Format.Bytes(bytes.Fetched)} fetched in {bytes.Asks:N0} asks");
					_ = SummarizeAsync();
					stage("Comparing");
				}, stopping.Token);
				files = engine.FoundMedia().Count;
				device = !settings.UseAiMatching ? "off" : engine.AiDeviceUsed ?? NpuComponents.DeviceFor(settings.AiDevice);
				string? cacheKey = engine.AiDeviceUsed != null ? engine.AiCacheKeyUsed : NpuComponents.CacheKeyFor(settings.AiDevice);
				var fingerprints = new ScanFingerprints(cacheKey, settings.UseAiMatching, stopping.Token) { TextDevice = settings.AiDevice };
				stage(AgentScanner.SortingSets);
				// Photos alike but not the same bytes are looked at up close and their words read: fetched again, for the log.
				long fetchedBefore = bytes.Fetched;
				int asksBefore = bytes.Asks;
				var sortTimer = Stopwatch.StartNew();
				// The aligned looks within this scan's share of this PC's processor; what couldn't be told of a file whose bytes
				// didn't come isn't kept as unknown.
				hashes.Threads = settings.MaxDegreeOfParallelism;
				hashes.NotFetched = mirror.Failed.ContainsKey;
				try {
					groups = ReportBuilder.Build(engine.Duplicates, fingerprints, hashes, null, null, progress, stopping.Token, dates, new SortFetch(mirror, name));
				}
				finally {
					if (fingerprints.FinishReading() is { } read) AgentPaths.AppendLog(read);
					long sortFetched = bytes.Fetched - fetchedBefore;
					double sortSeconds = Math.Max(0.001, sortTimer.Elapsed.TotalSeconds);
					AgentPaths.AppendLog($"scan for {name}: its sets sorted in {sortSeconds:N0} s, {Format.Bytes(sortFetched)} fetched again for it " +
						$"in {bytes.Asks - asksBefore:N0} asks{(sortFetched > 0 ? $", {Format.Bytes((long)(sortFetched / sortSeconds))}/s" : "")} " +
						"(photos alike looked at up close, and their words read)");
				}
			}
			catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
				return Stopped();
			}
			finally {
				RemoteFiles.Fetch = null;
				FfmpegEngine.VideoLeftOut = null;
				RemoteMedia.Stop();
				mirror.EmptyAll();
			}
			// Sorting the sets stops between rounds without saying so, and once the other PC ends it every read left fails at
			// once, so the scan can get to the end first: what it made of part of the files isn't what was found.
			if (stopping.IsCancellationRequested || mirror.Over) return Stopped();
			stage("Saving what it found");
			KeepTaken(sorted: true);
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
			ListingPages.Save(keep, ListingIn(home));

			var cost = new RemoteCost(bytes.Fetched, bytes.Asks, mirror.MostHeld, timer.Elapsed.TotalSeconds, mirror.Failed.Count);
			var findings = new RemoteFindings(RemoteFindings.Version, AppBuild.Current, listing.Stamp, started, timer.Elapsed.TotalSeconds, files, device,
				Mapped(groups, mirrorDir), cost, DateTime.UtcNow);
			AgentPaths.WriteAtomic(RemoteFindings.FilePath, JsonSerializer.Serialize(findings, AgentConfig.Json));
			AgentPaths.AppendLog($"scan for {name} done: {files:N0} files ({unchangedFiles:N0} as at the last scan), {groups.Count} set(s); " +
				$"{Format.Bytes(bytes.Fetched)} fetched in {bytes.Asks:N0} asks, at most {Format.Bytes(mirror.MostHeld)} held here at once, " +
				$"{mirror.Failed.Count:N0} not fetched; {timer.Elapsed.TotalSeconds:N0} s, AI on {device}");
			foreach (var (path, why) in mirror.Failed.Take(20)) AgentPaths.AppendLog($"  not fetched: {mirror.Original(path)} ({why})");
			return 0;
		}

		/// <summary>
		/// The photos the sets look at again, found by a first pass that fetches nothing and fetched just ahead of the sorting
		/// (<see cref="ReportBuilder.Build"/>, <see cref="RemoteMirror.SortAhead"/>); said in the log.
		/// </summary>
		sealed class SortFetch(RemoteMirror mirror, string name) : ISortFetch {
			public SortNeeds Refuse() => mirror.Refuse();

			public void Ahead(SortNeeds needs) {
				if (needs.Asks == 0) {
					AgentPaths.AppendLog($"scan for {name}: its sets sorted in {needs.Took.TotalSeconds:N0} s from what was kept of its files: nothing fetched again for them");
					return;
				}
				var (photos, bytes) = mirror.SortAhead(needs.Paths);
				AgentPaths.AppendLog($"scan for {name}: its sets look at {photos:N0} photos again ({Format.Bytes(bytes)}), found in {needs.Took.TotalSeconds:N0} s " +
					$"without fetching: fetched just ahead of the sorting, {mirror.AheadAsks} asks at once");
			}
		}

		/// <summary>The other PC's reading for this one so far (<c>GET /api/peer/scan/timing</c>); null when it doesn't say (an older Heiward there).</summary>
		static async Task<RemoteTimings.Target?> TargetTimingAsync(string pc, CancellationToken ct) {
			try {
				var (status, json) = await EstateLink.GetJsonAsync(EstateLink.PeerPath(pc, "scan/timing"), TimeSpan.FromSeconds(10), ct, limit: 64 * 1024);
				using (json) return status == 200 && json != null ? RemoteTimings.Target.Parse(json.RootElement) : null;
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException or EstateMissing && !ct.IsCancellationRequested) {
				return null;
			}
		}

		/// <summary>The engine's steps since the last summary, each with the workers' time on it (waits for the other PC's bytes included).</summary>
		internal static string? Steps(ScanEngine.ReadCost c) {
			var parts = new List<string>();
			void One(string what, int n, double sec) {
				if (n > 0) parts.Add($"{what} {n:N0} × {(sec / n >= 1 ? $"{sec / n:N1} s" : $"{sec * 1000 / n:N0} ms")}");
			}
			One("fingerprints", c.Hashes, c.HashSec);
			One("videos probed", c.Probes, c.ProbeSec);
			One("photos decoded", c.Photos, c.PhotoSec);
			One("videos sampled", c.Videos, c.VideoSec);
			return parts.Count == 0 ? null : string.Join(", ", parts);
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

		static void TryDelete(string path) {
			try { File.Delete(path); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		internal static Dictionary<string, DateKept> ReadDates(string path) {
			try {
				if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, DateKept>>(File.ReadAllText(path), AgentConfig.Json) is { } kept)
					return kept;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
			return new Dictionary<string, DateKept>(StringComparer.OrdinalIgnoreCase);
		}
	}
}
