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

namespace VDF.Agent {
	sealed record RecycleRequest(List<string> Paths);

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
		static readonly object recycleGate = new();

		public static async Task<int> RunAsync(AgentConfig cfg, bool openBrowser, CancellationToken ct) {
			int port = cfg.Port;
			if (await IsUpAsync(port)) {
				Console.WriteLine($"The review page is already running: http://127.0.0.1:{port}/");
				if (openBrowser) OpenBrowser(port);
				return 0;
			}
			string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
			var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"127.0.0.1:{port}", $"localhost:{port}" };

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
			app.MapGet("/app.js", () => Results.Content(Asset("app.js"), "text/javascript; charset=utf-8"));
			app.MapGet("/app.css", () => Results.Content(Asset("app.css"), "text/css; charset=utf-8"));
			app.MapGet("/api/ping", () => Results.Json(new { app = "vdf-agent" }));
			app.MapGet("/api/state", () => Results.Json(State(cfg), AgentConfig.Json));

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
				RecycleResult result;
				lock (recycleGate) {
					result = Recycler.Recycle(g, request.Paths ?? new());
					if (result.Recycled.Count > 0)
						DecisionStore.Set(key, new Decision("recycled", DateTime.UtcNow, result.Recycled, result.RecycledBytes));
				}
				return Results.Json(result, AgentConfig.Json);
			});
			app.MapPost("/api/groups/{key}/keep", (string key) => {
				DecisionStore.Set(key, new Decision("kept", DateTime.UtcNow, new(), 0));
				return Results.Ok();
			});
			app.MapPost("/api/groups/{key}/reopen", (string key) => {
				DecisionStore.Set(key, null);
				return Results.Ok();
			});
			app.MapPost("/api/scan", () => {
				if (AgentScanner.IsRunning()) return Results.Conflict(new { error = "A scan is already running." });
				StartDetached("scan");
				return Results.Accepted();
			});

			var lifetime = app.Lifetime;
			_ = Task.Run(async () => {
				while (!lifetime.ApplicationStopping.IsCancellationRequested) {
					await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
					if (Environment.TickCount64 - Interlocked.Read(ref lastSeen) > cfg.ServerIdleMinutes * 60_000L && !AgentScanner.IsRunning()) {
						AgentPaths.AppendLog("review page idle, stopping");
						lifetime.StopApplication();
					}
				}
			}, CancellationToken.None);

			await app.StartAsync(ct);
			Console.WriteLine($"Review page: http://127.0.0.1:{port}/");
			AgentPaths.AppendLog($"review page up on port {port}");
			if (openBrowser) OpenBrowser(port);
			await app.WaitForShutdownAsync(ct);
			return 0;
		}

		/// <summary>Everything the page draws, in one poll.</summary>
		static object State(AgentConfig cfg) {
			Report? report = Report.Load();
			var decisions = DecisionStore.Load();
			var groups = report?.Groups ?? new();
			var pending = groups.Where(g => !decisions.ContainsKey(g.Key)).ToList();
			var done = decisions
				.OrderByDescending(d => d.Value.AtUtc)
				.Take(100)
				.Select(d => {
					ReportGroup? g = groups.FirstOrDefault(x => x.Key == d.Key);
					return new {
						key = d.Key, action = d.Value.Action, atUtc = d.Value.AtUtc, recycled = d.Value.Recycled.Count, recycledBytes = d.Value.RecycledBytes,
						kind = g?.Kind, keepName = g?.Items.FirstOrDefault(i => i.Keep)?.Name, inReport = g != null,
					};
				}).ToList();
			return new {
				report = report == null ? null : new {
					report.ScannedAtUtc, report.DurationSec, report.Device, report.FilesScanned, report.Folders, report.ExcludedExtensions, report.Notes,
				},
				pending,
				done,
				totals = new {
					groups = pending.Count,
					similar = pending.Count(g => g.Kind == "similar"),
					reclaimableBytes = pending.Sum(g => g.ReclaimBytes),
					recycledBytes = decisions.Values.Sum(d => d.RecycledBytes),
				},
				scan = new { running = AgentScanner.IsRunning(), status = AgentScanner.ReadStatus() },
				schedule = new { next = Scheduler.NextRun(), everyMinutes = cfg.ScanEveryMinutes },
				config = new { cfg.Folders, cfg.ExcludeExtensions, cfg.AiDevice, path = AgentPaths.Config },
			};
		}

		static string Asset(string name) {
			using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/" + name)
				?? throw new FileNotFoundException(name);
			using var r = new StreamReader(s);
			return r.ReadToEnd();
		}

		public static async Task<bool> IsUpAsync(int port) {
			try {
				using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
				string body = await http.GetStringAsync($"http://127.0.0.1:{port}/api/ping");
				return body.Contains("vdf-agent", StringComparison.Ordinal);
			}
			catch { return false; }
		}

		/// <summary>Starts the review page in its own process (no window) unless it is already up.</summary>
		public static void EnsureRunningInBackground(AgentConfig cfg) {
			if (IsUpAsync(cfg.Port).GetAwaiter().GetResult()) return;
			StartDetached("serve", "--no-browser");
		}

		static void StartDetached(params string[] args) {
			var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "vdf-agent.exe")) { UseShellExecute = false, CreateNoWindow = true };
			foreach (string a in args) psi.ArgumentList.Add(a);
			Process.Start(psi);
		}

		public static void OpenBrowser(int port) =>
			Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/") { UseShellExecute = true });
	}
}
