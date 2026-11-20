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
using System.Text.Json;
using System.Text.Json.Nodes;
using HEI.Core.AI;

namespace HEI.Agent.Estate {
	/// <summary>
	/// Another of the person's PCs scans this one's photos and videos for it, over the Estate: a PC whose NPU can, for one
	/// that has none (or a slow one). This PC starts it, from its page; it only lists its files and hands over their bytes,
	/// gently (<see cref="PeerFiles"/>), and the other PC does the rest and sends back what it found, which this PC's page
	/// shows and cleans up with its own buttons. Both PCs run the same build: another build's rules judge sets otherwise.
	/// <para>The routes, each under <c>/api/peer</c> (only through Castellan, naming the asking PC):</para>
	/// <list type="bullet">
	/// <item>The PC that scans: <c>scan</c> (whether it can now, its build and NPU), <c>scan/out</c> (the loan's results:
	/// how the scan is getting on, then that it's done), <c>findings</c> (what it found, for the PC asking only).</item>
	/// <item>The PC scanned: <c>scan/in</c> (the work: which listing), <c>scan/listing</c> (its files), <c>scan/bytes</c>
	/// (their bytes), each only for the PC it asked, while it asks.</item>
	/// </list>
	/// </summary>
	static class ScanForOthers {
		public const string Route = "scan";
		const int Version = 1;

		// ---- A game here: neither side's work goes on while one is played. As the kit's game check (PEER-FILES.md, Gently):
		// its answer stands for 30 s, one check at a time is asked behind it however many ask, and a failed reading says no game.
		static readonly object gameGate = new();
		static ThreeDWatch? games;
		static (long At, string? Game)? lastGame;
		static int checkingGame;

		internal static string? GameNow() {
			var last = lastGame;
			if (last is { } l && Environment.TickCount64 - l.At < PeerFiles.GameKeepMs) return l.Game;
			if (last == null) {
				CheckGame(); // the first answer is waited for
				return lastGame?.Game;
			}
			if (Interlocked.Exchange(ref checkingGame, 1) == 0)
				_ = Task.Run(() => {
					try { CheckGame(); }
					finally { Volatile.Write(ref checkingGame, 0); }
				});
			return last.Value.Game; // the last answer, until the next is in
		}

		static void CheckGame() {
			lock (gameGate) {
				string? playing;
				try { playing = (games ??= new ThreeDWatch()).Busy(); }
				catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { playing = null; }
				lastGame = (Environment.TickCount64, playing);
			}
		}

		// ================= What the page shows =================

		/// <summary>The other PCs on the Estate that can scan this one now (same build), and those that could once Heiward there is updated.</summary>
		static (long At, List<object> Can, List<string> Update)? offers;
		static int refreshing;

		/// <summary>
		/// For the page's state: who could scan this PC (asked of the other PCs at most once a minute, in the background), the
		/// scan asked for, the scan this PC is doing for another, and what another PC found here. Null when there's none of it:
		/// free Heiward, with no Castellan, never shows any.
		/// </summary>
		internal static object? View() {
			if (Volatile.Read(ref refreshing) == 0 && (offers is not { } o || Environment.TickCount64 - o.At > 60_000)) _ = Task.Run(RefreshOffersAsync);
			var known = offers;
			Asked? a = asked;
			ForeignFindings? found = ForeignFindings.LoadAny();
			var lent = ScanningFor;
			if (known is not { Can.Count: > 0 } && known is not { Update.Count: > 0 } && a == null && found == null && lent == null) return null;
			return new {
				others = known?.Can ?? [],
				updateOn = known?.Update ?? [],
				asked = a == null ? null : new {
					pc = a.Lender.Id, name = a.Lender.Name, stage = a.Stage, done = a.Done, total = a.Total, fetchedBytes = a.Fetched,
					waiting = a.Waiting, finished = a.Finished, problem = a.Problem, sets = a.Sets, startedUtc = a.StartedUtc,
				},
				scanningFor = lent is { } l ? new { name = l.To.Name, sinceUtc = l.Since, linked = l.Linked } : null,
				found = found == null ? null : new { from = found.FromName, atUtc = found.ScannedAtUtc, sets = found.Groups.Count, setAside = found.Build != AppBuild.Current },
			};
		}

