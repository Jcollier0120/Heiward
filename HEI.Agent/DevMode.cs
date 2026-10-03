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
	/// Developer mode as it stands now. With Manor installed and its settings.json saying "developerOptions": true or
	/// false, Manor's Developer options decide it, for every agent in the manor: the page's Developer area, the daily
	/// developer check, automatic cleanup of developer leftovers, the developer requests and <c>hei dev</c>. Heiward's own
	/// switch then waits, its "developerMode" kept in its settings.json as the user left it, so it decides again without
	/// Manor, or once Manor's settings stop saying. Otherwise Heiward's own switch decides, as it always has.
	/// Read fresh for each page load and poll, each scan's housekeeping, and each command: never kept.
	/// </summary>
	/// <param name="On">Developer mode is on.</param>
	/// <param name="Manor">The manor whose Developer options decide it; null when Heiward's own switch does.</param>
	sealed record DevMode(bool On, Manor? Manor) {
		/// <summary>Developer mode now: Manor's settings.json, read fresh, then Heiward's own switch in <paramref name="cfg"/>.</summary>
		public static DevMode Now(AgentConfig cfg) => Of(cfg, Manor.Load());

		/// <summary>Developer mode with <paramref name="manor"/> (null when Manor isn't installed) and Heiward's own switch.</summary>
		public static DevMode Of(AgentConfig cfg, Manor? manor) =>
			manor?.DeveloperOptions is bool on ? new DevMode(on, manor) : new DevMode(cfg.DeveloperModeOn, null);

		/// <summary>Manor's Developer options decide it, not Heiward's own switch.</summary>
		[MemberNotNullWhen(true, nameof(Manor))]
		public bool ByManor => Manor != null;

		string OnOff => On ? "on" : "off";

		/// <summary>"Weasel Manor's Developer options turn this on", as the page's Settings says it in place of the switch.</summary>
		public string? ManorNote => ByManor ? $"{Manor.Name}'s Developer options turn this {OnOff}" : null;

		/// <summary>The developer requests' answer while it's off: where it's turned on.</summary>
		public string PageOffText => ByManor
			? $"Developer mode is off: {Manor.Name}'s Developer options turn it off. Change it in {Manor.Name}."
			: "Developer mode is off: turn it on in Settings.";

		/// <summary><c>hei dev</c>'s answer while it's off: where it's turned on.</summary>
		public string CommandOffText => ByManor
			? $"Developer mode is off: {Manor.Name}'s Developer options turn it off. Change it in {Manor.Name}: {Manor.Url}"
			: "Developer mode is off: turn it on in the review page's Settings, or set \"developerMode\": \"on\" in " + AgentPaths.Config;

		/// <summary>Why the page's Settings can't turn it on or off while Manor decides.</summary>
		public string? ManorDecidesText => ByManor
			? $"{Manor.Name}'s Developer options turn developer mode {OnOff}, for every agent in the manor. Change it in {Manor.Name}."
			: null;

		/// <summary>"on", "off", "on (Weasel Manor's Developer options)": for <c>hei status</c> and heiward.log.</summary>
		public string Describe() => ByManor ? $"{OnOff} ({Manor.Name}'s Developer options)" : OnOff;
	}
}
