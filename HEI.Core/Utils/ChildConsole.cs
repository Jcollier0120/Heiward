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

using System.Runtime.InteropServices;

namespace HEI.Core.Utils {
	/// <summary>
	/// Whether the console programs Heiward starts and waits for (ffprobe, ffmpeg, fpcalc, git) get a console of their own.
	/// On Windows a program started with <c>CreateNoWindow</c> gets a console host (conhost.exe) of its own: a second
	/// process for every ffprobe a scan runs, one per new file. Started without it from a process that has a console
	/// (hei, from a terminal or started hidden, which keeps a console with no window), it shares that one and opens no
	/// window. From one with none (the desktop app), it would open a window, so there it still gets its own, hidden.
	/// So: <c>CreateNoWindow = ChildConsole.OwnConsole</c>.
	/// </summary>
	internal static class ChildConsole {
		/// <summary>True when a child needs a console of its own: this process has none to share, or isn't on Windows (where it changes nothing).</summary>
		public static bool OwnConsole => !shares.Value;

		static readonly Lazy<bool> shares = new(HasConsole);

		/// <summary>Whether this process has a console: one or more processes, this one among them, are attached to it.</summary>
		internal static bool HasConsole() {
			if (!OperatingSystem.IsWindows()) return false;
			try {
				return GetConsoleProcessList(new uint[1], 1) > 0;
			}
			catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) {
				return false;
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
	}
}
