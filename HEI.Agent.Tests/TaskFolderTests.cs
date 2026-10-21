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

using System.Security.Principal;

namespace HEI.Agent.Tests;

/// <summary>
/// Each account's tasks in a folder of its own, \Heiward (&lt;account&gt;), and the move off older Heiwards' names: only
/// this account's tasks, never another's. Task Scheduler is a fake here: no test registers, ends or deletes a real task.
/// </summary>
[Collection(AgentHomeCollection.Name)] // the move writes to heiward.log: a folder of its own
public sealed class TaskFolderTests : IDisposable {
	readonly string dir = Directory.CreateTempSubdirectory("hei-tasks-test").FullName;
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public TaskFolderTests() => Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch (IOException) { }
	}

	static readonly string MySid = WindowsIdentity.GetCurrent().User!.Value;
	const string OtherSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";
	static readonly string Me = Environment.UserName;

	[Theory]
	// As Manor's and the Steward's kit's: the profile folder's name, which renaming the account doesn't change.
	[InlineData(@"C:\Users\Jcoll", "jeremy", "Jcoll")]
	[InlineData(@"C:\Users\Pat Smith\", null, "Pat Smith")]
	[InlineData(@"C:\Users\a:b*c?""d<e>f|g", null, "a_b_c__d_e_f_g")] // what a task's name can't hold
	[InlineData(null, "pat", "pat")] // no profile folder: the user's name
	[InlineData(null, null, "user")]
	[InlineData("", "", "user")]
	// Odd ones: a domain account's folder, slashes either way and doubled at the end, a drive alone.
	[InlineData(@"C:\Users\pat.CONTOSO.001", "pat", "pat.CONTOSO.001")]
	[InlineData(@"D:/Profiles/Ann O'Neil (work)//", null, "Ann O'Neil (work)")]
	[InlineData(@"C:\", "pat", "pat")]
	[InlineData(@"C:Ann", null, "Ann")]
	public void Account_IsTheProfileFoldersName_ElseTheUsers(string? profile, string? userName, string expected) =>
		Assert.Equal(expected, Scheduler.Account(profile, userName));

	[Fact]
	public void EveryTask_IsInThisAccountsFolder() {
		string folder = $@"\Heiward ({Scheduler.Account(Environment.GetEnvironmentVariable("USERPROFILE"), Environment.GetEnvironmentVariable("USERNAME"))})";
		Assert.Equal(folder, Scheduler.Folder);
		Assert.Equal(folder + @"\Scan", Scheduler.ScanTask);
		Assert.Equal(folder + @"\Open review page", Scheduler.OpenTask);
		Assert.Equal(folder + @"\Uninstall", Scheduler.UninstallTask);
		Assert.Equal(folder + @"\Remove GitHub copy", Scheduler.RemoveKeysTask);
		Assert.Equal([@"\Heiward\Scan", $@"\Heiward\{Me}\Scan"], Scheduler.OldNames("Scan"));
	}

	[Fact]
	public void Owned_GoesByThePrincipal_NotTheSignInTriggersUser() {
		Assert.True(Scheduler.Owned(Xml(MySid), MySid, @"PC\pat"));
		Assert.False(Scheduler.Owned(Xml(OtherSid), MySid, @"PC\pat"));
		Assert.False(Scheduler.Owned(Xml(MySid), null, @"PC\pat")); // this user's SID unknown: never assumed
		// The sign-in task names its user in its trigger too: another account's, whose principal is that account.
		Assert.False(Scheduler.Owned(Xml(OtherSid, trigger: @"PC\pat"), MySid, @"PC\pat"));
		Assert.True(Scheduler.Owned(Xml(@"PC\pat"), MySid, @"PC\pat"));
		Assert.True(Scheduler.Owned(Xml("pat"), MySid, @"PC\pat"));
		Assert.False(Scheduler.Owned(Xml(@"PC\sam"), MySid, @"PC\pat"));
		Assert.False(Scheduler.Owned(Xml(@"OTHER\pat"), MySid, @"PC\pat"));
	}

	[Fact]
	public void Owned_WithoutAPrincipalUser_GoesByTheAuthor_AndAGroupsTaskIsNoOnes() {
		string authored = $"<Task><RegistrationInfo><Author>{MySid}</Author></RegistrationInfo><Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType></Principal></Principals></Task>";
		Assert.True(Scheduler.Owned(authored, MySid, @"PC\pat"));
		Assert.False(Scheduler.Owned(authored.Replace("<LogonType>", "<GroupId>S-1-5-32-545</GroupId><LogonType>"), MySid, @"PC\pat"));
		Assert.False(Scheduler.Owned("<Task />", MySid, @"PC\pat"));
	}

	[Fact]
	public void DropOld_DeletesThisAccountsOldTasks_EndingTheScan_AndLeavesAnotherAccountsAlone() {
		var tasks = new FakeTasks {
			[@"\Heiward\Scan"] = MySid,                     // this account's, from before
			[@"\Heiward\Open review page"] = OtherSid,       // another account's, which Task Scheduler would let this one delete
			[$@"\Heiward\{Me}\Open review page"] = MySid,    // the folder an older install fell back to
			[$@"\Heiward\{Me}\Uninstall"] = MySid,
			[Scheduler.ScanTask] = MySid,                    // this account's own name: stays
		};
		List<string> dropped = Scheduler.DropOld(tasks.Run);

		Assert.Equal([@"\Heiward\Scan", $@"\Heiward\{Me}\Open review page", $@"\Heiward\{Me}\Uninstall"], dropped);
		Assert.Equal([Scheduler.ScanTask, @"\Heiward\Open review page"], tasks.Names.Order(StringComparer.Ordinal));
		// The scan is ended before its task goes; the others' short runs are left to finish.
		Assert.Equal([@"/End \Heiward\Scan", @"/Delete \Heiward\Scan", $@"/Delete \Heiward\{Me}\Open review page", $@"/Delete \Heiward\{Me}\Uninstall"], tasks.Changes);
		// Another account's task is only looked at.
		Assert.All(tasks.Calls.Where(c => c.Contains(@"\Heiward\Open review page")), c => Assert.StartsWith("/Query", c));
	}

	[Fact]
	public void DropOld_WithNoOldTask_ChangesNothing() {
		var tasks = new FakeTasks { [Scheduler.ScanTask] = MySid, [Scheduler.OpenTask] = MySid };
		Assert.Empty(Scheduler.DropOld(tasks.Run));
		Assert.Empty(tasks.Changes);
		Assert.Equal(2, tasks.Names.Count);
	}

	[Fact]
	public void HasOld_OnlyForThisAccountsOwn() {
		Assert.True(Scheduler.HasOld(new FakeTasks { [$@"\Heiward\{Me}\Scan"] = MySid }.Run));
		Assert.False(Scheduler.HasOld(new FakeTasks { [@"\Heiward\Scan"] = OtherSid, [@"\Heiward\Open review page"] = OtherSid }.Run));
		Assert.False(Scheduler.HasOld(new FakeTasks().Run));
	}

	[Fact]
	public void TheScanTask_IsLookedForByItsName_ThenByThisAccountsOldOne() {
		// Mid-update (the Store version, until its page moves its tasks): the old name still schedules this account's scans.
		var moved = new FakeTasks { [Scheduler.ScanTask] = MySid, [@"\Heiward\Scan"] = MySid };
		Assert.Equal((0, "TaskName: " + Scheduler.ScanTask), Scheduler.QueryScan(moved.Run));
		var notYet = new FakeTasks { [$@"\Heiward\{Me}\Scan"] = MySid };
		Assert.Equal((0, $@"TaskName: \Heiward\{Me}\Scan"), Scheduler.QueryScan(notYet.Run));
		// Another account's scan task isn't this account's schedule.
		var others = new FakeTasks { [@"\Heiward\Scan"] = OtherSid };
		Assert.NotEqual(0, Scheduler.QueryScan(others.Run).Item1);
		Assert.Empty(others.Changes);
	}

	[Fact]
	public void Uninstall_RemovesThisAccountsTasks_AndItsOwnOldOnes_NeverAnotherAccounts() {
		var tasks = new FakeTasks {
			[Scheduler.ScanTask] = MySid,
			[Scheduler.OpenTask] = MySid,
			[Scheduler.UninstallTask] = MySid, // what runs this uninstall: deleted, not ended
			[@"\Heiward\Scan"] = OtherSid,
			[$@"\Heiward\{Me}\Scan"] = MySid,
			[@"\Heiward\Open review page"] = MySid,
		};
		Scheduler.RemoveAll(tasks.Run);

		Assert.Equal([@"\Heiward\Scan"], tasks.Names);
		Assert.DoesNotContain(tasks.Changes, c => c.StartsWith("/End") && !c.EndsWith(@"\Scan"));
		Assert.Contains($@"/End \Heiward\{Me}\Scan", tasks.Changes);
		Assert.DoesNotContain(tasks.Changes, c => c.EndsWith(@" \Heiward\Scan"));
	}

	[Fact]
	public void Uninstall_WithNoTaskAtAll_IsFine() {
		var tasks = new FakeTasks();
		Scheduler.RemoveAll(tasks.Run);
		Assert.Empty(tasks.Names);
	}

	/// <summary>What schtasks /Query /XML gives for a task of Heiward's: the user it runs as, and the sign-in trigger's user.</summary>
	static string Xml(string principal, string? trigger = null) => $"""
		<?xml version="1.0" encoding="UTF-16"?>
		<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
		  <RegistrationInfo><URI>\Heiward\Scan</URI></RegistrationInfo>
		  <Triggers>{(trigger == null ? "" : $"<LogonTrigger><UserId>{trigger}</UserId></LogonTrigger>")}</Triggers>
		  <Principals>
		    <Principal id="Author">
		      <UserId>{principal}</UserId>
		      <LogonType>InteractiveToken</LogonType>
		    </Principal>
		  </Principals>
		</Task>
		""";

	/// <summary>Task Scheduler's tasks by name, each with the SID it runs as; schtasks' /Query, /End and /Delete on them.</summary>
	sealed class FakeTasks {
		readonly Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
		public readonly List<string> Calls = [];

		public string this[string name] { set => owners[name] = value; }
		public List<string> Names => [.. owners.Keys];
		/// <summary>The /End and /Delete calls, as "/End name".</summary>
		public List<string> Changes => [.. Calls.Where(c => !c.StartsWith("/Query"))];

		public (int, string) Run(string[] args) {
			Assert.Equal("/TN", args[1]);
			string name = args[2];
			Calls.Add(args[0] + " " + name);
			if (!owners.TryGetValue(name, out string? sid)) return (1, "ERROR: The system cannot find the file specified.");
			return args[0] switch {
				"/Query" when args.Contains("/XML") => (0, Xml(sid)),
				"/Query" => (0, "TaskName: " + name),
				"/End" => (0, "SUCCESS"),
				"/Delete" => (owners.Remove(name) ? 0 : 1, "SUCCESS"),
				_ => throw new InvalidOperationException("schtasks " + string.Join(' ', args)),
			};
		}

	}
}
