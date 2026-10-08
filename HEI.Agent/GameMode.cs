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

using System.Diagnostics.CodeAnalysis;

namespace HEI.Agent {
	/// <summary>
	/// Game mode as it stands now: the page's Games area, the daily games check, and automatic cleanup of what games leave
	/// behind. Heiward's own switch ("gameMode" in its settings.json, the page's Settings) decides it, off until it's turned on.
	/// The seam for Manor, as <see cref="DevMode"/> has for its Developer options: when Manor's settings.json says "gameMode":
	/// true or false (<see cref="HEI.Agent.Manor.GameMode"/>), Manor decides instead, and Heiward's own switch waits, kept as the
	/// user left it. Manor doesn't say yet; a hire for gaming can also set Heiward's own switch in its first settings.
	/// Read fresh for each page load and poll, each scan's housekeeping, and each command: never kept.
	/// </summary>
	/// <param name="On">Game mode is on.</param>
	/// <param name="Manor">The manor that decides it; null when Heiward's own switch does.</param>
	sealed record GameMode(bool On, Manor? Manor) {
		/// <summary>Game mode now: Manor's settings.json, read fresh, then Heiward's own switch in <paramref name="cfg"/>.</summary>
		public static GameMode Now(AgentConfig cfg) => Of(cfg, Manor.Load());

		/// <summary>Game mode with <paramref name="manor"/> (null when Manor isn't installed) and Heiward's own switch.</summary>
		public static GameMode Of(AgentConfig cfg, Manor? manor) =>
			manor?.GameMode is bool on ? new GameMode(on, manor) : new GameMode(cfg.GameModeOn, null);

		/// <summary>Manor decides it, not Heiward's own switch.</summary>
		[MemberNotNullWhen(true, nameof(Manor))]
		public bool ByManor => Manor != null;

		string OnOff => On ? "on" : "off";

		/// <summary>"Weasel Manor turns this on", as the page's Settings says it in place of the switch.</summary>
		public string? ManorNote => ByManor ? $"{Manor.Name} turns this {OnOff}" : null;

		/// <summary>The games requests' answer while it's off: where it's turned on.</summary>
		public string PageOffText => ByManor
			? $"Game mode is off: {Manor.Name} turns it off. Change it in {Manor.Name}."
			: "Game mode is off: turn it on in Settings.";

		/// <summary><c>hei games</c>' answer while it's off.</summary>
		public string CommandOffText => ByManor
			? $"Game mode is off: {Manor.Name} turns it off. Change it in {Manor.Name}: {Manor.Url}"
			: "Game mode is off: turn it on in the review page's Settings, or set \"gameMode\": \"on\" in " + AgentPaths.Config;

		/// <summary>Why the page's Settings can't turn it on or off while Manor decides.</summary>
		public string? ManorDecidesText => ByManor ? $"{Manor.Name} turns game mode {OnOff}. Change it in {Manor.Name}." : null;

		/// <summary>"on", "off", "on (Weasel Manor's)": for <c>hei status</c> and heiward.log.</summary>
		public string Describe() => ByManor ? $"{OnOff} ({Manor.Name}'s)" : OnOff;
	}
}
