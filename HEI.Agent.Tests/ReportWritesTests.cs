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

namespace HEI.Agent.Tests;

/// <summary>The report written only when its sets change (the owner's disk rule, docs/LOOKALIKE-BAR.md).</summary>
[Collection(AgentHomeCollection.Name)] // HEIWARD_HOME is process-wide
public sealed class ReportWritesTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-reportwrites-" + Guid.NewGuid().ToString("N"));
	readonly string? heiwardHome = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public ReportWritesTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", Path.Combine(dir, "heiward"));
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", heiwardHome);
		try { Directory.Delete(dir, true); } catch { }
	}

	static readonly DateTime Six = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

	static Report Made(DateTime at, double seconds, params string[] notes) =>
		new(Report.CurrentVersion, at, seconds, "cpu", 1000, new() { @"C:\Photos" }, new(), notes.ToList(), new(), AppBuild.Current);

	[Fact]
	public void AScanThatFindsTheSameSets_WritesOnlyItsOwnFacts() {
		Made(Six, 30).Save();
		Made(Six.AddHours(6), 31).Save();
		Assert.Contains("2026-11-01T06:00:00", File.ReadAllText(AgentPaths.Report)); // the sets weren't written again
		Report loaded = Report.LoadAny()!;
		Assert.Equal(Six.AddHours(6), loaded.ScannedAtUtc);
		Assert.Equal(31, loaded.DurationSec);

		Made(Six.AddHours(12), 29, "a new note").Save(); // the sets changed: written whole
		Assert.Contains("a new note", File.ReadAllText(AgentPaths.Report));
		Assert.Contains("2026-11-01T18:00:00", File.ReadAllText(AgentPaths.Report));
		Assert.Equal(Six.AddHours(12), Report.LoadAny()!.ScannedAtUtc);
	}

	[Fact]
	public void AReportAnotherBuildWroteSince_KeepsItsOwnFacts() {
		Made(Six, 30).Save();
		Made(Six.AddHours(6), 31).Save();
		// An older build writes report.json whole, and no facts file.
		AgentPaths.WriteAtomic(AgentPaths.Report, JsonSerializer.Serialize(Made(Six.AddHours(1), 40) with { Build = "another" }, AgentConfig.Json));
		Report loaded = Report.LoadAny()!;
		Assert.Equal(Six.AddHours(1), loaded.ScannedAtUtc);
		Assert.Equal(40, loaded.DurationSec);
		Assert.Null(Report.Load()); // and it's still another build's, set aside

		Made(Six.AddHours(12), 29).Save(); // the same sets as before, but report.json isn't the one they were written to
		Assert.Equal(AppBuild.Current, Report.Load()?.Build);
		Assert.Equal(Six.AddHours(12), Report.Load()!.ScannedAtUtc);
	}

	[Fact]
	public void AFileSavedUnchanged_IsLeftAsItIs() {
		string path = Path.Combine(AgentPaths.Home, "status.json");
		AgentPaths.WriteAtomic(path, "{\"state\":\"idle\"}");
		DateTime old = DateTime.UtcNow.AddDays(-1);
		File.SetLastWriteTimeUtc(path, old);
		AgentPaths.WriteAtomic(path, "{\"state\":\"idle\"}");
		Assert.Equal(old, File.GetLastWriteTimeUtc(path));
		AgentPaths.WriteAtomic(path, "{\"state\":\"scanning\"}");
		Assert.NotEqual(old, File.GetLastWriteTimeUtc(path));
		Assert.Equal("{\"state\":\"scanning\"}", File.ReadAllText(path));
	}
}
