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

namespace HEI.Agent {
	/// <summary>
	/// The home page's "In a manor" card, while Manor isn't installed on this PC: a few lines on what Manor adds, below
	/// everything else on the page, with a link to its site and a "Not now" that hides it for good
	/// (<see cref="AgentConfig.ManorCard"/>, which Settings can turn on again). It's static text in the page: nothing is
	/// asked of the site, and nothing is sent anywhere, unless you open the link. With Manor installed there's no card:
	/// the title bar's "Back to &lt;manor&gt;" already says where Heiward works (<see cref="Manor.Stamp"/>). Heiward is
	/// complete without Manor either way, and nothing here reads or runs any of Manor's code. Held back until launch: no
	/// card shows anywhere while <see cref="Launched"/> is off.
	/// </summary>
	/// <param name="Show">The card shows on the home page: Heiward's own switch, on until "Not now".</param>
	/// <param name="Url">Manor's site, which "See the manor" opens.</param>
	sealed record ManorCard(bool Show, string Url) {
		// The launch switch. Off, there's no card and no switch for it in Settings: For is null, as with Manor installed.
		// Turn it on when Castellan is on sale and ManorSiteUrl is its real site (see Manor's docs/SELLING.md); not before.
		// TODO: the card's copy (wwwroot/app.js's renderManorCard, index.html, Settings' switch, the READMEs and
		// docs/PRIVACY.md) says "Manor": it must become "Castellan", rewritten for it, when Launched is turned on.
		public const bool Launched = false;

		/// <summary>
		/// For tests only: the card as if <see cref="Launched"/> were on, so its tests run before launch. Heiward itself
		/// never sets it, so a release shows no card until <see cref="Launched"/> is turned on.
		/// </summary>
		internal static bool ShowBeforeLaunch { get; set; }

		// TODO: Manor's site has no domain yet: it's an open decision in the manor's selling plan (docs/SELLING.md, "Open
		// decisions": the domain). Set it here once it's chosen; nothing else names it.
		public const string ManorSiteUrl = "https://manor.example";

		/// <summary>
		/// The card for the page: null before launch (<see cref="Launched"/>) or with Manor installed
		/// (<paramref name="manor"/>), so neither the card nor its switch in Settings shows; else whether it shows, by
		/// <paramref name="cfg"/>.
		/// </summary>
		public static ManorCard? For(AgentConfig cfg, Manor? manor) =>
			!(Launched || ShowBeforeLaunch) || manor != null ? null : new ManorCard(cfg.ManorCard, ManorSiteUrl);
	}
}
