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
	/// <item>The PC scanned: <c>scan/in</c> (the work: which listing), <c>scan/pages</c> (its files, a page at a time:
	/// <see cref="ListingPages"/>), <c>scan/bytes</c> (their bytes), each only for the PC it asked, while it asks; and
	/// <c>scan/listing</c>, its files in one answer, as version 1 asked for them.</item>
	/// </list>
	/// </summary>
	static class ScanForOthers {
		public const string Route = "scan";
		/// <summary>2: the listing comes in pages (<c>scan/pages</c>).</summary>
		const int Version = 2;

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

		/// <summary>
		/// The other PCs on the Estate that can scan this one now (same build), those that could once Heiward there is
		/// updated (an older one), and those that could once Heiward here is (a newer one there).
		/// </summary>
		static (long At, List<object> Can, List<string> Update, List<string> UpdateHere, HashSet<string> CanIds, Dictionary<string, string> Builds)? offers;
		static int refreshing;

		/// <summary>
		/// For the page's state: who could scan this PC (asked of the other PCs at most once a minute, in the background), the
		/// scan asked for, the scan this PC is doing for another, and what another PC found here. Null when there's none of it:
		/// free Heiward, with no Castellan, never shows any.
		/// </summary>
		internal static object? View() {
			if (Volatile.Read(ref refreshing) == 0 && (offers is not { } o || Environment.TickCount64 - o.At > 60_000)) _ = Task.Run(RefreshOffersAsync);
			var known = offers;
			Asked? a = AskedNow();
			ForeignFindings? found = ForeignFindings.LoadAny();
			var lent = ScanningFor;
			if (known is not { Can.Count: > 0 } && known is not { Update.Count: > 0 } && known is not { UpdateHere.Count: > 0 } && a == null && found == null && lent == null) return null;
			// A scan that stopped part way: Scan again picks up where it stopped, once that PC can scan this one (its build is this one's).
			bool stopped = a is { Finished: true, Problem: not null, Begun: true };
			string? now = a?.LenderBuild != null && known?.Builds.TryGetValue(a.Lender.Id, out string? b) == true && b != a.LenderBuild ? b : null;
			return new {
				others = known?.Can ?? [],
				updateOn = known?.Update ?? [],
				newerOn = known?.UpdateHere ?? [],
				asked = a == null ? null : new {
					pc = a.Lender.Id, name = a.Lender.Name, stage = a.Stage, done = a.Done, total = a.Total, fetchedBytes = a.Fetched,
					waiting = a.Waiting, finished = a.Finished, problem = a.Problem, sets = a.Sets, startedUtc = a.StartedUtc,
					stopped, again = stopped && known?.CanIds.Contains(a.Lender.Id) == true,
					// Heiward there was updated since it began (its build then and now): the card says so.
					updatedTo = now == null ? null : Short(now),
				},
				scanningFor = lent is { } l ? new { name = l.To.Name, sinceUtc = l.Since, linked = l.Linked } : null,
				// Dated by the scan's end, not its start: a scan of a whole PC takes hours.
				found = found == null ? null : new { from = found.FromName, atUtc = found.FoundUtc(found.FinishedUtc == null ? ForeignFindings.Written() : null), sets = found.Groups.Count, setAside = found.Build != AppBuild.Current },
			};
		}

		static async Task RefreshOffersAsync() {
			if (Interlocked.Exchange(ref refreshing, 1) == 1) return;
			try {
				var can = new List<object>();
				var update = new List<string>();
				var updateHere = new List<string>();
				var canIds = new HashSet<string>(StringComparer.Ordinal);
				var builds = new Dictionary<string, string>(StringComparer.Ordinal);
				if (await EstateLink.ReadEstateAsync(CancellationToken.None) is { On: true } estate)
					foreach (EstateLink.EstatePc pc in estate.Pcs.Where(p => p.Here && p.HasHeiward != false)) {
						try {
							var (status, json) = await EstateLink.GetJsonAsync(EstateLink.PeerPath(pc.Id, Route), TimeSpan.FromSeconds(5), CancellationToken.None, limit: 64 * 1024);
							using (json) {
								if (status != 200 || json?.RootElement is not { ValueKind: JsonValueKind.Object } r) continue;
								bool able = r.TryGetProperty("can", out JsonElement c) && c.ValueKind == JsonValueKind.True;
								string? build = EstateLink.Str(r, "build");
								if (build != null) builds[pc.Id] = build;
								if (able && build == AppBuild.Current) {
									can.Add(new { id = pc.Id, name = pc.Name, npu = EstateLink.Str(r, "npu") });
									canIds.Add(pc.Id);
								}
								else if (able && build != null) (Newer(build, AppBuild.Current) ? updateHere : update).Add(pc.Name);
							}
						}
						catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or EstateMissing) { }
					}
				offers = (Environment.TickCount64, can, update, updateHere, canIds, builds);
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or EstateMissing or IOException) {
				offers = (Environment.TickCount64, [], [], [], [], []);
			}
			finally {
				Volatile.Write(ref refreshing, 0);
			}
		}

		// ================= This PC asks another to scan it =================

		/// <summary>
		/// The scan this PC asked another PC for, as its page shows it; kept on disk (<see cref="AskedFile"/>), so a scan that
		/// stopped, this PC's page with it (an update), still says so, with its Scan again.
		/// </summary>
		internal sealed class Asked {
			public required Pc Lender { get; init; }
			public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
			public string Stage { get; set; } = "Listing this PC's photos and videos";
			public int Done { get; set; }
			public int Total { get; set; }
			public long Fetched { get; set; }
			public string? Waiting { get; set; }
			public bool Finished { get; set; }
			public string? Problem { get; set; }
			public int? Sets { get; set; }
			/// <summary>The other PC began the scan (it sent how it's getting on): one that stops after is picked up where it stopped.</summary>
			public bool Begun { get; set; }
			/// <summary>The other PC's Heiward when it was asked: one that's another since was updated under the scan.</summary>
			public string? LenderBuild { get; set; }
		}

		/// <summary>The scan asked of another PC, as last kept (<see cref="Asked"/>).</summary>
		internal sealed record AskedKept(string Pc, string Name, DateTime StartedUtc, string Stage, int Done, int Total, long Fetched, bool Finished,
			string? Problem, int? Sets, bool Begun, string? LenderBuild);

		static string AskedFile => Path.Combine(AgentPaths.Home, "remote-asked.json");

		/// <summary>How long a scan asked of another PC is shown once it has ended, kept across this page's restarts.</summary>
		static readonly TimeSpan AskedShown = TimeSpan.FromDays(7);

		static long askedSavedAt;

		static void SaveAsked(Asked a) {
			askedSavedAt = Environment.TickCount64;
			try {
				AgentPaths.WriteAtomic(AskedFile, JsonSerializer.Serialize(new AskedKept(a.Lender.Id, a.Lender.Name, a.StartedUtc, a.Stage, a.Done, a.Total, a.Fetched,
					a.Finished, a.Problem, a.Sets, a.Begun, a.LenderBuild), AgentConfig.Json));
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		/// <summary>
		/// The scan asked of another PC when this page started, as kept: within <see cref="AskedShown"/> of its start. One kept
		/// as under way ended with this page's process, which nothing told: it's stopped, for Scan again.
		/// </summary>
		internal static Asked? LoadAsked(string file, DateTime nowUtc) {
			try {
				if (!File.Exists(file) || JsonSerializer.Deserialize<AskedKept>(File.ReadAllText(file), AgentConfig.Json) is not { } k) return null;
				if (nowUtc - k.StartedUtc > AskedShown) return null;
				var a = new Asked {
					Lender = new Pc(k.Pc, k.Name), StartedUtc = k.StartedUtc, Stage = k.Stage, Done = k.Done, Total = k.Total, Fetched = k.Fetched,
					Finished = k.Finished, Problem = k.Problem, Sets = k.Sets, Begun = k.Begun, LenderBuild = k.LenderBuild,
				};
				if (!a.Finished) {
					a.Finished = true;
					a.Problem = "Heiward on this PC stopped while it was scanned.";
				}
				return a;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return null;
			}
		}

		static int askedLoaded;

		/// <summary>The scan asked of another PC, the one kept from before this page started included.</summary>
		static Asked? AskedNow() {
			if (Interlocked.Exchange(ref askedLoaded, 1) == 0 && LoadAsked(AskedFile, DateTime.UtcNow) is { } kept)
				lock (gate) asked ??= kept;
			return asked;
		}

		static readonly object gate = new();
		static Borrowing? borrowing;
		static Asked? asked;
		/// <summary>What this PC listed to the PC scanning it, on disk while it scans (none after: the files it found are checked against it first).</summary>
		static LentListing? lentListing;
		/// <summary>The findings are being checked against the listing: it's kept until they are.</summary>
		static bool pulling;
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
					// Another build can't scan for this one (its rules judge sets otherwise); else the offer read in the kit's words.
					JsonElement? said = json?.RootElement;
					if (status == 200 && said is { ValueKind: JsonValueKind.Object } o) a.LenderBuild = EstateLink.Str(o, "build");
					string? why = status == 200 && said is { ValueKind: JsonValueKind.Object } offer && EstateLink.Str(offer, "build") is string build && build != AppBuild.Current
						? Versions(name, build)
						: NpuLoan.OfferFrom("Heiward", a.Lender, status, said) is { Can: false } no ? no.Why : null;
					if (why != null) return Fail(a, why);
					SaveAsked(a);
				}
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or EstateMissing && !ct.IsCancellationRequested) {
				return Fail(a, e is EstateMissing ? "Castellan isn't running on this PC." : NpuLoan.OfferFrom("Heiward", a.Lender, null, null).Why!);
			}
			_ = Task.Run(() => ListAndLendAsync(a));
			return null;
		}

		/// <summary>
		/// Why <paramref name="name"/> can't scan this PC, or be scanned by it, in words for this PC's page: it runs Heiward
		/// <paramref name="theirs"/>, this PC <paramref name="mine"/>, and the older one is the one to update.
		/// </summary>
		internal static string Versions(string name, string theirs, string? mine = null) {
			mine ??= AppBuild.Current;
			return Newer(theirs, mine) ? $"Heiward on {name} is newer ({Short(theirs)}; this PC has {Short(mine)}): update Heiward on this PC."
				: Newer(mine, theirs) ? $"Heiward on {name} is older ({Short(theirs)}; this PC has {Short(mine)}): update Heiward there."
				: $"Heiward on {name} is another version ({Short(theirs)}; this PC has {Short(mine)}): update Heiward on both PCs.";
		}

		/// <summary>Whether build <paramref name="a"/> is a later version than <paramref name="b"/> (false when either isn't one).</summary>
		internal static bool Newer(string a, string b) =>
			System.Version.TryParse(Short(a), out System.Version? x) && System.Version.TryParse(Short(b), out System.Version? y) && x > y;

		static string Short(string build) => build.Split('+')[0];

		static string Fail(Asked a, string why) {
			a.Problem = why;
			a.Finished = true;
			SaveAsked(a);
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
				var roots = AgentScanner.BuildSettings(AgentConfig.Load(), notes).IncludeList.Order(StringComparer.Ordinal).ToList();
				// What the listing found that isn't a photo or video (its database knows: a text file named .ts) isn't lent:
				// the other PC would only find that out again.
				LentListing.Listed? found = LentListing.Listed.Take();
				var notMedia = new HashSet<string>(found?.NotMedia ?? [], StringComparer.OrdinalIgnoreCase);
				LentListing listing = LentListing.Write(ListingPlan.Kept(roots), AppBuild.Current, leaveOut: notMedia.Count > 0 ? notMedia.Contains : null);
				int files = listing.Files;
				AgentPaths.AppendLog($"listed {files:N0} photos and videos for {a.Lender.Name}'s scan in {timer.Elapsed.TotalSeconds:N1} s " +
					$"({(found != null ? $"{found.Seconds:N1} s listing them{(found.How != null ? ": " + found.How : "")}; " : "")}{listCpu.TotalSeconds:N1} s of the processor), " +
					$"in {listing.Pages.Count:N0} page(s){(notMedia.Count > 0 ? $"; {notMedia.Count:N0} file(s) that aren't photos or videos left out" : "")}");
				lock (gate) {
					if (a != asked || a.Finished) {
						listing.Dispose();
						return;
					}
					lentListing?.Dispose();
					lentListing = listing;
					// Its files, for that PC alone, while it scans this one (the kit's PeerFiles), looked up in the listing on disk.
					Pc lender = a.Lender;
					serving = new PeerFiles(GameNow) { May = pc => pc.Id == lender.Id && borrowing?.Lender?.Id == lender.Id };
					serving.ReplaceWith(key => {
						try { return listing.PathOf(key); }
						catch (Exception e) when (e is ObjectDisposedException or IOException) { return null; } // the loan just ended
					}, files);
					a.Stage = $"Waiting for {a.Lender.Name}";
					a.Total = files;
					Borrow.Start(a.Lender);
					Borrow.Offer(new JsonObject { ["scan"] = new JsonObject { ["v"] = Version, ["build"] = AppBuild.Current, ["stamp"] = listing.Stamp, ["files"] = files } }, head: true);
				}
				// Reading what this PC keeps of a big library took memory for a moment: it goes back to Windows now, not
				// once the scan ends, hours later.
				GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
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
				string stage = a.Stage;
				a.Begun = true;
				a.Stage = (string?)s["stage"] ?? a.Stage;
				a.Done = (int?)s["done"] ?? a.Done;
				a.Total = (int?)s["total"] is int t && t > 0 ? t : a.Total;
				a.Fetched = (long?)s["fetchedBytes"] ?? a.Fetched;
				a.Waiting = (string?)s["waiting"];
				// Kept as it goes, so a stop this page doesn't live through still says how far it got.
				if (a.Stage != stage || Environment.TickCount64 - askedSavedAt > 60_000) SaveAsked(a);
			}
			else if (line["done"] is JsonObject d) {
				if ((int?)d["code"] is int code && code != 0) {
					string why = ((string?)d["error"] ?? (string?)d["stage"] ?? "it ended early").TrimEnd('.');
					Stop(Fail(a, $"{a.Lender.Name}'s scan of this PC didn't finish: {why}."));
					return;
				}
				a.Stage = "Fetching what it found";
				_ = Task.Run(() => PullFindingsAsync(a));
			}
		}

		static void OnEnded(string why) {
			Asked? a = asked;
			if (a is { Finished: false }) Fail(a, EndedWords(a.Lender.Name, why));
			Released();
		}

		/// <summary>Why the other PC's scan of this one ended, in words for this PC's card: its update in plain words, else the loan's.</summary>
		internal static string EndedWords(string lender, string why) =>
			why.Contains(Updated, StringComparison.Ordinal) ? $"Heiward on {lender} is being updated, so its scan of this PC stopped." : why;

		/// <summary>The page's Stop: the loan ends, and the other PC stops scanning this one.</summary>
		internal static void Stop(string? why = null) {
			lock (gate) {
				borrowing?.Stop();
				Released();
				if (asked is { Finished: false } a) Fail(a, why ?? "Stopped.");
			}
		}

		/// <summary>
		/// The loan is over: its files aren't offered any more, the listing on disk goes (once the findings are checked
		/// against it, while they are), and what handing them over cost is logged (once).
		/// </summary>
		static void Released() {
			lock (gate) {
				if (!pulling) {
					lentListing?.Dispose();
					lentListing = null;
				}
			}
			if (serving is not { Count: > 0 } s) return;
			s.Clear();
			if (s.Served > 0)
				AgentPaths.AppendLog($"handed {Format.Bytes(s.Served)} over for another PC's scan, {(listCpu + s.Cpu).TotalSeconds:N1} s of the processor in all " +
					$"({(listCpu + s.Cpu).TotalSeconds / Math.Max(1.0, s.Served / 1e9):N1} s per GB)");
		}

		/// <summary>The other PC's findings fetched, checked against what was listed to it, and kept for the page (<see cref="ForeignFindings"/>).</summary>
		static async Task PullFindingsAsync(Asked a) {
			lock (gate) pulling = true;
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
					Dictionary<string, ListedFile> listed = ListedOf(found);
					// Nothing new here since its last scan: the same findings as those kept, and nothing was fetched for them this time.
					ForeignFindings? before = ForeignFindings.LoadAny();
					bool again = before != null && before.FromId == a.Lender.Id && before.ScannedAtUtc == found.ScannedAtUtc;
					DateTime? beforeAt = before?.FoundUtc(before.FinishedUtc == null ? ForeignFindings.Written() : null); // before it's written again
					// Dated by the scan's end; one that found nothing new ended just now, its findings standing as they were.
					ForeignFindings kept = ForeignFindings.From(a.Lender, found, listed, again ? DateTime.UtcNow : null);
					kept.Save();
					a.Sets = kept.Groups.Count;
					a.Stage = kept.Groups.Count == 0 ? "Done: no copies found" : $"Done: {kept.Groups.Count:N0} set(s) of copies found";
					a.Done = a.Total;
					a.Finished = true;
					SaveAsked(a);
					AgentPaths.AppendLog(again ? $"{a.Lender.Name} found nothing new here since its scan of {beforeAt!.Value.ToLocalTime():g}: its {kept.Groups.Count:N0} set(s) stand"
						: $"{a.Lender.Name} scanned this PC: {kept.Groups.Count:N0} set(s)" + (found.Cost is { } c
							? $"; it fetched {Format.Bytes(c.FetchedBytes)} in {c.Asks:N0} asks and took {c.Seconds:N0} s" : ""));
				}
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException or EstateMissing or IOException) {
				Fail(a, $"{a.Lender.Name}'s findings didn't come: {e.Message}");
			}
			finally {
				lock (gate) {
					pulling = false;
					borrowing?.Stop();
					Released();
				}
			}
		}

		/// <summary>The files the findings name, as this PC listed them to that PC (those it didn't aren't there): looked up in the listing on disk.</summary>
		static Dictionary<string, ListedFile> ListedOf(RemoteFindings found) {
			var listed = new Dictionary<string, ListedFile>(StringComparer.OrdinalIgnoreCase);
			if (lentListing is not { } listing) return listed;
			foreach (ReportGroup g in found.Groups ?? [])
				foreach (string path in (g.Items ?? []).Select(i => i.Path).Append(g.KeepPath))
					if (path != null && !listed.ContainsKey(path) && listing.Find(path) is { } f) listed[path] = f;
			return listed;
		}

		/// <summary>GET /api/peer/scan/in: the PC scanning this one pulls the work.</summary>
		internal static Task InRouteAsync(HttpContext ctx) => Borrow.InRouteAsync(ctx);

		/// <summary>The listing, for the PC this one asked to scan it, while it does; null for any other ask.</summary>
		static LentListing? ListingFor(HttpContext ctx) =>
			EstateLink.AskerOf(ctx.Request) is { } asker && borrowing?.Lender?.Id == asker.Id ? lentListing : null;

		static Task NotAsking(HttpContext ctx) => NpuLoan.Json(410, new { error = "This PC isn't asking that PC to scan it now.", code = "none" }).ExecuteAsync(ctx);

		/// <summary>
		/// GET /api/peer/scan/listing?have=…: this PC's files in one answer, as version 1 asked for them, or word that they're
		/// as listed at the stamp the other PC has. Written from the listing on disk as it's read, never held whole.
		/// </summary>
		internal static async Task ListingRouteAsync(HttpContext ctx) {
			if (ListingFor(ctx) is not { } listing) {
				await NotAsking(ctx);
				return;
			}
			if (ctx.Request.Query["have"] == listing.Stamp) {
				await NpuLoan.Json(200, new { v = RemoteListing.Version, same = true, stamp = listing.Stamp }).ExecuteAsync(ctx);
				return;
			}
			ctx.Response.ContentType = "application/json";
			ctx.Response.Headers.CacheControl = "no-store";
			try { await listing.WriteWholeAsync(ctx.Response.Body, ctx.RequestAborted); }
			catch (Exception e) when (e is ObjectDisposedException or IOException) { ctx.Abort(); } // the loan ended as it went
		}

		/// <summary>
		/// GET /api/peer/scan/pages?n=…&amp;have=…&amp;stamp=…: page n of this PC's files (<see cref="ListingPages"/>); word that
		/// they're as listed at the stamp the other PC has (have, on the first page); or 409 when the stamp it pages through
		/// isn't this listing's any more, so it asks again from the first page.
		/// </summary>
		internal static async Task PagesRouteAsync(HttpContext ctx) {
			if (ListingFor(ctx) is not { } listing) {
				await NotAsking(ctx);
				return;
			}
			await PageAsync(ctx, listing);
		}

		/// <summary>The pages route's answer from <paramref name="listing"/> (tests: a listing of their own).</summary>
		internal static async Task PageAsync(HttpContext ctx, LentListing listing) {
			ctx.Response.Headers.CacheControl = "no-store";
			if (!int.TryParse(ctx.Request.Query["n"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)) {
				await NpuLoan.Json(400, new { error = "Ask for a page by its number (n).", code = "page" }).ExecuteAsync(ctx);
				return;
			}
			if (n == 0 && ctx.Request.Query["have"] == listing.Stamp) {
				await NpuLoan.Json(200, new { v = ListingPages.Version, same = true, stamp = listing.Stamp }).ExecuteAsync(ctx);
				return;
			}
			if (ctx.Request.Query["stamp"] is { Count: > 0 } stamp && stamp != listing.Stamp) {
				await NpuLoan.Json(409, new { error = "This PC's list of files changed since: ask for it again from its first page.", code = "changed", stamp = listing.Stamp })
					.ExecuteAsync(ctx);
				return;
			}
			if (n >= listing.Pages.Count) {
				await NpuLoan.Json(404, new { error = $"This PC's list of files has {listing.Pages.Count} page(s).", code = "page" }).ExecuteAsync(ctx);
				return;
			}
			var page = new System.Buffers.ArrayBufferWriter<byte>(LentListing.PageBytes + 64 * 1024);
			try { listing.WritePage(n, page); }
			catch (Exception e) when (e is ObjectDisposedException or IOException) {
				await NotAsking(ctx); // the loan ended as it was asked
				return;
			}
			ctx.Response.ContentType = "application/json";
			await ctx.Response.Body.WriteAsync(page.WrittenMemory, ctx.RequestAborted);
		}

		/// <summary>GET /api/peer/scan/bytes?path=…: a listed file's bytes, gently, as the kit's PeerFiles answers (none offered when no PC scans this one).</summary>
		internal static Task BytesRouteAsync(HttpContext ctx) => (serving ?? NoneOffered).ServeAsync(ctx);

		static readonly PeerFiles NoneOffered = new(() => null);

		/// <summary>GET /api/peer/scan/timing: where handing the files over has spent its time (<see cref="PeerFiles.Timing"/>), for the PC scanning this one, while it does.</summary>
		internal static Task TimingRouteAsync(HttpContext ctx) =>
			ListingFor(ctx) != null && serving is { } s ? NpuLoan.Json(200, s.Timing).ExecuteAsync(ctx) : NotAsking(ctx);

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
		internal static Task OfferRouteAsync(HttpContext ctx) => Lend.OfferRouteAsync(ctx, new { v = Version, build = AppBuild.Current });

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

		/// <summary>
		/// Heiward's page is stopping: a scan for another PC stops first, keeping what it checked (its next scan picks up from
		/// there), then that PC is told why; one asked of another PC ends too. An update says so (<see cref="ReviewServer.Updating"/>).
		/// </summary>
		internal static void Shutdown() {
			bool update = ReviewServer.Updating;
			job?.EndAndWait(ChildStopWait);
			if (update) lending?.Stop(Updated);
			else lending?.Stop();
			Stop(update ? "Heiward on this PC is being updated." : "Heiward stopped on this PC.");
		}

		/// <summary>What the other PC hears when this one's Heiward is updated under its scan, in the loan's words ("&lt;PC&gt; stopped lending its NPU: …").</summary>
		internal const string Updated = "Heiward is being updated there";

		/// <summary>How long a scan for another PC gets to stop by itself, saving what it checked, before its process is ended.</summary>
		internal static readonly TimeSpan ChildStopWait = TimeSpan.FromSeconds(30);

		/// <summary>The most a scan for another PC, or of this one by another, holds Heiward's update back, from its start.</summary>
		internal static readonly TimeSpan MostHold = TimeSpan.FromHours(24);

		/// <summary>
		/// What holds Heiward's update back now, in words for the ping's busy (Manor's Updates: "it updates once it's idle: it is
		/// busy: …"); null when nothing does. An update stops the page, and with it a scan this PC runs for another, or another's
		/// of this one (its listing and its files come from here). Each holds it at most <see cref="MostHold"/> from its start:
		/// past that, the update goes in, and the scan picks up where it stopped when it's asked again.
		/// </summary>
		internal static string? HoldsUpdate(DateTime nowUtc) =>
			Holding(ScanningFor is { } l ? (l.To.Name, l.Since) : null, asked is { Finished: false } a ? (a.Lender.Name, a.StartedUtc) : null, nowUtc);

		internal static string? Holding((string Name, DateTime SinceUtc)? scanningFor, (string Name, DateTime SinceUtc)? scannedBy, DateTime nowUtc) {
			if (scanningFor is { } s && nowUtc - s.SinceUtc < MostHold) return $"scanning {s.Name}'s photos and videos for it";
			if (scannedBy is { } b && nowUtc - b.SinceUtc < MostHold) return $"{b.Name} is scanning this PC's photos and videos";
			return null;
		}

		/// <summary>
		/// The scan of another PC's files, for its loan: <c>hei remote-scan</c> in a process of its own, with that PC's home
		/// here, started by the loan's head; how it's getting on sent every 2 seconds, and that it's done once it ends.
		/// </summary>
		sealed class RemoteJob(ILoan loan) : ILoanWork {
			readonly object gate = new();
			Process? child;
			string? home;
			Timer? watch;
			string? lastStatus;
			bool ended;

			public void Line(JsonObject line, long? n) {
				if (line["scan"] is not JsonObject scan) return;
				lock (gate) {
					if (child != null || ended) return; // the head again, after a dropped link
					string? build = (string?)scan["build"];
					if ((int?)scan["v"] != Version || build != AppBuild.Current) {
						// In words for that PC's page: this PC is the other one there.
						loan.End(build == null ? $"Heiward on {Environment.MachineName} is another version: update Heiward on both PCs"
							: Versions(Environment.MachineName, AppBuild.Current, build));
						return;
					}
					string home = this.home = RemoteScanner.HomeFor(loan.From.Id);
					Directory.CreateDirectory(home);
					// This PC's own settings (its AI device, its graphics card), for the scan in that home.
					try { if (File.Exists(AgentPaths.Config)) File.Copy(AgentPaths.Config, Path.Combine(home, "settings.json"), overwrite: true); }
					catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
					TryDelete(RemoteStatus.FilePath(home));
					TryDelete(RemoteScanner.StopFile(home));
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

			/// <summary>The other PC ended it (or its link was gone too long): the scan stops, as <see cref="EndAndWait"/>, without holding the loan's thread.</summary>
			public void Ended() {
				lock (gate) {
					if (ended) return;
				}
				_ = Task.Run(() => EndAndWait(ChildStopWait));
			}

			/// <summary>
			/// The scan stops by itself, asked to (<see cref="RemoteScanner.StopFile"/>): it saves what it checked, for its next
			/// scan to pick up from, and ends. One that hasn't ended within <paramref name="most"/> is ended.
			/// </summary>
			public void EndAndWait(TimeSpan most) {
				Process? c;
				string? h;
				lock (gate) {
					ended = true;
					watch?.Dispose();
					watch = null;
					(c, h) = (child, home);
				}
				if (c == null) return;
				try {
					if (c.HasExited) return;
					if (h != null) {
						try { File.WriteAllText(RemoteScanner.StopFile(h), ""); }
						catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
						var timer = Stopwatch.StartNew();
						if (c.WaitForExit(most)) {
							AgentPaths.AppendLog($"the scan of {loan.From.Name}'s files stopped in {timer.Elapsed.TotalSeconds:N1} s, keeping what it checked");
							return;
						}
					}
					AgentPaths.AppendLog($"the scan of {loan.From.Name}'s files didn't stop within {most.TotalSeconds:N0} s: its process is ended");
					c.Kill(entireProcessTree: true);
				}
				catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
			}
		}

		/// <summary><c>hei &lt;args&gt;</c> in the background; with <paramref name="home"/>, as Heiward's home there (HEIWARD_HOME).</summary>
		static Process? StartHei(string? home, params string[] args) {
			var psi = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "hei.exe")) { UseShellExecute = false, CreateNoWindow = true };
			foreach (string a in args) psi.ArgumentList.Add(a);
			if (home != null) psi.Environment["HEIWARD_HOME"] = home;
			// One thread's work, mostly: the workstation's collector keeps its memory down, where the server's gives each core a heap.
			psi.Environment["DOTNET_gcServer"] = "0";
			Installer.KeepStdHandlesToSelf();
			return Process.Start(psi);
		}

		static void TryDelete(string path) {
			try { File.Delete(path); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}
	}
}
