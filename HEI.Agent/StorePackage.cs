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

namespace HEI.Agent {
	/// <summary>
	/// The Microsoft Store version: Heiward running from its MSIX package (docs/STORE.md). The package is
	/// the install, and its folder is read-only, so nothing is copied or registered for Apps &amp; Features.
	/// Windows gives it a Start menu entry, an app ID for notifications, and the "hei" command, an app
	/// execution alias whose path stays the same across updates.
	/// </summary>
	static class StorePackage {
		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? name);

		const int AppModelErrorNoPackage = 15700;

		/// <summary>The package family name (TheNexus.Heiward_mcanr0hfqkj1g), or null when not running from the package.</summary>
		public static readonly string? FamilyName = ReadFamilyName();

		public static bool IsPackaged => FamilyName != null;

		/// <summary>The Start menu entry's app ID: notifications sent under it show Heiward's name and logo.</summary>
		public static string AppUserModelId => FamilyName + "!Heiward";

		/// <summary>The "hei" alias, which scheduled tasks run: the package's own folder has the version in its name.</summary>
		public static string Alias => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\hei.exe");

		/// <summary>The package's console hei.exe. The Start menu entry runs Heiward.exe, the same program without a window.</summary>
		public static string ConsoleExe => Path.Combine(AppContext.BaseDirectory, "hei.exe");

		/// <summary>
		/// The package's own storage, %LOCALAPPDATA%\Packages\{family}\LocalCache (ApplicationData's LocalCacheFolder).
		/// Windows removes it with the app, whatever it does with writes elsewhere in %LOCALAPPDATA%. It holds
		/// the AI components (<see cref="HEI.Core.Utils.CoreUtils.UseStateFolder"/>) and the setup's marker.
		/// </summary>
		public static string Storage => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", FamilyName ?? "", "LocalCache");

		static string? ReadFamilyName() {
			try {
				int length = 0;
				if (GetCurrentPackageFamilyName(ref length, null) == AppModelErrorNoPackage) return null;
				var name = new StringBuilder(length);
				return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null;
			}
			catch (EntryPointNotFoundException) { return null; }
		}
	}
}
