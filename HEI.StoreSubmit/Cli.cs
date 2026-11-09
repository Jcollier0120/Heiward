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

using System.Text.Json.Nodes;

namespace HEI.StoreSubmit;

/// <summary>A GitHub Actions step's way of saying things: annotations, step outputs and the run's summary.</summary>
public sealed class Actions(Func<string, string?> env, TextWriter output) {
	public const string Title = "Microsoft Store";

	public TextWriter Log => output;

	public void Notice(string message) => output.WriteLine($"::notice title={Title}::{Escape(message)}");

	public void Warning(string message) => output.WriteLine($"::warning title={Title}::{Escape(message)}");

	public void Error(string message) => output.WriteLine($"::error title={Title}::{Escape(message)}");

	/// <summary>Keeps a value out of the log from here on (a token); only on a runner, which reads the command.</summary>
	public void Mask(string value) {
		if (env("GITHUB_ACTIONS") == "true" && value.Length > 0) output.WriteLine("::add-mask::" + Escape(value));
	}

	public void Output(string name, string value) {
		if (env("GITHUB_OUTPUT") is { Length: > 0 } file) File.AppendAllText(file, $"{name}={value.ReplaceLineEndings(" ")}\n");
		output.WriteLine($"{name}: {value}");
	}

	public void Summary(string markdown) {
		if (env("GITHUB_STEP_SUMMARY") is { Length: > 0 } file) File.AppendAllText(file, markdown.TrimEnd() + "\n\n");
	}

	/// <summary>A workflow command's message: %, CR and LF are its own.</summary>
	public static string Escape(string text) => text.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
}

/// <summary>
/// The Microsoft Store workflow's steps (.github/workflows/store.yml), with the Entra app's credentials in
/// PARTNER_CENTER_TENANT_ID, PARTNER_CENTER_CLIENT_ID and PARTNER_CENTER_CLIENT_SECRET, and the app in STORE_ID:
/// <list type="bullet">
/// <item><c>check --target 1.9.137</c>: whether that release goes to the Store now (<see cref="Rules"/>). Step outputs
/// submit (true or false) and pending (the id of the submission in progress, empty when none), and a notice that says
/// why. It fails only when Partner Center can't be asked.</item>
/// <item><c>finish --target 1.9.137 --changelog CHANGELOG.md --before &lt;pending&gt; [--draft true]</c>: after
/// msstore publish --noCommit made the submission, sets its What's new from the changelog, and commits it (unless
/// a draft is asked for) and waits until the commit is through.</item>
/// <item><c>abandon --before &lt;pending&gt;</c>: after a failed run, deletes the draft that run made, so nothing is
/// left in Partner Center to hold the next release back. A submission it didn't make, or one already sent, stays.</item>
/// <item><c>notes --target 1.9.137 --changelog CHANGELOG.md [--after 1.9.130]</c>: prints the What's new finish would
/// write when the Store has --after, without signing in.</item>
/// </list>
/// </summary>
public static class Cli {
	public const string DefaultApp = "9NX5K0L4CW3D";

	/// <summary>How long a commit may take before the run stops waiting for it; Partner Center carries on either way.</summary>
	public static readonly TimeSpan CommitWait = TimeSpan.FromMinutes(15);

	public static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

