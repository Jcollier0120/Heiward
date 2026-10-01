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

/// <summary>A report is the rules of the build that made it: another build's is set aside until a scan replaces it.</summary>
[Collection(AgentHomeCollection.Name)]
public sealed class ReportBuildTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-build-" + Guid.NewGuid().ToString("N"));
	readonly string? home = Environment.GetEnvironmentVariable("HEIWARD_HOME");

	public ReportBuildTests() {
		Directory.CreateDirectory(dir);
		Environment.SetEnvironmentVariable("HEIWARD_HOME", dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("HEIWARD_HOME", home);
		try { Directory.Delete(dir, true); } catch { }
	}

	static Report Made(string? build) => new(Report.CurrentVersion, new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), 1, "NPU", 10, new(), new(), new(),
		new() { new ReportGroup("a", "copies", "image", @"C:\Pictures\a.jpg", "kept", 100, 99.9f, new()) }, build);

	[Fact]
	public void TheBuildsOwnReport_Loads() {
		Made(AppBuild.Current).Save();
		Assert.NotNull(Report.Load());
		Assert.False(Report.IsStale());
	}

	[Theory]
	[InlineData("1.3.0+c5cd82a")] // the same version, another commit: 1.3.0's first build
	[InlineData(null)]            // a report from before builds were recorded
	public void AnotherBuildsReport_IsSetAside_UntilAScanReplacesIt(string? build) {
		Made(build).Save();
		Assert.Null(Report.Load());
		Assert.True(Report.IsStale());
		Assert.Equal("a", Assert.Single(Report.LoadAny()!.Groups).Key); // still there to tell which sets are new
	}

	[Fact]
	public void NoReport_IsNotStale() {
		Assert.Null(Report.Load());
		Assert.False(Report.IsStale());
	}

	[Fact]
	public void AnotherBuildsDeveloperReport_IsSetAside() {
		new DevReport { ScannedAtUtc = DateTime.UtcNow, Build = "1.3.0+c5cd82a" }.Save();
		Assert.Null(DevReport.Load());
		new DevReport { ScannedAtUtc = DateTime.UtcNow, Build = AppBuild.Current }.Save();
		Assert.NotNull(DevReport.Load());
	}

	[Fact]
	public void TheBuild_NamesTheVersionAndTheCommit() => Assert.Matches(@"^\d+\.\d+\.\d+\+[0-9a-f]{7,}", AppBuild.Current);
}
