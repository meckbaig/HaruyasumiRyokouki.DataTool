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
		  ru: магазин
		  ja: 店
		  kind: tag
		  aliases: {en: [store], ru: [], ja: []}
		  sources: [shop, store]
		  photos: 3
		  existing: false
		- slug: temple
		  en: temple
		  ru: храм
		  ja: 寺
		  aliases: {en: [], ru: [], ja: []}
		  sources: [temple]
		dropped: []
		""";

	[Fact]
	public void Valid_file_has_no_issues_and_keeps_line_numbers()
	{
		var result = VocabularyCheck.Run(Valid, Known);

		Assert.Empty(result.Issues);
		Assert.Equal([3, 12], result.Tags.Select(t => t.Line));
		Assert.Equal(["store"], result.Tags[0].Aliases["en"]);
		Assert.Equal("храм", result.Tags[1].Labels["ru"]);
	}

	[Fact]
	public void Every_rule_reports_its_line()
	{
		const string yaml = """
			tags:
			- slug: Shop Tag
			  en: shop
			  ru: shop
			  ja: ""
			  aliases: {en: [Shop], ru: [], ja: []}
			  sources: [shop, typo]
			- slug: temple
			  en: shop
			  ru: храм
			  ja: 寺
			  sources: [shop]
			- slug: temple
			  en: ramen
			  ru: храм
			  ja: ラーメン
			  sources: []
			""";

		var issues = VocabularyCheck.Run(yaml, Known).Issues;

		void Has(int line, string text, bool error) =>
			Assert.Contains(issues, i => i.Line == line && i.Message.Contains(text) && i.IsError == error);
		Has(2, "lowercase Latin letters", true);
		Has(2, "no ja label", true);
		Has(2, "'shop' is used twice", true);          // en and ru labels are the same text
		Has(2, "en alias 'Shop' equals a label", true);
		Has(2, "source 'typo' is not a tag", true);
		Has(8, "en label 'shop' is also used on line 2", true);
		Has(8, "source 'shop' is also in the tag on line 2", true);
		Has(13, "slug is also used on line 8", true);
		Has(13, "ru label 'храм' is also used on line 8", false);
		Has(13, "no sources", false);
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
}
