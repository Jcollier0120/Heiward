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

using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace HEI.Agent {
	/// <summary>
	/// Whether an exe is Heiward's publisher's to install: its Authenticode signature is one Windows trusts (WinVerifyTrust:
	/// the file is as it was signed, by a certificate that chains to a trusted root and isn't revoked), it was made by one of
	/// <see cref="PublisherThumbprints"/>, and it carries an RFC 3161 timestamp, which keeps it valid after the certificate
	/// expires. <see cref="SelfUpdate"/> installs nothing that fails it.
	/// </summary>
	static class Authenticode {
		/// <summary>
		/// The certificates Heiward's releases are signed with: each one's SHA-1 thumbprint, and the day it expires (its
		/// NotAfter, in UTC). Certum's for Heiward's publisher, valid for a year. A renewed certificate is added here, in a
		/// release signed by the one before, before any release is signed by it: copies update themselves only to an exe signed
		/// by one they know (HEI.Agent/README.md, "Renewing the certificate"). release.ps1 signs only with one the release
		/// before pins, and checks its expiry here is the certificate's. One that expired stays: what it signed, timestamped,
		/// is still valid, and FFmpeg's DLLs are signed by it.
		/// </summary>
		public static readonly (string Thumbprint, DateOnly Expires)[] PublisherCertificates = [
			("6B654D9C34800D4307D42ACD63B925EDA5CE8D33", new DateOnly(2027, 10, 11)),
		];

		/// <summary>The thumbprints of <see cref="PublisherCertificates"/>.</summary>
		public static readonly string[] PublisherThumbprints = [.. PublisherCertificates.Select(c => c.Thumbprint)];

		/// <summary>
		/// What to do about the renewal, from three months before the last of <paramref name="certificates"/> expires: by then a
		/// renewed certificate must be pinned, so that a release that pins it can go out, signed by the current one, while that
		/// one still signs. Null while there's time.
		/// </summary>
		internal static string? RenewalDue(DateOnly today, IEnumerable<(string Thumbprint, DateOnly Expires)> certificates) {
			DateOnly expires = certificates.Max(c => c.Expires);
			if (today < expires.AddMonths(-3)) return null;
			string when = today < expires ? "expires on" : "expired on";
			return $"Heiward's code-signing certificate {when} {expires:yyyy-MM-dd}, and no certificate that outlasts it is pinned in Authenticode.PublisherCertificates. " +
				"Copies update themselves only to an exe signed by a certificate they know, so renew it in this order (HEI.Agent/README.md, \"Renewing the certificate\"): " +
				"1. get the renewed certificate from Certum; " +
				"2. add its thumbprint and expiry to PublisherCertificates in HEI.Agent/Authenticode.cs, keeping the current one; " +
				"3. release that, signed with the current certificate (release.ps1 -Publish); " +
				"4. once the copies have updated to it, sign with the renewed one (release.ps1 -Publish -Thumbprint <new>), and make it release.ps1's default.";
		}

		/// <summary>
		/// Why <paramref name="file"/> isn't one to install, in a few words ("it isn't signed"); null when it is. Revocation is
		/// checked unless <paramref name="checkRevocation"/> is false; where it can't be (no answer from the certificate's
		/// authority), the rest must still hold, as Windows itself has it.
		/// </summary>
		internal static string? Problem(string file, IReadOnlyCollection<string> thumbprints, bool checkRevocation = true) {
			int trust = Verify(file, checkRevocation);
			if (checkRevocation && trust is CERT_E_REVOCATION_FAILURE or CRYPT_E_REVOCATION_OFFLINE or CRYPT_E_NO_REVOCATION_CHECK)
				trust = Verify(file, checkRevocation: false);
			if (trust == TRUST_E_NOSIGNATURE) return "it isn't signed";
			if (trust != 0) return $"Windows doesn't trust its signature (0x{trust:X8})";
			SignerInfo signer;
			try {
				if (Signature(file) is not SignedCms cms || cms.SignerInfos.Count == 0) return "its signature can't be read";
				signer = cms.SignerInfos[0];
			}
			catch (Exception e) when (e is CryptographicException or BadImageFormatException or IOException) {
				return "its signature can't be read";
			}
			if (signer.Certificate is not { } cert) return "its signature names no certificate";
			if (!thumbprints.Contains(cert.Thumbprint, StringComparer.OrdinalIgnoreCase))
				return $"it's signed by {cert.GetNameInfo(X509NameType.SimpleName, false)} ({cert.Thumbprint}), not Heiward's publisher";
			if (!Timestamped(signer)) return "its signature has no timestamp";
			return null;
		}

		/// <summary>The signature's RFC 3161 timestamp (signtool's /tr), checked against the signature it vouches for.</summary>
		static bool Timestamped(SignerInfo signer) {
			foreach (CryptographicAttributeObject attribute in signer.UnsignedAttributes) {
				if (attribute.Oid.Value != "1.3.6.1.4.1.311.3.3.1") continue;
				foreach (AsnEncodedData value in attribute.Values)
					if (Rfc3161TimestampToken.TryDecode(value.RawData, out Rfc3161TimestampToken? token, out _) && token.VerifySignatureForSignerInfo(signer, out _))
						return true;
			}
			return false;
		}

		/// <summary>The PKCS #7 signature in the exe's certificate table (its first WIN_CERTIFICATE); null when it has none.</summary>
		static SignedCms? Signature(string file) {
			using FileStream stream = File.OpenRead(file);
			DirectoryEntry table = new PEHeaders(stream).PEHeader?.CertificateTableDirectory ?? default;
			if (table.Size < 8) return null;
			// The certificate table's address is a file offset, not a relative one.
			stream.Position = table.RelativeVirtualAddress;
			Span<byte> header = stackalloc byte[8];
			stream.ReadExactly(header);
			int length = BinaryPrimitives.ReadInt32LittleEndian(header);
			ushort type = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
			if (type != WIN_CERT_TYPE_PKCS_SIGNED_DATA || length <= 8 || length > table.Size) return null;
			byte[] pkcs7 = new byte[length - 8];
			stream.ReadExactly(pkcs7);
			var cms = new SignedCms();
			cms.Decode(pkcs7);
			return cms;
		}

		/// <summary>WinVerifyTrust's answer for the file's embedded signature, with no window: 0 when Windows trusts it.</summary>
		static int Verify(string file, bool checkRevocation) {
			var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
			IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
			Marshal.StructureToPtr(fileInfo, pFile, false);
			try {
				var data = new WINTRUST_DATA {
					cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
					dwUIChoice = WTD_UI_NONE,
					fdwRevocationChecks = checkRevocation ? WTD_REVOKE_WHOLECHAIN : WTD_REVOKE_NONE,
					dwUnionChoice = WTD_CHOICE_FILE,
					pFile = pFile,
					dwStateAction = WTD_STATEACTION_VERIFY,
					dwProvFlags = checkRevocation ? WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT : WTD_REVOCATION_CHECK_NONE,
				};
				Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
				int result = WinVerifyTrust(INVALID_HANDLE_VALUE, ref action, ref data);
				data.dwStateAction = WTD_STATEACTION_CLOSE;
				WinVerifyTrust(INVALID_HANDLE_VALUE, ref action, ref data);
				return result;
			}
			finally {
				Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
				Marshal.FreeHGlobal(pFile);
			}
		}

		const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
		const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x800B010E);
		const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);
		const int CRYPT_E_NO_REVOCATION_CHECK = unchecked((int)0x80092012);
		const ushort WIN_CERT_TYPE_PKCS_SIGNED_DATA = 2;
		const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_REVOKE_WHOLECHAIN = 1, WTD_CHOICE_FILE = 1;
		const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
		const uint WTD_REVOCATION_CHECK_NONE = 0x10, WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x80;
		static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
		static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct WINTRUST_FILE_INFO {
			public uint cbStruct;
			public string pcwszFilePath;
			public IntPtr hFile;
			public IntPtr pgKnownSubject;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct WINTRUST_DATA {
			public uint cbStruct;
			public IntPtr pPolicyCallbackData;
			public IntPtr pSIPClientData;
			public uint dwUIChoice;
			public uint fdwRevocationChecks;
			public uint dwUnionChoice;
			public IntPtr pFile;
			public uint dwStateAction;
			public IntPtr hWVTStateData;
			public IntPtr pwszURLReference;
			public uint dwProvFlags;
			public uint dwUIContext;
			public IntPtr pSignatureSettings;
		}

		[DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
		static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
	}
}
