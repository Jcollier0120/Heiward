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

namespace HEI.Agent.Tests;

/// <summary>The downloaded exe's setup window: the installed copy's page in setup mode (Installer.OpenSetupWindowAsync, StoreSetup).</summary>
public sealed class SetupWindowTests {
	[Fact]
	public void TheInstall_CopiesWhatTheDownloadedExesFolderHas() =>
		Assert.Equal("install --yes --no-browser --scan-speed background --device npu --reuse-from C:\\Users\\me\\Downloads",
			string.Join(' ', StoreSetup.Arguments(new SetupRequest("npu", false, "background"), reuse: [@"C:\Users\me\Downloads"])!));

	[Fact]
	public void ThePage_StartsInSetupMode_AllowedTheDownloadsFolder() =>
		Assert.Equal(["serve", "--setup", "--no-browser", "--reuse-from", @"C:\Users\me\Downloads"], Installer.SetupServeArguments(@"C:\Users\me\Downloads"));

	[Fact]
	public void TheWindow_IsAnEdgeAppWindow_OnThePage() {
		string[] args = Installer.EdgeArguments("http://heiward.localhost:28474/");
		Assert.Equal("--app=http://heiward.localhost:28474/", args[0]);
		Assert.Contains(args, a => a.StartsWith("--window-size=", StringComparison.Ordinal));
	}

	static readonly DateTime Seen = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

	[Theory]
	[InlineData(true, false, false, 4, true)]   // the window closed before the install: the page stops
	[InlineData(true, false, false, 2, false)]  // a poll missed, or the PC was busy: it waits
	[InlineData(true, false, true, 10, false)]  // the install is running: it finishes whatever the window does
	[InlineData(true, true, false, 10, false)]  // set up: the page stays, as the installed copy's
	[InlineData(false, false, false, 10, false)] // the review page as usual, or the Store version's
	public void ThePage_StopsOnlyWhenTheSetupWindowWasClosedBeforeTheInstall(bool firstRun, bool installed, bool running, int minutes, bool stops) =>
		Assert.Equal(stops, StoreSetup.Abandoned(firstRun, installed, running, Seen, Seen.AddMinutes(minutes)));
}