		static async Task RefreshOffersAsync() {
			if (Interlocked.Exchange(ref refreshing, 1) == 1) return;
			try {
				var can = new List<object>();
				var update = new List<string>();
				if (await EstateLink.ReadEstateAsync(CancellationToken.None) is { On: true } estate)
					foreach (EstateLink.EstatePc pc in estate.Pcs.Where(p => p.Here && p.HasHeiward != false)) {
						try {
							var (status, json) = await EstateLink.GetJsonAsync(EstateLink.PeerPath(pc.Id, Route), TimeSpan.FromSeconds(5), CancellationToken.None, limit: 64 * 1024);
							using (json) {
								if (status != 200 || json?.RootElement is not { ValueKind: JsonValueKind.Object } r) continue;
								bool able = r.TryGetProperty("can", out JsonElement c) && c.ValueKind == JsonValueKind.True;
								string? build = EstateLink.Str(r, "build");
								if (able && build == AppBuild.Current) can.Add(new { id = pc.Id, name = pc.Name, npu = EstateLink.Str(r, "npu") });
								else if (able && build != null) update.Add(pc.Name);
							}
						}
						catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or EstateMissing) { }
					}
				offers = (Environment.TickCount64, can, update);
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or EstateMissing or IOException) {
				offers = (Environment.TickCount64, [], []);
			}
			finally {
				Volatile.Write(ref refreshing, 0);
			}
		}

		// ================= This PC asks another to scan it =================

		/// <summary>The scan this PC asked another PC for, as its page shows it.</summary>
		internal sealed class Asked {
			public required Pc Lender { get; init; }
			public DateTime StartedUtc { get; } = DateTime.UtcNow;
			public string Stage { get; set; } = "Listing this PC's photos and videos";
			public int Done { get; set; }
			public int Total { get; set; }
			public long Fetched { get; set; }
			public string? Waiting { get; set; }
			public bool Finished { get; set; }
			public string? Problem { get; set; }
			public int? Sets { get; set; }
		}

		static readonly object gate = new();
		static Borrowing? borrowing;
		static Asked? asked;
		static RemoteListing? sent;
		static byte[]? sentJson;
		static Dictionary<string, ListedFile>? sentFiles;
		static PeerFiles? serving;
		static TimeSpan listCpu;

		/// <summary>The scan asked for now (or last), for the page.</summary>
		internal static Asked? Now => asked;

		/// <summary>What handing the files over has cost this PC so far: bytes, and the processor time of listing them and reading them.</summary>
		internal static (long Bytes, TimeSpan Cpu) Cost => (serving?.Served ?? 0, listCpu + (serving?.Cpu ?? TimeSpan.Zero));

		static Borrowing Borrow => borrowing ??= new Borrowing(Route, OnResult, OnEnded);

		/// <summary>
		/// The page's "Have &lt;PC&gt; scan this PC": asks <paramref name="pc"/> whether it can (and runs this build), lists this
		/// PC's files as its scans see them, and lends it the listing. Why not, in words, or null once it's under way.
		/// </summary>
		internal static async Task<string?> AskAsync(string pc, string name, CancellationToken ct) {
			lock (gate) {
				if (asked is { Finished: false }) return $"{asked.Lender.Name} is already scanning this PC.";
				asked = new Asked { Lender = new Pc(pc, name) };
			}
			Asked a = asked;
			try {
				var (status, json) = await EstateLink.GetJsonAsync(EstateLink.PeerPath(pc, Route), TimeSpan.FromSeconds(15), ct, limit: 64 * 1024);
				using (json) {
					string? why = status == 404 ? $"Heiward on {name} is too old for this: update Heiward there."
						: status != 200 || json?.RootElement is not { ValueKind: JsonValueKind.Object } offer ? $"{name} didn't answer."
						: EstateLink.Str(offer, "build") is string build && build != AppBuild.Current ? Versions(name, build)
						: offer.TryGetProperty("can", out JsonElement can) && can.ValueKind == JsonValueKind.True ? null
						: $"{name} can't now: {EstateLink.Str(offer, "why") ?? "it didn't say why"}.";
					if (why != null) return Fail(a, why);
				}
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or EstateMissing && !ct.IsCancellationRequested) {
				return Fail(a, e is EstateMissing ? "Castellan isn't running on this PC." : $"{name} didn't answer.");
			}
			_ = Task.Run(() => ListAndLendAsync(a));
			return null;
		}

