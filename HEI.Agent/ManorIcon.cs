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
using System.Text;
using System.Text.RegularExpressions;

namespace HEI.Agent {
	/// <summary>
	/// Manor's icon, for the title bar's "Back to &lt;manor&gt;" (<see cref="Manor"/>), served from Heiward's own address as
	/// /manor-icon.svg, since the page loads images from itself only. As the Steward's kit 2.3.0 has it (node/manor.ts):
	/// the icon Manor's page serves (its banner's), kept for ten minutes; else the generic one in Manor's app folder;
	/// else a plain house. Never an SVG with anything in it that runs.
	/// </summary>
	static class ManorIcon {
		/// <summary>A plain house, for when Manor's own icon can't be had.</summary>
		public const string HouseSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><path d=\"M2 7.5 8 2.5l6 5V14H9.8v-4H6.2v4H2z\" fill=\"none\" stroke=\"#5f5f5f\" stroke-width=\"1.3\" stroke-linejoin=\"round\"/></svg>";

		/// <summary>The most of an icon taken, from Manor's page or its app folder.</summary>
		internal const int MaxBytes = 512 * 1024;

		/// <summary>How long Manor's page has to answer, and how long its icon is kept.</summary>
		static readonly TimeSpan Wait = TimeSpan.FromSeconds(1.5), Keep = TimeSpan.FromMinutes(10);

		static readonly Regex AnSvg = new(@"^\s*(<\?xml[^>]*>\s*)?(<!--[\s\S]*?-->\s*)*<svg[\s>]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		static readonly Regex Runs = new(@"<script|\son[a-z]+\s*=|javascript:|<foreignObject", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		/// <summary>An SVG fit to serve from Heiward's address: an SVG, and nothing in it that runs (the kit's safeSvg).</summary>
		public static bool IsSafe(string? svg) => svg != null && AnSvg.IsMatch(svg) && !Runs.IsMatch(svg);

		sealed record Kept(DateTime AtUtc, int Port, string Svg);

		/// <summary>The icon Manor's page last gave, on which port and when.</summary>
		static Kept? kept;

		/// <summary>For tests: forget the kept icon.</summary>
		internal static void Forget() => Volatile.Write(ref kept, null);

		/// <summary>To Manor's page on this PC: no proxy, and no following it elsewhere.</summary>
		static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

		/// <summary>Manor's icon, from Manor's folder (MANOR_HOME, or %USERPROFILE%\.manor).</summary>
		public static Task<string> GetAsync() => Manor.Folder is string folder ? GetAsync(folder, DateTime.UtcNow) : Task.FromResult(HouseSvg);

		public static async Task<string> GetAsync(string folder, DateTime nowUtc) {
			if (Manor.Load(folder) is not Manor manor) return HouseSvg;
			if (Volatile.Read(ref kept) is { } k && k.Port == manor.Port && nowUtc >= k.AtUtc && nowUtc - k.AtUtc < Keep) return k.Svg;
			if (await FromPageAsync(manor.Port) is string live) {
				Volatile.Write(ref kept, new Kept(nowUtc, manor.Port, live));
				return live;
			}
			return FromApp(folder) ?? HouseSvg;
		}

		/// <summary>The icon Manor's page shows, as it serves it; null when it doesn't answer in time, or with something else.</summary>
		static async Task<string?> FromPageAsync(int port) {
			try {
				using var cts = new CancellationTokenSource(Wait);
				using HttpResponseMessage answer = await Http.GetAsync($"http://127.0.0.1:{port}/favicon.svg", HttpCompletionOption.ResponseHeadersRead, cts.Token);
				if (answer.StatusCode != HttpStatusCode.OK || answer.Content.Headers.ContentLength > MaxBytes) return null;
				await using Stream body = await answer.Content.ReadAsStreamAsync(cts.Token);
				var buffer = new byte[MaxBytes + 1];
				int read = 0, n;
				while (read < buffer.Length && (n = await body.ReadAsync(buffer.AsMemory(read), cts.Token)) > 0) read += n;
				if (read > MaxBytes) return null;
				using var text = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
				string svg = text.ReadToEnd();
				return IsSafe(svg) ? svg : null;
			}
			catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { return null; }
		}

		/// <summary>The generic icon in Manor's app folder (app\art\manor-icon.svg), or null.</summary>
		static string? FromApp(string folder) {
			try {
				var file = new FileInfo(Path.Combine(folder, "app", "art", "manor-icon.svg"));
				if (!file.Exists || file.Length > MaxBytes) return null;
				string svg = File.ReadAllText(file.FullName);
				return IsSafe(svg) ? svg : null;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
		}
	}
}
