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

using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HEI.Agent {
	/// <summary>
	/// A game's Uninstall, on its page: the launcher's own uninstaller, never Heiward deleting anything. How each launcher's games
	/// are uninstalled, found from what Windows lists in its uninstall entries (the registry's ...\CurrentVersion\Uninstall, in
	/// HKLM's 64-bit and 32-bit views and in HKCU):
	/// <list type="bullet">
	/// <item>Steam: its entry "Steam App &lt;app id&gt;" says Steam has the game; steam://uninstall/&lt;app id&gt; opens Steam's own
	/// question, which the entry's UninstallString runs too.</item>
	/// <item>GOG GALAXY ("&lt;id&gt;_is1"), Ubisoft Connect ("Uplay Install &lt;id&gt;"), Battle.net (the entry its game was found by),
	/// the EA app (the entry whose InstallLocation is the game's folder): the entry's UninstallString, exactly as registered, never
	/// its QuietUninstallString and never with a flag added. The page asks first, in Heiward's own dialog.</item>
	/// <item>The Xbox app and the Microsoft Store: Windows' Installed apps (ms-settings:appsfeatures), to find the game there.</item>
	/// <item>The Epic Games Launcher writes no entry and lets no other app uninstall its games: the page's button is greyed and
	/// says so, as for any game with no entry found. No link is guessed.</item>
	/// </list>
	/// The page sends only the game's id: the game comes from the last check and its entry is read again here, so nothing the
	/// request says is ever run.
	/// </summary>
	static class GameUninstall {
		/// <summary>The ways: Steam's link, an uninstall entry's own uninstaller, or Windows' Installed apps.</summary>
		public const string Steam = "steam", Entry = "entry", Settings = "settings";

		public const string SettingsLink = "ms-settings:appsfeatures";

		/// <summary>Where Windows lists what can be uninstalled: HKLM's 64-bit view, its 32-bit view (WOW6432Node), and this user's.</summary>
		internal static readonly string[] Views = [
			@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
			GameLibraries.UninstallKey,
			@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall",
		];

		/// <summary>One of Windows' uninstall entries: its view, its key's name, and the two values read.</summary>
		internal sealed record UninstallEntry(string View, string Key, string? InstallLocation, string? UninstallString);

		/// <summary>How a game is uninstalled now: <see cref="Way"/>, and the link opened or the UninstallString run.</summary>
		internal sealed record Plan(string Way, string Target);

		/// <summary>Every view's entries, in <see cref="Views"/>' order.</summary>
		internal static List<UninstallEntry> Entries(IGameRegistry registry) {
			var list = new List<UninstallEntry>();
			foreach (string view in Views)
				foreach (string key in registry.SubKeys(view))
					list.Add(new UninstallEntry(view, key, registry.Value(view + "\\" + key, "InstallLocation"), registry.Value(view + "\\" + key, "UninstallString")));
			return list;
		}

		/// <summary>The entry names a launcher gives its games' uninstall entries.</summary>
		internal static IEnumerable<string> OwnKeys(GameInstall game) => game.Launcher switch {
			GameLaunchers.Steam => ["Steam App " + game.AppId],
			GameLaunchers.Gog => [game.AppId + "_is1"],
			GameLaunchers.Ubisoft => ["Uplay Install " + game.AppId],
			GameLaunchers.BattleNet => [game.AppId], // the entry the game was found by
			_ => [],
		};

		/// <summary>How <paramref name="game"/> is uninstalled now, read fresh; null when it can't be from here.</summary>
		public static Plan? Find(GameInstall game, IGameRegistry registry) => Find(game, new Lazy<List<UninstallEntry>>(() => Entries(registry)));

		/// <summary>
		/// The launcher's own entry name first, in each view; else the entry whose InstallLocation is the game's folder. An entry
		/// whose UninstallString can't be run as it is, or would run without asking, isn't used.
		/// </summary>
		internal static Plan? Find(GameInstall game, Lazy<List<UninstallEntry>> entries) {
			if (game.Launcher == GameLaunchers.Epic) return null; // no entry, and no link anyone may use
			if (game.Launcher == GameLaunchers.Xbox) return new Plan(Settings, SettingsLink);
			UninstallEntry? entry = null;
			foreach (string key in OwnKeys(game)) {
				entry = entries.Value.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && Usable(e.UninstallString));
				if (entry != null) break;
			}
			entry ??= entries.Value.FirstOrDefault(e => SameFolder(e.InstallLocation, game.Folder) && Usable(e.UninstallString));
			if (entry == null) return null;
			if (game.Launcher == GameLaunchers.Steam) return SteamApp.IsMatch(game.AppId) ? new Plan(Steam, SteamLink(game.AppId)) : null;
			return new Plan(Entry, entry.UninstallString!);
		}

		static readonly Regex SteamApp = new(@"^\d{1,10}$", RegexOptions.CultureInvariant);

		public static string SteamLink(string appId) => "steam://uninstall/" + appId;

		/// <summary>The links Heiward opens: Steam's uninstall of an app, and Windows' Installed apps. Nothing else.</summary>
		internal static bool IsOwnLink(string link) =>
			link == SettingsLink || (link.StartsWith("steam://uninstall/", StringComparison.Ordinal) && SteamApp.IsMatch(link["steam://uninstall/".Length..]));

		/// <summary>Each game's way, from the check: read once for all of them, and only when one needs it.</summary>
		public static List<GameInstall> Mark(List<GameInstall> games, IGameRegistry registry) {
			var entries = new Lazy<List<UninstallEntry>>(() => Entries(registry));
			return games.Select(g => g with { Uninstall = Find(g, entries)?.Way }).ToList();
		}

		/// <summary>Why a game's Uninstall is greyed (<paramref name="way"/> null), for its tooltip; null when it isn't.</summary>
		public static string? Note(string launcher, string? way) => way != null ? null : launcher == GameLaunchers.Epic
			? "The Epic Games Launcher doesn't let other apps uninstall its games: uninstall it from its library."
			: $"Windows lists no uninstaller for it: uninstall it from {GameLaunchers.Name(launcher)}.";

		static bool SameFolder(string? location, string folder) {
			if (string.IsNullOrWhiteSpace(location)) return false;
			string path = location.Trim().Trim('"').Replace('/', '\\');
			return path.Length > 3 && Path.IsPathFullyQualified(path) && GameLibraries.Full(path).Equals(GameLibraries.Full(folder), StringComparison.OrdinalIgnoreCase);
		}

		// ------------------------------------------------------------------ an UninstallString, as Windows would run it

		/// <summary>
		/// An UninstallString as a program and its arguments: "C:\…\unins000.exe" quoted or not, then what follows as it is.
		/// MsiExec by name is Windows' own, in System32. Null for anything else: a program not named by its full path.
		/// </summary>
		internal static (string Exe, string Arguments)? Split(string? command) {
			if (string.IsNullOrWhiteSpace(command)) return null;
			string s = command.Trim();
			string exe, args;
			if (s.StartsWith('"')) {
				int close = s.IndexOf('"', 1);
				if (close < 0) return null;
				exe = s[1..close];
				args = s[(close + 1)..];
			}
			else {
				// Unquoted: the program ends at the first ".exe" followed by a space or the end, as Windows reads "C:\Program Files\…".
				Match m = UnquotedExe.Match(s);
				if (!m.Success) return null;
				exe = m.Value;
				args = s[m.Length..];
			}
			if (args.Length > 0 && !char.IsWhiteSpace(args[0])) return null; // "C:\x.exe"junk
			exe = exe.Trim();
			if (exe.Equals("msiexec", StringComparison.OrdinalIgnoreCase) || exe.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
				exe = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
			if (!Path.IsPathFullyQualified(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
			return (exe, args.Trim());
		}

		static readonly Regex UnquotedExe = new(@"^.+?\.exe(?=\s|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

		/// <summary>The switches that make an uninstaller run without asking: Inno Setup's, NSIS's, MsiExec's, and the usual long ones.</summary>
		static readonly HashSet<string> Quiet = new(StringComparer.OrdinalIgnoreCase) {
			"s", "silent", "verysilent", "quiet", "q", "qn", "qn+", "qn-", "qb", "qb+", "qb-", "qb!", "qr", "passive", "unattended", "unattendedmode",
		};

		/// <summary>One of <paramref name="arguments"/> asks the uninstaller to run without asking.</summary>
		internal static bool RunsQuietly(string arguments) =>
			arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(a => {
				string flag = a.Trim('"');
				if (!(flag.StartsWith('/') || flag.StartsWith('-'))) return false;
				flag = flag.TrimStart('/', '-');
				int eq = flag.IndexOfAny(['=', ':']);
				return Quiet.Contains(eq < 0 ? flag : flag[..eq]);
			});

		/// <summary>An UninstallString Heiward will run: a program by its full path (or MsiExec), and nothing that skips its questions.</summary>
		internal static bool Usable(string? command) => Split(command) is { } c && !RunsQuietly(c.Arguments);
	}

	/// <summary>What opens a game's uninstaller: the shell here, a recorder in tests, so no test ever starts one.</summary>
	interface IUninstallOpener {
		/// <summary>Opens Steam's steam://uninstall/&lt;app id&gt; or Windows' Installed apps, through the shell.</summary>
		void OpenLink(string link);
		/// <summary>Starts an uninstall entry's UninstallString, exactly as registered.</summary>
		void Run(string uninstallString);
	}

	/// <summary>The shell: a link opens in what Windows has for it; an uninstaller starts as Windows' Installed apps starts it, so one that needs an administrator asks Windows for one.</summary>
	sealed class ShellOpener : IUninstallOpener {
		public void OpenLink(string link) {
			if (!GameUninstall.IsOwnLink(link)) throw new InvalidOperationException("Not a link Heiward opens.");
			using Process? _ = Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
		}

		public void Run(string uninstallString) {
			if (GameUninstall.Split(uninstallString) is not { } c || GameUninstall.RunsQuietly(c.Arguments))
				throw new InvalidOperationException("Not an uninstaller Heiward runs.");
			using Process? _ = Process.Start(new ProcessStartInfo(c.Exe, c.Arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(c.Exe) ?? "" });
		}
	}

	/// <summary>
	/// The page's Uninstall: <see cref="Registry"/> read for the game's entry, <see cref="Opener"/> opening it. <see cref="Packaged"/>:
	/// the Microsoft Store version, whose answer says so if Windows won't let it start an uninstaller.
	/// </summary>
	sealed record GameUninstaller(IGameRegistry Registry, IUninstallOpener Opener, bool Packaged) {
		public static GameUninstaller Windows() => new(new WindowsRegistry(), new ShellOpener(), StorePackage.IsPackaged);

		/// <summary>What the page shows: opened, and what to do next; or why not, and where to uninstall it instead.</summary>
		public sealed record Answer(bool Opened, string Message, GameUninstall.Plan? Plan = null);

		/// <summary>Opens <paramref name="game"/>'s uninstaller (Steam's question, its own, or Windows' Installed apps). Never says it's uninstalled.</summary>
		public Answer Open(GameInstall game) {
			if (!Directory.Exists(game.Folder)) return new(false, $"{game.Name} isn't in its folder any more: check again to see what's installed.");
			if (GameUninstall.Find(game, Registry) is not { } plan) return new(false, GameUninstall.Note(game.Launcher, null)!);
			string launcher = GameLaunchers.Name(game.Launcher);
			string what = plan.Way switch {
				GameUninstall.Steam => "Steam",
				GameUninstall.Settings => "Windows' Installed apps",
				_ => $"{game.Name}'s uninstaller",
			};
			try {
				if (plan.Way == GameUninstall.Entry) Opener.Run(plan.Target);
				else Opener.OpenLink(plan.Target);
			}
			catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
				string instead = plan.Way == GameUninstall.Settings ? "Uninstall it in Settings > Apps > Installed apps." : $"Uninstall it from {launcher}, or in Settings > Apps > Installed apps.";
				if (e is Win32Exception { NativeErrorCode: 1223 }) // ERROR_CANCELLED: Windows asked for an administrator, and was told no
					return new(false, $"Windows asked whether to let {what} make changes, and was told no: nothing was opened. {instead}", plan);
				return new(false, (Packaged ? $"The Microsoft Store version of Heiward couldn't open {what}" : $"{what} didn't open") + $" ({e.Message}). {instead}", plan);
			}
			return new(true, plan.Way switch {
				GameUninstall.Steam => $"Steam is asking whether to uninstall {game.Name}: answer it in Steam's window. The next games check shows whether it's gone.",
				GameUninstall.Settings => $"Windows' Installed apps is open: find {game.Name} there to uninstall it. The next games check shows whether it's gone.",
				_ => $"{game.Name}'s uninstaller is open: it asks before it removes anything (if you don't see it, look on the taskbar). The next games check shows whether it's gone.",
			}, plan);
		}
	}
}