		static string Versions(string name, string theirs) =>
			$"Heiward on {name} is another version ({Short(theirs)}, this PC has {Short(AppBuild.Current)}): update Heiward on both PCs.";

		static string Short(string build) => build.Split('+')[0];

		static string Fail(Asked a, string why) {
			a.Problem = why;
			a.Finished = true;
			return why;
		}

		/// <summary>This PC's files listed (<c>hei scan --list-only</c>, from the change journals), then the loan begun with it.</summary>
		static async Task ListAndLendAsync(Asked a) {
			try {
				var timer = Stopwatch.StartNew();
				using (Process? list = StartHei(null, "scan", "--list-only")) {
					if (list == null) {
						Fail(a, "Heiward couldn't list this PC's files.");
						return;
					}
					await list.WaitForExitAsync();
					try { listCpu = list.TotalProcessorTime; }
					catch (InvalidOperationException) { }
					if (list.ExitCode != 0) {
						Fail(a, list.ExitCode == 1 ? "A scan is running on this PC: try again when it's done." : "Heiward couldn't list this PC's files.");
						return;
					}
				}
				if (a != asked || a.Finished) return;
				var notes = new List<string>();
				var roots = AgentScanner.BuildSettings(AgentConfig.Load(), notes).IncludeList.ToList();
				RemoteListing listing = RemoteListing.FromKept(ListingPlan.Kept(roots), AppBuild.Current);
				int files = listing.Roots.Sum(r => r.Files.Count);
				AgentPaths.AppendLog($"listed {files:N0} photos and videos for {a.Lender.Name}'s scan in {timer.Elapsed.TotalSeconds:N1} s ({listCpu.TotalSeconds:N1} s of the processor)");
				lock (gate) {
					sent = listing;
					sentJson = listing.ToJson();
					sentFiles = listing.Roots.SelectMany(r => r.Files).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
					// Its files, for that PC alone, while it scans this one (the kit's PeerFiles).
					Pc lender = a.Lender;
					serving = new PeerFiles(GameNow) { May = pc => pc.Id == lender.Id && borrowing?.Lender?.Id == lender.Id };
					serving.Replace(sentFiles.Keys);
					a.Stage = $"Waiting for {a.Lender.Name}";
					a.Total = files;
					Borrow.Start(a.Lender);
					Borrow.Offer(new JsonObject { ["scan"] = new JsonObject { ["v"] = Version, ["build"] = AppBuild.Current, ["stamp"] = listing.Stamp, ["files"] = files } }, head: true);
				}
			}
			catch (Exception e) {
				AgentPaths.AppendLog("listing for another PC's scan failed: " + e);
				Fail(a, "Heiward couldn't list this PC's files: " + e.Message);
			}
		}

		/// <summary>A line from the PC scanning: how it's getting on, or that it's done (its findings are pulled then).</summary>
		static void OnResult(JsonObject line) {
			Asked? a = asked;
			if (a == null || a.Finished) return;
			if (line["status"] is JsonObject s) {
				a.Stage = (string?)s["stage"] ?? a.Stage;
				a.Done = (int?)s["done"] ?? a.Done;
				a.Total = (int?)s["total"] is int t && t > 0 ? t : a.Total;
				a.Fetched = (long?)s["fetchedBytes"] ?? a.Fetched;
				a.Waiting = (string?)s["waiting"];
			}
			else if (line["done"] is JsonObject d) {
				if ((int?)d["code"] is int code && code != 0) {
					Stop(Fail(a, $"{a.Lender.Name}'s scan of this PC didn't finish: {(string?)d["error"] ?? (string?)d["stage"] ?? "code " + code}."));
					return;
				}
				a.Stage = "Fetching what it found";
				_ = Task.Run(() => PullFindingsAsync(a));
			}
		}

