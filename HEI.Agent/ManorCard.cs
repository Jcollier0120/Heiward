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
	/// The home page's Castellan card, while Manor (sold as Castellan) isn't installed on this PC: a few lines on what it adds, below
	/// everything else on the page, with a link to its site and a "Not now" that hides it for good
	/// (<see cref="AgentConfig.ManorCard"/>, which Settings can turn on again). It's static text in the page: nothing is
	/// asked of the site, and nothing is sent anywhere, unless you open the link. With Manor installed there's no card:
	/// the title bar's "Back to &lt;manor&gt;" already says where Heiward works (<see cref="Manor.Stamp"/>). Heiward is
	/// complete without Manor either way, and nothing here reads or runs any of Manor's code. Shown since 1.9.24, when
	/// <see cref="Launched"/> was turned on: Castellan is on sale.
	/// </summary>
	/// <param name="Show">The card shows on the home page: Heiward's own switch, on until "Not now".</param>
	/// <param name="Url">Castellan's site, which "See Castellan" opens.</param>
	sealed record ManorCard(bool Show, string Url) {
		// The launch switch, on since 1.9.24. Off, there's no card and no switch for it in Settings: For is null, as with
		// Manor installed.
		public const bool Launched = true;

		/// <summary>
		/// For tests only: the card as if <see cref="Launched"/> were on, whichever way it's set. Heiward itself never sets
		/// it.
		/// </summary>
		internal static bool ShowBeforeLaunch { get; set; }

		// Castellan's site, where it's sold, with its trial; nothing else names it.
		public const string ManorSiteUrl = "https://castellan-software.com";

		/// <summary>
		/// The card for the page: null before launch (<see cref="Launched"/>) or with Manor installed
		/// (<paramref name="manor"/>), so neither the card nor its switch in Settings shows; else whether it shows, by
		/// <paramref name="cfg"/>.
		/// </summary>
		public static ManorCard? For(AgentConfig cfg, Manor? manor) =>
			!(Launched || ShowBeforeLaunch) || manor != null ? null : new ManorCard(cfg.ManorCard, ManorSiteUrl);
	}
}
