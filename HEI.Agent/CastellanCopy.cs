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
using System.Net.Sockets;
using System.Text.Json;

namespace HEI.Agent {
	/// <summary>
	/// Castellan's own Heiward: the copy from GitHub that Manor (sold as Castellan) employs on this PC and keeps up to date
	/// itself. Castellan's Heiward and the Store's are separate copies, and only one runs on a PC, so the Store version
	/// stands aside for Castellan's (docs/STORE.md, "How the Store version runs"):
	/// <list type="bullet">
	/// <item>its setup asks nothing and installs nothing: its page says Castellan already keeps Heiward here, links to that
	/// copy's page, and says this app can be uninstalled in Settings &gt; Apps (<see cref="StoreSetup.View"/>);</item>
	/// <item>it never removes that copy, and its tasks never replace that copy's: <c>hei install</c> refuses (<see cref="Refusal"/>);</item>
	/// <item>it never stops that copy's page: its own page is on a free port of its own (<see cref="FreePort"/>), and stops
	/// once nobody looks at it.</item>
	/// </list>
	/// A copy from GitHub that Castellan doesn't employ is replaced as before (<see cref="Installer.RemoveGitHubCopy"/>).
	/// </summary>
	static class CastellanCopy {
		/// <summary>
		/// The Store version, with Castellan keeping the copy from GitHub: it stands aside. Read once per process: each
		/// start of the Store version (the Start menu, its page, <c>hei install</c>) looks again.
		/// </summary>
		public static bool StoreStandsAside => storeStandsAside.Value;

		static readonly Lazy<bool> storeStandsAside = new(() => StorePackage.IsPackaged && Keeps(Manor.Folder, Installer.InstalledExe));

		/// <summary>What <c>hei install</c> says in the Store version, standing aside, before it stops with nothing set up.</summary>
		public static string Refusal =>
			$"Castellan already keeps Heiward on this PC and up to date ({Installer.InstallDir}). Only one copy of Heiward runs on a PC, " +
			"so this copy from the Microsoft Store isn't set up, and Castellan's stays as it is. Open Heiward from Castellan. " +
			"You can uninstall this app in Settings > Apps > Installed apps.";

		/// <summary>
		/// Castellan keeps the copy from GitHub at <paramref name="gitHubExe"/>: Manor is installed (<see cref="Manor.Load(string)"/>),
		/// its agents.json employs "heiward", and of that entry's <c>paths.hei</c> (a path, or a list of them, with %VAR%s) the
		/// first that exists is that copy's hei.exe, as Manor finds it (its registry.ts, resolvePaths). Manor lists the copy
		/// from GitHub first and the Store's alias after it, so a Store copy alone, which Manor adopts as it is, isn't
		/// counted. An entry that names no <c>paths.hei</c> counts too: the copy from GitHub is the one Manor installs. An
		/// agents.json that can't be read, or isn't Manor's shape, counts as no.
		/// </summary>
		/// <param name="exists">Whether a file is there: File.Exists, unless a test says.</param>
		internal static bool Keeps(string? manorFolder, string gitHubExe, Func<string, bool>? exists = null) {
			exists ??= File.Exists;
			if (manorFolder == null || Manor.Load(manorFolder) == null || !exists(gitHubExe)) return false;
			try {
				using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(manorFolder, "agents.json")).TrimStart('﻿'));
				if (Entry(doc.RootElement) is not { } entry) return false;
				if (!entry.TryGetProperty("paths", out JsonElement paths) || paths.ValueKind != JsonValueKind.Object
					|| !paths.TryGetProperty("hei", out JsonElement hei)) return true;
				string? first = Candidates(hei).Select(Expand).FirstOrDefault(p => p != null && exists(p));
				return first != null && SamePath(first, gitHubExe);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException) {
				return false;
			}
		}

		/// <summary>agents.json's entry for Heiward ("id": "heiward"), when its root is Manor's: an object with an "agents" list.</summary>
		internal static JsonElement? Entry(JsonElement root) {
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("agents", out JsonElement agents) || agents.ValueKind != JsonValueKind.Array) return null;
			foreach (JsonElement a in agents.EnumerateArray())
				if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String && id.GetString() == "heiward")
					return a;
			return null;
		}

		/// <summary>A path, or a list of them, as Manor's registry takes "paths.&lt;name&gt;"; anything else gives none.</summary>
		static IEnumerable<string> Candidates(JsonElement value) => value.ValueKind switch {
			JsonValueKind.String => [value.GetString()!],
			JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!),
			_ => [],
		};

		/// <summary>%VAR%s filled in, as Manor's expandEnv does (an unknown one stays as written); null for a path that isn't a full one.</summary>
		static string? Expand(string candidate) {
			string path = Environment.ExpandEnvironmentVariables(candidate.Trim());
			try { return path.Length > 0 && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null; }
			catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
		}

		static bool SamePath(string a, string b) =>
			string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Where the Store version's page, standing aside, keeps its port, for the next start to find it up: the package's
		/// own storage, which Windows removes with the app.
		/// </summary>
		static string PortFile => Path.Combine(StorePackage.Storage, "page-port.txt");

		/// <summary>The port the Store version's page took last, standing aside; null when it has none.</summary>
		public static int? PagePort() {
			try { return int.TryParse(File.ReadAllText(PortFile).Trim(), out int port) && port is > 0 and <= 65535 ? port : null; }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
		}

		public static void SavePagePort(int port) {
			try { AgentPaths.WriteAtomic(PortFile, port.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AgentPaths.AppendLog("the page's port wasn't kept: " + e.Message); }
		}

		/// <summary>
		/// A port no program listens on now, on 127.0.0.1: the Store version's page, standing aside, never takes the port
		/// Castellan's copy serves its page on, nor any other copy's or agent's.
		/// </summary>
		public static int FreePort() {
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
			finally { listener.Stop(); }
		}
	}
}
