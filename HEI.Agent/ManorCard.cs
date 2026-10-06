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
	/// complete without Manor either way, and nothing here reads or runs any of Manor's code.
	/// </summary>
	/// <param name="Show">The card shows on the home page: Heiward's own switch, on until "Not now".</param>
	/// <param name="Url">Manor's site, which "See the manor" opens.</param>
	sealed record ManorCard(bool Show, string Url) {
		// TODO: Manor's site has no domain yet: it's an open decision in the manor's selling plan (docs/SELLING.md, "Open
		// decisions": the domain). Set it here once it's chosen; nothing else names it.
		public const string ManorSiteUrl = "https://manor.example";

		/// <summary>
		/// The card for the page: null with Manor installed (<paramref name="manor"/>), so neither the card nor its switch
		/// in Settings shows; else whether it shows, by <paramref name="cfg"/>.
		/// </summary>
		public static ManorCard? For(AgentConfig cfg, Manor? manor) => manor != null ? null : new ManorCard(cfg.ManorCard, ManorSiteUrl);
	}
}
