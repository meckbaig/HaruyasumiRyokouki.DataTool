using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HaruyasumiRyokouki.DataTool.Common;
using HaruyasumiRyokouki.DataTool.Tags.Llm;

namespace HaruyasumiRyokouki.DataTool.Tags.Tests;

public sealed class ChatServiceTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("haru-llm-").FullName;

	public void Dispose() => Directory.Delete(_root, recursive: true);

	internal sealed record Answer(List<string> Tags);

	private sealed class FakeServer : HttpMessageHandler
	{
		private readonly Queue<(string Content, string Finish)> _answers;

		public FakeServer(params (string Content, string Finish)[] answers) => _answers = new(answers);

		public List<JsonNode> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
			var (content, finish) = _answers.Dequeue();
			string body = JsonSerializer.Serialize(new
			{
				id = "x",
				@object = "chat.completion",
				created = 1,
				model = "m",
				choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = finish } },
				usage = new { prompt_tokens = 100, completion_tokens = 30, total_tokens = 130, completion_tokens_details = new { reasoning_tokens = 20 } }
			});
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		}
	}

	private ChatService Service(FakeServer server, bool jsonObject = false, string? reasoning = null, int? maxTokens = null)
	{
		var provider = new LlmProvider("test", "http://llm.test/v1", "test-model", null, reasoning, jsonObject, maxTokens);
		return new ChatService(provider, Path.Combine(_root, "cache"), new HttpClientPipelineTransport(new HttpClient(server)));
	}

	[Fact]
	public async Task Sends_schema_and_options_and_counts_tokens()
	{
		var server = new FakeServer(("""{"tags": ["torii"]}""", "stop"));
		var service = Service(server, reasoning: "none", maxTokens: 5000);

		var answer = await service.GetJsonResponseAsync<Answer>("list", "system prompt");

		Assert.Equal(["torii"], answer.Tags);
		var request = server.Requests[0];
		Assert.Equal("test-model", (string?)request["model"]);
		Assert.Equal("json_schema", (string?)request["response_format"]!["type"]);
		Assert.Equal("none", (string?)request["reasoning_effort"]);
		Assert.Equal("system prompt", (string?)request["messages"]![0]!["content"]);
		Assert.Equal(1, service.Usage.Requests);
		Assert.Equal(100, service.Usage.InputTokens);
		Assert.Equal(30, service.Usage.OutputTokens);
		Assert.Equal(20, service.Usage.ReasoningTokens);
	}

	[Fact]
	public async Task Json_object_mode_sends_no_schema()
	{
		var server = new FakeServer(("""{"tags": []}""", "stop"));
		await Service(server, jsonObject: true).GetJsonResponseAsync<Answer>("list", "system");
		Assert.Equal("json_object", (string?)server.Requests[0]["response_format"]!["type"]);
		// The field names reach the model through the prompt instead.
		string system = (string)server.Requests[0]["messages"]![0]!["content"]!;
		Assert.Contains("JSON schema", system);
		Assert.Contains("\"tags\"", system);
	}

	[Fact]
	public async Task Useless_answer_is_asked_again_and_never_cached()
	{
		var server = new FakeServer(("""{"tags": []}""", "stop"), ("""{"tags": []}""", "stop"), ("""{"tags": ["c"]}""", "stop"));
		static string? NotEmpty(Answer a) => a.Tags.Count == 0 ? "no tags" : null;

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => Service(server).GetJsonResponseAsync<Answer>("list", "system", NotEmpty));
		Assert.Contains("no tags", ex.Message);
		Assert.Contains("no tags", (string?)server.Requests[1]["messages"]![1]!["content"]);

		var answer = await Service(server).GetJsonResponseAsync<Answer>("list", "system", NotEmpty);
		Assert.Equal(["c"], answer.Tags);
	}

	[Fact]
	public async Task Same_request_is_answered_from_the_cache()
	{
		var server = new FakeServer(("""{"tags": ["a"]}""", "stop"));
		var first = Service(server);
		await first.GetJsonResponseAsync<Answer>("list", "system");

		var second = Service(server);
		var cached = await second.GetJsonResponseAsync<Answer>("list", "system");

		Assert.Equal(["a"], cached.Tags);
		Assert.Single(server.Requests);
		Assert.Equal(1, second.Usage.CachedAnswers);
		Assert.Equal(0, second.Usage.Requests);
	}

	[Fact]
	public async Task Invalid_answer_is_asked_again_with_the_error()
	{
		var server = new FakeServer(("not json", "stop"), ("""{"tags": ["b"]}""", "stop"));
		var answer = await Service(server).GetJsonResponseAsync<Answer>("list", "system");

		Assert.Equal(["b"], answer.Tags);
		Assert.Contains("could not be used", (string?)server.Requests[1]["messages"]![1]!["content"]);
	}

	[Fact]
	public async Task Cut_off_answer_names_the_setting_to_raise()
	{
		var server = new FakeServer(("""{"tags": ["a", """, "length"));
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(server).GetJsonResponseAsync<Answer>("list", "system"));
		Assert.Contains("max_tokens", ex.Message);
	}

	[Fact]
	public void Provider_comes_from_usage_and_providers_sections()
	{
		File.WriteAllText(Path.Combine(_root, Workspace.ConfigName), """
			[providers.deepseek]
			base_url = "https://api.deepseek.com/"
			model = "deepseek-flash"
			api_key_env = "HARU_TEST_LLM_KEY"
			json_mode = "object"
			max_tokens = 200000

			[usage]
			vocabulary = "deepseek"
			""");
		Environment.SetEnvironmentVariable("HARU_TEST_LLM_KEY", "sk-test");
		try
		{
			var provider = LlmProvider.FromWorkspace(Workspace.Open(_root), "vocabulary");
			Assert.Equal(new LlmProvider("deepseek", "https://api.deepseek.com", "deepseek-flash", "sk-test", null, true, 200000), provider);

			var ex = Assert.Throws<WorkspaceException>(() => LlmProvider.FromWorkspace(Workspace.Open(_root), "vision"));
			Assert.Contains("[usage] vision", ex.Message);
		}
		finally
		{
			Environment.SetEnvironmentVariable("HARU_TEST_LLM_KEY", null);
		}
	}
}
