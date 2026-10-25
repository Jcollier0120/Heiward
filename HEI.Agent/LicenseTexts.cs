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

using System.Reflection;

namespace HEI.Agent {
	/// <summary>
	/// The texts every copy of Heiward carries with it: the GNU AGPL's (the repository's LICENSE) and the third-party
	/// notices (THIRD-PARTY-NOTICES.txt, which tools\notices.ps1 writes). HEI.Agent.csproj embeds both in hei.exe, and puts
	/// them beside it in a build's output and a publish; About on the review page shows them (<see cref="ReviewServer"/>'s
	/// /license.txt and /third-party-notices.txt), and the install writes them beside the installed hei.exe.
	/// </summary>
	static class LicenseTexts {
		public const string License = "LICENSE.txt";
		public const string Notices = "THIRD-PARTY-NOTICES.txt";
		public static readonly string[] All = [License, Notices];

		/// <summary>One of <see cref="All"/>, from hei.exe's own resources.</summary>
		public static byte[] Read(string name) {
			using Stream text = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
				?? throw new InvalidOperationException($"This build of Heiward has no {name}.");
			using var bytes = new MemoryStream();
			text.CopyTo(bytes);
			return bytes.ToArray();
		}

		/// <summary>Writes each of <see cref="All"/> into <paramref name="folder"/>, only where it differs.</summary>
		public static void WriteBeside(string folder) {
			foreach (string name in All) {
				byte[] text = Read(name);
				string path = Path.Combine(folder, name);
				if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(text)) continue;
				string temp = path + ".new";
				File.WriteAllBytes(temp, text);
				File.Move(temp, path, overwrite: true);
			}
		}
	}
}