		static void OnEnded(string why) {
			Asked? a = asked;
			if (a is { Finished: false }) Fail(a, why);
			Released();
		}

		/// <summary>The page's Stop: the loan ends, and the other PC stops scanning this one.</summary>
		internal static void Stop(string? why = null) {
			lock (gate) {
				borrowing?.Stop();
				Released();
				if (asked is { Finished: false } a) Fail(a, why ?? "Stopped.");
			}
		}

		/// <summary>The loan is over: its files aren't offered any more, and what handing them over cost is logged (once).</summary>
		static void Released() {
			if (serving is not { Count: > 0 } s) return;
			s.Clear();
			if (s.Served > 0)
				AgentPaths.AppendLog($"handed {Format.Bytes(s.Served)} over for another PC's scan, {(listCpu + s.Cpu).TotalSeconds:N1} s of the processor in all " +
					$"({(listCpu + s.Cpu).TotalSeconds / Math.Max(1.0, s.Served / 1e9):N1} s per GB)");
		}

		/// <summary>The other PC's findings fetched, checked against what was listed to it, and kept for the page (<see cref="ForeignFindings"/>).</summary>
		static async Task PullFindingsAsync(Asked a) {
			try {
				var (status, json) = await EstateLink.GetJsonAsync(EstateLink.PeerPath(a.Lender.Id, "findings"), TimeSpan.FromMinutes(2), CancellationToken.None);
				using (json) {
					RemoteFindings? found = status == 200 && json != null ? json.RootElement.Deserialize<RemoteFindings>(AgentConfig.Json) : null;
					if (found is not { V: RemoteFindings.Version }) {
						Stop(Fail(a, $"{a.Lender.Name}'s findings didn't come."));
						return;
					}
					if (found.Build != AppBuild.Current) {
						Stop(Fail(a, Versions(a.Lender.Name, found.Build)));
						return;
					}
					Dictionary<string, ListedFile> listed = sentFiles ?? new(StringComparer.OrdinalIgnoreCase);
					ForeignFindings kept = ForeignFindings.From(a.Lender, found, listed);
					kept.Save();
					a.Sets = kept.Groups.Count;
					a.Stage = kept.Groups.Count == 0 ? "Done: no copies found" : $"Done: {kept.Groups.Count:N0} set(s) of copies found";
					a.Done = a.Total;
					a.Finished = true;
					// Nothing new here since its last scan: those findings stand, and nothing was fetched for them this time (made
					// before this ask, by that PC's clock, with a margin for the two clocks' difference).
					bool again = found.ScannedAtUtc < a.StartedUtc - TimeSpan.FromMinutes(2);
					AgentPaths.AppendLog(again ? $"{a.Lender.Name} found nothing new here since its scan of {found.ScannedAtUtc.ToLocalTime():g}: its {kept.Groups.Count:N0} set(s) stand"
						: $"{a.Lender.Name} scanned this PC: {kept.Groups.Count:N0} set(s)" + (found.Cost is { } c
							? $"; it fetched {Format.Bytes(c.FetchedBytes)} in {c.Asks:N0} asks and took {c.Seconds:N0} s" : ""));
				}
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException or EstateMissing or IOException) {
				Fail(a, $"{a.Lender.Name}'s findings didn't come: {e.Message}");
			}
			finally {
				lock (gate) {
					borrowing?.Stop();
					Released();
				}
			}
		}

		/// <summary>GET /api/peer/scan/in: the PC scanning this one pulls the work.</summary>
		internal static Task InRouteAsync(HttpContext ctx) => Borrow.InRouteAsync(ctx);

