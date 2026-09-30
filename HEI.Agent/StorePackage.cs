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

		/// <summary>
		/// This exe sits in a Store package's folder, next to its AppxManifest.xml, with or without the package's
		/// identity. Started by its path, as the package's desktop shortcut does, it has none: it must never
		/// take itself for the GitHub download and install itself.
		/// </summary>
		public static bool InPackageFolder => File.Exists(Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml"));

		/// <summary>
		/// Started from the package's folder without its identity: hands over to the packaged app, the way the
		/// Start menu starts it. False when that didn't work.
		/// </summary>
		public static bool ActivateFromFolder() {
			try {
				var manifest = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml"));
				System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
				var identity = manifest.Root!.Element(ns + "Identity")!;
				string appId = manifest.Root.Element(ns + "Applications")!.Element(ns + "Application")!.Attribute("Id")!.Value;
				string aumid = $"{identity.Attribute("Name")!.Value}_{PublisherId(identity.Attribute("Publisher")!.Value)}!{appId}";
				((IApplicationActivationManager)new ApplicationActivationManager()).ActivateApplication(aumid, null, 0, out _);
				return true;
			}
			catch (Exception e) {
				AgentPaths.AppendLog($"couldn't hand over to the Store app: {e.Message}");
				return false;
			}
		}

		/// <summary>
		/// The publisher part of a package family name (mcanr0hfqkj1g): the first 8 bytes of the SHA-256 of the
		/// publisher (UTF-16), in 13 characters of Crockford's base32.
		/// </summary>
		internal static string PublisherId(string publisher) {
			byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
			ulong bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash);
			const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
			var id = new StringBuilder(13);
			// 64 bits and a zero bit: 13 groups of 5.
			for (int i = 0; i < 13; i++) {
				int shift = 64 - 5 * (i + 1);
				int group = shift >= 0 ? (int)(bits >> shift) & 31 : (int)(bits << -shift) & 31;
				id.Append(alphabet[group]);
			}
			return id.ToString();
		}

		[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		interface IApplicationActivationManager {
			void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
		}

		[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
		class ApplicationActivationManager { }

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
