using System.Net;
using System.Text;
using System.Text.Json;

namespace HaruyasumiRyokouki.DataTool.Tags.Tests;

public sealed class BackendApiTests
{
	private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
	{
		public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
			Requests.Add((request, body));
			return respond(request, body);
		}
	}

	private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
		=> new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

	[Fact]
	public async Task Add_tag_to_media_posts_ids_with_basic_auth_and_returns_affected()
	{
		var handler = new FakeHandler((_, _) => Json("""{"affected": 2}"""));
		using var api = new BackendApi(new Uri("http://backend.test/"), "admin", "secret", handler);

		int affected = await api.AddTagToMediaAsync(7, [1, 2, 3]);

		Assert.Equal(2, affected);
		var (request, body) = Assert.Single(handler.Requests);
		Assert.Equal(HttpMethod.Post, request.Method);
		Assert.Equal("http://backend.test/v1/tags/7/media", request.RequestUri!.ToString());
		Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
		Assert.Equal("admin:secret", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
		using var doc = JsonDocument.Parse(body!);
		Assert.Equal([1, 2, 3], doc.RootElement.GetProperty("mediaFileIds").EnumerateArray().Select(e => e.GetInt32()));
	}

	[Fact]
	public async Task Add_tag_sends_the_backend_shape_and_returns_the_id()
	{
		var handler = new FakeHandler((_, _) => Json("""{"tag": {"id": 42, "slug": "torii"}}"""));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", handler);

		int id = await api.AddTagAsync("torii", [new TagText("en", "torii"), new TagText("ja", "鳥居")], [new TagText("en", "shrine gate")]);

		Assert.Equal(42, id);
		using var doc = JsonDocument.Parse(handler.Requests[0].Body!);
		var tag = doc.RootElement.GetProperty("tag");
		Assert.Equal("torii", tag.GetProperty("slug").GetString());
		Assert.Equal("ja", tag.GetProperty("translations")[1].GetProperty("languageCode").GetString());
		Assert.Equal("鳥居", tag.GetProperty("translations")[1].GetProperty("text").GetString());
		Assert.Equal("shrine gate", tag.GetProperty("aliases")[0].GetProperty("text").GetString());
	}

	[Fact]
	public async Task Wrong_credentials_are_named()
	{
		var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", handler);

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => api.EnsureReachableAsync());
		Assert.Contains("HARU_PASSWORD", ex.Message);
	}

	[Fact]
	public async Task Failed_request_carries_status_and_body()
	{
		var handler = new FakeHandler((_, _) => Json("""{"errors": {"slug": ["exists"]}}""", HttpStatusCode.BadRequest));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", handler);

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => api.AddTagAsync("x", [], []));
		Assert.Contains("HTTP 400", ex.Message);
		Assert.Contains("exists", ex.Message);
	}
}
