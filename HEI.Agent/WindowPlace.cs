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
using System.Text.Json;

namespace HEI.Agent {
	/// <summary>
	/// Where Heiward's window was, and how big, so it opens there again (the owner's ask, 2026-11-12). In Windows' scaled
	/// pixels (DIPs), as the page reads them (screenX, outerWidth) and as Edge takes them. The page sends it whenever the
	/// window has moved or changed size and puts the window back when it opens (app.js, placeWindow); Edge, when it starts
	/// with the window, opens it there from the first frame (<see cref="Installer.EdgeArguments(string)"/>). Edge
	/// remembers nothing of an app window's place itself, and takes a size and place only when it isn't already running.
	/// </summary>
	sealed record WindowPlace(int X, int Y, int Width, int Height, bool Maximized = false) {
		/// <summary>The setup and the page need about this much; Windows parks a minimized window at -32000.</summary>
		internal const int MinWidth = 480, MinHeight = 360, Far = 30000;

		static string FilePath => Path.Combine(AgentPaths.Home, "window.json");
		static readonly object Gate = new();

		/// <summary>A place a window can be put back to: not minimized, not too small to use, not miles off.</summary>
		[System.Text.Json.Serialization.JsonIgnore]
		public bool Sane => Width is >= MinWidth and < Far && Height is >= MinHeight and < Far && Math.Abs(X) < Far && Math.Abs(Y) < Far;

		/// <summary>The window's last place; null before any was kept, or when the file says nothing usable.</summary>
		public static WindowPlace? Load() {
			try {
				var place = JsonSerializer.Deserialize<WindowPlace>(File.ReadAllText(FilePath), AgentConfig.Json);
				return place is { Sane: true } ? place : null;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
		}

		/// <summary>
		/// Keeps the window's place. A maximized window keeps the size and place it had before, for when it's made smaller
		/// again, with Maximized set. Unchanged, the file isn't written (<see cref="AgentPaths.WriteAtomic"/>).
		/// </summary>
		/// <returns>False for a place that isn't <see cref="Sane"/>, or when it couldn't be written.</returns>
		public static bool Save(WindowPlace place) {
			lock (Gate) {
				WindowPlace? before = Load();
				WindowPlace next = place.Maximized && before != null ? before with { Maximized = true } : place;
				if (!next.Sane) return false;
				try { AgentPaths.WriteAtomic(FilePath, JsonSerializer.Serialize(next, AgentConfig.Json)); }
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
				return true;
			}
		}

		/// <summary>
		/// The first window, before any was moved: centred on the main screen's work area, 1280 x 860 at most with a margin
		/// round it; on a small screen (1366 x 768 at 125 % leaves 1093 x 566) the whole work area, which the setup's steps
		/// are laid out to fit (app.css). The page works the same out (app.js, defaultWindow).
		/// </summary>
		public static WindowPlace Default(WorkArea work) {
			int w = work.Width < 1200 ? work.Width : Math.Min(1280, work.Width - 48);
			int h = work.Height < 720 ? work.Height : Math.Min(860, work.Height - 24);
			return new(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h);
		}

		/// <summary>A screen's area without the taskbar, in DIPs.</summary>
		internal readonly record struct WorkArea(int Left, int Top, int Width, int Height);

		/// <summary>The main screen's work area in DIPs; null when Windows doesn't say.</summary>
		internal static WorkArea? MainWorkArea() {
			try {
				return WithPerMonitorPixels<WorkArea?>(() => {
					IntPtr monitor = MonitorFromPoint(default, MonitorDefaultToPrimary);
					var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
					if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
					double scale = Scale(monitor);
					Rect r = info.Work;
					return new WorkArea((int)Math.Round(r.Left / scale), (int)Math.Round(r.Top / scale),
						(int)Math.Round((r.Right - r.Left) / scale), (int)Math.Round((r.Bottom - r.Top) / scale));
				});
			}
			catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
		}

		/// <summary>
		/// Whether the top of the window, where it's dragged from, is on a screen now: a screen unplugged since, and the
		/// window opens on the main one instead (the page then centres it there). Worked out with the main screen's scaling,
		/// so on a second screen at another one it may say no to a place that's fine; the page puts it there all the same.
		/// </summary>
		public bool OnAScreen() {
			try {
				return WithPerMonitorPixels(() => {
					IntPtr main = MonitorFromPoint(default, MonitorDefaultToPrimary);
					double scale = Scale(main);
					var top = new Point { X = (int)Math.Round((X + Width / 2.0) * scale), Y = (int)Math.Round((Y + 12) * scale) };
					return MonitorFromPoint(top, MonitorDefaultToNull) != IntPtr.Zero;
				});
			}
			catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return true; }
		}

		/// <summary>Runs <paramref name="read"/> seeing screens in their real pixels, whatever this process's own setting.</summary>
		static T WithPerMonitorPixels<T>(Func<T> read) {
			IntPtr before = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
			try { return read(); }
			finally { if (before != IntPtr.Zero) SetThreadDpiAwarenessContext(before); }
		}

		static double Scale(IntPtr monitor) => GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;

		static readonly IntPtr PerMonitorAwareV2 = new(-4);
		const uint MonitorDefaultToNull = 0, MonitorDefaultToPrimary = 1;

		[StructLayout(LayoutKind.Sequential)]
		struct Point { public int X, Y; }

		[StructLayout(LayoutKind.Sequential)]
		struct Rect { public int Left, Top, Right, Bottom; }

		[StructLayout(LayoutKind.Sequential)]
		struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

		[DllImport("user32.dll")]
		static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

		[DllImport("user32.dll")]
		static extern IntPtr MonitorFromPoint(Point point, uint flags);

		[DllImport("user32.dll")]
		static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

		[DllImport("shcore.dll")]
		static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
	}
}
