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

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using HEI.StoreSubmit;

namespace HEI.Agent.Tests;

/// <summary>
/// The Microsoft Store workflow's talk with Partner Center (HEI.StoreSubmit, run by .github/workflows/store.yml): when a
/// release goes to the Store and when it waits, the listing's What's new from the changelog, and the submission's
/// commit. Partner Center is a fake here, answering with the JSON its submission API gives (PartnerCenter\*.json, as
/// its documentation shows it); nothing reaches Microsoft.
/// </summary>
public sealed class StoreSubmitTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-store-" + Guid.NewGuid().ToString("N"));

	public StoreSubmitTests() => Directory.CreateDirectory(dir);

	public void Dispose() {
		try { Directory.Delete(dir, true); } catch { }
	}

	static readonly Version V137 = Versions.Parse("1.9.137")!;

	static JsonObject Fixture(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PartnerCenter", name)))!.AsObject();

	static string Repo() {
		for (string? d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d))
			if (File.Exists(Path.Combine(d, "Heiward.sln"))) return d;
		throw new InvalidOperationException("No Heiward.sln above " + AppContext.BaseDirectory);
	}

	/// <summary>A submission as Partner Center reads it: one package of that version, kept.</summary>
	static Submission At(string status, string? version, string id = "2") {
		JsonObject json = Fixture("submission-draft.json");
		json["id"] = id;
		json["status"] = status;
		json["applicationPackages"] = version == null ? new JsonArray() : new JsonArray(new JsonObject {
			["fileName"] = $"Heiward-{version}.msixbundle", ["fileStatus"] = "Uploaded", ["version"] = version + ".0",
		});
		return Submission.Read(json);
	}

	static StoreState Published(string version, Submission? pending = null) => new(At("Published", version, "1"), pending);

	// ---- When a release goes, and when it waits ----

	[Fact]
	public void Versions_OfAReleaseAndOfAPackage_AreTheSame() {
		Assert.Equal(Versions.Parse("v1.9.137"), Versions.Parse("1.9.137.0"));
		Assert.Equal("1.9.137", Versions.Show(Versions.Parse("1.9.137.0")!));
		Assert.Null(Versions.Parse("Unreleased"));
	}

	[Fact]
	public void NothingInProgress_AndTheStoreOlder_Submits() {
		var d = Rules.Decide(Published("1.9.130"), V137);
		Assert.Equal(Verdict.Submit, d.Verdict);
		Assert.Equal("Heiward 1.9.137 goes to the Microsoft Store, which has Heiward 1.9.130 now.", d.Message);
	}

	[Fact]
	public void NothingPublished_SendsNothing_TheFirstGoesByHand() {
		foreach (Submission? pending in new[] { null, At("Certification", "1.9.130") }) {
			var d = Rules.Decide(new StoreState(null, pending), V137);
			Assert.Equal(Verdict.NotPublished, d.Verdict);
			Assert.False(d.Submits);
			Assert.Contains("no published submission", d.Message);
			Assert.Contains("by hand", d.Message);
		}
	}

	[Theory]
	[InlineData("CommitStarted", "being committed")]
	[InlineData("PreProcessing", "being checked")]
	[InlineData("Certification", "in certification")]
	[InlineData("Release", "through certification, waiting to be published")]
	[InlineData("PendingPublication", "being published")]
	[InlineData("Publishing", "being published")]
	public void ASubmissionOnItsWay_HoldsTheRelease_NamingBoth(string status, string words) {
		var d = Rules.Decide(Published("1.9.130", At(status, "1.9.135")), V137);
		Assert.Equal(Verdict.Hold, d.Verdict);
		Assert.False(d.Warn);
		Assert.StartsWith("Heiward 1.9.137 waits: Heiward 1.9.135 is " + words + " in the Microsoft Store.", d.Message);
		Assert.Contains("the next Friday run sends the newest release", d.Message);
	}

	[Fact]
	public void TheSameVersion_InCertification_IsNothingToSend() {
		var d = Rules.Decide(Published("1.9.130", At("Certification", "1.9.137")), V137);
		Assert.Equal(Verdict.Current, d.Verdict);
		Assert.Equal("Heiward 1.9.137 is in certification in the Microsoft Store already: nothing to send.", d.Message);
	}

	[Theory]
	[InlineData("PendingCommit", "a draft of Heiward 1.9.135 is in Partner Center, not sent yet")]
	[InlineData("Canceled", "Heiward 1.9.135 was canceled in Partner Center")]
	[InlineData("None", "Heiward 1.9.135 is in Partner Center (None)")]
	[InlineData("SomethingNew", "Heiward 1.9.135 is in Partner Center (SomethingNew)")]
	public void ADraft_IsNeverReplaced(string status, string words) {
		var d = Rules.Decide(Published("1.9.130", At(status, "1.9.135")), V137);
		Assert.Equal(Verdict.Hold, d.Verdict);
		Assert.StartsWith("Heiward 1.9.137 waits: " + words, d.Message);
		Assert.Contains("Send it or delete it there", d.Message);
	}

	[Fact]
	public void ADraftWithoutAVersion_IsNamedAsPartnerCenterNamesIt() {
		var d = Rules.Decide(Published("1.9.130", At("PendingCommit", null)), V137);
		Assert.StartsWith("Heiward 1.9.137 waits: a draft of Submission 2 is in Partner Center", d.Message);
	}

	[Fact]
	public void AFailedCommit_IsReplaced() {
		JsonObject json = Fixture("submission-draft.json");
		json["status"] = "CommitFailed";
		json["statusDetails"] = Fixture("status-commit-failed.json")["statusDetails"]!.DeepClone();
		var d = Rules.Decide(Published("1.9.130", Submission.Read(json)), V137);
		Assert.Equal(Verdict.Submit, d.Verdict);
		Assert.Contains("in place of Heiward 1.9.137, whose commit failed (InvalidParameterValue: The version of package", d.Message);
	}

	[Theory]
	[InlineData("CertificationFailed", "failed certification")]
	[InlineData("PreProcessingFailed", "failed the Store's package checks")]
	[InlineData("ReleaseFailed", "failed to be published")]
	[InlineData("PublishFailed", "failed to be published")]
	public void AFailedCertification_Holds_WithAWarning(string status, string words) {
		var d = Rules.Decide(Published("1.9.130", At(status, "1.9.135")), V137);
		Assert.Equal(Verdict.Hold, d.Verdict);
		Assert.True(d.Warn);
		Assert.StartsWith($"Heiward 1.9.137 waits: Heiward 1.9.135 {words} in the Microsoft Store.", d.Message);
		Assert.Contains("Partner Center's report says why", d.Message);
	}

	[Fact]
	public void TheStoreHasItOrNewer_NothingToSend() {
		var same = Rules.Decide(Published("1.9.137"), V137);
		Assert.Equal(Verdict.Current, same.Verdict);
		Assert.Equal("Heiward 1.9.137 is in the Microsoft Store already: nothing to send.", same.Message);
		var newer = Rules.Decide(Published("1.9.140"), V137);
		Assert.Equal(Verdict.Current, newer.Verdict);
		Assert.Equal("The Microsoft Store has Heiward 1.9.140, newer than 1.9.137: nothing to send.", newer.Message);
	}

	[Fact]
	public void ASubmission_KeepsTheVersionOfThePackagesItDoesntDelete() {
		// msstore's draft: the old bundle marked for deletion, the new one not processed yet (no version, only its file's name).
		Submission draft = Submission.Read(Fixture("submission-draft.json"));
		Assert.Equal("PendingCommit", draft.Status);
		Assert.Equal(V137, draft.Version);
		Assert.Equal("Heiward 1.9.137", draft.Describe());
		Assert.Equal(Versions.Parse("1.9.130"), Submission.Read(Fixture("submission-published.json")).Version);
	}

	[Fact]
	public void TheApp_WithoutASubmissionInProgress_HasNone() {
		JsonObject app = Fixture("application.json");
		Assert.Equal(FakePartnerCenter.PendingId, PartnerCenter.SubmissionId(app, "pendingApplicationSubmission"));
		app["pendingApplicationSubmission"] = null;
		Assert.Null(PartnerCenter.SubmissionId(app, "pendingApplicationSubmission"));
		app.Remove("pendingApplicationSubmission");
		Assert.Null(PartnerCenter.SubmissionId(app, "pendingApplicationSubmission"));
		Assert.Equal(FakePartnerCenter.PublishedId, PartnerCenter.SubmissionId(app, "lastPublishedApplicationSubmission"));
	}

	// ---- What's new in this version ----

	const string Changelog = """
		# Heiward's changelog

		Each version of Heiward, newest first.

		## 1.9.138

		**Too new for this submission.**

		### What changed

		- Not in it.

		### Before you update

		- Nothing: it updates itself as usual.

		## 1.9.137

		**The Store version leaves Castellan's Heiward alone.**

		### What's new

		- An **Open Heiward** button, to [Castellan's page](https://example.com/page) at `heiward.localhost`.

		### What changed

		- The Store version no longer stops Castellan's Heiward.
		  - Its scans never replace Castellan's.
		- Kit 2.152.5: Something only the staff need.
		  - A detail of the kit.

		### Before you update

		- Setup runs again.

		## 1.9.136

		**It carries kit 2.152.4: the parts all the staff share.**

		### What changed

		- Kit 2.152.4: Model servers answer only this account's staff.

		### Before you update

		Nothing: it updates itself as usual.

		## 1.9.135

		**Brings 2 changes: Scans are faster; The Games list no longer greys out games.**

		### What changed

		- Scans are faster.
		- The Games list no longer greys out games.

		### Before you update

		- Nothing: it updates itself as usual.

		## 1.9.134

		**In the Store already.**

		### What changed

		- Published before.
		""";

	[Fact]
	public void WhatsNew_TheVersionsSinceThePublishedOne_NewestFirst_InPlainText() {
		string notes = ReleaseNotes.For(Changelog, Versions.Parse("1.9.134"), V137);
		Assert.Equal("""
			1.9.137: The Store version leaves Castellan's Heiward alone.
			• An Open Heiward button, to Castellan's page at heiward.localhost.
			• The Store version no longer stops Castellan's Heiward.
			  – Its scans never replace Castellan's.

			1.9.135
			• Scans are faster.
			• The Games list no longer greys out games.
			""".ReplaceLineEndings("\n"), notes);
	}

	[Fact]
	public void WhatsNew_OnlyTheKit_SaysSo_Plainly() {
		Assert.Equal(ReleaseNotes.Fallback, ReleaseNotes.For(Changelog, Versions.Parse("1.9.135"), Versions.Parse("1.9.136")!));
		Assert.Equal(ReleaseNotes.Fallback, ReleaseNotes.For("# Heiward's changelog\n", null, V137));
	}

	[Fact]
	public void WhatsNew_AVersionOfOnlySmallFixes_IsLeftOut() {
		const string changelog = """
			## 1.9.140

			### What changed

			- Small fixes and improvements.

			### Before you update

			- Nothing: it updates itself as usual.

			## 1.9.139

			### What changed

			- Scans are faster.
			""";
		Assert.Equal("1.9.139\n• Scans are faster.", ReleaseNotes.For(changelog, null, Versions.Parse("1.9.140")!));
		Assert.Equal(ReleaseNotes.Fallback, ReleaseNotes.For(changelog, Versions.Parse("1.9.139"), Versions.Parse("1.9.140")!));
	}

	[Fact]
	public void WhatsNew_NothingPublishedBefore_TakesEveryVersionUpToIt() {
		string notes = ReleaseNotes.For(Changelog, null, V137);
		Assert.StartsWith("1.9.137: ", notes);
		Assert.EndsWith("1.9.134: In the Store already.\n• Published before.", notes);
		Assert.DoesNotContain("1.9.138", notes);
	}

	[Fact]
	public void WhatsNew_IsCutAtAWholeLine_ToFitTheStoresLimit() {
		var log = new StringBuilder("# Heiward's changelog\n\n");
		for (int v = 200; v > 100; v--)
			log.Append($"## 1.9.{v}\n\n**Version {v} makes things better.**\n\n### What changed\n\n- {new string('x', 80)} {v}.\n- Another line for {v}.\n\n");
		string all = ReleaseNotes.For(log.ToString(), null, Versions.Parse("1.9.200")!, int.MaxValue);
		string notes = ReleaseNotes.For(log.ToString(), null, Versions.Parse("1.9.200")!);
		Assert.True(ReleaseNotes.Cost(all) > ReleaseNotes.Limit);
		Assert.InRange(ReleaseNotes.Cost(notes), ReleaseNotes.Limit - 150, ReleaseNotes.Limit);
		Assert.StartsWith(notes, all);
		Assert.True(all.Length == notes.Length || all[notes.Length] == '\n', "cut inside a line");
		Assert.False(notes.EndsWith('\n'));
	}

	[Fact]
	public void WhatsNew_ALineLongerThanTheField_IsCutAtAWord() {
		string log = "## 1.9.137\n\n**Long.**\n\n### What changed\n\n- " + string.Join(' ', Enumerable.Repeat("word", 400)) + "\n";
		string notes = ReleaseNotes.For(log, null, V137);
		Assert.Equal("1.9.137: Long.", notes);
		// The version's own line, longer than the whole field.
		notes = ReleaseNotes.For("## 1.9.137\n\n**" + string.Join(' ', Enumerable.Repeat("word", 400)) + "**\n\n### What changed\n\n- Short.\n", null, V137, 50);
		Assert.StartsWith("1.9.137: word word ", notes);
		Assert.EndsWith(" word…", notes);
		Assert.True(ReleaseNotes.Cost(notes) <= 50);
	}

	[Fact]
	public void WhatsNew_FromHeiwardsOwnChangelog_FitsAndReadsPlainly() {
		string changelog = File.ReadAllText(Path.Combine(Repo(), "CHANGELOG.md"));
		string notes = ReleaseNotes.For(changelog, Versions.Parse("1.9.100"), V137);
		Assert.InRange(ReleaseNotes.Cost(notes), 200, ReleaseNotes.Limit);
		Assert.StartsWith("1.9.137: Heiward from the Microsoft Store leaves the Heiward that Castellan keeps alone.\n• ", notes);
		foreach (string markup in new[] { "**", "`", "](", "###", "Kit 2.", "Steward's kit", "Before you update", "updates itself as usual", ReleaseNotes.Fallback })
			Assert.DoesNotContain(markup, notes);
	}

	// ---- The workflow's steps, against a fake Partner Center ----

	sealed record Ran(int Code, string Log, Dictionary<string, string> Outputs, string Summary, List<TimeSpan> Waits);

	async Task<Ran> Run(FakePartnerCenter pc, string[] args, Dictionary<string, string?>? extra = null) {
		string outputs = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".out"), summary = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".md");
		var env = new Dictionary<string, string?> {
			[Credentials.TenantVar] = FakePartnerCenter.Tenant,
			[Credentials.ClientVar] = FakePartnerCenter.Client,
			[Credentials.SecretVar] = FakePartnerCenter.GoodSecret,
			["STORE_ID"] = "9NX5K0L4CW3D",
			["GITHUB_OUTPUT"] = outputs,
			["GITHUB_STEP_SUMMARY"] = summary,
		};
		foreach (var (k, v) in extra ?? []) env[k] = v;
		var log = new StringWriter();
		var waits = new List<TimeSpan>();
		int code = await Cli.Run(args, k => env.GetValueOrDefault(k), pc, log, (t, _) => { waits.Add(t); return Task.CompletedTask; });
		var values = new Dictionary<string, string>();
		if (File.Exists(outputs))
			foreach (string line in File.ReadAllLines(outputs)) values[line[..line.IndexOf('=')]] = line[(line.IndexOf('=') + 1)..];
		return new Ran(code, log.ToString(), values, File.Exists(summary) ? File.ReadAllText(summary) : "", waits);
	}

	string WriteChangelog() {
		string path = Path.Combine(dir, "CHANGELOG.md");
		File.WriteAllText(path, Changelog.Replace("## 1.9.134", "## 1.9.130"));
		return path;
	}

	static void NeverShowsTheCredentials(Ran ran) {
		foreach (string secret in new[] { FakePartnerCenter.GoodSecret, FakePartnerCenter.Client, FakePartnerCenter.Tenant, FakePartnerCenter.Token })
			Assert.DoesNotContain(secret, ran.Log + ran.Summary + string.Join(' ', ran.Outputs.Values));
	}

	[Fact]
	public async Task Check_SignsInAsTheEntraApp_AndSaysWhatGoes() {
		var pc = new FakePartnerCenter();
		pc.NoPending();
		Ran ran = await Run(pc, ["check", "--target", "1.9.137"]);
		Assert.Equal(0, ran.Code);
		Assert.Equal("true", ran.Outputs["submit"]);
		Assert.Equal("", ran.Outputs["pending"]);
		Assert.Equal("1.9.130", ran.Outputs["published"]);
		Assert.Contains("::notice title=Microsoft Store::Heiward 1.9.137 goes to the Microsoft Store, which has Heiward 1.9.130 now.", ran.Log);
		Assert.Contains("Heiward 1.9.137 goes to the Microsoft Store", ran.Summary);
		// Client credentials, for the submission API's resource, in the app's tenant.
		Assert.Equal($"/{FakePartnerCenter.Tenant}/oauth2/token", pc.Calls[0].Path);
		var form = HttpUtility.ParseQueryString(pc.Calls[0].Body);
		Assert.Equal("client_credentials", form["grant_type"]);
		Assert.Equal(FakePartnerCenter.Client, form["client_id"]);
		Assert.Equal("https://manage.devcenter.microsoft.com", form["resource"]);
		Assert.Equal(new[] { "GET applications/9NX5K0L4CW3D", $"GET applications/9NX5K0L4CW3D/submissions/{FakePartnerCenter.PublishedId}" }, pc.ApiCalls());
		NeverShowsTheCredentials(ran);
	}

	[Fact]
	public async Task Check_InCertification_SendsNothing_AndPasses() {
		var pc = new FakePartnerCenter();
		pc.Pending("Certification", "1.9.135");
		Ran ran = await Run(pc, ["check", "--target", "1.9.137"]);
		Assert.Equal(0, ran.Code);
		Assert.Equal("false", ran.Outputs["submit"]);
		Assert.Equal(FakePartnerCenter.PendingId, ran.Outputs["pending"]);
		Assert.Contains("::notice title=Microsoft Store::Heiward 1.9.137 waits: Heiward 1.9.135 is in certification", ran.Log);
		Assert.DoesNotContain(pc.Calls, c => c.Method != "GET" && c.Method != "POST");
	}

	[Fact]
	public async Task Check_AFailedCertification_IsAWarning() {
		var pc = new FakePartnerCenter();
		pc.Pending("CertificationFailed", "1.9.135");
		Ran ran = await Run(pc, ["check", "--target", "1.9.137"]);
		Assert.Equal(0, ran.Code);
		Assert.Equal("false", ran.Outputs["submit"]);
		Assert.Contains("::warning title=Microsoft Store::Heiward 1.9.137 waits: Heiward 1.9.135 failed certification", ran.Log);
	}

	[Fact]
	public async Task OnARunner_TheTokenIsMaskedBeforeAnythingElse() {
		var pc = new FakePartnerCenter();
		pc.NoPending();
		Ran ran = await Run(pc, ["check", "--target", "1.9.137"], new() { ["GITHUB_ACTIONS"] = "true" });
		string[] lines = ran.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal("::add-mask::" + FakePartnerCenter.Token, lines[0].TrimEnd());
		Assert.Single(lines, l => l.Contains(FakePartnerCenter.Token));
		Assert.DoesNotContain(FakePartnerCenter.GoodSecret, ran.Log);
	}

	[Fact]
	public async Task AWrongKey_FailsTheRun_WithoutShowingTheCredentials() {
		var pc = new FakePartnerCenter { Secret = "the-key-that-expired" };
		Ran ran = await Run(pc, ["check", "--target", "1.9.137"]);
		Assert.Equal(1, ran.Code);
		Assert.Contains("::error title=Microsoft Store::Microsoft's sign-in refused the Entra app (401): AADSTS7000215: Invalid client secret provided for app '***'.", ran.Log);
		Assert.DoesNotContain("Trace ID", ran.Log);
		Assert.Empty(pc.ApiCalls());
		NeverShowsTheCredentials(ran);
	}

	[Fact]
	public async Task NoSecrets_AsksForNothing() {
		var pc = new FakePartnerCenter();
		Ran ran = await Run(pc, ["check", "--target", "1.9.137"], new() { [Credentials.SecretVar] = "", [Credentials.TenantVar] = null });
		Assert.Equal(2, ran.Code);
		Assert.Contains("These secrets aren't set: PARTNER_CENTER_TENANT_ID, PARTNER_CENTER_CLIENT_SECRET", ran.Log);
		Assert.Empty(pc.Calls);
	}

	[Fact]
	public async Task NotAVersionOrNotAStep_IsRefusedBeforeSigningIn() {
		var pc = new FakePartnerCenter();
		Assert.Equal(2, (await Run(pc, ["check", "--target", "latest"])).Code);
		Assert.Equal(2, (await Run(pc, ["publish"])).Code);
		Assert.Equal(2, (await Run(pc, ["finish", "--target", "1.9.137", "--changelog", Path.Combine(dir, "none.md")])).Code);
		Assert.Empty(pc.Calls);
	}

	[Fact]
	public async Task Notes_PrintsWhatsNew_WithoutSigningIn() {
		var pc = new FakePartnerCenter();
		string changelog = WriteChangelog();
		Ran ran = await Run(pc, ["notes", "--target", "1.9.137", "--after", "1.9.130", "--changelog", changelog], new() { [Credentials.SecretVar] = null });
		Assert.Equal(0, ran.Code);
		Assert.StartsWith(ReleaseNotes.For(File.ReadAllText(changelog), Versions.Parse("1.9.130"), V137) + "\n(", ran.Log.ReplaceLineEndings("\n"));
		Assert.Empty(pc.Calls);
	}

	[Fact]
	public async Task PartnerCentersOwnTrouble_IsTriedAgain() {
		var pc = new FakePartnerCenter();
		pc.NoPending();
		pc.Troubles.Enqueue(503);
		pc.Troubles.Enqueue(429);
		Ran ran = await Run(pc, ["check", "--target", "1.9.137"]);
		Assert.Equal(0, ran.Code);
		Assert.Equal("true", ran.Outputs["submit"]);
		Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) }, ran.Waits);
	}

	[Fact]
	public async Task Finish_SetsWhatsNew_KeepsTheRest_AndCommits() {
		var pc = new FakePartnerCenter();
		pc.Statuses.Enqueue(new JsonObject { ["status"] = "CommitStarted" });
		pc.Statuses.Enqueue(new JsonObject { ["status"] = "PreProcessing" });
		JsonObject before = pc.Submissions[FakePartnerCenter.PendingId].DeepClone().AsObject();
		Ran ran = await Run(pc, ["finish", "--target", "1.9.137", "--changelog", WriteChangelog(), "--before", "", "--draft", "false"]);
		Assert.Equal(0, ran.Code);

		string draft = $"applications/9NX5K0L4CW3D/submissions/{FakePartnerCenter.PendingId}";
		Assert.Equal(new[] {
			"GET applications/9NX5K0L4CW3D", "GET " + draft, $"GET applications/9NX5K0L4CW3D/submissions/{FakePartnerCenter.PublishedId}",
			"PUT " + draft, "POST " + draft + "/commit", "GET " + draft + "/status", "GET " + draft + "/status",
		}, pc.ApiCalls());

		// The PUT is the draft as Partner Center gave it, with only its What's new changed.
		JsonObject put = JsonNode.Parse(pc.Calls.Last(c => c.Method == "PUT").Body)!.AsObject();
		string notes = put["listings"]!["en-us"]!["baseListing"]!["releaseNotes"]!.ToString();
		Assert.Equal(ReleaseNotes.For(File.ReadAllText(WriteChangelog()), Versions.Parse("1.9.130"), V137), notes);
		Assert.StartsWith("1.9.137: The Store version leaves Castellan's Heiward alone.", notes);
		Assert.DoesNotContain("1.9.130", notes);
		before["listings"]!["en-us"]!["baseListing"]!["releaseNotes"] = notes;
		Assert.True(JsonNode.DeepEquals(before, put));

		Assert.Contains("::notice title=Microsoft Store::Heiward 1.9.137 is with the Microsoft Store for certification (PreProcessing).", ran.Log);
		Assert.Contains("### What's new in Heiward 1.9.137", ran.Summary);
		Assert.Equal(new[] { Cli.Poll, Cli.Poll }, ran.Waits);
		NeverShowsTheCredentials(ran);
	}

	[Fact]
	public async Task Finish_AsADraft_LeavesItForYouToSend() {
		var pc = new FakePartnerCenter();
		Ran ran = await Run(pc, ["finish", "--target", "1.9.137", "--changelog", WriteChangelog(), "--draft", "true"]);
		Assert.Equal(0, ran.Code);
		Assert.Contains(pc.Calls, c => c.Method == "PUT");
		Assert.DoesNotContain(pc.Calls, c => c.Path.EndsWith("/commit", StringComparison.Ordinal));
		Assert.Contains("Heiward 1.9.137 is a draft in Partner Center, for you to look at and send", ran.Log);
	}

	[Fact]
	public async Task Finish_AFailedCommit_FailsTheRun_SayingWhy() {
		var pc = new FakePartnerCenter();
		pc.Statuses.Enqueue(Fixture("status-commit-failed.json"));
		Ran ran = await Run(pc, ["finish", "--target", "1.9.137", "--changelog", WriteChangelog()]);
		Assert.Equal(1, ran.Code);
		Assert.Contains("::error title=Microsoft Store::Partner Center couldn't commit Heiward 1.9.137's submission: InvalidParameterValue: The version of package", ran.Log);
	}

	[Fact]
	public async Task Finish_ACommitStillGoing_AfterTheWait_Passes() {
		var pc = new FakePartnerCenter();
		Ran ran = await Run(pc, ["finish", "--target", "1.9.137", "--changelog", WriteChangelog()]);
		Assert.Equal(0, ran.Code);
		Assert.Contains("::warning title=Microsoft Store::Heiward 1.9.137's submission was still being committed after 15 minutes.", ran.Log);
		Assert.Equal((int)(Cli.CommitWait / Cli.Poll), ran.Waits.Count);
	}

	[Theory]
	[InlineData("the one there before")]
	[InlineData("not a draft")]
	[InlineData("without the package")]
	[InlineData("none at all")]
	public async Task Finish_TouchesOnlyTheDraftMsstoreJustMade(string what) {
		var pc = new FakePartnerCenter();
		string before = "";
		JsonObject draft = pc.Submissions[FakePartnerCenter.PendingId];
		switch (what) {
			case "the one there before": before = FakePartnerCenter.PendingId; break;
			case "not a draft": draft["status"] = "Certification"; break;
			case "without the package": draft["applicationPackages"]!.AsArray().RemoveAt(1); break;
			case "none at all": pc.NoPending(); break;
		}
		Ran ran = await Run(pc, ["finish", "--target", "1.9.137", "--changelog", WriteChangelog(), "--before", before]);
		Assert.Equal(1, ran.Code);
		Assert.Contains("::error title=Microsoft Store::", ran.Log);
		Assert.DoesNotContain(pc.Calls, c => c.Method is "PUT" or "DELETE" || c.Path.EndsWith("/commit", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Abandon_DeletesTheDraftThisRunMade() {
		var pc = new FakePartnerCenter();
		Ran ran = await Run(pc, ["abandon", "--before", ""]);
		Assert.Equal(0, ran.Code);
		Assert.Contains($"DELETE applications/9NX5K0L4CW3D/submissions/{FakePartnerCenter.PendingId}", pc.ApiCalls());
		Assert.Contains("This run's unfinished submission (Heiward 1.9.137) was deleted from Partner Center", ran.Log);
	}

	[Theory]
	[InlineData("there before")]
	[InlineData("already sent")]
	[InlineData("commit failed")]
	[InlineData("none")]
	public async Task Abandon_LeavesEveryOtherSubmission(string what) {
		var pc = new FakePartnerCenter();
		string before = "";
		switch (what) {
			case "there before": before = FakePartnerCenter.PendingId; break;
			case "already sent": pc.Submissions[FakePartnerCenter.PendingId]["status"] = "CommitStarted"; break;
			case "commit failed": pc.Submissions[FakePartnerCenter.PendingId]["status"] = "CommitFailed"; break;
			case "none": pc.NoPending(); break;
		}
		Ran ran = await Run(pc, ["abandon", "--before", before]);
		Assert.Equal(0, ran.Code);
		Assert.DoesNotContain(pc.Calls, c => c.Method == "DELETE");
	}

	// ---- The workflow ----

	[Fact]
	public void TheWorkflow_SendsOnFridaysAt10NewYorkTime_OneRunAtATime_AndKeepsTheSecretsInTheEnvironment() {
		string yml = File.ReadAllText(Path.Combine(Repo(), ".github", "workflows", "store.yml")).ReplaceLineEndings("\n");
		// One schedule, kept at 10:00 in New York through daylight saving time by GitHub's timezone key (cron alone is UTC).
		Assert.Matches(new Regex(@"^  schedule:\n    - cron: '0 10 \* \* 5'\n      timezone: 'America/New_York'\n  workflow_dispatch:", RegexOptions.Multiline), yml);
		// Friday's run and runs by hand queue in one group; a release's run, which only builds, is kept out of it, so a
		// release can't cancel Friday's run waiting there.
		Assert.Matches(new Regex(@"^concurrency:\n  group: \$\{\{ github\.event_name == 'release' && format\('store-build-\{0\}', github\.run_id\) \|\| 'store' \}\}\n  cancel-in-progress: false$", RegexOptions.Multiline), yml);
		// A release sends nothing: its run stops before Partner Center, and the submit job won't run for one anyway.
		int release = yml.IndexOf("if [[ \"$EVENT\" == \"release\" ]]; then", StringComparison.Ordinal);
		int ready = yml.IndexOf("echo \"ready=true\"", StringComparison.Ordinal);
		Assert.InRange(release, 0, ready);
		Assert.Contains("exit 0", yml[release..ready]);
		Assert.Contains("  submit:\n    needs: [check, build]\n    if: github.event_name != 'release' && needs.check.outputs.submit == 'true'\n", yml);
		// A secret reaches a step only through its env, never written into a script, where a log or a quote could show it.
		var secretLine = new Regex(@"^\s+[A-Z_]+: \$\{\{ secrets\.PARTNER_CENTER_(TENANT_ID|CLIENT_ID|CLIENT_SECRET|SELLER_ID) \}\}$");
		string[] uses = yml.Split('\n').Where(l => l.Contains("secrets.", StringComparison.Ordinal) && !l.Contains("secrets.GITHUB_TOKEN", StringComparison.Ordinal)).ToArray();
		Assert.NotEmpty(uses);
		Assert.All(uses, l => Assert.Matches(secretLine, l));
		Assert.Contains("STORE_SUBMIT", yml);
	}
}

/// <summary>Partner Center's sign-in and submission API, answering with the fixtures' JSON and keeping the submissions it holds.</summary>
sealed class FakePartnerCenter : HttpMessageHandler {
	public const string Tenant = "7d1e2a3b-4c5d-4e6f-8a9b-0c1d2e3f4a5b", Client = "c0ffee00-1234-4abc-9def-0123456789ab", Token = "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.fake-token-for-tests";
	public const string PublishedId = "1152921505690000001", PendingId = "1152921505690000002";
	public const string GoodSecret = "Qx8~fake.client-secret_for-tests";

	/// <summary>The key the Entra app has; the run sends <see cref="GoodSecret"/>.</summary>
	public string Secret { get; init; } = GoodSecret;

	public JsonObject App { get; } = Load("application.json");

	public Dictionary<string, JsonObject> Submissions { get; } = new() { [PublishedId] = Load("submission-published.json"), [PendingId] = Load("submission-draft.json") };

	/// <summary>What /status answers, one after another; then the submission's own status.</summary>
	public Queue<JsonObject> Statuses { get; } = new();

	/// <summary>Status codes the API answers with first, as trouble of its own.</summary>
	public Queue<int> Troubles { get; } = new();

	public List<(string Method, string Path, string Body)> Calls { get; } = [];

	static JsonObject Load(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PartnerCenter", name)))!.AsObject();

	public void NoPending() {
		App.Remove("pendingApplicationSubmission");
		Submissions.Remove(PendingId);
	}

	/// <summary>The submission in progress, at this status, with one package of this version.</summary>
	public void Pending(string status, string version) {
		JsonObject s = Submissions[PendingId];
		s["status"] = status;
		s["applicationPackages"] = new JsonArray(new JsonObject { ["fileName"] = $"Heiward-{version}.msixbundle", ["fileStatus"] = "Uploaded", ["version"] = version + ".0" });
	}

	public List<string> ApiCalls() => Calls.Where(c => !c.Path.EndsWith("/oauth2/token", StringComparison.Ordinal)).Select(c => c.Method + " " + c.Path).ToList();

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
		string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
		Uri uri = request.RequestUri!;
		if (uri.Host == "login.microsoftonline.com") {
			Calls.Add((request.Method.Method, uri.AbsolutePath, body));
			var form = HttpUtility.ParseQueryString(body);
			if (form["client_id"] != Client || form["client_secret"] != Secret)
				return Answer(HttpStatusCode.Unauthorized, new JsonObject {
					["error"] = "invalid_client",
					["error_description"] = $"AADSTS7000215: Invalid client secret provided for app '{Client}'.\r\nTrace ID: 3f2c0000-0000-0000-0000-000000000000\r\nCorrelation ID: 0000",
				});
			return Answer(HttpStatusCode.OK, new JsonObject { ["token_type"] = "Bearer", ["expires_in"] = "3599", ["access_token"] = Token });
		}
		Assert.Equal("manage.devcenter.microsoft.com", uri.Host);
		Assert.Equal("Bearer " + Token, request.Headers.Authorization?.ToString());
		string path = uri.AbsolutePath["/v1.0/my/".Length..];
		Calls.Add((request.Method.Method, path, body));
		if (Troubles.TryDequeue(out int trouble)) return Answer((HttpStatusCode)trouble, new JsonObject { ["code"] = "ServiceUnavailable", ["message"] = "Try again later." });

		const string AppPath = "applications/9NX5K0L4CW3D";
		if (path == AppPath && request.Method == HttpMethod.Get) return Answer(HttpStatusCode.OK, App.DeepClone());
		if (path.StartsWith(AppPath + "/submissions/", StringComparison.Ordinal)) {
			string[] parts = path[(AppPath.Length + "/submissions/".Length)..].Split('/');
			if (!Submissions.TryGetValue(parts[0], out JsonObject? submission))
				return Answer(HttpStatusCode.NotFound, new JsonObject { ["code"] = "ResourceNotFound", ["message"] = "No such submission." });
			if (parts.Length == 1 && request.Method == HttpMethod.Get) return Answer(HttpStatusCode.OK, submission.DeepClone());
			if (parts.Length == 1 && request.Method == HttpMethod.Put) {
				Submissions[parts[0]] = JsonNode.Parse(body)!.AsObject();
				return Answer(HttpStatusCode.OK, Submissions[parts[0]].DeepClone());
			}
			if (parts.Length == 1 && request.Method == HttpMethod.Delete) {
				Submissions.Remove(parts[0]);
				App.Remove("pendingApplicationSubmission");
				return new HttpResponseMessage(HttpStatusCode.NoContent);
			}
			if (parts is [_, "commit"] && request.Method == HttpMethod.Post) {
				submission["status"] = "CommitStarted";
				return Answer(HttpStatusCode.OK, new JsonObject { ["status"] = "CommitStarted" });
			}
			if (parts is [_, "status"] && request.Method == HttpMethod.Get) {
				if (Statuses.TryDequeue(out JsonObject? next)) {
					submission["status"] = next["status"]!.ToString();
					return Answer(HttpStatusCode.OK, next);
				}
				return Answer(HttpStatusCode.OK, new JsonObject { ["status"] = submission["status"]!.ToString() });
			}
		}
		return Answer(HttpStatusCode.NotFound, new JsonObject { ["code"] = "NotFound", ["message"] = request.Method + " " + path });
	}

	static HttpResponseMessage Answer(HttpStatusCode code, JsonNode json) =>
		new(code) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
}
