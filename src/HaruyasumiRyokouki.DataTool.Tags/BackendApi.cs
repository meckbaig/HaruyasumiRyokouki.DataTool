using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HaruyasumiRyokouki.DataTool.Common;

namespace HaruyasumiRyokouki.DataTool.Tags;

/// <summary>
/// Thin client of the site's HTTP API. Tags go through the backend's own commands, with its
/// validation; this app has no write access to the tag tables. DTO shapes follow the backend's
/// Features/Tags and Controllers/V1/TagsController.cs.
/// </summary>
internal sealed class BackendApi : IDisposable
{
	public static readonly JsonSerializerOptions Json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private readonly HttpClient _http;

	public BackendApi(Uri baseAddress, string user, string password, HttpMessageHandler? handler = null)
	{
		_http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
		{
			BaseAddress = new Uri(baseAddress.ToString().TrimEnd('/') + "/"),
			// A completion is a call to the backend's AI model and may take a while.
			Timeout = TimeSpan.FromMinutes(2)
		};

		string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
		_http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
	}

	/// <summary>The API address for printing before writing.</summary>
	public Uri BaseAddress => _http.BaseAddress!;

	/// <summary>
	/// Client for the API in <c>[backend] api</c> (or HARU_API), with HARU_USER and HARU_PASSWORD.
	/// There is no default address: writing to a server nobody chose is the mistake this prevents.
	/// </summary>
	public static BackendApi FromWorkspace(Workspace workspace)
	{
		string api = workspace.Require("backend.api", "HARU_API");
		if (!Uri.TryCreate(api, UriKind.Absolute, out var uri))
			throw new WorkspaceException($"backend api '{api}' is not an absolute URL.");

		return new BackendApi(uri, Workspace.RequireSecret("HARU_USER"), Workspace.RequireSecret("HARU_PASSWORD"));
	}

	/// <summary>Fails within a second, with a clear reason, instead of at the hundredth tag.</summary>
	public async Task EnsureReachableAsync(CancellationToken cancellationToken = default)
	{
		HttpResponseMessage response;
		try
		{
			response = await _http.GetAsync("v1/tags", cancellationToken);
		}
		catch (HttpRequestException ex)
		{
			throw new InvalidOperationException($"Cannot reach {_http.BaseAddress}. Is the backend running? ({ex.Message})");
		}

		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			throw new InvalidOperationException(
				$"The backend answers {(int)response.StatusCode}: HARU_USER or HARU_PASSWORD is wrong.");

		await ThrowIfFailedAsync(response, "reading tags");
	}

	/// <summary>GET /v1/tags: the site's current vocabulary.</summary>
	public async Task<List<ExistingTag>> GetTagsAsync(CancellationToken cancellationToken = default)
	{
		var response = await _http.GetAsync("v1/tags", cancellationToken);
		await ThrowIfFailedAsync(response, "reading tags");

		var payload = await response.Content.ReadFromJsonAsync<GetTagsEnvelope>(Json, cancellationToken);
		return payload?.Items ?? [];
	}

	/// <summary>
	/// POST /v1/tags/completion: the backend's AI drafts translations into en, ru and ja and search
	/// aliases for one tag. Nothing is saved on the site. A busy or rate-limited model (429, 5xx) is
	/// asked again after a growing pause, since free tiers of cloud models limit requests per minute.
	/// </summary>
	public async Task<TagDraft> CompleteTagAsync(string tag, CancellationToken cancellationToken = default)
	{
		for (int attempt = 1; ; attempt++)
		{
			var response = await _http.PostAsJsonAsync("v1/tags/completion", new { tag }, Json, cancellationToken);
			bool retryable = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
			if (retryable && attempt < RetryAttempts)
			{
				await Task.Delay(RetryDelay * Math.Pow(2, attempt - 1), cancellationToken);
				continue;
			}

			await ThrowIfFailedAsync(response, $"translating tag '{tag}'");
			var payload = await response.Content.ReadFromJsonAsync<CompletionEnvelope>(Json, cancellationToken)
				?? throw new InvalidOperationException($"Empty answer when translating tag '{tag}'");
			return payload.Tag;
		}
	}

	/// <summary>Attempts of a request the backend's AI could not serve right now.</summary>
	public int RetryAttempts { get; init; } = 5;

	/// <summary>Pause before the first retry; it doubles every time.</summary>
	public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>POST /v1/tags: creates a tag and returns its id.</summary>
	public async Task<int> AddTagAsync(string slug, IEnumerable<TagText> translations, IEnumerable<TagText> aliases,
		CancellationToken cancellationToken = default)
	{
		var body = new { tag = new { slug, translations = translations.ToList(), aliases = aliases.ToList() } };
		var response = await _http.PostAsJsonAsync("v1/tags", body, Json, cancellationToken);
		await ThrowIfFailedAsync(response, $"creating tag '{slug}'");

		var payload = await response.Content.ReadFromJsonAsync<AddTagEnvelope>(Json, cancellationToken)
			?? throw new InvalidOperationException($"Empty answer when creating tag '{slug}'");
		return payload.Tag.Id;
	}

	/// <summary>
	/// POST /v1/tags/{id}/media: adds the tag to the files and returns how many did not have it.
	/// It never removes tags, so running it again is safe. (PATCH /v1/media replaces a file's
	/// whole tag set and would wipe tags added by hand; it is not used.)
	/// </summary>
	public async Task<int> AddTagToMediaAsync(int tagId, IReadOnlyCollection<int> mediaFileIds,
		CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsJsonAsync($"v1/tags/{tagId}/media", new { mediaFileIds }, Json, cancellationToken);
		await ThrowIfFailedAsync(response, $"adding tag {tagId} to {mediaFileIds.Count} files");

		var payload = await response.Content.ReadFromJsonAsync<AddTagToMediaEnvelope>(Json, cancellationToken);
		return payload?.Affected ?? 0;
	}

	private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string what)
	{
		if (response.IsSuccessStatusCode)
			return;

		string body = await response.Content.ReadAsStringAsync();
		if (body.Length > 400)
			body = body[..400] + "…";

		throw new InvalidOperationException($"{what}: HTTP {(int)response.StatusCode}. {body}");
	}

	public void Dispose() => _http.Dispose();

	private sealed record CompletionEnvelope(TagDraft Tag);
	private sealed record AddTagEnvelope(AddedTag Tag);
	private sealed record AddedTag(int Id, string Slug);
	private sealed record GetTagsEnvelope(List<ExistingTag> Items);
	private sealed record AddTagToMediaEnvelope(int Affected);
}

/// <summary>A language and a text, as in the backend's TagTranslationDto.</summary>
internal sealed record TagText(string LanguageCode, string Text);

/// <summary>Translations and aliases the backend's AI drafted for a tag (TagDto without id).</summary>
internal sealed record TagDraft(string? Slug, List<TagText>? Translations, List<TagText>? Aliases);

/// <summary>An item of GET /v1/tags.</summary>
internal sealed record ExistingTag(int Id, string Slug, List<TagText>? Translations, List<TagText>? Aliases);
