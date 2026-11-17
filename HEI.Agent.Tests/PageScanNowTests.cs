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

using System.Text.Json;
using Jint;

namespace HEI.Agent.Tests;

/// <summary>
/// Scan now on a folder, Scan this drive now and Scan now instead of at 20:13, on the page: each starts its scan at once,
/// says so, and once that scan has ended says what it found, or why it read nothing. The owner, 2026-11-17: Scan now on
/// E:\Steam read nothing in a second (scans leave out a Steam folder's game library), and the page said nothing about it,
/// only "Last scanned: never. Next scheduled scan: today 08:13 PM", so the scan looked put off to then. app.js's own
/// functions run in a JavaScript engine, against a server whose posts answer when the test says.
/// </summary>
public sealed class PageScanNowTests {
	const string FakePage = """
		function makeEl(tag) {
		  return { tagName: tag.toUpperCase(), children: [], className: '', textContent: '', append: function () { } };
		}
		var document = { createElement: makeEl };
		var SEP = '\\';
		// The server: each post answers when the test says.
		var posts = [];
		function post(url, body) { return new Promise(function (ok, fail) { posts.push({ url: url, body: body, ok: ok, fail: fail }); }); }
		function answer(i) { posts[i].ok({}); }
		function refuse(i, words) { posts[i].fail(new Error(words)); }
		var notices = [], errors = [], refreshes = 0;
		function showNotice(m) { notices.push(m); } function showError(m) { if (m) errors.push(m); }
		function refresh() { refreshes++; }
		// The owner's second drive, which scheduled scans read; the next one due at 20:13.
		var drive = { root: 'E:\\', type: 'fixed', scans: 'auto' };
		var state = { drives: [drive], scan: { running: false }, schedule: { everyMinutes: 360, nextUtc: '2026-11-18T01:13:00Z' }, agent: { firstScanWaits: false } };
		function cardFor(p) { return drive; }
		function nameOf(p) { return p === 'E:\\' ? 'Storage 0 - WDB' : p.slice(p.lastIndexOf('\\') + 1); }
		function scanTime() { return 'today 08:13 PM'; }
		// The state the page draws next: the scan running, then ended as `last` says.
		function running() { sayScanEnded({ scan: { running: true } }); }
		function ended(last) { sayScanEnded({ scan: { running: false, last: last } }); }
		function startedNow() { return new Date(Date.now()).toISOString(); }
		""";

	readonly Engine js = new();

	public PageScanNowTests() {
		string script = ThePage.Asset("app.js").ReplaceLineEndings("\n");
		js.Execute(FakePage);
		js.Execute(Line(script, "const count = "));
		// scanDrive, otherDrivesStay, scanFolder, scanFolderWhy, scanAsked, sayScanEnded, scanEndedText and bumpScan, as the page has them.
		js.Execute(Slice(script, "async function scanDrive(", "\n/** A scan's time"));
	}

	static string Line(string script, string start) {
		int at = script.IndexOf("\n" + start, StringComparison.Ordinal);
		Assert.True(at >= 0, start);
		return script[(at + 1)..script.IndexOf('\n', at + 1)];
	}

	static string Slice(string script, string from, string to) {
		int start = script.IndexOf(from, StringComparison.Ordinal);
		Assert.True(start >= 0, from);
		int end = script.IndexOf(to, start + from.Length, StringComparison.Ordinal);
		Assert.True(end > start, to);
		return script[start..end];
	}

	/// <summary>Runs <paramref name="code"/>, then what it awaited that has answered, as the browser does once the script is done.</summary>
	void Run(string code) {
		js.Execute(code);
		js.Advanced.ProcessTasks();
	}

	T Js<T>(string expression) => JsonSerializer.Deserialize<T>(js.Evaluate($"JSON.stringify({expression})").AsString())!;

	string[] Notices => Js<string[]>("notices");
	string[] Errors => Js<string[]>("errors");

	[Fact]
	public void ScanNowOnAFolder_WithAScheduledScanDue_StartsAtOnce_AndSaysWhatItFound_NotTheScheduledScan() {
		Run("scanFolder('E:\\\\Steam');");
		Assert.Equal(("/api/scan/folder", @"E:\Steam"), (Js<string>("posts[0].url"), Js<string>("posts[0].body.path")));
		Assert.Empty(Notices); // nothing said before Heiward has answered
		Run("answer(0);");
		Assert.Equal(new[] { "Scanning Steam and the folders in it." }, Notices);
		Assert.Equal(1, Js<int>("refreshes")); // the page is drawn again at once, the scan running
		Run("running();");
		Assert.Single(Notices);
		// The owner's: a second later it has ended, having found nothing, since scans leave out the game library.
		Run("ended({ startedUtc: startedNow(), ok: true, exitCode: 0, scope: 'folder', asked: ['E:\\\\Steam'], found: 0," +
			" leftOut: [{ name: 'steamapps', why: 'Game library' }] });");
		Assert.Equal("Steam scanned: no photos or videos in it. Scans leave out its folder steamapps (game library), where pictures and" +
			" videos belong to something else. Right-click one to include it in scans.", Notices[^1]);
		Assert.DoesNotContain(Notices, n => n.Contains("08:13") || n.Contains("scheduled"));
		Assert.Empty(Errors);
		// Said once: the page's next draws say nothing more of it.
		Run("ended({ startedUtc: startedNow(), ok: true, exitCode: 0, scope: 'folder', found: 0 });");
		Assert.Equal(2, Notices.Length);
	}

