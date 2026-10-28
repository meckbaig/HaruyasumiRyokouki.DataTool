using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HaruyasumiRyokouki.DataTool.Tags.Llm;

/// <summary>
/// JSON schemas and parsing of structured model answers. Taken from the backend
/// (Common/Helpers/AiJsonResponse.cs), so both sides treat answers the same way.
/// </summary>
/// <remarks>
/// Property names are camelCase and matched case-insensitively (<see cref="JsonSerializerOptions.Web"/>),
/// so the schema, the prompt and the deserialization agree.
/// </remarks>
internal static class AiJsonResponse
{
	private static readonly ConcurrentDictionary<Type, JsonElement> _schemas = new();

	// Without "required" a model constrained by the schema may legally omit every property.
	// Nullable properties stay nullable, so the model can still return null for them.
	private static readonly AIJsonSchemaCreateOptions _schemaOptions = new()
	{
		TransformOptions = new AIJsonSchemaTransformOptions
		{
			RequireAllProperties = true,
			DisallowAdditionalProperties = true
		}
	};

	/// <summary>Returns the JSON schema of <typeparamref name="T"/>, generated once per type.</summary>
	public static JsonElement GetSchema<T>()
	{
		return _schemas.GetOrAdd(typeof(T), type => AIJsonUtilities.CreateJsonSchema(type, serializerOptions: JsonSerializerOptions.Web, inferenceOptions: _schemaOptions));
	}

	/// <summary>
	/// Deserializes the answer of a model. A Markdown code fence around the JSON is removed first,
	/// since models without an enforced response format often add one.
	/// </summary>
	/// <exception cref="InvalidOperationException">The answer is not a JSON of <typeparamref name="T"/>.</exception>
	public static T Deserialize<T>(string text)
	{
		var json = RemoveCodeFence(text);
		try
		{
			return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)
				?? throw new InvalidOperationException($"The response from the AI is null: {text}");
		}
		catch (JsonException ex)
		{
			throw new InvalidOperationException($"The response from the AI is not a valid JSON of {typeof(T).Name}: {ex.Message}", ex);
		}
	}

	private static string RemoveCodeFence(string text)
	{
		var trimmed = text.Trim();
		if (!trimmed.StartsWith("```"))
			return trimmed;

		// Skip the opening fence with its language tag, e.g. "```json".
		var start = trimmed.IndexOf('\n');
		var end = trimmed.LastIndexOf("```");
		if (start < 0 || end <= start)
			return trimmed;

		return trimmed[(start + 1)..end].Trim();
	}
}
