using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TagsTool;

/// <summary>
/// Тонкая обёртка над HTTP API сервера. Всё, что требует базы или DeepSeek,
/// делается здесь — своего подключения к Postgres у инструмента нет.
/// </summary>
internal sealed class BackendApi : IDisposable
{
	private readonly HttpClient _http;

	public static readonly JsonSerializerOptions Json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private BackendApi(HttpClient http) => _http = http;

	public static BackendApi Create(Options options)
	{
		string baseUrl = options.GetOrEnv("api", "TAGSTOOL_API", "http://localhost:5101")!;
		string? user = options.GetOrEnv("user", "TAGSTOOL_USER");
		string? password = options.GetOrEnv("password", "TAGSTOOL_PASSWORD");

		if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
			throw new ArgumentException(
				"Нужны --user и --password (или переменные TAGSTOOL_USER / TAGSTOOL_PASSWORD). " +
				"Эндпоинты тегов помечены [Authorize].");

		// По умолчанию HttpClient не ограничивает число соединений к хосту, но
		// выставляем явно по числу потоков: так поведение предсказуемо и видно,
		// что параллельность упирается именно в --threads, а не в транспорт.
		int threads = int.TryParse(options.Get("threads"), out int t) ? t : 1;

		var handler = new SocketsHttpHandler
		{
			MaxConnectionsPerServer = Math.Max(threads, 10),
			PooledConnectionLifetime = TimeSpan.FromMinutes(10)
		};

		var http = new HttpClient(handler)
		{
			BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
			// Перевод одного тега — это вызов к DeepSeek, он может быть долгим
			Timeout = TimeSpan.FromMinutes(2)
		};

		string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
		http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

		return new BackendApi(http);
	}

	/// <summary>
	/// Проверка доступности до того, как начнётся длинная работа.
	/// Лучше упасть на первой секунде, чем на сотом теге.
	/// </summary>
	public async Task EnsureReachableAsync()
	{
		try
		{
			var response = await _http.GetAsync("v1/Tags");

			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
				throw new InvalidOperationException(
					$"Сервер отвечает {(int)response.StatusCode} — не подошли --user/--password.");

			response.EnsureSuccessStatusCode();
		}
		catch (HttpRequestException ex)
		{
			throw new InvalidOperationException(
				$"Не достучаться до {_http.BaseAddress}. Сервер запущен? ({ex.Message})");
		}
	}

	/// <summary>
	/// POST /v1/Tags/completion — перевод и алиасы через GetTagCompletionCommand.
	/// Потокобезопасен: HttpClient рассчитан на параллельные запросы.
	/// </summary>
	public async Task<TagCompletion> CompleteTagAsync(string tag, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsJsonAsync(
			"v1/Tags/completion",
			new { tag },
			Json,
			cancellationToken);

		await ThrowIfFailedAsync(response, $"перевод тега '{tag}'");

		var payload = await response.Content.ReadFromJsonAsync<CompletionEnvelope>(Json, cancellationToken)
			?? throw new InvalidOperationException($"Пустой ответ на перевод тега '{tag}'");

		return payload.Tag;
	}

	/// <summary>POST /v1/Tags — создание тега через AddTagCommand.</summary>
	public async Task<int> AddTagAsync(string slug, IEnumerable<TagText> translations, IEnumerable<TagText> aliases)
	{
		var response = await _http.PostAsJsonAsync(
			"v1/Tags",
			new
			{
				tag = new
				{
					slug,
					translations = translations.ToList(),
					aliases = aliases.ToList()
				}
			},
			Json);

		await ThrowIfFailedAsync(response, $"создание тега '{slug}'");

		var payload = await response.Content.ReadFromJsonAsync<AddTagEnvelope>(Json)
			?? throw new InvalidOperationException($"Пустой ответ на создание тега '{slug}'");

		return payload.Tag.Id;
	}

	/// <summary>GET /v1/Tags — текущий словарь, чтобы не плодить дубли.</summary>
	public async Task<List<ExistingTag>> GetTagsAsync()
	{
		var response = await _http.GetAsync("v1/Tags");
		await ThrowIfFailedAsync(response, "получение списка тегов");

		var payload = await response.Content.ReadFromJsonAsync<GetTagsEnvelope>(Json);
		return payload?.Items ?? [];
	}

	/// <summary>PATCH /v1/Media — простановка тегов пачке медиафайлов через EditMediaCommand.</summary>
	public async Task AssignTagsAsync(IReadOnlyCollection<int> mediaIds, IReadOnlyCollection<int> tagIds)
	{
		var request = new HttpRequestMessage(HttpMethod.Patch, "v1/Media")
		{
			Content = JsonContent.Create(
				new
				{
					ids = mediaIds,
					changes = new { tagIds },
					autoTranslate = false
				},
				options: Json)
		};

		var response = await _http.SendAsync(request);
		await ThrowIfFailedAsync(response, $"простановка тегов {mediaIds.Count} файлам");
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

	// -- формы ответов сервера ------------------------------------------------

	private sealed record CompletionEnvelope(TagCompletion Tag);
	private sealed record AddTagEnvelope(AddedTag Tag);
	private sealed record AddedTag(int Id, string Slug);
	private sealed record GetTagsEnvelope(List<ExistingTag> Items);
}

/// <summary>Ответ GetTagCompletionCommand.</summary>
internal sealed record TagCompletion(
	string? Slug,
	List<TagText>? Translations,
	List<TagText>? Aliases);

/// <summary>Пара «язык — текст», как в TagTranslationDto.</summary>
internal sealed record TagText(string LanguageCode, string Text);

/// <summary>Элемент GET /v1/Tags — нужен только чтобы не создавать дубли.</summary>
internal sealed record ExistingTag(int Id, string Slug);
