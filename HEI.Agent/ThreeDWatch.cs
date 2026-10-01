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
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HEI.Agent {
	/// <summary>
	/// Whether something else wants the PC's graphics more than a scan does: a program running full
	/// screen (a game, a presentation), as Windows tells notification senders, or another program
	/// keeping the GPU's 3D engine busy, windowed or borderless, from Windows' GPU performance counters
	/// (as Task Manager's GPU column). The desktop's own compositor (dwm) and Heiward don't count. A scan
	/// steps back while it lasts (<see cref="AgentScanner"/>).
	/// </summary>
	sealed partial class ThreeDWatch : IDisposable {
		/// <summary>Share of a GPU 3D engine another program must keep busy to count: a game takes most of it, a browser or a video a few percent.</summary>
		internal const double BusyPercent = 30;

		IntPtr query, counter;

		public ThreeDWatch() {
			if (!OperatingSystem.IsWindows())
				return;
			try {
				if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) {
					query = IntPtr.Zero;
					return;
				}
				// Every GPU's 3D engines, per process; a utilization counter needs two samples, the first taken now.
				if (PdhAddEnglishCounter(query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out counter) != 0) {
					counter = IntPtr.Zero;
					return;
				}
				PdhCollectQueryData(query);
			}
			catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) {
				query = counter = IntPtr.Zero;
			}
		}

		/// <summary>What wants the graphics now ("a full-screen game", "Cyberpunk2077 using the GPU"), or null.</summary>
		public string? Busy() {
			if (!OperatingSystem.IsWindows())
				return null;
			try {
				if (SHQueryUserNotificationState(out int state) == 0) {
					const int QunsBusy = 2, QunsRunningD3DFullScreen = 3, QunsPresentationMode = 4;
					if (state == QunsRunningD3DFullScreen) return "a full-screen game";
					if (state is QunsBusy or QunsPresentationMode) return "a full-screen program";
				}
			}
			catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
			return counter == IntPtr.Zero ? null : BusiestOther();
		}

		string? BusiestOther() {
			if (PdhCollectQueryData(query) != 0)
				return null;
			const uint PdhFmtDouble = 0x200, PdhFmtNoCap100 = 0x8000, PdhMoreData = 0x800007D2;
			uint size = 0;
			if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, IntPtr.Zero) != PdhMoreData || size == 0)
				return null;
			IntPtr buffer = Marshal.AllocHGlobal((int)size);
			try {
				if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out uint items, buffer) != 0)
					return null;
				var samples = new List<(string Instance, double Percent)>((int)items);
				int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
				for (int i = 0; i < items; i++) {
					var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * stride);
					if (item.CStatus == 0 && Marshal.PtrToStringUni(item.Name) is { } name)
						samples.Add((name, item.DoubleValue));
				}
				int? pid = Busiest(samples, Environment.ProcessId, DesktopCompositor());
				if (pid is not int busy)
					return null;
				try {
					using var p = Process.GetProcessById(busy);
					return $"{p.ProcessName} using the GPU";
				}
				catch (ArgumentException) { return null; } // gone since
			}
			finally {
				Marshal.FreeHGlobal(buffer);
			}
		}

		/// <summary>
		/// The process keeping a 3D engine at least <see cref="BusyPercent"/> busy, other than <paramref name="self"/>
		/// and <paramref name="compositor"/>: per process, its busiest engine (instances look like
		/// "pid_1234_luid_0x0_0x1_phys_0_eng_0_engtype_3D", one per engine).
		/// </summary>
		internal static int? Busiest(IEnumerable<(string Instance, double Percent)> samples, int self, int? compositor) {
			int? busiest = null;
			double most = 0;
			foreach (var (instance, percent) in samples) {
				if (PidPattern().Match(instance) is not { Success: true } m || !int.TryParse(m.Groups[1].Value, out int pid))
					continue;
				if (pid == self || pid == compositor || pid == 0 || percent < BusyPercent || percent <= most)
					continue;
				busiest = pid;
				most = percent;
			}
			return busiest;
		}

		[GeneratedRegex(@"^pid_(\d+)_")]
		private static partial Regex PidPattern();

		static int? DesktopCompositor() {
			Process[] dwm = Process.GetProcessesByName("dwm");
			try { return dwm.Length > 0 ? dwm[0].Id : null; }
			finally { foreach (Process p in dwm) p.Dispose(); }
		}

		public void Dispose() {
			if (query != IntPtr.Zero)
				PdhCloseQuery(query);
			query = counter = IntPtr.Zero;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct PdhFmtCounterValueItem {
			public IntPtr Name;
			public uint CStatus;
			public double DoubleValue; // PDH_FMT_COUNTERVALUE's union, as a double (8-aligned after the status)
		}

		[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
		static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
		[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
		static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
		[DllImport("pdh.dll")]
		static extern uint PdhCollectQueryData(IntPtr query);
		[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
		static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);
		[DllImport("pdh.dll")]
		static extern uint PdhCloseQuery(IntPtr query);
		[DllImport("shell32.dll")]
		static extern int SHQueryUserNotificationState(out int state);
	}
}
