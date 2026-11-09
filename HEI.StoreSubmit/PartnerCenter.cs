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
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HEI.StoreSubmit;

/// <summary>Something Partner Center or its sign-in refused, said without the credentials.</summary>
public sealed class StoreException(string message) : Exception(message);

/// <summary>The Microsoft Entra app Partner Center is reached through: the repository's PARTNER_CENTER_* secrets.</summary>
public sealed record Credentials(string Tenant, string Client, string Secret) {
	public const string TenantVar = "PARTNER_CENTER_TENANT_ID", ClientVar = "PARTNER_CENTER_CLIENT_ID", SecretVar = "PARTNER_CENTER_CLIENT_SECRET";

	/// <summary>Null when one isn't set; <paramref name="missing"/> names those.</summary>
	public static Credentials? From(Func<string, string?> env, out List<string> missing) {
		missing = [];
		string? tenant = env(TenantVar), client = env(ClientVar), secret = env(SecretVar);
		if (string.IsNullOrWhiteSpace(tenant)) missing.Add(TenantVar);
		if (string.IsNullOrWhiteSpace(client)) missing.Add(ClientVar);
		if (string.IsNullOrWhiteSpace(secret)) missing.Add(SecretVar);
		return missing.Count == 0 ? new Credentials(tenant!.Trim(), client!.Trim(), secret!.Trim()) : null;
	}

	/// <summary>Text from the sign-in with the credentials themselves taken out.</summary>
	public string Redact(string text) {
		foreach (string value in new[] { Secret, Client, Tenant })
			if (value.Length > 0) text = text.Replace(value, "***", StringComparison.OrdinalIgnoreCase);
		return text;
	}

	public override string ToString() => "Credentials { *** }";
}

/// <summary>
/// Partner Center's Microsoft Store submission API, for one app, signed in with a client-credentials token
/// (https://learn.microsoft.com/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services).
/// The JSON is kept as Partner Center sends it, so a submission updated here keeps every field it had.
/// </summary>
public sealed class PartnerCenter {
	public const string Api = "https://manage.devcenter.microsoft.com/v1.0/my/";
	public const string SignInHost = "https://login.microsoftonline.com/";

	readonly HttpClient http;
	readonly string token;
	readonly Func<TimeSpan, CancellationToken, Task> wait;

	public string AppId { get; }

	PartnerCenter(HttpClient http, string appId, string token, Func<TimeSpan, CancellationToken, Task> wait) {
		this.http = http;
		AppId = appId;
		this.token = token;
		this.wait = wait;
	}

