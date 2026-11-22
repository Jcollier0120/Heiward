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

using HEI.Core.AI;
using HEI.Core.Utils;

namespace HEI.Core.Tests;

/// <summary>
/// The database's checkpoint keeps the AI's embeddings so far too: they were saved only once the analysis ended, so a
/// scan's process ended under it (an update, a crash) lost every one, and the next scan read all its files again for them.
/// </summary>
[Collection("DatabaseUtils")] // the checkpoint saves the static DatabaseUtils state
public class CheckpointEmbeddingsTests {
	[Fact]
	public void ACheckpoint_KeepsTheEmbeddingsSoFar() {
		string dbDir = Directory.CreateTempSubdirectory("hei-checkpoint-embeddings").FullName;
		try {
			DatabaseUtils.CustomDatabaseFolder = dbDir;
			DatabaseUtils.InvalidateDatabaseFolder();
			// A store of its own, in this test's folder: the store tests' path override can't move it.
			string storePath = Path.Combine(dbDir, "UnionEmbeddings.test.db");
			UnionEmbeddingStore store = UnionEmbeddingStore.LoadFrom(storePath);
			var photo = new FileEntry { Folder = @"P:\Pictures", FileSize = 1000, DateModified = new DateTime(700_000_000_000L, DateTimeKind.Utc) };
			photo.Path = @"P:\Pictures\IMG_000001.jpg";
			var embedding = new byte[EmbeddingMath.Dimensions];
			new Random(1).NextBytes(embedding);
			store.Put(photo, 0, embedding);

			var engine = new ScanEngine { unionEmbeddingStore = store };
			engine.Settings.DatabaseCheckpointIntervalMinutes = 1;
			engine.InitProgress(1000, "");
			engine.lastCheckpointTime = DateTime.UtcNow - TimeSpan.FromMinutes(10);
			engine.IncrementProgress(photo.Path);

			// The process ended here, before the analysis did: what the next scan loads.
			Assert.Equal(embedding, UnionEmbeddingStore.LoadFrom(storePath).GetEmbedding(photo, 0));
		}
		finally {
			DatabaseUtils.CustomDatabaseFolder = null;
			DatabaseUtils.InvalidateDatabaseFolder();
			try { Directory.Delete(dbDir, true); } catch { /* best effort */ }
		}
	}
}