	[Fact]
	public void ScanThisDriveNow_StartsAtOnce_AndSaysHowManyItFoundOnTheDrive() {
		Run("scanDrive('E:\\\\'); answer(0);");
		Assert.Equal(("/api/scan/drive", @"E:\"), (Js<string>("posts[0].url"), Js<string>("posts[0].body.root")));
		Assert.Equal("Scanning Storage 0 - WDB only. The other drives stay as their last scans found them, and the scheduled scans keep their times.", Notices[0]);
		Run("running(); ended({ startedUtc: startedNow(), ok: true, exitCode: 0, scope: 'drive', asked: ['E:\\\\'], found: 12 });");
		Assert.Equal("Storage 0 - WDB scanned: 12 photos and videos on it.", Notices[^1]);
		// One photo, and none.
		Run("scanDrive('E:\\\\'); answer(1);");
		Run("ended({ startedUtc: startedNow(), ok: true, exitCode: 0, scope: 'drive', found: 1 });");
		Assert.Equal("Storage 0 - WDB scanned: 1 photo or video on it.", Notices[^1]);
		Run("scanDrive('E:\\\\'); answer(2);");
		Run("ended({ startedUtc: startedNow(), ok: true, exitCode: 0, scope: 'drive', found: 0 });");
		Assert.Equal("Storage 0 - WDB scanned: no photos or videos on it.", Notices[^1]);
	}

	[Fact]
	public void ScanNowInsteadOfAtTheScheduledTime_StartsAtOnce_AndSaysWhenItHasEnded() {
		Run("bumpScan('2026-11-18T01:13:00Z'); answer(0);");
		Assert.Equal("/api/scan/bump", Js<string>("posts[0].url"));
		Assert.Equal("Scanning now. Once it has gone through, the scheduled scan at today 08:13 PM is skipped.", Notices[0]);
		Run("running(); ended({ startedUtc: startedNow(), ok: true, exitCode: 0, found: 40 });");
		Assert.Equal("Scan done: 40 photos and videos on the drives Heiward scans.", Notices[^1]);
	}

	[Fact]
	public void AScanThatReadNothing_SaysWhy() {
		Run("scanFolder('E:\\\\Steam'); answer(0);");
		Run("ended({ startedUtc: startedNow(), ok: false, exitCode: 1, scope: 'folder', why: 'that folder isn\\'t there now, or scans leave it out' });");
		Assert.Equal("The scan of Steam didn't go through: that folder isn't there now, or scans leave it out.", Notices[^1]);
		// Nor a why, nor an error: the code it ended with.
		Run("scanFolder('E:\\\\Steam'); answer(1);");
		Run("ended({ startedUtc: startedNow(), ok: false, exitCode: 3, scope: 'folder' });");
		Assert.Equal("The scan of Steam didn't go through: it ended with code 3.", Notices[^1]);
	}

	[Fact]
	public void AScanStopped_SaysSo() {
		Run("scanDrive('E:\\\\'); answer(0);");
		Run("ended({ startedUtc: startedNow(), ok: false, exitCode: 130, scope: 'drive' });");
		Assert.Equal("The scan of Storage 0 - WDB was stopped.", Notices[^1]);
	}

	[Fact]
	public void AScanThatNeverStarted_SaysSo_NotTheScanBeforeIt() {
		// The last scan the page knows of is an older one: this one ended before writing its own.
		Run("scanFolder('E:\\\\Steam'); answer(0);");
		Run("ended({ startedUtc: new Date(Date.now() - 3600000).toISOString(), ok: true, exitCode: 0, found: 900 });");
		Assert.Equal(new[] { "Scanning Steam and the folders in it." }, Notices);
		Assert.Equal(new[] { "The scan of Steam didn't start. Try again: it starts at once when no other scan is running." }, Errors);
		// Nor when the page has no last scan at all.
		Run("scanDrive('E:\\\\'); answer(1);");
		Run("ended(null);");
		Assert.Equal("The scan of Storage 0 - WDB didn't start. Try again: it starts at once when no other scan is running.", Errors[^1]);
	}

	[Fact]
	public void ScanNowRefused_WhileAnotherScanRuns_SaysWhyAndWhen_AndNothingLater() {
		const string Busy = "Another scan is running now, so this folder wasn't scanned. Once that scan has finished, scan this folder again.";
		Run($"scanFolder('E:\\\\Steam'); refuse(0, {JsonSerializer.Serialize(Busy)});");
		Assert.Equal(new[] { Busy }, Errors);
		Assert.Empty(Notices);
		Assert.Equal(0, Js<int>("refreshes"));
		// The scan that was running ends: it isn't this one, and the page doesn't speak of it as this one.
		Run("ended({ startedUtc: startedNow(), ok: true, exitCode: 0, found: 5 });");
		Assert.Empty(Notices);
		Assert.Single(Errors);
	}

	[Fact]
	public void TheMenusScanNow_WhileAScanRuns_SaysWhenItCan() {
		Assert.Null(js.Evaluate("scanFolderWhy()").ToObject());
		js.Execute("state.scan.running = true;");
		Assert.Equal("A scan is running now: once it has finished, this folder can be scanned.", js.Evaluate("scanFolderWhy()").AsString());
	}
}
