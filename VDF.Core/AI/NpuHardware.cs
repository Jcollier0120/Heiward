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
using System.Text;
using VDF.Core.Utils;

namespace VDF.Core.AI {
	/// <summary>Who made this PC's NPU.</summary>
	public enum NpuVendor { None, Qualcomm, Intel, Amd }

	/// <summary>
	/// The NPU Windows lists (Device Manager's "Neural processors": the ComputeAccelerator device class),
	/// read without loading any AI runtime, because which NPU pack to download depends on it.
	/// </summary>
	public static class NpuHardware {
		static readonly Lazy<(NpuVendor Vendor, string Name)> detected = new(Detect);

		/// <summary>Tests pick the vendor instead of the hardware.</summary>
		internal static NpuVendor? Override;

		public static NpuVendor Vendor => Override ?? detected.Value.Vendor;
		/// <summary>The NPU's name as Windows shows it, or "" when there is none.</summary>
		public static string Name => detected.Value.Name;

		/// <summary>The vendor a device's manufacturer and name point to.</summary>
		public static NpuVendor Classify(string? manufacturer, string? name) {
			string s = $"{manufacturer} {name}";
			if (s.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) || s.Contains("Hexagon", StringComparison.OrdinalIgnoreCase))
				return NpuVendor.Qualcomm;
			if (s.Contains("Intel", StringComparison.OrdinalIgnoreCase))
				return NpuVendor.Intel;
			if (s.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
				System.Text.RegularExpressions.Regex.IsMatch(s, @"\bAMD\b"))
				return NpuVendor.Amd;
			return NpuVendor.None;
		}

		static (NpuVendor, string) Detect() {
			if (!CoreUtils.IsWindows) return (NpuVendor.None, "");
			try {
				foreach (var (manufacturer, name) in ComputeAccelerators()) {
					NpuVendor vendor = Classify(manufacturer, name);
					if (vendor != NpuVendor.None) return (vendor, name);
				}
			}
			catch (Exception e) {
				Logger.Instance.Info($"Could not list NPUs: {e.Message}");
			}
			return (NpuVendor.None, "");
		}

		// ---- SetupAPI: the present devices of the ComputeAccelerator class --------------------------

		static readonly Guid ComputeAcceleratorClass = new("F01A9D53-3FF6-48D2-9F97-C8A7004BE10C");
		const uint DIGCF_PRESENT = 0x2;
		const uint SPDRP_DEVICEDESC = 0x0, SPDRP_MFG = 0xB, SPDRP_FRIENDLYNAME = 0xC;
		static readonly IntPtr InvalidHandle = new(-1);

		[StructLayout(LayoutKind.Sequential)]
		struct SP_DEVINFO_DATA {
			public uint cbSize;
			public Guid ClassGuid;
			public uint DevInst;
			public IntPtr Reserved;
		}

		[DllImport("setupapi.dll", SetLastError = true)]
		static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
		[DllImport("setupapi.dll", SetLastError = true)]
		static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
		[DllImport("setupapi.dll", SetLastError = true)]
		static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property,
			out uint regType, byte[]? buffer, uint size, out uint required);
		[DllImport("setupapi.dll")]
		static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

		static List<(string Manufacturer, string Name)> ComputeAccelerators() {
			var result = new List<(string, string)>();
			Guid cls = ComputeAcceleratorClass;
			IntPtr set = SetupDiGetClassDevsW(ref cls, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
			if (set == InvalidHandle) return result;
			try {
				var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
				for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++) {
					string name = Property(set, ref data, SPDRP_FRIENDLYNAME) ?? Property(set, ref data, SPDRP_DEVICEDESC) ?? "";
					result.Add((Property(set, ref data, SPDRP_MFG) ?? "", name));
				}
			}
			finally {
				SetupDiDestroyDeviceInfoList(set);
			}
			return result;
		}

		static string? Property(IntPtr set, ref SP_DEVINFO_DATA data, uint property) {
			SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out uint required);
			if (required == 0) return null;
			var buffer = new byte[required];
			if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, required, out _)) return null;
			return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
		}
	}
}