	public static async Task<int> Run(string[] args, Func<string, string?> env, HttpMessageHandler handler, TextWriter output,
		Func<TimeSpan, CancellationToken, Task>? wait = null, CancellationToken ct = default) {
		var gh = new Actions(env, output);
		wait ??= Task.Delay;
		if (args.Length == 0) {
			gh.Error("Say what to do: check, finish, abandon or notes.");
			return 2;
		}
		Func<PartnerCenter, Task<int>> step;
		try {
			Dictionary<string, string> options = Options(args[1..]);
			string before = options.GetValueOrDefault("before", "");
			switch (args[0]) {
				case "check": {
					Version target = Target(options);
					step = store => Check(store, gh, target, ct);
					break;
				}
				case "finish": {
					Version target = Target(options);
					string changelog = Need(options, "changelog");
					if (!File.Exists(changelog)) throw new ArgumentException($"There's no {changelog}.");
					bool draft = options.GetValueOrDefault("draft") == "true";
					step = store => Finish(store, gh, target, changelog, before, draft, wait, ct);
					break;
				}
				case "abandon":
					step = store => Abandon(store, gh, before, ct);
					break;
				case "notes": {
					// What's new as finish writes it, without Partner Center: --after is the version the Store has.
					Version target = Target(options);
					string changelog = Need(options, "changelog");
					if (!File.Exists(changelog)) throw new ArgumentException($"There's no {changelog}.");
					Version? after = options.TryGetValue("after", out string? a) ? Versions.Parse(a) ?? throw new ArgumentException($"--after {a} isn't a version.") : null;
					string notes = ReleaseNotes.For(File.ReadAllText(changelog), after, target);
					output.WriteLine(notes);
					output.WriteLine($"({ReleaseNotes.Cost(notes)} of {ReleaseNotes.Limit} characters)");
					return 0;
				}
				default:
					throw new ArgumentException($"There's no \"{args[0]}\": check, finish, abandon or notes.");
			}
		}
		catch (ArgumentException e) {
			gh.Error(e.Message);
			return 2;
		}
		Credentials? credentials = Credentials.From(env, out List<string> missing);
		if (credentials == null) {
			gh.Error("These secrets aren't set: " + string.Join(", ", missing) + " (docs/STORE.md, \"Releasing from CI\").");
			return 2;
		}
		string app = env("STORE_ID") is { Length: > 0 } id ? id : DefaultApp;
		using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(2) };
		try {
			return await step(await PartnerCenter.SignIn(http, app, credentials, gh.Mask, wait, ct));
		}
		catch (StoreException e) {
			gh.Error(credentials.Redact(e.Message));
			return 1;
		}
	}

	static async Task<int> Check(PartnerCenter store, Actions gh, Version target, CancellationToken ct) {
		StoreState state = await store.State(ct);
		Decision decision = Rules.Decide(state, target);
		gh.Output("submit", decision.Submits ? "true" : "false");
		gh.Output("pending", state.Pending?.Id ?? "");
		gh.Output("published", state.Published?.Version is { } v ? Versions.Show(v) : "");
		if (decision.Warn) gh.Warning(decision.Message);
		else gh.Notice(decision.Message);
		gh.Summary("### Microsoft Store\n\n" + decision.Message);
		return 0;
	}

	static async Task<int> Finish(PartnerCenter store, Actions gh, Version target, string changelog, string before, bool draft,
		Func<TimeSpan, CancellationToken, Task> wait, CancellationToken ct) {
		string release = "Heiward " + Versions.Show(target);
		JsonObject app = await store.GetApp(ct);
		string id = PartnerCenter.SubmissionId(app, "pendingApplicationSubmission")
			?? throw new StoreException($"No submission is in progress in Partner Center: msstore didn't leave {release}'s.");
		if (id == before) throw new StoreException($"The submission in progress in Partner Center ({id}) is the one that was there before this run: msstore didn't make {release}'s.");
		JsonObject submission = await store.GetSubmission(id, ct);
		string status = submission["status"]?.ToString() ?? "None";
		if (status != "PendingCommit") throw new StoreException($"The new submission ({id}) is {status}, not a draft: it was left as it is.");
		string package = $"Heiward-{Versions.Show(target)}.msixbundle";
		bool carries = submission["applicationPackages"] is JsonArray packages && packages.Any(p =>
			string.Equals(p?["fileName"]?.ToString(), package, StringComparison.OrdinalIgnoreCase) && p?["fileStatus"]?.ToString() != "PendingDelete");
		if (!carries) throw new StoreException($"The new submission ({id}) doesn't carry {package}.");

		// What's new: every version since the published one, from the changelog this run checked out.
		string? published = PartnerCenter.SubmissionId(app, "lastPublishedApplicationSubmission");
		Version? inStore = published != null ? Submission.Read(await store.GetSubmission(published, ct)).Version : null;
		string notes = ReleaseNotes.For(File.ReadAllText(changelog), inStore, target);
		JsonObject? listing = null;
		if (submission["listings"] is JsonObject listings)
			foreach ((string language, JsonNode? value) in listings)
				if (language.Equals("en-us", StringComparison.OrdinalIgnoreCase) && value?["baseListing"] is JsonObject b) listing = b;
		if (listing != null) {
			listing["releaseNotes"] = notes;
			gh.Log.WriteLine("What's new in this version:");
			gh.Log.WriteLine(notes);
			gh.Summary($"### What's new in Heiward {Versions.Show(target)}\n\n```\n{notes}\n```");
		}
		else gh.Warning($"{release}'s submission has no English (United States) listing: its What's new is the last one's.");
		await store.PutSubmission(id, submission, ct);

		if (draft) {
			gh.Notice($"{release} is a draft in Partner Center, for you to look at and send: Apps and games > Heiward > the submission in progress.");
			return 0;
		}
		JsonObject commit = await store.Commit(id, ct);
		status = commit["status"]?.ToString() ?? "CommitStarted";
		JsonNode? details = null;
		for (TimeSpan waited = TimeSpan.Zero; status == "CommitStarted" && waited < CommitWait; waited += Poll) {
			await wait(Poll, ct);
			JsonObject now = await store.GetStatus(id, ct);
			status = now["status"]?.ToString() ?? status;
			details = now["statusDetails"];
		}
		if (status == "CommitFailed") {
			IReadOnlyList<string> problems = Submission.ProblemsOf(details);
			string why = problems.Count > 0 ? ": " + string.Join("; ", problems) : "";
			gh.Error($"Partner Center couldn't commit {release}'s submission{why}. The next scheduled run sends the newest release in its place.");
			return 1;
		}
		if (status == "CommitStarted") {
			gh.Warning($"{release}'s submission was still being committed after {CommitWait.TotalMinutes:0} minutes. Partner Center carries on with it; the next scheduled run looks at it again.");
			return 0;
		}
		gh.Notice($"{release} is with the Microsoft Store for certification ({status}). The Store publishes it once it passes, and Windows updates the installed copies.");
		return 0;
	}

	static async Task<int> Abandon(PartnerCenter store, Actions gh, string before, CancellationToken ct) {
		JsonObject app = await store.GetApp(ct);
		string? id = PartnerCenter.SubmissionId(app, "pendingApplicationSubmission");
		if (id == null || id == before) {
			gh.Log.WriteLine("This run left no submission in Partner Center.");
			return 0;
		}
		Submission submission = Submission.Read(await store.GetSubmission(id, ct));
		if (submission.Status != "PendingCommit") {
			gh.Log.WriteLine($"The submission in progress ({submission.Describe()}) is {submission.Status}: it stays.");
			return 0;
		}
		await store.DeleteSubmission(id, ct);
		gh.Warning($"This run's unfinished submission ({submission.Describe()}) was deleted from Partner Center, so it doesn't hold the next release back. The next scheduled run tries again.");
		return 0;
	}

	static Version Target(Dictionary<string, string> options) =>
		Versions.Parse(Need(options, "target")) ?? throw new ArgumentException($"--target {options["target"]} isn't a version.");

	static string Need(Dictionary<string, string> options, string name) =>
		options.TryGetValue(name, out string? value) && value.Length > 0 ? value : throw new ArgumentException($"--{name} is needed.");

	static Dictionary<string, string> Options(string[] args) {
		var options = new Dictionary<string, string>(StringComparer.Ordinal);
		for (int i = 0; i < args.Length; i++) {
			if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"\"{args[i]}\" isn't an option.");
			string name = args[i][2..];
			options[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
		}
		return options;
	}
}
