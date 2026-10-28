using System.Net;
using System.Text;
using System.Text.Json;
using HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

namespace HaruyasumiRyokouki.DataTool.Tags.Tests;

public sealed class TranslationTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("haru-vocab-").FullName;

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private static VocabTag Tag(string name, params string[] entries)
	{
		var tag = new VocabTag(name);
		foreach (string entry in entries.Prepend(name).Distinct())
		{
			tag.Entries.Add(new VocabEntry(entry, "tag"));
			tag.RawTags.Add(entry);
		}
		return tag;
	}

	internal sealed class CompletionServer(Func<string, object> answer) : HttpMessageHandler
	{
		public List<string> Asked { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string tag = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("tag").GetString()!;
			lock (Asked)
				Asked.Add(tag);
			string body = JsonSerializer.Serialize(new { tag = answer(tag), similarExisting = Array.Empty<object>() });
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		}
	}

	internal static object Draft(string en, string ru, string ja, params (string Language, string Text)[] aliases) => new
	{
		slug = en,
		translations = new[] { new { languageCode = "en", text = en }, new { languageCode = "ru", text = ru }, new { languageCode = "ja", text = ja } },
		aliases = aliases.Select(a => new { languageCode = a.Language, text = a.Text })
	};

	[Fact]
	public void The_vocabulary_is_english_with_merged_variants_as_aliases()
	{
		var tag = Tag("cherry blossom", "sakura", "cherry blossoms", "桜");

		Translator.SetEnglish(tag);

		Assert.Equal("cherry blossom", tag.Labels["en"]);
		Assert.False(tag.Labels.ContainsKey("ru"));
		Assert.Equal(["cherry blossoms", "sakura"], tag.Aliases["en"]);   // no Latin letters, no English alias
	}

	[Fact]
	public async Task Labels_come_from_the_backend_and_aliases_are_cleaned()
	{
		var server = new CompletionServer(_ => Draft("Cherry Blossoms", "сакура", "桜", ("en", "sakura"), ("ru", "Сакура"), ("ja", "さくら")));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", server);
		var tag = new TranslatedTag("cherry_blossom", "cherry blossom", ["sakura"]);
		var errors = new List<string>();

		await new Translator(api, Path.Combine(_root, "cache"), 2, TextWriter.Null).TranslateAsync([tag], errors, default);

		Assert.Empty(errors);
		Assert.Equal(["cherry blossom"], server.Asked);
		Assert.Equal("cherry blossom", tag.Labels["en"]);   // the approved name stays, the backend's wording becomes an alias
		Assert.Equal("сакура", tag.Labels["ru"]);
		Assert.Equal("桜", tag.Labels["ja"]);
		Assert.Equal(["cherry blossoms", "sakura"], tag.Aliases["en"].Order());
		Assert.Empty(tag.Aliases["ru"]);   // "Сакура" equals the label
		Assert.Equal(["さくら"], tag.Aliases["ja"]);
	}

	[Fact]
	public async Task Translations_are_cached_and_matching_only_links_existing_tags()
	{
		var server = new CompletionServer(t => Draft(t, "рамен", "ラーメン"));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", server);
		var translator = new Translator(api, Path.Combine(_root, "cache"), 1, TextWriter.Null);

		await translator.TranslateAsync([new TranslatedTag("ramen", "ramen", [])], [], default);
		await translator.TranslateAsync([new TranslatedTag("ramen", "ramen", [])], [], default);
		Assert.Equal(["ramen"], server.Asked);

		var torii = Tag("torii gate");
		Translator.MatchExisting([Tag("ramen"), torii], [new ExistingTag(5, "torii", [new("en", "torii"), new("ja", "鳥居")], [new("en", "torii gate")])]);
		Assert.Equal(5, torii.ExistingId);
		Assert.Empty(torii.Labels);   // the site has its labels; nothing to translate or review
	}

	[Fact]
	public void Vocabulary_checks_need_no_translations()
	{
		VocabTag English(string name, params string[] aliases)
		{
			var tag = Tag(name);
			tag.Labels["en"] = name;
			tag.Aliases["en"] = [.. aliases];
			return tag;
		}

		var problems = Checks.Run([
			English("七咲ナギ"),
			English("maidreamin", "maidreamin"),
			English("heian jingu"),
			English("heian-jingu"),
			English("shop"),
		]);

		Assert.Contains(problems, p => p.Kind == "slug" && p.Tag == "七咲ナギ");
		Assert.Contains(problems, p => p.Kind == "duplicate text" && p.Tag == "maidreamin");
		Assert.Contains(problems, p => p.Kind == "slug" && p.Message.Contains("heian_jingu"));
		Assert.DoesNotContain(problems, p => p.Tag == "shop");
	}

	[Fact]
	public void Translations_check_the_site_rules_in_every_language()
	{
		const string yaml = """
			tags:
			- slug: shop
			  en: shop
			  ru: магазин
			  ja: 店
			  aliases: {en: [store], ru: [], ja: []}
			- slug: store
			  en: store
			  ru: магазин
			  ja: ""
			  aliases: {en: [], ru: [], ja: []}
			- slug: maidreamin
			  en: maidreamin
			  ru: maidreamin
			  ja: めいどりーみん
			- slug: extra
			  en: extra
			  ru: экстра
			  ja: 余分
			""";

		var result = VocabularyCheck.RunTranslations(yaml, ["shop", "store", "maidreamin", "temple"]);

		void Has(string slug, string text, bool error) =>
			Assert.Contains(result.Issues, i => i.Slug == slug && i.Message.Contains(text) && i.IsError == error);
		Has("store", "ru label 'магазин' is also used", false);
		Has("store", "no ja label", true);
		Has("maidreamin", "'maidreamin' is used twice", true);
		Has("extra", "not a new tag of the approved vocabulary", true);
		Has("temple", "is missing", true);
	}

	[Fact]
	public void Review_files_have_short_lists_on_one_line_and_problems_on_top()
	{
		var tag = Tag("cherry blossom", "sakura");
		tag.Keys.UnionWith(["a", "b"]);
		Translator.SetEnglish(tag);
		var dropped = Tag("laptop");
		dropped.Keep = false;
		dropped.Reason = "incidental object";
		string path = Path.Combine(_root, "vocabulary.yaml");

		VocabularyFile.From([tag, dropped]).Write(path, ["slug: tags 'a', 'b' get the same slug 'a'"]);
		string text = File.ReadAllText(path);
		var read = VocabularyFile.Read(path);

		Assert.Contains("#   slug: tags 'a', 'b' get the same slug 'a'", text);
		Assert.Contains("sources: [cherry blossom, sakura]", text);
		Assert.Contains("aliases: [sakura]", text);
		Assert.DoesNotContain("ru:", text);
		var entry = Assert.Single(read.Tags);
		Assert.Equal("cherry_blossom", entry.Slug);
		Assert.Equal(2, entry.Photos);
		Assert.Equal("incidental object", Assert.Single(read.Dropped).Reason);

		var translated = new TranslatedTag("cherry_blossom", "cherry blossom", ["sakura"]);
		translated.Labels["ru"] = "сакура";
		string translations = TranslationsFile.From([translated]).Render(["warning: x"]);
		Assert.Contains("aliases: {en: [sakura], ru: [], ja: []}", translations);
		Assert.Contains("#   warning: x", translations);
	}
}
