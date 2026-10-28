using HaruyasumiRyokouki.DataTool.Common;

namespace HaruyasumiRyokouki.DataTool.Tags.Llm;

/// <summary>
/// Connection settings of the language model that <c>[usage]</c> assigns to a task, from
/// <c>[providers.&lt;name&gt;]</c> of haru-data.toml. The same keys as the analysis app reads.
/// </summary>
/// <param name="Name">Provider name in the settings, for messages.</param>
/// <param name="BaseUrl">OpenAI-compatible endpoint, such as https://api.deepseek.com or http://localhost:1234/v1.</param>
/// <param name="ApiKey">Key from the environment variable named by <c>api_key_env</c>; null for local servers.</param>
/// <param name="ReasoningEffort"><c>reasoning_effort</c>: "none" turns thinking off; null leaves the model's default.</param>
/// <param name="JsonObject"><c>json_mode = "object"</c>: the API takes only {"type": "json_object"}, not a JSON schema.</param>
/// <param name="MaxOutputTokens"><c>max_tokens</c>: reasoning models need a high limit or long answers are cut off.</param>
internal sealed record LlmProvider(
	string Name, string BaseUrl, string Model, string? ApiKey, string? ReasoningEffort, bool JsonObject, int? MaxOutputTokens)
{
	/// <exception cref="WorkspaceException">The task has no provider or the provider is incomplete.</exception>
	public static LlmProvider FromWorkspace(Workspace workspace, string task)
	{
		string name = workspace.GetString($"usage.{task}")
			?? throw new WorkspaceException($"[usage] {task} is not set in {Workspace.ConfigName}: name the provider to use.");
		string section = $"providers.{name}";
		if (workspace.GetValue(section) is null)
			throw new WorkspaceException($"[usage] {task} = '{name}', but there is no [{section}] in {Workspace.ConfigName}.");
		if ((workspace.GetString($"{section}.type") ?? "openai") != "openai")
			throw new WorkspaceException($"[{section}] type must be 'openai' (any OpenAI-compatible server).");

		string baseUrl = workspace.GetString($"{section}.base_url") ?? throw new WorkspaceException($"[{section}] base_url is not set.");
		string model = workspace.GetString($"{section}.model") ?? throw new WorkspaceException($"[{section}] model is not set.");
		string? keyVariable = workspace.GetString($"{section}.api_key_env");

		return new LlmProvider(
			name,
			baseUrl.TrimEnd('/'),
			model,
			keyVariable is null ? null : Workspace.RequireSecret(keyVariable),
			workspace.GetString($"{section}.reasoning_effort"),
			workspace.GetString($"{section}.json_mode") == "object",
			workspace.GetInt($"{section}.max_tokens"));
	}
}
