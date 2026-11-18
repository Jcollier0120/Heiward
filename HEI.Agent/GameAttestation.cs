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
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace HEI.Agent {
	/// <summary>
	/// A warning on a game's page: for a Call of Duty title, what this PC doesn't meet of the game's security check, and what
	/// Heiward couldn't check. Heiward only reads; it changes nothing on the PC.
	/// </summary>
	/// <param name="Title">The warning's heading.</param>
	/// <param name="Intro">What the check is, in a line or two.</param>
	/// <param name="Lines">Each requirement not met, or not checked, in the order to fix them.</param>
	/// <param name="Outro">What Heiward doesn't do, and what it can't see.</param>
	sealed record GameWarning(string Title, string Intro, List<GameWarningLine> Lines, string Outro);

	/// <param name="State"><see cref="CodAttestation.Unmet"/>, <see cref="CodAttestation.Advised"/> (an update the game's makers say may be
	/// needed) or <see cref="CodAttestation.Unchecked"/> (Heiward couldn't read it: never taken as met).</param>
	/// <param name="Name">What was found, or for one not checked, what it is.</param>
	/// <param name="Detail">Why it matters, in a line without its full stop (the page adds it).</param>
	/// <param name="Fix">What the person can do, or for one not checked, how to look for themselves.</param>
	sealed record GameWarningLine(string State, string Name, string Detail, string Fix);

	/// <summary>
	/// What Call of Duty's security check (attestation) looks at, as Heiward reads it without administrator rights. Each null
	/// when it couldn't be read.
	/// </summary>
	/// <param name="WindowsBuild">Windows' build number (19045 is Windows 10 22H2; Windows 11 is 22000 and later).</param>
	/// <param name="Uefi">Windows started in UEFI mode; false for Legacy (CSM).</param>
	/// <param name="SecureBoot">Secure Boot is on.</param>
	/// <param name="Gpt">The disk Windows is on is GPT; false for MBR.</param>
	/// <param name="Tpm">The TPM, as Windows' TPM service answers; null when it couldn't be asked.</param>
	sealed record AttestationSignals(int WindowsBuild, bool? Uefi, bool? SecureBoot, bool? Gpt, TpmReading? Tpm);

	/// <param name="Present">Windows sees a TPM. False: none, or it's turned off in the firmware settings.</param>
	/// <param name="Version">"2.0" or "1.2"; null when it couldn't be told.</param>
	/// <param name="Maker">Its maker's code, as tpm.msc's Manufacturer Name shows it ("AMD", "INTC", "IFX"); null when unread.</param>
	/// <param name="Firmware">Its firmware, as tpm.msc's Manufacturer Version shows it ("3.87.0.5"); null when unread.</param>
	sealed record TpmReading(bool Present, string? Version, string? Maker, string? Firmware);

	/// <summary>
	/// Call of Duty's security check, before the game finds it: its titles that check (Black Ops 7, Warzone and Modern Warfare 4,
	/// and the Call of Duty launcher that holds them), and what each of the check's requirements needs. From Activision's support
	/// article, as of November 2026: Windows 10 22H2 or later, or Windows 11; a TPM 2.0, on; its firmware not AMD 3.*.0.* (not
	/// accepted) or Intel 302.12.*.* or 303.12.*.* (may need an update); a GPT disk; UEFI boot; Secure Boot on.
	/// </summary>
	static class CodAttestation {
		public const string Unmet = "unmet", Advised = "advised", Unchecked = "unchecked";

		/// <summary>Windows 10 22H2's build: Call of Duty's oldest Windows.</summary>
		public const int OldestBuild = 19045;

		/// <summary>
		/// The game is a Call of Duty title that runs the check, by the name its launcher records: Black Ops 7, Warzone, Modern
		/// Warfare 4, or the launcher that holds them ("Call of Duty", "Call of Duty HQ"). Older titles don't check, so aren't.
		/// </summary>
		internal static bool Needs(string name) {
			string[] words = GameScanner.SameName(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (words.Length < 3 || words[0] != "call" || words[1] != "of" || words[2] != "duty") return false;
			string[] rest = words[3..];
			return rest.Length == 0 || rest is ["hq"] || rest.Contains("warzone")
				|| Has(rest, "black", "ops", "7") || Has(rest, "black", "ops", "vii")
				|| Has(rest, "modern", "warfare", "4") || Has(rest, "modern", "warfare", "iv");
		}

		static bool Has(string[] words, params string[] run) {
			for (int i = 0; i + run.Length <= words.Length; i++)
				if (words.AsSpan(i, run.Length).SequenceEqual(run)) return true;
			return false;
		}

		/// <summary>
		/// The games, each Call of Duty title that checks with the warning this PC gets (none when it meets every requirement).
		/// The PC is read only when there's such a title, and once for all of them.
		/// </summary>
		public static List<GameInstall> Mark(List<GameInstall> games, Func<AttestationSignals> read) {
			if (!games.Any(g => Needs(g.Name))) return games;
			GameWarning? warning = Warning(read());
			return games.Select(g => Needs(g.Name) ? g with { Warning = warning } : g).ToList();
		}

		static readonly Regex AmdNotAccepted = new(@"^3\.\d+\.0\.\d+$", RegexOptions.CultureInvariant);
		static readonly Regex IntelMayNeed = new(@"^30[23]\.12\.\d+\.\d+$", RegexOptions.CultureInvariant);

		/// <summary>The TPM's firmware against the check: <see cref="Unmet"/> (AMD 3.*.0.*), <see cref="Advised"/> (Intel 302.12, 303.12) or null.</summary>
		internal static string? FirmwareState(string maker, string firmware) =>
			maker.Equals("AMD", StringComparison.OrdinalIgnoreCase) && AmdNotAccepted.IsMatch(firmware) ? Unmet
			: maker.Equals("INTC", StringComparison.OrdinalIgnoreCase) && IntelMayNeed.IsMatch(firmware) ? Advised
			: null;

		const string FirmwareSettings = "your PC's firmware settings (the BIOS setup, opened with a key such as Del or F2 as the PC starts)";

		const string BiosUpdate = "Look on your motherboard or PC maker's support page, for your exact model, for a BIOS update that mentions " +
			"TPM or fTPM firmware, and install it the way the maker says, with the PC on mains power. If the newest BIOS doesn't help, ask " +
			"the maker: until they release one, there may be no fix.";

		/// <summary>The warning for a PC that reads as <paramref name="s"/>; null when every requirement was checked and is met.</summary>
		internal static GameWarning? Warning(AttestationSignals s) {
			var lines = new List<GameWarningLine>();
			if (s.WindowsBuild < OldestBuild)
				lines.Add(new(Unmet, "Windows is older than Call of Duty supports",
					$"It needs Windows 10 version 22H2 or later, or Windows 11, and this PC has build {s.WindowsBuild}",
					"Install the latest feature update from Settings › Windows Update."));

			switch (s.Tpm) {
				case null:
					lines.Add(new(Unchecked, "TPM 2.0", "Heiward couldn't read the TPM on this PC",
						"Press Windows+R and enter tpm.msc: it should say \"The TPM is ready for use\", with Specification Version 2.0. " +
						"Its Manufacturer Version matters too: AMD 3.x.0.x, or Intel 302.12 or 303.12, needs a BIOS update."));
					break;
				case { Present: false }:
					lines.Add(new(Unmet, "No TPM 2.0 found",
						"Windows sees no TPM. Most PCs have one built into the processor that's turned off in the firmware settings",
						$"Turn it on in {FirmwareSettings}: it's usually called fTPM on AMD, and PTT or Platform Trust Technology on Intel. Then restart."));
					break;
				case { Version: "1.2" }:
					lines.Add(new(Unmet, "The TPM is version 1.2", "Call of Duty needs TPM 2.0",
						"Look in the firmware settings for a TPM 2.0 option: Intel processors from the 8th generation on have PTT, and AMD Ryzen " +
						"2000 and later have fTPM. Otherwise the PC needs a TPM 2.0 module."));
					break;
				case { Version: not "2.0" }:
					lines.Add(new(Unchecked, "TPM 2.0", "Heiward found a TPM but couldn't tell its version",
						"Press Windows+R and enter tpm.msc: Specification Version should say 2.0."));
					break;
				case { Maker: { } maker, Firmware: { } firmware }:
					string? state = FirmwareState(maker, firmware);
					if (state == Unmet)
						lines.Add(new(Unmet, "The TPM's firmware needs an update",
							$"Its AMD firmware is {firmware}, and Activision says AMD firmware 3.x.0.x doesn't pass", BiosUpdate));
					else if (state == Advised)
						lines.Add(new(Advised, "The TPM's firmware may need an update",
							$"Its Intel firmware is {firmware}, and Activision says Intel firmware 302.12 and 303.12 may need one", BiosUpdate));
					break;
				default:
					lines.Add(new(Unchecked, "The TPM's firmware version", "Heiward found a TPM 2.0 but couldn't read its firmware version",
						"Press Windows+R and enter tpm.msc to see its Manufacturer Version: AMD 3.x.0.x, or Intel 302.12 or 303.12, needs a " +
						"BIOS update from your motherboard's maker."));
					break;
			}

			if (s.Gpt == false)
				lines.Add(new(Unmet, "The Windows disk is MBR, not GPT", "Call of Duty needs the disk Windows starts from to be GPT",
					"Back up everything you can't lose first: converting the disk to GPT, with Windows' own MBR2GPT tool, can't be undone, and " +
					"BitLocker must be suspended while it runs. Right after converting, switch the firmware settings from Legacy or CSM to " +
					"UEFI, or Windows won't start. Heiward never converts a disk."));
			else if (s.Gpt == null)
				lines.Add(new(Unchecked, "The Windows disk's partition style", "Heiward couldn't read it on this PC",
					"Open Disk Management (diskmgmt.msc), right-click the disk Windows is on › Properties › Volumes: Partition style should say GPT."));

			if (s.Uefi == false)
				lines.Add(new(Unmet, "Windows starts in Legacy (CSM) mode, not UEFI", "Call of Duty needs UEFI, and Secure Boot works only with it",
					"Switch the firmware settings from Legacy or CSM to UEFI only once the Windows disk is GPT: from an MBR disk, Windows won't start in UEFI mode."));
			else if (s.Uefi == null)
				lines.Add(new(Unchecked, "Boot mode", "Heiward couldn't read it on this PC",
					"Press Windows+R and enter msinfo32: BIOS Mode should say UEFI."));

			// In Legacy mode Secure Boot is off whatever the registry says (it may not say at all).
			if (s.Uefi == false && s.SecureBoot != true)
				lines.Add(new(Unmet, "Secure Boot is off", "It can only be on when Windows starts in UEFI mode",
					"Once Windows starts in UEFI mode, turn on Secure Boot in the firmware settings."));
			else if (s.SecureBoot == false)
				lines.Add(new(Unmet, "Secure Boot is off", "Call of Duty needs it on",
					$"Turn on Secure Boot in {FirmwareSettings}, often under Boot or Security, then restart."));
			else if (s.SecureBoot == null)
				lines.Add(new(Unchecked, "Secure Boot", "Heiward couldn't read it on this PC",
					"Press Windows+R and enter msinfo32: Secure Boot State should say On."));

			if (lines.Count == 0) return null;
			string title = lines.Any(l => l.State == Unmet) ? "This PC doesn't meet Call of Duty's security requirements"
				: lines.Any(l => l.State == Advised) ? "Call of Duty may ask for a firmware update on this PC"
				: "Heiward couldn't check all of Call of Duty's security requirements";
			return new GameWarning(title,
				"Before you play online, Call of Duty checks this PC's security setup. When it doesn't pass, the game shows \"Failed " +
				"Attestation Status\" and keeps you out of some or all of its online modes. It isn't a ban.",
				lines,
				"Heiward only reads these settings: it changes nothing on this PC. Before changing firmware settings or updating the BIOS, " +
				"save your BitLocker recovery key if the drive is encrypted, as Windows may ask for it at the next start. The game's own " +
				"check also needs Windows up to date, and your OK when Windows asks to let it set up its security key; Activision's Secure " +
				"Attestation Wizard names the exact reason it fails.");
		}
	}

	/// <summary>
	/// This PC's <see cref="AttestationSignals"/>, each read the way that works without administrator rights (Get-Tpm, Win32_Tpm
	/// and Confirm-SecureBootUEFI all need them): Windows' build; the firmware type Windows started with (GetFirmwareType);
	/// Secure Boot's state in the registry (SecureBoot\State\UEFISecureBootEnabled); the Windows volume's partition style
	/// (IOCTL_DISK_GET_PARTITION_INFO_EX, which needs no access to the disk); and the TPM through Windows' TPM service: its
	/// version (Tbsi_GetDeviceInfo), maker and firmware (TPM2_GetCapability, the read tpm.msc shows). Nothing is written.
	/// </summary>
	static class PlatformReads {
		public static AttestationSignals Read() =>
			new(Environment.OSVersion.Version.Build, Uefi(), SecureBoot(new WindowsRegistry()), Gpt(), Tpm());

		internal const string SecureBootKey = @"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State";

		/// <summary>1 on, 0 off; null when it isn't there (a PC started in Legacy mode may not have it) or can't be read.</summary>
		internal static bool? SecureBoot(IGameRegistry registry) => registry.Value(SecureBootKey, "UEFISecureBootEnabled") switch {
			"1" => true,
			"0" => false,
			_ => null,
		};

		static bool? Uefi() {
			try {
				if (!GetFirmwareType(out int type)) return null;
				return type switch { 2 => true, 1 => false, _ => null };
			}
			catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return null; }
		}

		static bool? Gpt() {
			string? root = Path.GetPathRoot(Environment.SystemDirectory);
			if (string.IsNullOrEmpty(root)) return null;
			try {
				using SafeFileHandle volume = CreateFile(@"\\.\" + root.TrimEnd('\\'), 0, 3 /* share read, write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero);
				if (volume.IsInvalid) return null;
				var info = new byte[256];
				if (!DeviceIoControl(volume, 0x00070048 /* IOCTL_DISK_GET_PARTITION_INFO_EX */, IntPtr.Zero, 0, info, info.Length, out int got, IntPtr.Zero) || got < 4)
					return null;
				return BitConverter.ToInt32(info, 0) switch { 1 => true, 0 => false, _ => null }; // PARTITION_STYLE_GPT, _MBR; RAW can't be told
			}
			catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return null; }
		}

		const uint TbsTpmNotFound = 0x8028400F;

		static TpmReading? Tpm() {
			try {
				uint rc = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out TpmDeviceInfo info);
				if (rc == TbsTpmNotFound) return new TpmReading(false, null, null, null);
				if (rc != 0) return null;
				string? version = info.TpmVersion switch { 2 => "2.0", 1 => "1.2", _ => null };
				if (version != "2.0") return new TpmReading(true, version, null, null);
				var asked = new TbsContextParams2 { Version = 2, Flags = 4 /* includeTpm20 */ };
				if (Tbsi_Context_Create(ref asked, out IntPtr context) != 0) return new TpmReading(true, version, null, null);
				try {
					var answer = new byte[512];
					uint size = (uint)answer.Length;
					if (Tbsip_Submit_Command(context, 0, 200, GetProperties, (uint)GetProperties.Length, answer, ref size) != 0)
						return new TpmReading(true, version, null, null);
					var (maker, firmware) = Properties(answer.AsSpan(0, (int)Math.Min(size, (uint)answer.Length)));
					return new TpmReading(true, version, maker, firmware);
				}
				finally { Tbsip_Context_Close(context); }
			}
			catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return null; }
		}

		/// <summary>
		/// TPM2_GetCapability of the TPM's fixed properties, from TPM_PT_MANUFACTURER (0x105) for 8: its maker, vendor strings,
		/// type, and the two firmware version words. A read every user may make.
		/// </summary>
		internal static readonly byte[] GetProperties = [
			0x80, 0x01,             // TPM_ST_NO_SESSIONS
			0x00, 0x00, 0x00, 0x16, // 22 bytes
			0x00, 0x00, 0x01, 0x7A, // TPM_CC_GetCapability
			0x00, 0x00, 0x00, 0x06, // TPM_CAP_TPM_PROPERTIES
			0x00, 0x00, 0x01, 0x05, // from TPM_PT_MANUFACTURER
			0x00, 0x00, 0x00, 0x08, // 8 of them
		];

		/// <summary>
		/// The TPM's maker and firmware from its answer to <see cref="GetProperties"/>, the way tpm.msc shows them: the maker's four
		/// letters ("INTC", "AMD"), and the firmware as the two version words' halves ("600.18.32.2473"). Null for what isn't there.
		/// </summary>
		internal static (string? Maker, string? Firmware) Properties(ReadOnlySpan<byte> answer) {
			// tag (2), size (4), response code (4), more data (1), capability (4), count (4), then (property, value) pairs.
			if (answer.Length < 19 || Word(answer, 6) != 0 || Word(answer, 11) != 6) return (null, null);
			uint count = Word(answer, 15);
			uint? manufacturer = null, version1 = null, version2 = null;
			for (int i = 0, at = 19; i < count && at + 8 <= answer.Length; i++, at += 8) {
				uint property = Word(answer, at), value = Word(answer, at + 4);
				if (property == 0x105) manufacturer = value;
				else if (property == 0x10B) version1 = value;
				else if (property == 0x10C) version2 = value;
			}
			string? maker = null;
			if (manufacturer is { } m) {
				string letters = Encoding.ASCII.GetString(new[] { (byte)(m >> 24), (byte)(m >> 16), (byte)(m >> 8), (byte)m }).TrimEnd('\0', ' ');
				maker = letters.Length > 0 && letters.All(c => c is >= ' ' and <= '~') ? letters : null;
			}
			string? firmware = version1 is { } a && version2 is { } b ? $"{a >> 16}.{a & 0xFFFF}.{b >> 16}.{b & 0xFFFF}" : null;
			return (maker, firmware);
		}

		static uint Word(ReadOnlySpan<byte> b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

		[StructLayout(LayoutKind.Sequential)]
		struct TpmDeviceInfo { public uint StructVersion, TpmVersion, TpmInterfaceType, TpmImpRevision; }

		[StructLayout(LayoutKind.Sequential)]
		struct TbsContextParams2 { public uint Version, Flags; }

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool GetFirmwareType(out int type);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
		static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

		[DllImport("tbs.dll")]
		static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

		[DllImport("tbs.dll")]
		static extern uint Tbsi_Context_Create(ref TbsContextParams2 asked, out IntPtr context);

		[DllImport("tbs.dll")]
		static extern uint Tbsip_Submit_Command(IntPtr context, uint locality, uint priority, byte[] command, uint commandSize, byte[] result, ref uint resultSize);

		[DllImport("tbs.dll")]
		static extern uint Tbsip_Context_Close(IntPtr context);
	}
}
