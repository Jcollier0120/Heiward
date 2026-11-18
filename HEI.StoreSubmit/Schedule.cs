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

using System.Globalization;

namespace HEI.StoreSubmit;

/// <summary>
/// When store.yml's scheduled runs go on: Tuesdays from 14:00 and Fridays from 10:00, New York time. GitHub's schedule is
/// in UTC, so each of the two has a run at its summer hour, one at its winter hour and a backup (store.yml, on:
/// schedule). This says no to the one an hour early in winter, and to one GitHub started so late that it's the next day
/// in New York.
/// </summary>
public static class Schedule {
	/// <summary>The days the Store run goes on, and the hour in New York it goes on from.</summary>
	public static readonly IReadOnlyList<(DayOfWeek Day, int Hour)> Runs = [(DayOfWeek.Tuesday, 14), (DayOfWeek.Friday, 10)];

	/// <summary>The scheduled runs, as the notices name them.</summary>
	public const string Named = "Tuesday afternoon or Friday morning";

	/// <summary>The scheduled runs' times, as a skipped run's log says them.</summary>
	public const string Times = "Tuesdays from 14:00 and Fridays from 10:00";

	/// <summary>New York's time: its IANA name on Linux (and on Windows with ICU), Windows' own name without.</summary>
	public static readonly TimeZoneInfo NewYork = Find();

	static TimeZoneInfo Find() {
		try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
		catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) {
			return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
		}
	}

	public static DateTimeOffset InNewYork(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, NewYork);

	/// <summary>Whether a scheduled run started at <paramref name="at"/> goes on.</summary>
	public static bool Goes(DateTimeOffset at) {
		DateTimeOffset there = InNewYork(at);
		return Runs.Any(r => there.DayOfWeek == r.Day && there.Hour >= r.Hour);
	}

	/// <summary>What a scheduled run started at <paramref name="at"/> says in its log.</summary>
	public static string Says(DateTimeOffset at) {
		string there = InNewYork(at).ToString("dddd HH:mm", CultureInfo.InvariantCulture);
		return Goes(at)
			? $"It's {there} in New York: the Store run goes on."
			: $"It's {there} in New York. The Store run goes on {Times} there: nothing to do now.";
	}
}
