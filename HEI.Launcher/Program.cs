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
using System.IO;
using System.Linq;
using System.Text;

namespace HEI.Launcher {
	/// <summary>
	/// Heiward.exe, beside hei.exe: starts hei.exe with its own arguments ("open" without any), without a console
	/// window, and exits. Windows' Startup apps runs it at sign-in, as Heiward.
	/// </summary>
	static class Program {
		static int Main(string[] args) {
			string dir = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
			string hei = Path.Combine(dir, "hei.exe");
			if (!File.Exists(hei)) return 2;
			var psi = new ProcessStartInfo(hei, string.Join(" ", (args.Length > 0 ? args : new[] { "open" }).Select(Quote))) {
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = dir,
			};
			using (Process.Start(psi)) { }
			return 0;
		}

		/// <summary>One argument, as Windows' command-line parsing reads it back.</summary>
		static string Quote(string arg) {
			if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
			var b = new StringBuilder("\"");
			int slashes = 0;
			foreach (char c in arg) {
				if (c == '\\') { slashes++; continue; }
				b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
				slashes = 0;
			}
			return b.Append('\\', slashes * 2).Append('"').ToString();
		}
	}
}
