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

namespace HEI.Agent.Tests;

/// <summary>
/// report.json, decisions.json and index.json kept parsed in memory (StampedFile), and parsed again only once the file has
/// changed: the review page read all three for each folder it listed (the owner, 2026-11-12: "When heiward deletes a lot
/// of pairs, it's slow to reload the ui"). A scan writes them from another process, so a file another process rewrote is
/// read again, even one rewritten to the same length within the same step of Windows' last-write time.
/// </summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME is process-wide
public sealed class StampedFileTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-stamped-" + Guid.NewGuid().ToString("N"));
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public StampedFileTests() {
		Directory.CreateDirectory(Path.Combine(dir, "heiward"));
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "heiward"));
		// The page in use: files are kept only then (KeptFiles). The tests below that need it otherwise say so.
		KeptFiles.PageUsed(DateTime.UtcNow);
	}

	/// <summary>The page last used longer ago than KeptFiles keeps files for.</summary>
	static void PageUnused() => KeptFiles.PageUsed(DateTime.UtcNow - KeptFiles.Idle - TimeSpan.FromMinutes(1));

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	static readonly DateTime Six = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

	static ReportItem Item(string name, bool keep) =>
		new(@"E:\Photos\" + name, name, @"E:\Photos", 100, 10, 10, "jpg", 0, 0, 0, Six, 100, false, keep ? "keep" : "identical", keep, !keep);

	static ReportGroup Set(string key, params string[] names) =>
		new(key, "copies", "image", @"E:\Photos\" + names[0], "oldest", 100, 100, names.Select((n, i) => Item(n, i == 0)).ToList());

	static Report Made(params ReportGroup[] sets) =>
		new(Report.CurrentVersion, Six, 30, "cpu", 1000, new() { @"E:\" }, new(), new() { "a note" }, sets.ToList(), AppBuild.Current);

	/// <summary>Another process puts <paramref name="contents"/> in place of <paramref name="path"/>, as a scan writes it: a new file moved over it.</summary>
	static void AnotherProcessWrites(string path, string contents, DateTime? writtenUtc = null) {
		string tmp = path + ".other.tmp";
		File.WriteAllText(tmp, contents);
		if (writtenUtc is { } at) File.SetLastWriteTimeUtc(tmp, at);
		using Process move = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c move /y \"{tmp}\" \"{path}\" >nul") {
			UseShellExecute = false, CreateNoWindow = true,
		})!;
		move.WaitForExit();
		Assert.Equal(0, move.ExitCode);
		Assert.False(File.Exists(tmp));
	}

	/// <summary>The same length as <paramref name="json"/>, with one set's key changed.</summary>
	static string SameLength(string json, string key, string other) {
		Assert.Equal(key.Length, other.Length);
		Assert.Contains($"\"{key}\"", json);
		return json.Replace($"\"{key}\"", $"\"{other}\"");
	}

	[Fact]
	public void TheReport_IsParsedOnce_WhileItsFileStaysTheSame() {
		Made(Set("a1", "a.jpg", "a copy.jpg")).Save();
		int before = Report.Saved.Parses;
		for (int i = 0; i < 5; i++) Assert.Equal("a1", Report.Load()!.Groups.Single().Key);
		Assert.Equal(before + 1, Report.Saved.Parses);
	}

	[Fact]
	public void AnotherProcessRewritingTheReport_IsSeenOnTheNextRead() {
		Made(Set("a1", "a.jpg", "a copy.jpg")).Save();
		Assert.Equal("a1", Report.Load()!.Groups.Single().Key);
		string json = JsonSerializer.Serialize(Made(Set("a1", "a.jpg", "a copy.jpg"), Set("b2", "b.jpg", "b copy.jpg")), AgentConfig.Json);
		AnotherProcessWrites(AgentPaths.Report, json);
		Assert.Equal(new[] { "a1", "b2" }, Report.Load()!.Groups.Select(g => g.Key));
	}

	[Fact]
	public void ARewriteToTheSameLength_AtTheSameLastWriteTime_IsSeen_WhileTheFileIsFresh() {
		// Windows' last-write time moves in steps: a scan writing twice within one keeps the same time.
		Made(Set("a1", "a.jpg", "a copy.jpg")).Save();
		DateTime written = File.GetLastWriteTimeUtc(AgentPaths.Report);
		Assert.Equal("a1", Report.Load()!.Groups.Single().Key);
		AnotherProcessWrites(AgentPaths.Report, SameLength(File.ReadAllText(AgentPaths.Report), "a1", "z9"), written);
		Assert.Equal(written, File.GetLastWriteTimeUtc(AgentPaths.Report));
		Assert.Equal("z9", Report.Load()!.Groups.Single().Key);
	}

	[Fact]
	public void AFileStillTheSame_IsntParsedAgain_FreshOrSettled() {
		Made(Set("a1", "a.jpg", "a copy.jpg")).Save();
		int before = Report.Saved.Parses;
		Report.Load();
		Report.Load();
		Assert.Equal(before + 1, Report.Saved.Parses);
		// Settled long ago (another build's report, a copy): read once, then taken as it is.
		string json = SameLength(File.ReadAllText(AgentPaths.Report), "a1", "c3");
		AnotherProcessWrites(AgentPaths.Report, json, DateTime.UtcNow.AddMinutes(-5));
		Assert.Equal("c3", Report.Load()!.Groups.Single().Key);
		Assert.Equal("c3", Report.Load()!.Groups.Single().Key);
		Assert.Equal(before + 2, Report.Saved.Parses);
	}

	[Fact]
	public void AReportThatGoes_IsGone() {
		Made(Set("a1", "a.jpg", "a copy.jpg")).Save();
		Assert.NotNull(Report.Load());
		File.Delete(AgentPaths.Report);
		Assert.Null(Report.Load());
		Assert.Null(Report.LoadAny());
	}

	[Fact]
	public void TheLastScansOwnFacts_AreLaidOverTheKeptReport_WithoutParsingItAgain() {
		Made(Set("a1", "a.jpg", "a copy.jpg")).Save();
		Assert.Equal(Six, Report.Load()!.ScannedAtUtc);
		int before = Report.Saved.Parses;
		// A later scan found the same sets: it writes its own facts only (report-scan.json), and report.json stays.
		(Made(Set("a1", "a.jpg", "a copy.jpg")) with { ScannedAtUtc = Six.AddHours(6), DurationSec = 31 }).Save();
		Report now = Report.Load()!;
		Assert.Equal(Six.AddHours(6), now.ScannedAtUtc);
		Assert.Equal(31, now.DurationSec);
		Assert.Equal(before, Report.Saved.Parses);
	}

	[Fact]
	public void WhatACallerChanges_InTheReport_DoesntReachTheNext() {
		Made(Set("a1", "a.jpg", "a copy.jpg"), Set("b2", "b.jpg", "b copy.jpg")).Save();
		Report first = Report.Load()!;
		first.Groups[0].Items.Clear();
		first.Groups.RemoveAt(1);
		first.Notes.Add("changed");
		first.Folders.Clear();
		Report next = Report.Load()!;
		Assert.Equal(new[] { "a1", "b2" }, next.Groups.Select(g => g.Key));
		Assert.Equal(2, next.Groups[0].Items.Count);
		Assert.Equal(new[] { "a note" }, next.Notes);
		Assert.Equal(new[] { @"E:\" }, next.Folders);
	}

	[Fact]
	public void TheDecisions_ThePagesOwnShowAtOnce_WithoutParsingTheFileAgain() {
		var cfg = new AgentConfig { KeepHistory = true };
		DecisionStore.Record(cfg, "a1", new Decision("kept", Six, new(), 0));
		Assert.True(DecisionStore.Load().ContainsKey("a1"));
		int before = DecisionStore.Saved.Parses;
		for (int i = 0; i < 20; i++)
			DecisionStore.Record(cfg, "k" + i, new Decision("recycled", Six, new() { @"E:\Photos\" + i + ".jpg" }, 100));
		var all = DecisionStore.Load();
		Assert.Equal(21, all.Count);
		Assert.Equal(new[] { @"E:\Photos\19.jpg" }, all["k19"].Recycled);
		Assert.Equal(before, DecisionStore.Saved.Parses);
	}

	[Fact]
	public void TheDecisions_AnotherProcessWrote_AreSeen_EvenRightAfterThePagesOwn() {
		var cfg = new AgentConfig { KeepHistory = true };
		DecisionStore.Record(cfg, "a1", new Decision("kept", Six, new(), 0));
		DateTime written = File.GetLastWriteTimeUtc(AgentPaths.Decisions);
		Assert.True(DecisionStore.Load().ContainsKey("a1"));
		// A scan's automatic cleanup, straight after: the same length, within the same step of the last-write time.
		AnotherProcessWrites(AgentPaths.Decisions, SameLength(File.ReadAllText(AgentPaths.Decisions), "a1", "b2"), written);
		Assert.Equal(new[] { "b2" }, DecisionStore.Load().Keys);
		// And a longer one, the usual case.
		AnotherProcessWrites(AgentPaths.Decisions, JsonSerializer.Serialize(new Dictionary<string, Decision> {
			["b2"] = new("kept", Six, new(), 0), ["c3"] = new("recycled", Six, new() { @"E:\c.jpg" }, 5, Auto: true),
		}, AgentConfig.Json));
		Assert.Equal(new[] { "b2", "c3" }, DecisionStore.Load().Keys.Order());
		Assert.True(DecisionStore.Load()["c3"].Auto);
	}

	[Fact]
	public void WhatACallerChanges_InTheDecisions_DoesntReachTheNext() {
		var cfg = new AgentConfig { KeepHistory = true };
		DecisionStore.Record(cfg, "a1", new Decision("recycled", Six, new() { @"E:\a.jpg" }, 100));
		var first = DecisionStore.Load();
		first["x"] = new Decision("kept", Six, new(), 0);
		first["a1"].Recycled.Add(@"E:\b.jpg");
		var next = DecisionStore.Load();
		Assert.Equal(new[] { "a1" }, next.Keys);
		Assert.Equal(new[] { @"E:\a.jpg" }, next["a1"].Recycled);
	}

	[Fact]
	public void TheIndex_IsParsedOnce_SeenAgainOnceRewritten_AndACallersChangesStayItsOwn() {
		static ScanIndex Index(params string[] folders) => ScanIndex.Build(Six, new[] { @"E:\" },
			folders.Select(f => (f + @"\a.jpg", 10L)), new Dictionary<string, TimeSpan>(), new Dictionary<string, TimeSpan>());
		Index(@"E:\Photos", @"E:\Photos\2019").Save();
		int before = ScanIndex.Saved.Parses;
		ScanIndex first = ScanIndex.Load()!;
		Assert.Equal((2, 20L), first.Subtree(@"E:\photos")); // its folders as the page asks, in any case
		first.Folders.Remove(@"E:\Photos\2019");
		first.Roots.Clear();
		ScanIndex next = ScanIndex.Load()!;
		Assert.Equal((2, 20L), next.Subtree(@"E:\Photos"));
		Assert.Equal(new[] { @"E:\" }, next.Roots);
		Assert.Equal(before + 1, ScanIndex.Saved.Parses);

		AnotherProcessWrites(ScanIndex.FilePath, JsonSerializer.Serialize(Index(@"E:\Photos", @"E:\Photos\2019", @"E:\Photos\2020"), AgentConfig.Json));
		Assert.Equal((3, 30L), ScanIndex.Load()!.Subtree(@"E:\Photos"));
		Assert.Equal(before + 2, ScanIndex.Saved.Parses);
	}

	[Fact]
	public void AFileThatCantBeParsed_IsReadAgainNextTime_AndKeepsNothing() {
		Made(Set("a1", "a.jpg", "a copy.jpg")).Save();
		Assert.NotNull(Report.Load());
		AnotherProcessWrites(AgentPaths.Report, "{ not json");
		Assert.Null(Report.LoadAny()); // logged as unreadable, as before
		Made(Set("b2", "b.jpg", "b copy.jpg")).Save();
		Assert.Equal("b2", Report.Load()!.Groups.Single().Key);
	}

	[Fact]
	public void WhileThePageIsntInUse_EachReadParsesTheFile_AndNothingIsKept() {
		// The page's process stays up all day; Manor reads the disk glance (the report, decisions and index) every minute.
		Made(Set("a1", "a.jpg", "a copy.jpg")).Save();
		Index().Save();
		PageUnused();
		int report = Report.Saved.Parses, index = ScanIndex.Saved.Parses;
		for (int i = 0; i < 3; i++) {
			Assert.Equal("a1", Report.Load()!.Groups.Single().Key);
			Assert.NotNull(ScanIndex.Load());
		}
		Assert.Equal(report + 3, Report.Saved.Parses);
		Assert.Equal(index + 3, ScanIndex.Saved.Parses);
		Assert.False(Report.Saved.Holds);
		Assert.False(ScanIndex.Saved.Holds);
		// A decision recorded meanwhile (a scan's automatic cleanup in this process) keeps nothing either.
		DecisionStore.Record(new AgentConfig { KeepHistory = true }, "a1", new Decision("kept", Six, new(), 0));
		Assert.False(DecisionStore.Saved.Holds);
		Assert.True(DecisionStore.Load().ContainsKey("a1"));
		Assert.False(DecisionStore.Saved.Holds);
	}

	[Fact]
	public void ThePageGoneUnused_LetsGoOfWhatsKept_AndTheNextReadParsesAgain() {
		Made(Set("a1", "a.jpg", "a copy.jpg")).Save();
		Index().Save();
		DecisionStore.Record(new AgentConfig { KeepHistory = true }, "a1", new Decision("kept", Six, new(), 0));
		DateTime used = DateTime.UtcNow;
		KeptFiles.PageUsed(used);
		Report.Load();
		ScanIndex.Load();
		Assert.True(Report.Saved.Holds && ScanIndex.Saved.Holds && DecisionStore.Saved.Holds);

		// A minute later, and just short of the idle time: kept.
		Assert.False(KeptFiles.DropIfIdle(used.AddMinutes(1)));
		Assert.False(KeptFiles.DropIfIdle(used + KeptFiles.Idle - TimeSpan.FromSeconds(1)));
		Assert.True(Report.Saved.Holds && ScanIndex.Saved.Holds && DecisionStore.Saved.Holds);
		// The idle time past: all let go.
		Assert.True(KeptFiles.DropIfIdle(used + KeptFiles.Idle));
		Assert.False(Report.Saved.Holds || ScanIndex.Saved.Holds || DecisionStore.Saved.Holds);
		Assert.False(KeptFiles.DropIfIdle(used + KeptFiles.Idle)); // nothing left to let go

		// The page used again: the next read parses the file, and keeps it.
		int before = Report.Saved.Parses;
		KeptFiles.PageUsed(DateTime.UtcNow);
		Assert.Equal("a1", Report.Load()!.Groups.Single().Key);
		Assert.Equal("a1", Report.Load()!.Groups.Single().Key);
		Assert.Equal(before + 1, Report.Saved.Parses);
		Assert.True(Report.Saved.Holds);
	}

	[Theory]
	[InlineData("/api/state", true)]
	[InlineData("/api/tree", true)]
	[InlineData("/api/thumb/a1/0", true)]
	[InlineData("/api/groups/a1/recycle", true)]
	[InlineData("/api/progress", true)]
	// Manor's: its tiles and its ping, every minute or so all day.
	[InlineData("/api/glance", false)]
	[InlineData("/api/disk", false)]
	[InlineData("/api/ping", false)]
	// Another agent's (Chamberlain comparing pictures), and another copy's installer.
	[InlineData("/api/pictures/compare", false)]
	[InlineData("/api/quit", false)]
	// The page's files: what follows them is.
	[InlineData("/app.js", false)]
	[InlineData("/", false)]
	public void OnlyThePagesOwnRequests_CountAsItsUse(string path, bool fromThePage) =>
		Assert.Equal(fromThePage, KeptFiles.FromThePage(new Microsoft.AspNetCore.Http.PathString(path)));

	static ScanIndex Index() => ScanIndex.Build(Six, new[] { @"E:\" }, new[] { (@"E:\Photos\a.jpg", 10L) },
		new Dictionary<string, TimeSpan>(), new Dictionary<string, TimeSpan>());
}
