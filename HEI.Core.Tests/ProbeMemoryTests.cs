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

using HEI.Core.Utils;

namespace HEI.Core.Tests;

/// <summary>
/// Probe memory: ffprobe's answer that a file holds no video stands while the file is unchanged, so a scan doesn't start
/// ffprobe for it again; TypeScript's .ts files are told from videos by their first bytes and never counted as videos.
/// Flags from before the probe memory get one more look, once.
/// </summary>
[Collection("DatabaseUtils")] // DatabaseUtils is static
public class ProbeMemoryTests : IDisposable {
	readonly string dir;

	public ProbeMemoryTests() {
		dir = Directory.CreateTempSubdirectory("hei-probememory-test").FullName;
		DatabaseUtils.CustomDatabaseFolder = dir;
		DatabaseUtils.InvalidateDatabaseFolder();
		DatabaseUtils.Database.Clear();
	}

	public void Dispose() {
		DatabaseUtils.Database.Clear();
		DatabaseUtils.CustomDatabaseFolder = null;
		DatabaseUtils.InvalidateDatabaseFolder();
		try { Directory.Delete(dir, true); } catch { /* best effort */ }
	}

	string MarkerPath => Path.Combine(dir, "ScannedFiles.probeheal1");

	static MediaInfo Read() => new() { Duration = TimeSpan.FromSeconds(10), Streams = Array.Empty<MediaInfo.StreamInfo>() };

	static FileEntry Entry(string path, EntryFlags flags = 0, MediaInfo? info = null) => new() {
		_Path = path,
		Folder = Path.GetDirectoryName(path)!,
		FileSize = 1000,
		Flags = flags,
		mediaInfo = info,
	};

	static FileEntry Get(string name) => DatabaseUtils.Database.Single(e => Path.GetFileName(e.Path) == name);

	[Fact]
	public void KnownNotVideo_SkipsOnlyWhatFfprobeAnsweredBefore() {
		Assert.Null(ScanEngine.KnownNotVideo(Entry(@"C:\v\new.mp4"), alwaysRetry: false)); // never asked
		Assert.NotNull(ScanEngine.KnownNotVideo(Entry(@"C:\v\broken.mp4", EntryFlags.MetadataError), alwaysRetry: false));
		Assert.NotNull(ScanEngine.KnownNotVideo(Entry(@"C:\src\app.ts", EntryFlags.NotMedia), alwaysRetry: false));
		// Read before: what ffprobe said stands.
		Assert.Null(ScanEngine.KnownNotVideo(Entry(@"C:\v\read.mp4", EntryFlags.MetadataError, Read()), alwaysRetry: false));
		// Photos aren't probed.
		Assert.Null(ScanEngine.KnownNotVideo(Entry(@"C:\p\a.jpg", EntryFlags.IsImage | EntryFlags.MetadataError), alwaysRetry: false));
		// Retrying every failed file asks again.
		Assert.Null(ScanEngine.KnownNotVideo(Entry(@"C:\v\broken.mp4", EntryFlags.MetadataError), alwaysRetry: true));
		Assert.Null(ScanEngine.KnownNotVideo(Entry(@"C:\src\app.ts", EntryFlags.NotMedia), alwaysRetry: true));
	}

	[Fact]
	public void OldFailures_GetOneMoreLook_Once() {
		DatabaseUtils.Database.Add(Entry(@"C:\v\failed.mp4", EntryFlags.MetadataError));
		DatabaseUtils.Database.Add(Entry(@"C:\v\read.mp4", EntryFlags.MetadataError, Read()));
		DatabaseUtils.Database.Add(Entry(@"C:\p\a.jpg", EntryFlags.IsImage | EntryFlags.MetadataError));
		DatabaseUtils.SaveDatabase();
		File.Delete(MarkerPath); // as a database from before the probe memory
		DatabaseUtils.Database.Clear();

		Assert.True(DatabaseUtils.LoadDatabase());
		Assert.False(Get("failed.mp4").Flags.Has(EntryFlags.MetadataError));
		Assert.True(Get("read.mp4").Flags.Has(EntryFlags.MetadataError));
		Assert.True(Get("a.jpg").Flags.Has(EntryFlags.MetadataError));
		Assert.False(File.Exists(MarkerPath)); // only once the look is saved

		// The look finds no video again, and the scan saves: the file is skipped from then on.
		Get("failed.mp4").Flags.Set(EntryFlags.MetadataError);
		DatabaseUtils.SaveDatabase();
		Assert.True(File.Exists(MarkerPath));
		DatabaseUtils.Database.Clear();
		Assert.True(DatabaseUtils.LoadDatabase());
		Assert.True(Get("failed.mp4").Flags.Has(EntryFlags.MetadataError));
	}

	[Fact]
	public async Task FoundMedia_LeavesOutTextWithAVideosExtension() {
		string root = Path.Combine(dir, "files");
		Directory.CreateDirectory(root);
		File.WriteAllText(Path.Combine(root, "app.ts"), "export const answer = 42;\n");
		File.WriteAllBytes(Path.Combine(root, "clip.mp4"), new byte[64]);
		var engine = new ScanEngine();
		engine.Settings.CustomDatabaseFolder = dir;
		engine.Settings.IncludeImages = true;
		engine.Settings.IncludeSubDirectories = true;
		engine.Settings.IncludeList.Add(root);
		await engine.BuildFileList(CancellationToken.None);
		Assert.Equal(2, engine.FoundMedia().Count); // not known yet

		Get("app.ts").Flags.Set(EntryFlags.NotMedia);
		Assert.Equal(new[] { "clip.mp4" }, engine.FoundMedia().Select(f => Path.GetFileName(f.Path)));
		Assert.Equal(2, engine.FoundFiles.Count); // the listing itself is unchanged
	}
}
