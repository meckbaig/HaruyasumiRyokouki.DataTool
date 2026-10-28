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

	private sealed class CompletionServer(Func<string, object> answer) : HttpMessageHandler
	{
		public List<string> Asked { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string tag = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("tag").GetString()!;
			Asked.Add(tag);
			string body = JsonSerializer.Serialize(new { tag = answer(tag), similarExisting = Array.Empty<object>() });
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		}
	}

	private static object Draft(string en, string ru, string ja, params (string Language, string Text)[] aliases) => new
	{
		slug = en,
		translations = new[] { new { languageCode = "en", text = en }, new { languageCode = "ru", text = ru }, new { languageCode = "ja", text = ja } },
		aliases = aliases.Select(a => new { languageCode = a.Language, text = a.Text })
	};

	[Fact]
	public async Task Labels_come_from_the_backend_and_aliases_are_cleaned()
	{
		var server = new CompletionServer(_ => Draft("Cherry Blossoms", "сакура", "桜", ("en", "sakura"), ("ru", "Сакура"), ("ja", "さくら")));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", server);
		var tag = Tag("cherry blossom", "sakura", "cherry blossoms");
		var errors = new List<string>();

		await new Translator(api, Path.Combine(_root, "cache"), 2, TextWriter.Null).TranslateAsync([tag], errors, default);

		Assert.Empty(errors);
		Assert.Equal("cherry blossom", tag.Labels["en"]);   // the model's name stays, the backend's wording becomes an alias
		Assert.Equal("сакура", tag.Labels["ru"]);
		Assert.Equal("桜", tag.Labels["ja"]);
		Assert.Equal(["cherry blossoms", "sakura"], tag.Aliases["en"].Order());
		Assert.False(tag.Aliases.ContainsKey("ru") && tag.Aliases["ru"].Count > 0);   // "Сакура" equals the label
		Assert.Equal(["さくら"], tag.Aliases["ja"]);
	}

	[Fact]
	public async Task Translations_are_cached_and_existing_site_tags_are_not_asked()
	{
		var server = new CompletionServer(t => Draft(t, "рамен", "ラーメン"));
		using var api = new BackendApi(new Uri("http://backend.test"), "u", "p", server);
		var translator = new Translator(api, Path.Combine(_root, "cache"), 1, TextWriter.Null);

		var ramen = Tag("ramen");
		var torii = Tag("torii gate");
		Translator.MatchExisting([ramen, torii], [new ExistingTag(5, "torii", [new("en", "torii"), new("ja", "鳥居")], [new("en", "torii gate")])]);
		await translator.TranslateAsync([ramen, torii], [], default);
		await translator.TranslateAsync([Tag("ramen")], [], default);

		Assert.Equal(["ramen"], server.Asked);
		Assert.Equal(5, torii.ExistingId);
		Assert.Equal("鳥居", torii.Labels["ja"]);
	}

	[Fact]
	public void Checks_repeat_the_backend_rules_and_catch_shared_labels()
	{
		VocabTag Labeled(string name, string en, string ru, string ja, params string[] enAliases)
		{
			var tag = Tag(name);
			tag.Labels["en"] = en;
			tag.Labels["ru"] = ru;
			if (ja.Length > 0)
				tag.Labels["ja"] = ja;
			tag.Aliases["en"] = [.. enAliases];
			return tag;
		}

		var problems = Checks.Run([
			Labeled("shop", "shop", "магазин", "店"),
			Labeled("store", "store", "магазин", "ストア"),
			Labeled("七咲ナギ", "七咲ナギ", "Нанасаки Наги", "七咲ナギ"),
			Labeled("maidreamin", "maidreamin", "maidreamin", "めいどりーみん"),
			Labeled("fast food", "fast food", "фастфуд", ""),
			Labeled("heian jingu", "heian jingu", "Хэйан-дзингу", "平安神宮"),
			Labeled("heian-jingu", "heian-jingu", "Хэйан дзингу", "平安神宮 "),
		]);

		Assert.Contains(problems, p => p.Kind == "same label" && p.Message.Contains("магазин") && p.Message.Contains("'shop'"));
		Assert.Contains(problems, p => p.Kind == "slug" && p.Tag == "七咲ナギ");
		Assert.Contains(problems, p => p.Kind == "duplicate text" && p.Tag == "maidreamin");
		Assert.Contains(problems, p => p.Kind == "translation" && p.Tag == "fast food" && p.Message.Contains("ja"));
		Assert.Contains(problems, p => p.Kind == "slug" && p.Message.Contains("heian_jingu"));
		Assert.Contains(problems, p => p.Kind == "same label" && p.Message.Contains("平安神宮"));
	}

	[Fact]
	public void Vocabulary_file_round_trips_with_short_lists_on_one_line()
	{
		var tag = Tag("cherry blossom", "sakura");
		tag.Keys.UnionWith(["a", "b"]);
		tag.Labels["en"] = "cherry blossom";
		tag.Labels["ru"] = "сакура";
		tag.Labels["ja"] = "桜";
		tag.Aliases["en"] = ["sakura"];
		var dropped = Tag("laptop");
		dropped.Keep = false;
		dropped.Reason = "incidental object";
		string path = Path.Combine(_root, "vocabulary.yaml");

		VocabularyFile.From([tag, dropped]).Write(path, [new Checks.Problem("same label", null, "ru label 'еда' is used by 'food', 'eating'")]);
		string text = File.ReadAllText(path);
		var read = VocabularyFile.Read(path);

		Assert.Contains("#   ru label 'еда' is used by 'food', 'eating'", text);
		Assert.Contains("sources: [cherry blossom, sakura]", text);
		Assert.Contains("aliases: {en: [sakura], ru: [], ja: []}", text);
		var entry = Assert.Single(read.Tags);
		Assert.Equal("cherry_blossom", entry.Slug);
		Assert.Equal("桜", entry.Ja);
		Assert.Equal(2, entry.Photos);
		Assert.Equal("incidental object", Assert.Single(read.Dropped).Reason);
	}
}