	/// <summary>Signs in as the Entra app. <paramref name="mask"/> gets the token before anything else can print it.</summary>
	public static async Task<PartnerCenter> SignIn(HttpClient http, string appId, Credentials credentials, Action<string> mask, Func<TimeSpan, CancellationToken, Task> wait, CancellationToken ct) {
		var form = new FormUrlEncodedContent(new Dictionary<string, string> {
			["grant_type"] = "client_credentials",
			["client_id"] = credentials.Client,
			["client_secret"] = credentials.Secret,
			["resource"] = "https://manage.devcenter.microsoft.com",
		});
		using var request = new HttpRequestMessage(HttpMethod.Post, SignInHost + Uri.EscapeDataString(credentials.Tenant) + "/oauth2/token") { Content = form };
		HttpResponseMessage response;
		try {
			response = await http.SendAsync(request, ct);
		}
		catch (HttpRequestException e) {
			throw new StoreException("Couldn't reach Microsoft's sign-in: " + credentials.Redact(e.Message));
		}
		using (response) {
			string body = await response.Content.ReadAsStringAsync(ct);
			JsonNode? json = TryParse(body);
			string? access = json?["access_token"] is JsonValue v && v.TryGetValue(out string? t) ? t : null;
			if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(access)) {
				mask(access);
				return new PartnerCenter(http, appId, access, wait);
			}
			string why = json?["error_description"]?.ToString() ?? json?["error"]?.ToString() ?? Shorten(body);
			// The first line of Entra's description says it ("AADSTS7000215: Invalid client secret provided..."); the rest is trace ids.
			why = why.Split('\n')[0].Trim();
			throw new StoreException($"Microsoft's sign-in refused the Entra app ({(int)response.StatusCode}): {credentials.Redact(why)}");
		}
	}

	string App => "applications/" + Uri.EscapeDataString(AppId);

	public string SubmissionPath(string id) => App + "/submissions/" + Uri.EscapeDataString(id);

	public Task<JsonObject> GetApp(CancellationToken ct) => Send(HttpMethod.Get, App, null, ct);

	public Task<JsonObject> GetSubmission(string id, CancellationToken ct) => Send(HttpMethod.Get, SubmissionPath(id), null, ct);

	public Task<JsonObject> PutSubmission(string id, JsonObject submission, CancellationToken ct) => Send(HttpMethod.Put, SubmissionPath(id), submission, ct);

	public Task<JsonObject> Commit(string id, CancellationToken ct) => Send(HttpMethod.Post, SubmissionPath(id) + "/commit", null, ct);

	public Task<JsonObject> GetStatus(string id, CancellationToken ct) => Send(HttpMethod.Get, SubmissionPath(id) + "/status", null, ct);

	public Task DeleteSubmission(string id, CancellationToken ct) => Send(HttpMethod.Delete, SubmissionPath(id), null, ct);

	/// <summary>The app's last published submission and the one in progress, read in full.</summary>
	public async Task<StoreState> State(CancellationToken ct) {
		JsonObject app = await GetApp(ct);
		string? published = SubmissionId(app, "lastPublishedApplicationSubmission"), pending = SubmissionId(app, "pendingApplicationSubmission");
		Submission? last = published != null ? Submission.Read(await GetSubmission(published, ct)) : null;
		Submission? next = pending != null && pending != published ? Submission.Read(await GetSubmission(pending, ct)) : null;
		return new StoreState(last, next);
	}

	/// <summary>The id of the app's "lastPublishedApplicationSubmission" or "pendingApplicationSubmission"; null when there's none.</summary>
	public static string? SubmissionId(JsonObject app, string which) {
		JsonNode? id = app[which]?["id"];
		string? text = id is JsonValue v ? (v.TryGetValue(out string? s) ? s : v.ToJsonString()) : null;
		return string.IsNullOrWhiteSpace(text) || text == "0" ? null : text;
	}

	/// <summary>One call, tried again on Partner Center's own trouble (429, 5xx, no answer): up to 4 times, a minute at most.</summary>
	async Task<JsonObject> Send(HttpMethod method, string path, JsonObject? body, CancellationToken ct) {
		TimeSpan[] backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(40)];
		for (int attempt = 0; ; attempt++) {
			using var request = new HttpRequestMessage(method, Api + path);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
			if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
			else if (method == HttpMethod.Post) request.Content = new StringContent("", Encoding.UTF8, "application/json");
			HttpResponseMessage? response = null;
			string? trouble;
			try {
				response = await http.SendAsync(request, ct);
				trouble = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500 ? $"{(int)response.StatusCode}" : null;
			}
			catch (HttpRequestException e) {
				trouble = e.Message;
			}
			catch (TaskCanceledException) when (!ct.IsCancellationRequested) {
				trouble = "no answer in time";
			}
			using (response) {
				if (trouble != null && attempt < backoff.Length) {
					await wait(backoff[attempt], ct);
					continue;
				}
				if (response == null) throw new StoreException($"Partner Center didn't answer {method} {path}: {trouble}");
				string text = await response.Content.ReadAsStringAsync(ct);
				if (!response.IsSuccessStatusCode) throw new StoreException($"Partner Center answered {(int)response.StatusCode} ({response.StatusCode}) to {method} {path}: {Why(text)}");
				if (response.StatusCode == HttpStatusCode.NoContent || text.Trim().Length == 0) return [];
				return TryParse(text) as JsonObject ?? throw new StoreException($"Partner Center's answer to {method} {path} isn't a JSON object: {Shorten(text)}");
			}
		}
	}

	/// <summary>The API's error, { "code": ..., "message": ... }, or the start of its answer.</summary>
	static string Why(string body) {
		JsonNode? json = TryParse(body);
		string? code = json?["code"]?.ToString(), message = json?["message"]?.ToString();
		if (code != null || message != null) return string.Join(": ", new[] { code, message }.Where(s => !string.IsNullOrEmpty(s)));
		return Shorten(body);
	}

	static JsonNode? TryParse(string text) {
		try {
			return JsonNode.Parse(text);
		}
		catch (JsonException) {
			return null;
		}
	}

	static string Shorten(string text) {
		text = text.ReplaceLineEndings(" ").Trim();
		return text.Length > 300 ? text[..300] + "…" : text.Length == 0 ? "(nothing)" : text;
	}
}
