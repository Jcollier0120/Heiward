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

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HEI.StoreSubmit;

/// <summary>Heiward's versions as releases (v1.9.137) and Store packages (1.9.137.0) write them, compared as one.</summary>
public static class Versions {
	/// <summary>Four parts, the missing ones 0: 1.9.137 and 1.9.137.0 are the same version. Null when it isn't one.</summary>
	public static Version? Parse(string? text) {
		if (string.IsNullOrWhiteSpace(text)) return null;
		text = text.Trim();
		if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
		if (!Version.TryParse(text, out Version? v)) return null;
		return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
	}

	/// <summary>As Heiward's releases name it: three parts, four only when the fourth isn't 0.</summary>
	public static string Show(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);
}

/// <summary>A submission, as Partner Center's submission API gives it: its id and status, and the version of the
/// packages it keeps (those it doesn't delete).</summary>
public sealed record Submission(string Id, string Status, string? Name, Version? Version, IReadOnlyList<string> Problems) {
	static readonly Regex InFileName = new(@"(\d+\.\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);

	public static Submission Read(JsonNode json) {
		string id = Text(json["id"]) ?? "";
		string status = Text(json["status"]) ?? "None";
		string? name = Text(json["friendlyName"]);
		Version? version = null;
		if (json["applicationPackages"] is JsonArray packages)
			foreach (JsonNode? p in packages) {
				if (p == null || Text(p["fileStatus"]) == "PendingDelete") continue;
				// A package not processed yet has no version: its file's name has it (msstore names it after the file).
				Version? v = Versions.Parse(Text(p["version"]));
				if (v == null && Text(p["fileName"]) is { } file && InFileName.Match(file) is { Success: true } m) v = Versions.Parse(m.Value);
				if (v != null && (version == null || v > version)) version = v;
			}
		return new Submission(id, status, name, version, ProblemsOf(json["statusDetails"]));
	}

	/// <summary>statusDetails' errors, as "code: details".</summary>
	public static IReadOnlyList<string> ProblemsOf(JsonNode? details) {
		var problems = new List<string>();
		if (details?["errors"] is JsonArray errors)
			foreach (JsonNode? e in errors) {
				if (e == null) continue;
				string code = Text(e["code"]) ?? "", text = Text(e["details"]) ?? "";
				string both = code.Length > 0 && text.Length > 0 ? code + ": " + text : code + text;
				if (both.Length > 0) problems.Add(both);
			}
		return problems;
	}

	/// <summary>"Heiward 1.9.136", or its name in Partner Center ("Submission 5") when its version isn't known.</summary>
	public string Describe() => Version != null ? "Heiward " + Versions.Show(Version) : Name ?? "submission " + Id;

	static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>The app in Partner Center: its last published submission and the one in progress, each null when there's none.</summary>
public sealed record StoreState(Submission? Published, Submission? Pending);

public enum Verdict {
	/// <summary>Send the release.</summary>
	Submit,
	/// <summary>A submission in Partner Center holds it back: in certification, a draft, or failed there.</summary>
	Hold,
	/// <summary>The Store has it, or a newer one, already.</summary>
	Current,
	/// <summary>Nothing was ever published: the first submission goes through Partner Center by hand.</summary>
	NotPublished,
}

/// <summary>What to do with a release, and the notice that says why. <see cref="Warn"/>: a warning, not a notice.</summary>
public sealed record Decision(Verdict Verdict, string Message, bool Warn = false) {
	public bool Submits => Verdict == Verdict.Submit;
}

/// <summary>
/// Whether a release goes to the Microsoft Store now. Partner Center keeps one submission in progress per app, and
/// msstore publish deletes it to make its own; so a release never replaces one in certification, nor a draft
/// (yours, made in Partner Center, or one left for you to send), only one whose commit failed. The release waits,
/// and the next Friday run sends the newest release once nothing is in progress.
/// </summary>
public static class Rules {
	/// <summary>The run that tries again: store.yml's weekly one, Fridays at 10:00 New York time.</summary>
	public const string NextRun = "the next Friday run", NextRunCapital = "The next Friday run";

	/// <summary>Statuses of a submission on its way, after its commit.</summary>
	static readonly Dictionary<string, string> Moving = new(StringComparer.Ordinal) {
		["CommitStarted"] = "being committed",
		["PreProcessing"] = "being checked",
		["Certification"] = "in certification",
		["Release"] = "through certification, waiting to be published",
		["PendingPublication"] = "being published",
		["Publishing"] = "being published",
	};

	/// <summary>Statuses of a submission the Store turned down after its commit: a person reads why in Partner Center.</summary>
	static readonly Dictionary<string, string> Failed = new(StringComparer.Ordinal) {
		["PreProcessingFailed"] = "failed the Store's package checks",
		["CertificationFailed"] = "failed certification",
		["ReleaseFailed"] = "failed to be published",
		["PublishFailed"] = "failed to be published",
	};

	public static Decision Decide(StoreState store, Version target) {
		string release = "Heiward " + Versions.Show(target);
		if (store.Published == null)
			return new(Verdict.NotPublished, $"{release} wasn't sent: Heiward has no published submission in the Microsoft Store yet. " +
				"The first one goes through Partner Center by hand (docs/STORE.md, \"Releasing\"); once it's published, this workflow sends the newest release every Friday.");

		Submission? pending = store.Pending is { Status: not "Published" } p && p.Id != store.Published.Id ? store.Pending : null;
		string? replacing = null;
		if (pending != null) {
			string what = pending.Describe(), status = pending.Status;
			if (Moving.TryGetValue(status, out string? moving))
				return pending.Version != null && pending.Version >= target
					? new(Verdict.Current, $"{what} is {moving} in the Microsoft Store already: nothing to send.")
					: new(Verdict.Hold, $"{release} waits: {what} is {moving} in the Microsoft Store. Once it's published, {NextRun} sends the newest release.");
			if (Failed.TryGetValue(status, out string? failed))
				return new(Verdict.Hold, $"{release} waits: {what} {failed} in the Microsoft Store{Because(pending)}. Partner Center's report says why. " +
					$"Delete that submission in Partner Center, or fix it and send it again, and {NextRun} sends the newest release.", Warn: true);
			if (status == "CommitFailed") replacing = $", in place of {what}, whose commit failed{Because(pending)}";
			else {
				string draft = status switch {
					"PendingCommit" => $"a draft of {what} is in Partner Center, not sent yet",
					"Canceled" => $"{what} was canceled in Partner Center and is still there",
					_ => $"{what} is in Partner Center ({status})",
				};
				return new(Verdict.Hold, $"{release} waits: {draft}. Send it or delete it there, and {NextRun} sends the newest release.");
			}
		}

		Version? inStore = store.Published.Version;
		if (inStore != null && inStore >= target)
			return new(Verdict.Current, inStore == target
				? $"{release} is in the Microsoft Store already: nothing to send."
				: $"The Microsoft Store has Heiward {Versions.Show(inStore)}, newer than {Versions.Show(target)}: nothing to send.");
		string now = inStore != null ? $", which has Heiward {Versions.Show(inStore)} now" : "";
		return new(Verdict.Submit, $"{release} goes to the Microsoft Store{now}{replacing}.");
	}

	static string Because(Submission s) => s.Problems.Count > 0 ? " (" + string.Join("; ", s.Problems) + ")" : "";
}
