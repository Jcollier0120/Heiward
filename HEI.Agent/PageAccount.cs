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

using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace HEI.Agent {
	/// <summary>
	/// Whose page this is. Every Windows account on a PC has its own Heiward, with its own photos, settings and Recycle Bin,
	/// and every account's programs can reach 127.0.0.1. So the page answers only programs of the account it runs as: each
	/// connection is traced to the process that opened it (Windows' TCP table), and that process to its account (its token's
	/// user). Another account's, or one that can't be told (the process is gone, or Windows won't say: a service's, or an
	/// elevated one), is refused, the page itself included, so the token inside it never reaches another account.
	/// </summary>
	static class PageAccount {
		/// <summary>This process's account: its SID.</summary>
		public static readonly string Sid = WindowsIdentity.GetCurrent().User?.Value ?? "";

		/// <summary>What /api/ping says of the account it answers for: a hash of its SID, which tells two accounts' pages apart and names neither.</summary>
		public static string Hash(string sid) =>
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("heiward-account|" + sid.ToUpperInvariant())))[..16].ToLowerInvariant();

		/// <summary>This account's <see cref="Hash"/>.</summary>
		public static readonly string Mine = Hash(Sid);

		/// <summary>The account a caller runs as is this one: never when it couldn't be told.</summary>
		public static bool IsThisAccount(string? sid) => !string.IsNullOrEmpty(sid) && Sid.Length > 0 && string.Equals(sid, Sid, StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// The program at the other end of a connection made to this PC's <paramref name="localPort"/> from
		/// <paramref name="remote"/>:<paramref name="remotePort"/> (its process, and that process's account); null for what can't be told.
		/// </summary>
		public static (int? Pid, string? Sid) Caller(IPAddress? remote, int remotePort, IPAddress? local, int localPort) {
			if (remote == null) return (null, null);
			if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
			if (local is { IsIPv4MappedToIPv6: true }) local = local.MapToIPv4();
			if (remote.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return (null, null); // the page listens on 127.0.0.1 alone
			uint remoteAddr = Addr(remote);
			uint? localAddr = local == null ? null : Addr(local);
			// The caller's own end of the connection: its local end is the page's remote one, and its remote end the page.
			foreach (Row r in Table(TcpTableOwnerPidConnections))
				if (r.LocalPort == remotePort && r.LocalAddr == remoteAddr && r.RemotePort == localPort && (localAddr == null || r.RemoteAddr == localAddr) && r.Pid != 0)
					return (r.Pid, OfProcess(r.Pid));
			return (null, null);
		}

		/// <summary>
		/// A program of another account (or of one that can't be told) listens on <paramref name="port"/> at 127.0.0.1 or every
		/// address: another account's Heiward page, mostly, on a PC where more than one account signs in.
		/// </summary>
		public static bool HeldByAnother(int port) =>
			Table(TcpTableOwnerPidListener).Where(r => r.LocalPort == port && (r.LocalAddr == Loopback || r.LocalAddr == 0))
				.Any(r => !IsThisAccount(OfProcess(r.Pid)));

		/// <summary>Whether every program listening on <paramref name="port"/> (127.0.0.1 or every address) is this account's, and one is.</summary>
		public static bool ListenedByThisAccount(int port) {
			var listening = Table(TcpTableOwnerPidListener).Where(r => r.LocalPort == port && (r.LocalAddr == Loopback || r.LocalAddr == 0)).ToList();
			return listening.Count > 0 && listening.All(r => IsThisAccount(OfProcess(r.Pid)));
		}

		/// <summary>The account a process runs as (its token's user); null when Windows won't say, as for another account's.</summary>
		internal static string? OfProcess(int pid) {
			if (pid == Environment.ProcessId) return Sid;
			IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
			if (process == IntPtr.Zero) return null;
			try {
				if (!OpenProcessToken(process, TokenQuery, out IntPtr token)) return null;
				try {
					GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int needed);
					if (needed <= 0) return null;
					IntPtr buffer = Marshal.AllocHGlobal(needed);
					try {
						if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return null;
						return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value; // TOKEN_USER: its SID_AND_ATTRIBUTES' Sid comes first
					}
					finally { Marshal.FreeHGlobal(buffer); }
				}
				finally { CloseHandle(token); }
			}
			catch (Exception e) when (e is ArgumentException or OutOfMemoryException) { return null; }
			finally { CloseHandle(process); }
		}

		// ------------------------------------------------------------------ Windows' TCP table (IPv4)

		readonly record struct Row(uint State, uint LocalAddr, int LocalPort, uint RemoteAddr, int RemotePort, int Pid);

		static readonly uint Loopback = Addr(IPAddress.Loopback);

		/// <summary>An IPv4 address as MIB_TCPROW_OWNER_PID keeps it: network byte order, read as a little-endian DWORD.</summary>
		static uint Addr(IPAddress a) => BitConverter.ToUInt32(a.GetAddressBytes(), 0);

		/// <summary>A port as MIB_TCPROW_OWNER_PID keeps it: network byte order in the DWORD's low 16 bits.</summary>
		static int Port(uint p) => (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));

		static List<Row> Table(int tableClass) {
			var rows = new List<Row>();
			int size = 0;
			for (int attempt = 0; attempt < 5; attempt++) {
				uint first = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, tableClass, 0);
				if (first != ErrorInsufficientBuffer && first != NoError) return rows;
				IntPtr buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
				try {
					uint result = GetExtendedTcpTable(buffer, ref size, false, AfInet, tableClass, 0);
					if (result == ErrorInsufficientBuffer) continue; // the table grew in between
					if (result != NoError) return rows;
					int count = Marshal.ReadInt32(buffer);
					for (int i = 0; i < count; i++) {
						IntPtr row = buffer + 4 + i * RowSize;
						rows.Add(new Row(
							(uint)Marshal.ReadInt32(row), (uint)Marshal.ReadInt32(row, 4), Port((uint)Marshal.ReadInt32(row, 8)),
							(uint)Marshal.ReadInt32(row, 12), Port((uint)Marshal.ReadInt32(row, 16)), Marshal.ReadInt32(row, 20)));
					}
					return rows;
				}
				finally { Marshal.FreeHGlobal(buffer); }
			}
			return rows;
		}

		const int AfInet = 2, TcpTableOwnerPidListener = 3, TcpTableOwnerPidConnections = 4, RowSize = 24;
		const uint NoError = 0, ErrorInsufficientBuffer = 122;
		const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008;
		const int TokenUser = 1;

		[DllImport("iphlpapi.dll")]
		static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

		[DllImport("kernel32.dll")]
		static extern bool CloseHandle(IntPtr handle);

		[DllImport("advapi32.dll", SetLastError = true)]
		static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

		[DllImport("advapi32.dll", SetLastError = true)]
		static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int needed);
	}
}
