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

using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace HEI.Core.Utils {
	/// <summary>A graphics card as Windows lists it (DXGI).</summary>
	/// <param name="Index">Its place in DXGI's list: the adapter number DirectML and FFmpeg's D3D11VA and D3D12VA take.</param>
	/// <param name="Key">What settings.json's gpu holds: the name, and " #2" on the second card of the same name.</param>
	/// <param name="DedicatedMemory">Its own video memory in bytes; a graphics chip that shares the PC's RAM has little or none.</param>
	/// <param name="Luid">Its LUID as Windows' GPU Engine counters name it ("0x00000000_0x0000c6b1"), lowercase; "" when unknown. It changes from boot to boot.</param>
	/// <param name="Driver">Its driver's version ("32.0.101.6127"), "" when DXGI doesn't say.</param>
	public sealed record GpuAdapter(int Index, string Key, string Name, ulong DedicatedMemory, uint VendorId, string Luid = "", string Driver = "") {
		/// <summary>The manor's id for it (Manor's docs/ACCELERATORS.md): "gpu-" and its <see cref="Key"/> slugged.</summary>
		public string AcceleratorId => AI.Accelerators.GpuId(Key);
	}

	/// <summary>One adapter as DXGI describes it, before <see cref="GpuAdapters.Keyed"/> leaves out the software ones and names it.</summary>
	internal readonly record struct DxgiAdapter(int Index, string Name, ulong DedicatedMemory, uint VendorId, bool Software, string Luid = "", string Driver = "");

	/// <summary>
	/// The graphics cards on this PC, and the one a scan's GPU work runs on: AI matching on the GPU (DirectML),
	/// and the video and iPhone photo decoders (FFmpeg's D3D11VA and D3D12VA). Without a choice that's Windows'
	/// default, the first card DXGI lists (the one driving the main display), as before there was a choice.
	///
	/// A desktop with two cards (a graphics card and the processor's graphics, or two graphics cards) names the
	/// one it wants in settings.json's gpu, by <see cref="GpuAdapter.Key"/>: DXGI's numbers aren't kept from one
	/// boot to the next, the names are. A scan reads it once at its start (<see cref="Choose"/>): its decoders
	/// and its AI session keep that card until the scan ends, so a different one applies from the next scan.
	///
	/// HEI_GPU_SOFTWARE=1 lists Windows' software adapter too (Microsoft Basic Render Driver), so a PC with one
	/// card can show the choice. DirectML and the decoders refuse it, and the work goes to the CPU.
	/// </summary>
	public static unsafe class GpuAdapters {
		/// <summary>Microsoft's own adapters: the Basic Render Driver (WARP), the Remote Display Adapter, Hyper-V's.</summary>
		const uint MicrosoftVendor = 0x1414;
		const uint SoftwareFlag = 2; // DXGI_ADAPTER_FLAG_SOFTWARE

		static readonly bool ListSoftware = Environment.GetEnvironmentVariable("HEI_GPU_SOFTWARE") == "1";

		/// <summary>The graphics cards on this PC, in DXGI's order; none when DXGI can't say (not Windows).</summary>
		public static IReadOnlyList<GpuAdapter> List() {
			if (!OperatingSystem.IsWindows())
				return Array.Empty<GpuAdapter>();
			try {
				return Keyed(Enumerate(), ListSoftware);
			}
			catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException) {
				Logger.Instance.Info($"Couldn't list the graphics cards: {e.Message}");
				return Array.Empty<GpuAdapter>();
			}
		}

		/// <summary>
		/// The hardware adapters (the software ones too with <paramref name="software"/>), each named: a second
		/// card of the same model is "name #2", in DXGI's order.
		/// </summary>
		internal static IReadOnlyList<GpuAdapter> Keyed(IEnumerable<DxgiAdapter> adapters, bool software = false) {
			var list = new List<GpuAdapter>();
			var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (DxgiAdapter a in adapters) {
				if (!software && (a.Software || a.VendorId == MicrosoftVendor))
					continue;
				string name = string.IsNullOrWhiteSpace(a.Name) ? $"Graphics card {a.Index + 1}" : a.Name.Trim();
				int n = seen.TryGetValue(name, out int before) ? before + 1 : 1;
				seen[name] = n;
				list.Add(new GpuAdapter(a.Index, n == 1 ? name : $"{name} #{n}", name, a.DedicatedMemory, a.VendorId, a.Luid, a.Driver));
			}
			return list;
		}

		/// <summary>The one to suggest: the most memory of its own (a graphics card over the processor's graphics), the first on a tie.</summary>
		public static GpuAdapter? Recommended(IReadOnlyList<GpuAdapter> adapters) =>
			adapters.OrderByDescending(a => a.DedicatedMemory).ThenBy(a => a.Index).FirstOrDefault();

		/// <summary>The card settings.json's gpu names, or null for none (Windows' default) or one that isn't on this PC now.</summary>
		public static GpuAdapter? Find(string? key, IReadOnlyList<GpuAdapter> adapters) =>
			string.IsNullOrWhiteSpace(key) ? null : adapters.FirstOrDefault(a => string.Equals(a.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

		/// <summary>
		/// The card GPU work runs on when settings.json's gpu is <paramref name="key"/>: that card, else Windows' default
		/// (the first DXGI lists, as DirectML's device 0 and FFmpeg's default are). Null on a PC with none.
		/// </summary>
		public static GpuAdapter? InUse(string? key, IReadOnlyList<GpuAdapter> adapters) =>
			Find(key, adapters) ?? adapters.MinBy(a => a.Index);

		/// <summary>The card this process's GPU work runs on, or null for Windows' default.</summary>
		public static GpuAdapter? Chosen { get; private set; }

		/// <summary>The card this process's GPU work runs on now: <see cref="Chosen"/>, else Windows' default. Null on a PC with none.</summary>
		public static GpuAdapter? InUseNow() => Chosen ?? List().MinBy(a => a.Index);

		/// <summary>The card the settings named at the last <see cref="Choose"/>, found or not; null when they named none.</summary>
		public static string? Requested { get; private set; }

		/// <summary>
		/// For the scan's summary in heiward.log, which is what someone with two cards can send: "; GPU work on the
		/// NVIDIA GeForce RTX 4070", or that the card named isn't on this PC now. Empty when the settings name none.
		/// </summary>
		public static string Describe() =>
			Requested == null ? "" : Chosen != null ? $"; GPU work on the {Chosen.Key}" : $"; GPU work on Windows' default card ({Requested} isn't on this PC now)";

		/// <summary>FFmpeg's device string for <see cref="Chosen"/> (its DXGI number), or null for the default.</summary>
		internal static string? DeviceString => Chosen?.Index.ToString(CultureInfo.InvariantCulture);

		/// <summary>DirectML's device id for <see cref="Chosen"/>: its DXGI number, 0 for the default.</summary>
		internal static int DirectMLDevice => Chosen?.Index ?? 0;

		/// <summary>
		/// At a scan's start: the card settings.json's gpu names. One that isn't on this PC now (taken out, or
		/// renamed by a new driver) leaves Windows' default, and the log says so.
		/// </summary>
		public static void Choose(string? key) {
			if (string.IsNullOrWhiteSpace(key)) {
				Chosen = null;
				Requested = null;
				return;
			}
			Requested = key.Trim();
			IReadOnlyList<GpuAdapter> adapters = List();
			Chosen = Find(key, adapters);
			if (Chosen == null)
				Logger.Instance.Warn($"The graphics card in the settings, {key.Trim()}, isn't on this PC now: GPU work runs on Windows' default" +
					(adapters.Count > 0 ? $" ({adapters.MinBy(a => a.Index)!.Name})." : "."));
			else
				Logger.Instance.Info($"GPU work runs on the {Chosen.Key}.");
		}

		[DllImport("dxgi.dll", ExactSpelling = true)]
		static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

		static readonly Guid IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
		/// <summary>IDXGIDevice: asked about, IDXGIAdapter::CheckInterfaceSupport gives the user-mode driver's version (as browsers read it).</summary>
		static readonly Guid IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

		/// <summary>The LUID as the GPU Engine counters' instance names have it: high part, then low part, each as 8 hex digits.</summary>
		internal static string LuidText(int high, uint low) => $"0x{(uint)high:x8}_0x{low:x8}";

		/// <summary>A driver version as Windows shows it: four 16-bit parts, most significant first.</summary>
		internal static string DriverText(long version) {
			ulong v = (ulong)version;
			return $"{v >> 48}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
		}

		/// <summary>DXGI_ADAPTER_DESC1.</summary>
		[StructLayout(LayoutKind.Sequential)]
		struct AdapterDesc1 {
			public fixed char Description[128];
			public uint VendorId, DeviceId, SubSysId, Revision;
			public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
			public uint LuidLow;
			public int LuidHigh;
			public uint Flags;
		}

		/// <summary>Every adapter DXGI lists, through IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1.</summary>
		static List<DxgiAdapter> Enumerate() {
			var found = new List<DxgiAdapter>();
			Marshal.ThrowExceptionForHR(CreateDXGIFactory1(IDXGIFactory1, out IntPtr factory));
			try {
				// IUnknown (0-2), IDXGIObject (3-6), IDXGIFactory (7-11), then IDXGIFactory1::EnumAdapters1.
				var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)factory)[12];
				for (uint i = 0; ; i++) {
					IntPtr adapter;
					if (enumAdapters1(factory, i, &adapter) < 0) // DXGI_ERROR_NOT_FOUND after the last one
						break;
					try {
						// IUnknown (0-2), IDXGIObject (3-6), IDXGIAdapter (7-9: EnumOutputs, GetDesc, CheckInterfaceSupport),
						// then IDXGIAdapter1::GetDesc1.
						var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(void***)adapter)[10];
						var checkInterfaceSupport = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, long*, int>)(*(void***)adapter)[9];
						AdapterDesc1 desc;
						if (getDesc1(adapter, &desc) >= 0) {
							Guid device = IDXGIDevice;
							long umd;
							string driver = checkInterfaceSupport(adapter, &device, &umd) >= 0 ? DriverText(umd) : "";
							found.Add(new DxgiAdapter((int)i, new string(desc.Description), desc.DedicatedVideoMemory, desc.VendorId, (desc.Flags & SoftwareFlag) != 0,
								LuidText(desc.LuidHigh, desc.LuidLow), driver));
						}
					}
					finally {
						Marshal.Release(adapter);
					}
				}
			}
			finally {
				Marshal.Release(factory);
			}
			return found;
		}
	}
}
