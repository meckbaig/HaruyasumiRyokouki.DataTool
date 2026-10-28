using HaruyasumiRyokouki.DataTool.Common;
using HaruyasumiRyokouki.DataTool.Tags.Vocabulary;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Tests;

public sealed class ReviewTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("haru-review-").FullName;

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(_root, recursive: true);
	}

	private static readonly HashSet<string> Known = ["shop", "store", "temple", "ramen", "food"];

	private const string Valid = """
		# comment
		tags:
		- slug: shop
		  en: shop
		  aliases: [store]
		  kind: tag
		  sources: [shop, store]
		  photos: 3
		  existing: false
		- slug: temple
		  en: temple
		  aliases: []
		  sources: [temple]
		dropped: []
		""";

	[Fact]
	public void Valid_file_has_no_issues_and_keeps_line_numbers()
	{
		var result = VocabularyCheck.Run(Valid, Known);

		Assert.Empty(result.Issues);
		Assert.Equal([3, 10], result.Tags.Select(t => t.Line));
		Assert.Equal(["store"], result.Tags[0].Aliases["en"]);
		Assert.Equal("temple", result.Tags[1].Labels["en"]);
	}

	[Fact]
	public void Every_rule_reports_its_line()
	{
		const string yaml = """
			tags:
			- slug: Shop Tag
			  en: shop
			  aliases: [Shop, shop2, shop2]
			  sources: [shop, typo]
			- slug: temple
			  en: shop
			  sources: [shop]
			- slug: temple
			  en: ramen
			  sources: []
			- slug: food
			  en: ""
			  sources: [food]
			""";

		var issues = VocabularyCheck.Run(yaml, Known).Issues;

		void Has(int line, string text, bool error) =>
			Assert.Contains(issues, i => i.Line == line && i.Message.Contains(text) && i.IsError == error);
		Has(2, "lowercase Latin letters", true);
		Has(2, "en alias 'Shop' equals a label", true);
		Has(2, "'shop2' is used twice", true);
		Has(2, "source 'typo' is not a tag", true);
		Has(6, "en label 'shop' is also used on line 2", true);
		Has(6, "source 'shop' is also in the tag on line 2", true);
		Has(9, "slug is also used on line 6", true);
		Has(9, "no sources", false);
		Has(12, "no en label", true);
	}

	[Fact]
	public void Broken_yaml_is_an_error_with_its_line()
	{
		var issue = Assert.Single(VocabularyCheck.Run("tags:\n- slug: a\n  en: [unclosed\n", Known).Issues);
		Assert.True(issue.IsError);
		Assert.Contains("not valid YAML", issue.Message);
	}

	[Fact]
	public void Approval_is_stored_with_the_file_hash_and_a_new_vocabulary_takes_it_back()
	{
		File.WriteAllText(Path.Combine(_root, Workspace.ConfigName), "");
		using var db = WorkDb.Open(Workspace.Open(_root));
		VocabDb.Ensure(db);
		var tags = VocabularyCheck.Run(Valid, Known).Tags;
		var keys = new Dictionary<string, HashSet<string>> { ["shop"] = ["a", "b"], ["store"] = ["b", "c"], ["temple"] = ["d"] };

		VocabDb.Approve(db, tags, keys, "abc");

		Assert.Equal("abc", VocabDb.ApprovedSha256(db));
		using (var command = db.CreateCommand())
		{
			command.CommandText = "SELECT photos FROM vocab_tags WHERE slug = 'shop'";
			Assert.Equal(3L, command.ExecuteScalar());
		}

		VocabDb.Save(db, [], []);
		Assert.Null(VocabDb.ApprovedSha256(db));

		var marked = VocabularyCheck.Run(Valid.Replace("existing: false", "existing: true"), Known).Tags;
		var ex = Assert.Throws<InvalidOperationException>(() => VocabDb.Approve(db, marked, keys, "def"));
		Assert.Contains("'shop' (line 3)", ex.Message);
	}

	[Fact]
	public async Task Translations_come_after_the_approval_and_wait_for_review_only_with_problems()
	{
		File.WriteAllText(Path.Combine(_root, Workspace.ConfigName), "");
		var workspace = Workspace.Open(_root);
		using var db = WorkDb.Open(workspace);
		using (var command = db.CreateCommand())
		{
			command.CommandText = """
				CREATE TABLE media (key TEXT PRIMARY KEY, path TEXT NOT NULL, type TEXT NOT NULL, size INTEGER NOT NULL,
				  mtime_ns INTEGER NOT NULL, missing INTEGER NOT NULL DEFAULT 0, frame_status TEXT, frame_error TEXT,
				  tag_status TEXT NOT NULL DEFAULT 'pending', tag_error TEXT, tag_description TEXT,
				  embed_status TEXT NOT NULL DEFAULT 'pending', embed_error TEXT);
				CREATE TABLE raw_tags (key TEXT NOT NULL, tag TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'tag', evidence TEXT, PRIMARY KEY (key, tag));
				CREATE TABLE embeddings (key TEXT PRIMARY KEY, model TEXT NOT NULL, dim INTEGER NOT NULL, vector BLOB NOT NULL);
				INSERT INTO schema_versions(owner, version) VALUES ('analysis', 3);
				INSERT INTO media(key, path, type, size, mtime_ns) VALUES ('a', 'a.jpg', 'image', 1, 1), ('d', 'd.jpg', 'image', 1, 1);
				INSERT INTO raw_tags(key, tag) VALUES ('a', 'shop'), ('a', 'store'), ('d', 'temple');
				""";
			command.ExecuteNonQuery();
		}
		File.WriteAllText(workspace.VocabularyPath, Valid);
		var review = new ReviewStep(workspace.VocabularyPath, workspace.TranslationsPath, TextWriter.Null);
		string Review(ReviewStep.Mode mode) => review.Run(db, mode, new StepReport("review"));

		// Both tags get the same Russian label: a problem the review has to see.
		var server = new TranslationTests.CompletionServer(t => TranslationTests.Draft(t, "здание", t == "shop" ? "店" : "寺"));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", server);
		var translate = new TranslateStep(new Translator(api, workspace.LlmCacheDir, 2, TextWriter.Null),
			workspace.VocabularyPath, workspace.TranslationsPath, TextWriter.Null);
		Task<string?> Translate() => translate.RunAsync(db, new StepReport("translate"), default);

		Assert.Equal("needs_review", Review(ReviewStep.Mode.Summary));
		await Assert.ThrowsAsync<WorkspaceException>(Translate);   // nothing is translated before the approval
		Assert.Empty(server.Asked);

		Assert.Equal("ok", Review(ReviewStep.Mode.Approve));
		Assert.Equal("ok", Review(ReviewStep.Mode.Summary));
		Assert.Equal("needs_review", await Translate());
		Assert.Contains("ru label 'здание' is also used", File.ReadAllText(workspace.TranslationsPath));
		Assert.Throws<WorkspaceException>(() => VocabDb.RequireTranslationsApproved(db, workspace.VocabularyPath, workspace.TranslationsPath));

		// The waiting file is not overwritten by the step, and review now works on it.
		string text = File.ReadAllText(workspace.TranslationsPath);
		int at = text.IndexOf("здание", text.IndexOf("slug: temple", StringComparison.Ordinal), StringComparison.Ordinal);
		File.WriteAllText(workspace.TranslationsPath, text[..at] + "храм" + text[(at + "здание".Length)..]);
		Assert.Equal("needs_review", await Translate());
		Assert.Contains("ru: храм", File.ReadAllText(workspace.TranslationsPath));
		Assert.Equal("needs_review", Review(ReviewStep.Mode.Summary));
		Assert.Equal("ok", Review(ReviewStep.Mode.Approve));

		VocabDb.RequireTranslationsApproved(db, workspace.VocabularyPath, workspace.TranslationsPath);
		Assert.Null(await Translate());
		var temple = VocabDb.ReadApproved(db).Single(t => t.Slug == "temple");
		Assert.Equal("храм", temple.Labels["ru"]);
		Assert.Equal(["shop", "temple"], server.Asked.Order());

		// A new approval of the vocabulary starts the translations again; the answers come from the cache.
		File.AppendAllText(workspace.VocabularyPath, "\n");
		Assert.Equal("needs_review", Review(ReviewStep.Mode.Summary));
		Assert.Equal("ok", Review(ReviewStep.Mode.Approve));
		Assert.Throws<WorkspaceException>(() => VocabDb.RequireTranslationsApproved(db, workspace.VocabularyPath, workspace.TranslationsPath));
		Assert.Equal("needs_review", await Translate());
		Assert.Equal(2, server.Asked.Count);
	}
}
