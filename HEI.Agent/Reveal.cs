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

namespace HEI.Agent {
	/// <summary>
	/// "Show in File Explorer" on a file or folder the review page lists: File Explorer opens its folder
	/// with it selected, or (for a folder, when asked) opens the folder itself. Only File Explorer is
	/// started, and only for a path that exists on this PC: a file is shown, never opened or run.
	/// </summary>
	static class Reveal {
		/// <summary>Null when <paramref name="path"/> is a file or folder on this PC that's there now; otherwise why it can't be shown.</summary>
		public static string? Check(string? path) {
			if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000) return "No file or folder was given.";
			// A full path, not a device path (\\?\, \\.\) or one File Explorer would read as more than a path.
			if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
				|| path.IndexOfAny(['"', '*', '?', '<', '>', '|', '\0', '\r', '\n']) >= 0)
				return "That isn't a file or folder on this PC.";
			if (!File.Exists(path) && !Directory.Exists(path))
				return "It isn't there any more: it was moved, renamed or deleted since Heiward listed it.";
			return null;
		}

		/// <summary>
		/// File Explorer's arguments: <c>/select,"path"</c> to show it in its folder, selected; the folder
		/// in quotes to open it. A drive's root ("C:\") goes unquoted: in quotes, its last backslash would
		/// read as escaping the quote.
		/// </summary>
		public static string Arguments(string path, bool open) {
			string arg = path.EndsWith('\\') && path.Length <= 3 ? path : "\"" + path.TrimEnd('\\') + "\"";
			return open ? arg : "/select," + arg;
		}

		/// <summary>Shows it in File Explorer (see <see cref="Check"/> first). <paramref name="open"/> opens a folder itself instead.</summary>
		public static void Show(string path, bool open) {
			bool openFolder = open && Directory.Exists(path);
			string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
			using var _ = Process.Start(new ProcessStartInfo(explorer, Arguments(path, openFolder)) { UseShellExecute = false });
		}
	}
}
