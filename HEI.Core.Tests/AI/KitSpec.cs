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

namespace HEI.Core.Tests.AI;

/// <summary>
/// The Steward's kit, its spec part: the rules every implementation follows (NPU-QUEUE.md, ACCELERATORS.md) and the
/// cases each runs unchanged (npu-queue-vectors.json). tools\kit.ps1 fills it into kit\spec at the repository root, at
/// the version kit.json pins, and the build copies it beside the tests (HEI.Core.Tests.csproj); without it the build
/// stops first, so a missing kit is never a test failure.
/// </summary>
static class KitSpec {
	public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "kit", "spec", name);

	/// <summary>npu-queue-vectors.json: the line's order ("order"), dead waiters ("dead"), and each accelerator's lock folders and line ("slots").</summary>
	public static readonly JsonElement QueueVectors = JsonDocument.Parse(File.ReadAllText(PathOf("npu-queue-vectors.json"))).RootElement;
}