		/// <summary>Only the PC this one asked to scan it, while it does.</summary>
		static bool IsLender(HttpContext ctx) => EstateLink.AskerOf(ctx.Request) is { } asker && borrowing?.Lender?.Id == asker.Id && sent != null;

		/// <summary>GET /api/peer/scan/listing?have=…: this PC's files, or word that they're as listed at the stamp the other PC has.</summary>
		internal static async Task ListingRouteAsync(HttpContext ctx) {
			if (!IsLender(ctx)) {
				await NpuLoan.Json(410, new { error = "This PC isn't asking that PC to scan it now." }).ExecuteAsync(ctx);
				return;
			}
			RemoteListing listing = sent!;
			if (ctx.Request.Query["have"] == listing.Stamp) {
				await NpuLoan.Json(200, new { v = RemoteListing.Version, same = true, stamp = listing.Stamp }).ExecuteAsync(ctx);
				return;
			}
			ctx.Response.ContentType = "application/json";
			ctx.Response.Headers.CacheControl = "no-store";
			await ctx.Response.Body.WriteAsync(sentJson!, ctx.RequestAborted);
		}

		/// <summary>GET /api/peer/scan/bytes?path=…: a listed file's bytes, gently, as the kit's PeerFiles answers (none offered when no PC scans this one).</summary>
		internal static Task BytesRouteAsync(HttpContext ctx) => (serving ?? NoneOffered).ServeAsync(ctx);

		static readonly PeerFiles NoneOffered = new(() => null);

		// ================= This PC scans another's files =================

		static Lending? lending;
		static RemoteJob? job;

		static Lending Lend => lending ??= new Lending(Route, WhyNot, () => NpuComponents.NpuName, loan => job = new RemoteJob(loan));

		/// <summary>The PC this one is scanning for now, for its page.</summary>
		internal static (Pc To, DateTime Since, bool Linked)? ScanningFor => lending?.Now;

		/// <summary>Why this PC can't scan another's files now, in words for that PC's page; null when it can.</summary>
		static string? WhyNot() {
			AgentConfig cfg = AgentConfig.Load();
			AiDevice device = Enum.TryParse(cfg.AiDevice, ignoreCase: true, out AiDevice d) ? d : AiDevice.Auto;
			if (!AiComponents.IsReady || !NpuComponents.WillUseNpu(device)) return "it has no NPU Heiward uses";
			if (AgentScanner.IsRunning()) return "it's scanning its own files now";
			if (GameNow() is string game) return $"{game} is running on it";
			if (!RemoteMirror.CanHold(AgentPaths.Home)) return "Heiward's folder there is on a drive that can't hold the other PC's files";
			return null;
		}

		/// <summary>GET /api/peer/scan: whether this PC can scan the asking PC's files now, with its build (which must be the asker's).</summary>
		internal static async Task OfferRouteAsync(HttpContext ctx) {
			if (EstateLink.AskerOf(ctx.Request) is not { } asker) {
				await NpuLoan.Json(403, new { error = "Only another of your PCs, through Castellan, can ask." }).ExecuteAsync(ctx);
				return;
			}
			await Results.Json(Lend.Offer(asker, new { v = Version, build = AppBuild.Current })).ExecuteAsync(ctx);
		}

		/// <summary>GET /api/peer/scan/out?loan=…: the PC to scan asks for the results, which starts its scan here.</summary>
		internal static Task OutRouteAsync(HttpContext ctx) => Lend.OutRouteAsync(ctx);

		/// <summary>GET /api/peer/findings: what this PC's last scan of the asking PC's files found; only that PC's own.</summary>
		internal static async Task FindingsRouteAsync(HttpContext ctx) {
			if (EstateLink.AskerOf(ctx.Request) is not { } asker) {
				await NpuLoan.Json(403, new { error = "Only another of your PCs, through Castellan, can ask." }).ExecuteAsync(ctx);
				return;
			}
			string path = Path.Combine(RemoteScanner.HomeFor(asker.Id), "findings.json");
			if (!File.Exists(path)) {
				await NpuLoan.Json(404, new { error = "This PC hasn't scanned that PC's files.", code = "none" }).ExecuteAsync(ctx);
				return;
			}
			ctx.Response.ContentType = "application/json";
			ctx.Response.Headers.CacheControl = "no-store";
			await ctx.Response.SendFileAsync(path, ctx.RequestAborted);
		}

