using System.ClientModel;
using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace HaruyasumiRyokouki.DataTool.Tags.Llm;

#pragma warning disable OPENAI001 // Reasoning effort is marked as preview in the OpenAI package.

/// <summary>
/// Chat model of an OpenAI-compatible API, based on the backend's OpenAiChatService. On top of it:
/// answers are cached on disk by a hash of the whole request, so a run interrupted by an error or
/// Ctrl+C does not pay again for what it already got; token use is counted for the step report;
/// an answer that is not valid JSON is asked once more with the parse error.
/// </summary>
/// <remarks>
/// Requests that fail with 429 or 5xx are retried with a pause by the OpenAI package itself.
/// </remarks>
internal sealed class ChatService
{
	// Local servers such as LM Studio need no key, but the client does not accept an empty one.
	private const string NoApiKey = "none";

	private readonly LlmProvider _provider;
	private readonly ChatClient _client;
	private readonly string _cacheDir;
	private readonly object _lock = new();

	public ChatService(LlmProvider provider, string cacheDir, PipelineTransport? transport = null)
	{
		var options = new OpenAIClientOptions
		{
			Endpoint = new Uri(provider.BaseUrl),
			// Reasoning over a list of a thousand tags takes minutes.
			NetworkTimeout = TimeSpan.FromMinutes(30)
		};
		if (transport is not null)
			options.Transport = transport;

		_client = new ChatClient(provider.Model, new ApiKeyCredential(provider.ApiKey ?? NoApiKey), options);
		_provider = provider;
		_cacheDir = cacheDir;
	}

	/// <summary>Tokens and requests so far, for the step report.</summary>
	public LlmUsage Usage { get; } = new();

	/// <summary>"deepseek: deepseek-flash at https://api.deepseek.com", for messages and reports.</summary>
	public string Describe() => $"{_provider.Name}: {_provider.Model} at {_provider.BaseUrl}";

	/// <summary>Asks for a JSON answer of <typeparamref name="T"/>, from the cache when the same request was answered before.</summary>
	/// <param name="validate">
	/// Checks the content of a parsed answer and returns what is wrong, or null. A parsed answer can
	/// still be useless (an empty list for a non-empty batch); such an answer is asked again and never cached.
	/// </param>
	/// <exception cref="InvalidOperationException">No valid answer after one retry, or the answer was cut off by the token limit.</exception>
	public async Task<T> GetJsonResponseAsync<T>(string message, string systemMessage, Func<T, string?>? validate = null,
		CancellationToken cancellationToken = default)
	{
		// Without a schema in the request, the model only knows the field names from the prompt.
		if (_provider.JsonObject)
			systemMessage += "\n\nAnswer with a JSON object that matches this JSON schema:\n" + AiJsonResponse.GetSchema<T>().GetRawText();

		string cachePath = Path.Combine(_cacheDir, CacheKey<T>(message, systemMessage) + ".json");
		if (File.Exists(cachePath))
		{
			lock (_lock)
				Usage.CachedAnswers++;
			return AiJsonResponse.Deserialize<T>(await File.ReadAllTextAsync(cachePath, cancellationToken));
		}

		string text = await CompleteAsync<T>(message, systemMessage, cancellationToken);
		var (result, problem) = Parse(text, validate);
		if (problem is not null)
		{
			string retry = message + $"\n\nYour previous answer could not be used: {problem}\nAnswer again with valid JSON only.";
			text = await CompleteAsync<T>(retry, systemMessage, cancellationToken);
			(result, problem) = Parse(text, validate);
			if (problem is not null)
				throw new InvalidOperationException($"{_provider.Model} gave no usable answer twice: {problem}");
		}

		Directory.CreateDirectory(_cacheDir);
		await File.WriteAllTextAsync(cachePath, text, cancellationToken);
		return result!;
	}

	private static (T? Result, string? Problem) Parse<T>(string text, Func<T, string?>? validate)
	{
		try
		{
			var result = AiJsonResponse.Deserialize<T>(text);
			return (result, validate?.Invoke(result));
		}
		catch (InvalidOperationException ex)
		{
			return (default, ex.Message);
		}
	}

	private async Task<string> CompleteAsync<T>(string message, string systemMessage, CancellationToken cancellationToken)
	{
		var options = new ChatCompletionOptions
		{
			Temperature = 0,
			ResponseFormat = _provider.JsonObject
				? ChatResponseFormat.CreateJsonObjectFormat()
				: ChatResponseFormat.CreateJsonSchemaFormat(
					jsonSchemaFormatName: typeof(T).Name,
					jsonSchema: BinaryData.FromString(AiJsonResponse.GetSchema<T>().GetRawText()))
		};
		if (_provider.ReasoningEffort is not null)
			options.ReasoningEffortLevel = new ChatReasoningEffortLevel(_provider.ReasoningEffort);
		if (_provider.MaxOutputTokens is not null)
			options.MaxOutputTokenCount = _provider.MaxOutputTokens;

		ChatCompletion response = await _client.CompleteChatAsync(
			[new SystemChatMessage(systemMessage), new UserChatMessage(message)], options, cancellationToken);

		lock (_lock)
		{
			Usage.Requests++;
			Usage.InputTokens += response.Usage?.InputTokenCount ?? 0;
			Usage.OutputTokens += response.Usage?.OutputTokenCount ?? 0;
			Usage.ReasoningTokens += response.Usage?.OutputTokenDetails?.ReasoningTokenCount ?? 0;
		}

		if (response.FinishReason == ChatFinishReason.Length)
			throw new InvalidOperationException(
				$"The answer of {_provider.Model} was cut off by the token limit. Raise max_tokens of [providers.{_provider.Name}].");

		string? text = response.Content.FirstOrDefault()?.Text;
		if (string.IsNullOrEmpty(text))
			throw new InvalidOperationException("The response from the AI does not contain text.");
		return text;
	}

	private string CacheKey<T>(string message, string systemMessage)
	{
		string request = JsonSerializer.Serialize(new
		{
			_provider.BaseUrl,
			_provider.Model,
			_provider.ReasoningEffort,
			_provider.JsonObject,
			Answer = typeof(T).FullName,
			systemMessage,
			message
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request))).ToLowerInvariant();
	}
}

/// <summary>What the model calls of a step cost.</summary>
internal sealed class LlmUsage
{
	public int Requests { get; set; }
	public int CachedAnswers { get; set; }
	public long InputTokens { get; set; }
	public long OutputTokens { get; set; }

	/// <summary>Part of <see cref="OutputTokens"/> spent on thinking.</summary>
	public long ReasoningTokens { get; set; }
}
