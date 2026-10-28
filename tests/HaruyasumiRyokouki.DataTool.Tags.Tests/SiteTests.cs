using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HaruyasumiRyokouki.DataTool.Common;
using HaruyasumiRyokouki.DataTool.Tags.Site;
using HaruyasumiRyokouki.DataTool.Tags.Vocabulary;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Tests;

public sealed class SiteTests : IDisposable
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

	private const string Vocabulary = """
		tags:
		- slug: torii
		  en: torii
		  ru: тории
		  ja: 鳥居
		  aliases: {en: [shrine gate], ru: [], ja: []}
		  sources: [torii]
		- slug: ramen
		  en: ramen
		  ru: рамен
		  ja: ラーメン
		  aliases: {en: [], ru: [], ja: []}
		  sources: [ramen]
		dropped: []
		""";

	private static readonly Confirm Yes = (_, _) => true;

	private readonly string _root = Directory.CreateTempSubdirectory("haru-site-").FullName;
	private readonly Workspace _workspace;
	private readonly SqliteConnection _db;

	public SiteTests()
	{
		File.WriteAllText(Path.Combine(_root, Workspace.ConfigName), "");
		_workspace = Workspace.Open(_root);
		_db = WorkDb.Open(_workspace);

		string source = Directory.CreateDirectory(Path.Combine(_root, "photos")).FullName;
		foreach (string name in new[] { "A.jpg", "b.HEIC", "c.mp4" })
			File.WriteAllText(Path.Combine(source, name), name);

		using var command = _db.CreateCommand();
		command.CommandText = $"""
			CREATE TABLE media (key TEXT PRIMARY KEY, path TEXT NOT NULL, type TEXT NOT NULL, size INTEGER NOT NULL,
			  mtime_ns INTEGER NOT NULL, missing INTEGER NOT NULL DEFAULT 0, frame_status TEXT, frame_error TEXT,
			  tag_status TEXT NOT NULL DEFAULT 'pending', tag_error TEXT, tag_description TEXT,
			  embed_status TEXT NOT NULL DEFAULT 'pending', embed_error TEXT);
			CREATE TABLE raw_tags (key TEXT NOT NULL, tag TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'tag', evidence TEXT,
			  PRIMARY KEY (key, tag));
			CREATE TABLE embeddings (key TEXT PRIMARY KEY, model TEXT NOT NULL, dim INTEGER NOT NULL, vector BLOB NOT NULL);
			INSERT INTO schema_versions(owner, version) VALUES ('analysis', 3);
			INSERT INTO media(key, path, type, size, mtime_ns) VALUES
			  ('a', '{source}/A.jpg', 'image', 1, 1), ('b', '{source}/b.HEIC', 'image', 1, 1), ('c', '{source}/c.mp4', 'video', 1, 1);
			INSERT INTO raw_tags(key, tag) VALUES ('a', 'torii'), ('b', 'torii'), ('b', 'ramen'), ('c', 'ramen');
			""";
		command.ExecuteNonQuery();
	}

	public void Dispose()
	{
		_db.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_root, recursive: true);
	}

	private void Approve()
	{
		File.WriteAllText(_workspace.VocabularyPath, Vocabulary);
		VocabDb.Ensure(_db);
		var tags = VocabularyCheck.Run(Vocabulary, new HashSet<string> { "torii", "ramen" }).Tags;
		var keys = new Dictionary<string, HashSet<string>> { ["torii"] = ["a", "b"], ["ramen"] = ["b", "c"] };
		VocabDb.Approve(_db, tags, keys, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_workspace.VocabularyPath))).ToLowerInvariant());
	}

	private Dictionary<string, (long Id, long Created)> SiteTags()
	{
		var result = new Dictionary<string, (long, long)>();
		using var command = _db.CreateCommand();
		command.CommandText = "SELECT slug, tag_id, created FROM site_tags";
		using var reader = command.ExecuteReader();
		while (reader.Read())
			result[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
		return result;
	}

	[Fact]
	public async Task Push_tags_needs_the_approval_and_creates_only_missing_tags()
	{
		var site = new List<string> { """{"id": 5, "slug": "torii"}""" };
		var handler = new FakeHandler((request, _) => request.Method == HttpMethod.Get
			? Json($$"""{"items": [{{string.Join(",", site)}}]}""")
			: Json("""{"tag": {"id": 9, "slug": "ramen"}}"""));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", handler);
		var step = new PushTagsStep(api, _workspace.VocabularyPath, Yes, TextWriter.Null);

		await Assert.ThrowsAsync<WorkspaceException>(() => step.RunAsync(_db, new StepReport("push-tags"), default));

		Approve();
		var report = new StepReport("push-tags");
		Assert.Null(await step.RunAsync(_db, report, default));

		Assert.Empty(report.Errors);
		Assert.Equal(1, report.Counts["created"]);
		Assert.Equal(1, report.Counts["already on the site"]);
		var (_, body) = Assert.Single(handler.Requests, r => r.Request.Method == HttpMethod.Post);
		using (var doc = JsonDocument.Parse(body!))
		{
			var tag = doc.RootElement.GetProperty("tag");
			Assert.Equal("ramen", tag.GetProperty("slug").GetString());
			Assert.Equal(["en", "ru", "ja"], tag.GetProperty("translations").EnumerateArray().Select(t => t.GetProperty("languageCode").GetString()));
		}
		Assert.Equal((5, 0), SiteTags()["torii"]);
		Assert.Equal((9, 1), SiteTags()["ramen"]);

		// Second run: the site has both, nothing is created and the created mark stays.
		site.Add("""{"id": 9, "slug": "ramen"}""");
		handler.Requests.Clear();
		var again = new StepReport("push-tags");
		await step.RunAsync(_db, again, default);
		Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Post);
		Assert.Equal(2, again.Counts["already on the site"]);
		Assert.Equal((9, 1), SiteTags()["ramen"]);
	}

	[Fact]
	public async Task A_refused_tag_is_an_error_but_the_others_are_created()
	{
		Approve();
		var handler = new FakeHandler((request, body) => request.Method == HttpMethod.Get ? Json("""{"items": []}""")
			: body!.Contains("\"torii\"") ? Json("""{"errors": ["slug taken"]}""", HttpStatusCode.BadRequest)
			: Json("""{"tag": {"id": 9, "slug": "ramen"}}"""));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", handler);

		var report = new StepReport("push-tags");
		await new PushTagsStep(api, _workspace.VocabularyPath, Yes, TextWriter.Null).RunAsync(_db, report, default);

		var error = Assert.Single(report.Errors);
		Assert.Equal("torii", error.Key);
		Assert.Contains("slug taken", error.Message);
		Assert.Equal(["ramen"], SiteTags().Keys);
	}

	[Fact]
	public async Task Push_tags_writes_nothing_without_confirmation()
	{
		Approve();
		var handler = new FakeHandler((_, _) => Json("""{"items": []}"""));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", handler);

		var status = await new PushTagsStep(api, _workspace.VocabularyPath, (_, _) => false, TextWriter.Null)
			.RunAsync(_db, new StepReport("push-tags"), default);

		Assert.Equal("needs_review", status);
		Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Post);
	}

	[Fact]
	public async Task Upload_skips_keys_the_storage_has_with_any_extension_and_keeps_the_file_time()
	{
		string storageDir = Directory.CreateDirectory(Path.Combine(_root, "storage")).FullName;
		File.WriteAllText(Path.Combine(storageDir, "a.webp"), "converted by the site");
		var modified = new DateTime(2024, 4, 1, 10, 0, 0, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(Path.Combine(_root, "photos", "b.HEIC"), modified);
		using var storage = new LocalMediaStorage(storageDir);
		string? targets = null;

		var report = new StepReport("upload");
		await new UploadStep(storage, 2, (_, t) => { targets = string.Join(";", t); return true; }, TextWriter.Null).RunAsync(_db, report, default);

		Assert.Equal(["a.webp", "b.HEIC", "c.mp4"], Directory.GetFiles(storageDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
		Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(storageDir, "b.HEIC")));
		Assert.Equal(2, report.Counts["uploaded"]);
		Assert.Equal(1, report.Counts["already in the storage"]);
		Assert.Contains(storageDir, targets);

		// Everything is there now: no question, nothing copied.
		var again = new StepReport("upload");
		await new UploadStep(storage, 2, (_, _) => throw new InvalidOperationException("asked"), TextWriter.Null).RunAsync(_db, again, default);
		Assert.Equal(3, again.Counts["already in the storage"]);
	}

	[Fact]
	public async Task Assign_adds_each_tag_to_its_site_files_in_batches()
	{
		Approve();
		SiteDb.Ensure(_db);
		var handler = new FakeHandler((request, body) =>
		{
			if (request.Method == HttpMethod.Get)
				return Json("""{"items": []}""");
			int count = JsonDocument.Parse(body!).RootElement.GetProperty("mediaFileIds").GetArrayLength();
			return Json($$"""{"affected": {{count - 1}}}""");
		});
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", handler);
		var step = new AssignStep(api, _workspace.VocabularyPath, Yes, TextWriter.Null) { BatchSize = 1 };

		await Assert.ThrowsAsync<WorkspaceException>(() => step.RunAsync(_db, new StepReport("assign"), default));

		SiteDb.SaveTag(_db, "torii", 5, created: false);
		SiteDb.SaveTag(_db, "ramen", 9, created: true);
		// c is not on the site: the site skipped it.
		SiteDb.SaveMedia(_db, [("a", new SiteMediaFile(101, "A.webp", "Image")), ("b", new SiteMediaFile(102, "b.webp", "Image"))]);

		var report = new StepReport("assign");
		await step.RunAsync(_db, report, default);

		var calls = handler.Requests.Where(r => r.Request.Method == HttpMethod.Post).Select(r => (r.Request.RequestUri!.AbsolutePath,
			JsonDocument.Parse(r.Body!).RootElement.GetProperty("mediaFileIds")[0].GetInt32())).ToList();
		Assert.Equal([("/v1/tags/9/media", 102), ("/v1/tags/5/media", 101), ("/v1/tags/5/media", 102)], calls);
		Assert.Equal(0, report.Counts["links added"]);
		Assert.Equal(3, report.Counts["links already there"]);
	}

	[Fact]
	public void Site_files_with_one_key_are_collisions_not_a_guess()
	{
		var (byKey, collisions) = SiteDatabase.MatchKeys([
			new SiteMediaFile(1, "IMG_1.webp", "Image"),
			new SiteMediaFile(2, "img_1.mp4", "Video"),
			new SiteMediaFile(3, "Trip.Day.webp", "Image")]);

		Assert.Equal(["trip.day"], byKey.Keys);
		Assert.Equal([1, 2], Assert.Single(collisions).Select(f => f.Id));
	}

	[Fact]
	public async Task Sync_waits_while_another_sync_runs()
	{
		int calls = 0;
		var handler = new FakeHandler((request, _) =>
		{
			Assert.Equal(HttpMethod.Put, request.Method);
			return new HttpResponseMessage(++calls < 3 ? HttpStatusCode.Conflict : HttpStatusCode.OK);
		});
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", handler) { SyncRetryDelay = TimeSpan.Zero };

		await api.SyncMediaAsync(TextWriter.Null);

		Assert.Equal(3, calls);
		Assert.Equal("http://backend.test/v1/media/sync", handler.Requests[0].Request.RequestUri!.ToString());
	}

	[Fact]
	public async Task WebDav_lists_files_only_and_puts_with_the_file_time()
	{
		var handler = new FakeHandler((request, _) => request.Method.Method == "PROPFIND"
			? new HttpResponseMessage(HttpStatusCode.MultiStatus)
			{
				Content = new StringContent("""
					<?xml version="1.0"?>
					<d:multistatus xmlns:d="DAV:">
					  <d:response><d:href>/dav/files/u/trip/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
					  <d:response><d:href>/dav/files/u/trip/IMG%201.webp</d:href><d:propstat><d:prop><d:resourcetype/></d:prop></d:propstat></d:response>
					  <d:response><d:href>/dav/files/u/trip/old/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
					</d:multistatus>
					""")
			}
			: new HttpResponseMessage(HttpStatusCode.Created));
		using var storage = new WebDavMediaStorage(new Uri("https://cloud.test/dav/files/u/trip"), "u", "p", handler);

		Assert.Equal(["IMG 1.webp"], await storage.ListAsync(default));
		Assert.Equal("1", handler.Requests[0].Request.Headers.GetValues("Depth").Single());

		string file = Path.Combine(_root, "photos", "b.HEIC");
		await storage.UploadAsync(file, "b 2.HEIC", new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), default);
		var put = handler.Requests[1].Request;
		Assert.Equal(HttpMethod.Put, put.Method);
		Assert.Equal("https://cloud.test/dav/files/u/trip/b%202.HEIC", put.RequestUri!.AbsoluteUri);
		Assert.Equal("1711929600", put.Headers.GetValues("X-OC-MTime").Single());
		Assert.Equal("b.HEIC", handler.Requests[1].Body);
		Assert.Equal("storage: WebDAV https://cloud.test/dav/files/u/trip/", storage.Description);
	}
}
