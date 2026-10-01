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
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HEI.Agent {
	/// <param name="Batch">Set when the page cleans a whole folder (<paramref name="Folder"/>) set by set: one History row for them all.</param>
	sealed record RecycleRequest(List<string> Paths, string? Batch = null, string? Folder = null);
	sealed record SkipRequest(List<string> Keys, string? Batch, string? Folder);
	sealed record BatchRequest(string Batch);
	sealed record SettingsRequest(bool? KeepHistory, string? ScanSpeed);
	/// <param name="Minutes">How long; null: until the user resumes.</param>
	sealed record PauseRequest(int? Minutes);
	sealed record FolderOverrideRequest(string Path, bool Include, string? RemoveRule);
	sealed record AutoHoldRequest(string Target, bool Hold);
	sealed record AutoAllowRequest(string Pair, bool Allow);

	/// <summary>
	/// The review page on http://127.0.0.1:{port}. LOCAL ONLY, AND THE BUTTONS ARE GUARDED:
	/// <list type="bullet">
	/// <item>It binds the loopback address only, and answers only requests whose Host is its own
	/// (a DNS-rebinding page can't talk to it).</item>
	/// <item>Every action (POST) needs the token that exists only inside the page this process served,
	/// and a same-origin Origin header, so no other web page can press a button.</item>
	/// <item>Thumbnails are served only for files in the current report — never for an arbitrary path.</item>
	/// </list>
	/// It exits after <see cref="AgentConfig.ServerIdleMinutes"/> without a request (the open page polls).
	/// </summary>
	static class ReviewServer {
		public static async Task<int> RunAsync(AgentConfig cfg, bool openBrowser, CancellationToken ct) {
			int port = cfg.Port;
			var actions = new CleanupActions(cfg, automatic: false);
			if (await IsUpAsync(port)) {
				Console.WriteLine($"The review page is already running: {PageUrl(port)}");
				if (openBrowser) OpenBrowser(port);
				return 0;
			}
			string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
			// heiward.localhost is the name the page is opened under: browsers resolve every *.localhost name to
			// this PC themselves (no hosts file, no admin rights), so no DNS answer can point it anywhere else.
			var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{HostName}:{port}", $"127.0.0.1:{port}", $"localhost:{port}" };

			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
			var app = builder.Build();
			long lastSeen = Environment.TickCount64;

			app.Use(async (ctx, next) => {
				if (!hosts.Contains(ctx.Request.Host.Value ?? "")) {
					ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
					return;
				}
				Interlocked.Exchange(ref lastSeen, Environment.TickCount64);
				if (HttpMethods.IsPost(ctx.Request.Method)) {
					string? origin = ctx.Request.Headers.Origin;
					bool sameOrigin = origin == null || hosts.Any(h => origin.Equals("http://" + h, StringComparison.OrdinalIgnoreCase));
					if (!sameOrigin || ctx.Request.Headers["X-Agent-Token"] != token) {
						ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
						return;
					}
				}
				ctx.Response.Headers.XContentTypeOptions = "nosniff";
				ctx.Response.Headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
				await next();
			});

			app.MapGet("/", () => Results.Content(Asset("index.html").Replace("__AGENT_TOKEN__", token), "text/html; charset=utf-8"));
			// Revalidated on every load, so an updated agent's page never runs yesterday's script.
			app.MapGet("/app.js", (HttpContext ctx) => { ctx.Response.Headers.CacheControl = "no-cache"; return Results.Content(Asset("app.js"), "text/javascript; charset=utf-8"); });
			app.MapGet("/app.css", (HttpContext ctx) => { ctx.Response.Headers.CacheControl = "no-cache"; return Results.Content(Asset("app.css"), "text/css; charset=utf-8"); });
			// The tab icon. Its own policy lets its <style> (light and dark colours) apply; it holds no script.
			app.MapGet("/favicon.svg", (HttpContext ctx) => {
				ctx.Response.Headers.CacheControl = "no-cache";
				ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
				return Results.Content(Asset("favicon.svg"), "image/svg+xml");
			});
			app.MapGet("/theme.js", (HttpContext ctx) => { ctx.Response.Headers.CacheControl = "no-cache"; return Results.Content(Asset("theme.js"), "text/javascript; charset=utf-8"); });
			app.MapGet("/api/ping", () => Results.Json(new { app = "heiward", store = StorePackage.IsPackaged }));
			// seen=1: the page is showing, so scans run at full speed (ScanPace); a hidden tab leaves it out.
			app.MapGet("/api/state", (bool? seen) => {
				if (seen == true) ScanPace.MarkPageSeen();
				return Results.Json(State(cfg), AgentConfig.Json);
			});
			// Folder names only, and only below what the agent scans (a GET from another site can't read
			// the answer: no CORS, and a foreign Host header is refused above).
			app.MapGet("/api/tree", (string path, bool? all) => {
				var decisions = DecisionStore.Load();
				var pending = (Report.Load()?.Groups ?? new()).Where(g => !decisions.ContainsKey(g.Key)).ToList();
				TreeListing? listing = ExplorerView.Tree(path, all == true, cfg, ScanIndex.Load(), pending);
				return listing == null ? Results.NotFound(new { error = "That folder isn't one Heiward scans." }) : Results.Json(listing, AgentConfig.Json);
			});

			app.MapGet("/api/thumb/{key}/{index:int}", (string key, int index, HttpContext ctx) => {
				ReportGroup? g = Report.Load()?.Groups.FirstOrDefault(x => x.Key == key);
				if (g == null || index < 0 || index >= g.Items.Count) return Results.NotFound();
				byte[]? jpeg = Thumbnails.Get(g.Items[index], g.Media == "image");
				if (jpeg == null) return Results.NotFound();
				ctx.Response.Headers.CacheControl = "private, max-age=3600";
				return Results.Bytes(jpeg, "image/jpeg");
			});

			app.MapPost("/api/groups/{key}/recycle", (string key, RecycleRequest request) => {
				ReportGroup? g = Report.Load()?.Groups.FirstOrDefault(x => x.Key == key);
				if (g == null) return Results.NotFound(new { error = "That group is no longer in the report; scan again." });
				if (!ValidBatch(request.Batch, request.Folder)) return Results.BadRequest(new { error = "Unknown batch." });
				return Guarded(() => Results.Json(actions.Recycle(g, request.Paths ?? new(), request.Batch, request.Folder), AgentConfig.Json));
			});
			app.MapPost("/api/groups/{key}/keep", (string key) => Guarded(() => {
				using (CleanLock.Acquire()) DecisionStore.Record(cfg, key, new Decision("kept", DateTime.UtcNow, new(), 0));
				return Results.Ok();
			}));
			app.MapPost("/api/groups/{key}/reopen", (string key) => Guarded(() => {
				using (CleanLock.Acquire()) DecisionStore.Set(key, null);
				return Results.Ok();
			}));
			// "Skip all" on a folder's look-alikes: every set kept as it is, as one History row.
			app.MapPost("/api/groups/skip", (SkipRequest request) => Guarded(() => {
				if (!ValidBatch(request.Batch, request.Folder)) return Results.BadRequest(new { error = "Unknown batch." });
				var inReport = (Report.Load()?.Groups ?? new()).Select(g => g.Key).ToHashSet();
				var now = DateTime.UtcNow;
				int skipped;
				using (CleanLock.Acquire()) {
					var open = DecisionStore.Load();
					var keys = (request.Keys ?? new()).Distinct().Where(k => inReport.Contains(k) && !open.ContainsKey(k)).ToList();
					DecisionStore.SetMany(cfg, keys.Select(k => (k, new Decision("kept", now, new(), 0, Batch: request.Batch, Folder: request.Folder))));
					skipped = keys.Count;
				}
				AgentPaths.AppendLog($"skipped {skipped} look-alike set(s)");
				return Results.Json(new { skipped });
			}));
			app.MapPost("/api/history/reopen", (BatchRequest request) => Guarded(() => {
				if (!ValidBatch(request.Batch, null)) return Results.BadRequest(new { error = "Unknown batch." });
				int reopened;
				using (CleanLock.Acquire()) reopened = DecisionStore.RemoveBatch(request.Batch, "kept");
				return Results.Json(new { reopened });
			}));
			app.MapPost("/api/history/clear", () => Guarded(() => {
				int cleared;
				using (CleanLock.Acquire()) cleared = DecisionStore.ClearHistory();
				AgentPaths.AppendLog($"history cleared ({cleared} entries)");
				return Results.Json(new { cleared });
			}));
			// The page's own settings: history on or off, and how hard scans work.
			app.MapPost("/api/settings", (SettingsRequest request) => {
				if (request.ScanSpeed != null && !AgentConfig.ScanSpeeds.Contains(request.ScanSpeed)) return Results.BadRequest(new { error = "Unknown scan speed." });
				AgentConfig saved = AgentConfig.Load();
				if (request.KeepHistory is bool keep) saved.KeepHistory = cfg.KeepHistory = keep;
				if (request.ScanSpeed is string speed) saved.ScanSpeed = cfg.ScanSpeed = speed;
				saved.Save();
				AgentPaths.AppendLog($"settings: history {(saved.KeepHistory ? "kept" : "off")}, scans " +
					(saved.AlwaysFullSpeed ? "always at full speed" : saved.AlwaysInBackground ? "always in the background" : "at full speed when you're here"));
				return Results.Json(new { saved.KeepHistory, saved.ScanSpeed }, AgentConfig.Json);
			});
			// The Store version's first run: the page's answers, installed in the background (StoreSetup).
			app.MapPost("/api/setup", (SetupRequest request) => {
				if (!StoreSetup.Needed) return Results.Conflict(new { error = "Heiward is set up already." });
				// What the install writes to settings.json, which this page read before it ran.
				void Reload() {
					AgentConfig saved = AgentConfig.Load();
					cfg.AiDevice = saved.AiDevice;
					cfg.ScanEveryMinutes = saved.ScanEveryMinutes;
					cfg.ScanOnBattery = saved.ScanOnBattery;
					cfg.ScanSpeed = saved.ScanSpeed;
				}
				return StoreSetup.Start(request, Reload) is string error ? Results.BadRequest(new { error }) : Results.Accepted();
			});
			// Right-click "Include in scans" / "Leave out of scans", saved to folders / excludeFolders.
			app.MapPost("/api/folders/override", (FolderOverrideRequest request) => {
				AgentConfig saved = AgentConfig.Load();
				OverrideResult result = request.Include ? FolderOverride.Include(saved, request.Path ?? "", request.RemoveRule) : FolderOverride.Exclude(saved, request.Path ?? "");
				if (result.Error != null)
					return result.Rule != null ? Results.Conflict(new { error = result.Error, rule = result.Rule }) : Results.BadRequest(new { error = result.Error });
				saved.Save();
				cfg.Folders = saved.Folders;
				cfg.ExcludeFolders = saved.ExcludeFolders;
				AgentPaths.AppendLog($"folder {(request.Include ? "included" : "left out")} on the page: {request.Path}");
				return Results.Json(result, AgentConfig.Json);
			});
			// The last check, and the projects the user bundled repositories into (read fresh: they're edited here).
			app.MapGet("/api/dev", () => Results.Json(new { report = DevReport.Load() ?? new DevReport(), projects = AgentConfig.Load().DevProjects }, AgentConfig.Json));
			app.MapPost("/api/dev/projects", (List<DevProject> projects) => {
				var saved = AgentConfig.Load();
				saved.DevProjects = DevProject.Normalize(projects);
				saved.Save();
				return Results.Json(saved.DevProjects, AgentConfig.Json);
			});
			app.MapPost("/api/dev/scan", () => {
				if (DevScan.IsRunning()) return Results.Conflict(new { error = "A developer check is already running." });
				StartDetached("dev", "--scan");
				return Results.Accepted();
			});
			app.MapPost("/api/dev/items/{id}/clean", (string id) => {
				DevItem? item = DevReport.Load()?.Categories.SelectMany(c => c.Items).FirstOrDefault(i => i.Id == id);
				if (item == null) return Results.NotFound(new { error = "That item is no longer in the list; check again." });
				if (item.Blocked != null) return Results.Conflict(new { error = item.Blocked });
				return Guarded(() => Results.Json(actions.Clean(item), AgentConfig.Json));
			});
			app.MapPost("/api/dev/repos/{id}/prune", (string id) => {
				RepoBranches? repo = DevReport.Load()?.Repositories.FirstOrDefault(r => r.Id == id);
				if (repo == null) return Results.NotFound(new { error = "That repository is no longer in the list; check again." });
				return Guarded(() => Results.Json(actions.Prune(repo, null), AgentConfig.Json));
			});
			// Automatic cleanup: its settings (saved to settings.json), and "leave it" / "allow" per thing.
			app.MapPost("/api/auto/settings", (AutoCleanConfig wanted) => Guarded(() => {
				AutoCleanConfig next = wanted.Normalized();
				AgentConfig saved = AgentConfig.Load();
				saved.AutoClean = next;
				saved.Save();
				cfg.AutoClean = next;
				AutoCleanState.Update(s => AutoCleaner.SyncSince(next, s, DateTime.UtcNow));
				AgentPaths.AppendLog($"automatic cleanup set: duplicates {(next.Duplicates ? "on" : "off")}, developer " +
					(next.Developer ? $"on ({string.Join(", ", next.DeveloperKinds)})" : "off") + $", after {next.AfterDays} day(s)");
				return Results.Json(next, AgentConfig.Json);
			}));
			app.MapPost("/api/auto/hold", (AutoHoldRequest request) => Guarded(() => {
				if (request.Target == null || !AutoTarget.IsMatch(request.Target)) return Results.BadRequest(new { error = "Unknown item." });
				AutoCleanState.Update(s => { if (request.Hold) s.Held.Add(request.Target); else s.Held.Remove(request.Target); });
				return Results.Ok();
			}));
			app.MapPost("/api/auto/allow", (AutoAllowRequest request) => Guarded(() => {
				if (request.Pair is not { Length: > 2 and < 2000 } pair || !pair.Contains('|')) return Results.BadRequest(new { error = "Unknown folders." });
				string key = pair.ToLowerInvariant();
				AutoCleanState.Update(s => { if (request.Allow) s.AllowedFolderPairs.Add(key); else s.AllowedFolderPairs.Remove(key); });
				return Results.Ok();
			}));
			// The agent: stop the scan that's running, pause scheduled scans for a while (and stop the running
			// one), resume them, or register the tasks again when they're gone or disabled.
			app.MapPost("/api/scan/stop", () => {
				if (!AgentScanner.IsRunning()) return Results.Conflict(new { error = "No scan is running." });
				ScanStop.Request();
				return Results.Accepted();
			});
			app.MapPost("/api/agent/pause", (PauseRequest request) => {
				if (request.Minutes is < 1 or > AgentPause.MaxMinutes) return Results.BadRequest(new { error = "Pause for 1 minute to a week, or until you resume." });
				AgentPause.Start(request.Minutes, DateTime.UtcNow);
				if (AgentScanner.IsRunning()) ScanStop.Request();
				return Results.Json(AgentView(cfg), AgentConfig.Json);
			});
			app.MapPost("/api/agent/resume", () => {
				AgentPause.Resume();
				return Results.Json(AgentView(cfg), AgentConfig.Json);
			});
			app.MapPost("/api/agent/schedule", () => {
				if (Installer.RegisterTasks(AgentConfig.Load()) is string error) return Results.Conflict(new { error });
				AgentPause.Resume();
				AgentPaths.AppendLog("scheduled scans turned back on from the review page");
				return Results.Json(AgentView(cfg), AgentConfig.Json);
			});
			app.MapPost("/api/scan", () => {
				if (AgentScanner.IsRunning()) return Results.Conflict(new { error = "A scan is already running." });
				if (StoreSetup.Needed) return Results.Conflict(new { error = "Set Heiward up first: the setup starts the first scan." });
				StartDetached("scan");
				return Results.Accepted();
			});

			var lifetime = app.Lifetime;
			_ = Task.Run(async () => {
				while (!lifetime.ApplicationStopping.IsCancellationRequested) {
					await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
					if (Environment.TickCount64 - Interlocked.Read(ref lastSeen) > cfg.ServerIdleMinutes * 60_000L && !AgentScanner.IsRunning() && !StoreSetup.Running) {
						AgentPaths.AppendLog("review page idle, stopping");
						lifetime.StopApplication();
					}
				}
			}, CancellationToken.None);

			await app.StartAsync(ct);
			Console.WriteLine($"Review page: {PageUrl(port)}");
			AgentPaths.AppendLog($"review page up on port {port}");
			RescanAfterUpdate();
			if (openBrowser) OpenBrowser(port);
			await app.WaitForShutdownAsync(ct);
			return 0;
		}

		/// <summary>A set, developer item or repository automatic cleanup can be told to leave (see <see cref="AutoCleanState.Held"/>).</summary>
		static readonly Regex AutoTarget = new("^[gdb]:[0-9a-f]{16}$", RegexOptions.CultureInvariant);

		static readonly Regex BatchId = new("^[0-9a-f]{8,32}$", RegexOptions.CultureInvariant);

		/// <summary>No batch, or the page's random id with the full path of the folder it was done in.</summary>
		static bool ValidBatch(string? batch, string? folder) =>
			batch == null ? folder == null : BatchId.IsMatch(batch) && (folder == null || folder.Length < 1024 && Path.IsPathFullyQualified(folder));

		/// <summary>A cleanup endpoint: another cleanup holding the lock too long is a 409, not a crash.</summary>
		static IResult Guarded(Func<IResult> action) {
			try { return action(); }
			catch (TimeoutException e) { return Results.Conflict(new { error = e.Message }); }
		}

		/// <summary>
		/// A new build of Heiward (an update, from GitHub or the Store) sets the last report aside, since
		/// its sets were judged by the old build's rules: a scan with this build finds them again. Not while
		/// scans are paused, and not before the Store version's setup, which starts its own first scan.
		/// </summary>
		static void RescanAfterUpdate() {
			if (!Report.IsStale() || AgentScanner.IsRunning() || StoreSetup.Needed || AgentPause.Load() != null) return;
			AgentPaths.AppendLog($"Heiward {AppBuild.Current} set aside the report of {Report.LoadAny()?.Build ?? "an older build"}: scanning again");
			StartDetached("scan");
		}

		/// <summary>Everything the page draws, in one poll.</summary>
		static object State(AgentConfig cfg) {
			Report? report = Report.Load();
			ScanIndex? index = ScanIndex.Load();
			var decisions = DecisionStore.Load();
			DevReport? devReport = cfg.DeveloperModeOn ? DevReport.Load() : null;
			var groups = report?.Groups ?? new();
			var pending = groups.Where(g => !decisions.ContainsKey(g.Key)).ToList();
			var byKey = groups.DistinctBy(g => g.Key).ToDictionary(g => g.Key);
			// The History: newest first, a folder-wide action (a batch) as one row, cleared entries left out.
			var done = decisions
				.Where(d => !d.Value.Unlisted)
				.GroupBy(d => d.Value.Batch != null ? "batch:" + d.Value.Batch + ":" + d.Value.Action : "key:" + d.Key)
				.Select(rows => {
					var d = rows.MaxBy(r => r.Value.AtUtc);
					ReportGroup? g = rows.Count() == 1 ? byKey.GetValueOrDefault(d.Key) : null;
					return new {
						key = d.Key, batch = d.Value.Batch, folder = d.Value.Folder, sets = rows.Count(),
						action = d.Value.Action, atUtc = d.Value.AtUtc, recycled = rows.Sum(r => r.Value.Recycled.Count), recycledBytes = rows.Sum(r => r.Value.RecycledBytes),
						kind = g?.Kind, keepName = g?.Items.FirstOrDefault(i => i.Keep)?.Name, inReport = rows.Any(r => byKey.ContainsKey(r.Key)),
						label = d.Value.Action is "dev-cleaned" or "branches-pruned" ? d.Value.Recycled.FirstOrDefault() : null,
						auto = d.Value.Auto,
					};
				})
				.OrderByDescending(r => r.atUtc)
				.Take(100)
				.ToList();
			return new {
				report = report == null ? null : new {
					report.ScannedAtUtc, report.DurationSec, report.Device, report.FilesScanned, report.Folders, report.ExcludedExtensions, report.Notes,
				},
				// The last report is another build's, set aside until a scan with this one (RescanAfterUpdate).
				updated = report == null && Report.IsStale(),
				pending,
				done,
				totals = new {
					groups = pending.Count,
					similar = pending.Count(g => g.Kind == "similar"),
					reclaimableBytes = pending.Sum(g => g.ReclaimBytes),
					recycledBytes = decisions.Values.Sum(d => d.RecycledBytes),
					decisions = decisions.Count,
				},
				dev = DevSummary(cfg, devReport),
				auto = AutoView(report, devReport, decisions),
				drives = ExplorerView.Drives(cfg, index, pending),
				hotspots = ExplorerView.Hotspots(pending, 6),
				scan = new { running = AgentScanner.IsRunning(), status = AgentScanner.ReadStatus() },
				setup = StoreSetup.View(),
				agent = AgentView(cfg),
				ai = AiStatus.Load(),
				schedule = new { next = Scheduler.NextRun(), everyMinutes = cfg.ScanEveryMinutes },
				config = new {
					folders = ScanScope.Roots(cfg), allDrives = cfg.ScanAllDrives, cfg.ExcludeExtensions, cfg.AiDevice, path = AgentPaths.Config,
					cfg.KeepHistory, cfg.ScanSpeed, fullSpeedCores = cfg.ParallelismFor(true), backgroundCores = cfg.ParallelismFor(false),
				},
			};
		}

		/// <summary>
		/// Whether Heiward scans on its own: paused (and until when), or with its scan task gone or disabled in
		/// Task Scheduler although settings.json asks for scheduled scans.
		/// </summary>
		static object AgentView(AgentConfig cfg) {
			DateTime now = DateTime.UtcNow;
			AgentPause? pause = AgentPause.Load(now);
			return new {
				paused = pause != null,
				pausedUntilUtc = pause?.UntilUtc,
				pausedText = pause?.Describe(now),
				scheduleMissing = cfg.ScanEveryMinutes > 0 && Scheduler.NextRun() == null,
				stopping = AgentScanner.IsRunning() && ScanStop.Requested(AgentScanner.ReadStatus()?.StartedUtc ?? DateTime.MinValue),
			};
		}

		/// <summary>The home page's developer card: totals per category of the last check.</summary>
		static object DevSummary(AgentConfig cfg, DevReport? r) {
			return new {
				enabled = cfg.DeveloperModeOn,
				running = cfg.DeveloperModeOn && DevScan.IsRunning(),
				scannedAtUtc = r?.ScannedAtUtc,
				totalBytes = r?.Categories.SelectMany(c => c.Items).Sum(i => i.Bytes) ?? 0,
				suggestedBytes = r?.Categories.SelectMany(c => c.Items).Where(i => i.Suggested).Sum(i => i.Bytes) ?? 0,
				// A category emptied by cleaning since the check has nothing to show.
				categories = r?.Categories.Where(c => c.Items.Count > 0).Select(c => new { c.Key, c.Title, bytes = c.Items.Sum(i => i.Bytes), count = c.Items.Count }).ToList(),
			};
		}

		/// <summary>Automatic cleanup for the page: the settings, what happens to each thing and when, and the last run.</summary>
		static object AutoView(Report? report, DevReport? dev, Dictionary<string, Decision> decisions) {
			AgentConfig fresh = AgentConfig.Load(); // settings.json, as the page or the user last left it
			AutoCleanState s = AutoCleanState.Load();
			AutoPlan plan = AutoCleaner.Plan(fresh, report, dev, decisions, s, DateTime.UtcNow);
			static object Upcoming(IEnumerable<AutoPlanEntry> entries) {
				var due = entries.Where(e => e.DueUtc != null).ToList();
				return new { count = due.Count, files = due.Sum(e => e.Count), bytes = due.Sum(e => e.Bytes), firstDueUtc = due.Min(e => e.DueUtc) };
			}
			return new {
				settings = fresh.AutoClean,
				fresh.StaleProjectDays,
				fresh.TempOlderThanDays,
				groups = plan.Groups,
				devItems = plan.DevItems,
				repos = plan.Repos,
				upcoming = new { groups = Upcoming(plan.Groups.Values), dev = Upcoming(plan.DevItems.Values), branches = Upcoming(plan.Repos.Values) },
				lastRun = s.Runs.FirstOrDefault(),
			};
		}

		static string Asset(string name) {
			using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/" + name)
				?? throw new FileNotFoundException(name);
			using var r = new StreamReader(s);
			return r.ReadToEnd();
		}

		/// <param name="fromStore">Only the Store version's page counts (a GitHub copy's says "store":false, or nothing).</param>
		public static async Task<bool> IsUpAsync(int port, bool fromStore = false) {
			try {
				using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
				string body = await http.GetStringAsync($"http://127.0.0.1:{port}/api/ping");
				return body.Contains("\"heiward\"", StringComparison.Ordinal) && (!fromStore || body.Contains("\"store\":true", StringComparison.Ordinal));
			}
			catch { return false; }
		}

		/// <summary>Starts the review page in its own process (no window) unless it is already up.</summary>
		public static void EnsureRunningInBackground(AgentConfig cfg) {
			if (IsUpAsync(cfg.Port).GetAwaiter().GetResult()) return;
			StartDetached("serve", "--no-browser");
		}

		static void StartDetached(params string[] args) {
			var psi = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "hei.exe")) { UseShellExecute = false, CreateNoWindow = true };
			foreach (string a in args) psi.ArgumentList.Add(a);
			Process.Start(psi);
		}

		/// <summary>The name the page goes by: Heiward's own, on this PC only (see the Host check).</summary>
		public const string HostName = "heiward.localhost";

		public static string PageUrl(int port) => $"http://{HostName}:{port}/";

		public static void OpenBrowser(int port) =>
			Process.Start(new ProcessStartInfo(PageUrl(port)) { UseShellExecute = true });
	}
}