		/// <summary>Heiward's page is stopping: a scan for another PC ends, that PC told why; one asked of another PC ends too.</summary>
		internal static void Shutdown() {
			lending?.Stop();
			job?.Ended();
			Stop("Heiward stopped on this PC.");
		}

		/// <summary>
		/// The scan of another PC's files, for its loan: <c>hei remote-scan</c> in a process of its own, with that PC's home
		/// here, started by the loan's head; how it's getting on sent every 2 seconds, and that it's done once it ends.
		/// </summary>
		sealed class RemoteJob(ILoan loan) : ILoanWork {
			readonly object gate = new();
			Process? child;
			Timer? watch;
			string? lastStatus;
			bool ended;

			public void Line(JsonObject line, long? n) {
				if (line["scan"] is not JsonObject scan) return;
				lock (gate) {
					if (child != null || ended) return; // the head again, after a dropped link
					string? build = (string?)scan["build"];
					if ((int?)scan["v"] != Version || build != AppBuild.Current) {
						loan.End(build == null ? "that PC's Heiward is another version: update Heiward on both PCs" : Versions(Environment.MachineName, build));
						return;
					}
					string home = RemoteScanner.HomeFor(loan.From.Id);
					Directory.CreateDirectory(home);
					// This PC's own settings (its AI device, its graphics card), for the scan in that home.
					try { if (File.Exists(AgentPaths.Config)) File.Copy(AgentPaths.Config, Path.Combine(home, "settings.json"), overwrite: true); }
					catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
					TryDelete(RemoteStatus.FilePath(home));
					child = StartHei(home, "remote-scan", "--pc", loan.From.Id, "--name", loan.From.Name);
					if (child == null) {
						loan.End("Heiward couldn't start the scan there");
						return;
					}
					AgentPaths.AppendLog($"scanning {loan.From.Name}'s files for it (process {child.Id})");
					watch = new Timer(_ => Tick(home), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
				}
			}

			void Tick(string home) {
				lock (gate) {
					if (ended || child == null) return;
					RemoteStatus? s = RemoteStatus.Load(home);
					if (s != null && s.Pid == child.Id && JsonSerializer.Serialize(s, AgentConfig.Json) is var text && text != lastStatus) {
						lastStatus = text;
						if (JsonSerializer.SerializeToNode(s, AgentConfig.Json) is JsonObject o) loan.Send(new JsonObject { ["status"] = o });
					}
					if (!child.HasExited) return;
					watch?.Dispose();
					watch = null;
					var done = new JsonObject { ["code"] = child.ExitCode };
					if (s is { Pid: var pid } && pid == child.Id) {
						done["stage"] = s.Stage;
						if (s.Error != null) done["error"] = s.Error;
					}
					loan.Send(new JsonObject { ["done"] = done });
				}
			}

			/// <summary>The other PC ended it (or its link was gone too long), or Heiward stops here: the scan stops.</summary>
			public void Ended() {
				lock (gate) {
					ended = true;
					watch?.Dispose();
					watch = null;
					try { if (child is { HasExited: false }) child.Kill(entireProcessTree: true); }
					catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
				}
			}
		}

		/// <summary><c>hei &lt;args&gt;</c> in the background; with <paramref name="home"/>, as Heiward's home there (HEIWARD_HOME).</summary>
		static Process? StartHei(string? home, params string[] args) {
			var psi = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "hei.exe")) { UseShellExecute = false, CreateNoWindow = true };
			foreach (string a in args) psi.ArgumentList.Add(a);
			if (home != null) psi.Environment["HEIWARD_HOME"] = home;
			Installer.KeepStdHandlesToSelf();
			return Process.Start(psi);
		}

		static void TryDelete(string path) {
			try { File.Delete(path); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}
	}
}
