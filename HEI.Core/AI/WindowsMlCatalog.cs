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

namespace HEI.Core.AI {
	/// <summary>
	/// Windows ML's execution-provider catalog, through its C API (WinMLEpCatalog.h in the NuGet package
	/// Microsoft.Windows.AI.MachineLearning): Windows downloads the vendor's ONNX Runtime plugin for this
	/// PC's NPU (AMD's Vitis AI, for one), shares it system-wide and keeps it updated. The DLL imports
	/// nothing but Windows itself, so it is loaded only for this, beside our own ONNX Runtime, which the
	/// plugin is then registered with. Windows 11 24H2 or later.
	/// </summary>
	internal sealed class WindowsMlCatalog : IDisposable {
		public const string DllName = "Microsoft.Windows.AI.MachineLearning.dll";

		public enum ReadyState { Ready = 0, NotReady = 1, NotPresent = 2 }

		delegate int CatalogCreate(out IntPtr catalog);
		delegate void CatalogRelease(IntPtr catalog);
		delegate int CatalogFindProvider(IntPtr catalog, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr packageFamilyName, out IntPtr ep);
		delegate int EpGetReadyState(IntPtr ep, out ReadyState state);
		delegate int EpEnsureReady(IntPtr ep);
		delegate int EpGetStringSize(IntPtr ep, out nuint size);
		delegate int EpGetString(IntPtr ep, nuint bufferSize, byte[] buffer, out nuint used);

		static readonly object loadLock = new();
		static IntPtr library;

		readonly IntPtr catalog;
		readonly CatalogRelease release;
		readonly CatalogFindProvider findProvider;
		readonly EpGetReadyState getReadyState;
		readonly EpEnsureReady ensureReady;
		readonly EpGetStringSize libraryPathSize, versionSize;
		readonly EpGetString libraryPath, version;

		/// <param name="dllPath">Microsoft.Windows.AI.MachineLearning.dll for this process's architecture.</param>
		public WindowsMlCatalog(string dllPath) {
			lock (loadLock) {
				if (library == IntPtr.Zero) library = NativeLibrary.Load(dllPath);
			}
			T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
			release = Export<CatalogRelease>("WinMLEpCatalogRelease");
			findProvider = Export<CatalogFindProvider>("WinMLEpCatalogFindProvider");
			getReadyState = Export<EpGetReadyState>("WinMLEpGetReadyState");
			ensureReady = Export<EpEnsureReady>("WinMLEpEnsureReady");
			libraryPathSize = Export<EpGetStringSize>("WinMLEpGetLibraryPathSize");
			libraryPath = Export<EpGetString>("WinMLEpGetLibraryPath");
			versionSize = Export<EpGetStringSize>("WinMLEpGetVersionSize");
			version = Export<EpGetString>("WinMLEpGetVersion");
			Check(Export<CatalogCreate>("WinMLEpCatalogCreate")(out catalog), "WinMLEpCatalogCreate");
		}

		/// <summary>The catalog's entry for a provider, by its full name ("VitisAIExecutionProvider"; a short "VitisAI" is not found), or null when this PC has none.</summary>
		public Provider? Find(string name) =>
			findProvider(catalog, name, IntPtr.Zero, out IntPtr ep) >= 0 && ep != IntPtr.Zero ? new Provider(this, ep) : null;

		public void Dispose() => release(catalog);

		static void Check(int hr, string call) {
			if (hr < 0) throw new InvalidOperationException($"{call} failed (0x{hr:X8}).");
		}

		string Read(EpGetStringSize size, EpGetString get, IntPtr ep, string call) {
			Check(size(ep, out nuint n), call + "Size");
			if (n == 0) return "";
			var buffer = new byte[(int)n];
			Check(get(ep, n, buffer, out _), call);
			return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
		}

		/// <summary>One execution provider in the catalog. Valid while the catalog is.</summary>
		public sealed class Provider {
			readonly WindowsMlCatalog owner;
			readonly IntPtr ep;
			internal Provider(WindowsMlCatalog owner, IntPtr ep) { this.owner = owner; this.ep = ep; }

			public ReadyState State {
				get { Check(owner.getReadyState(ep, out ReadyState s), "WinMLEpGetReadyState"); return s; }
			}

			/// <summary>
			/// Downloads and installs the provider when it isn't on the PC yet (seconds to minutes), and adds
			/// it to this process's dependencies, which every process that uses it needs (quick once installed).
			/// </summary>
			public void EnsureReady() => Check(owner.ensureReady(ep), "WinMLEpEnsureReady");

			/// <summary>The plugin DLL to register with ONNX Runtime (after <see cref="EnsureReady"/>).</summary>
			public string LibraryPath => owner.Read(owner.libraryPathSize, owner.libraryPath, ep, "WinMLEpGetLibraryPath");

			public string Version => owner.Read(owner.versionSize, owner.version, ep, "WinMLEpGetVersion");
		}
	}
}
