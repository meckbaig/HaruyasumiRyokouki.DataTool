using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Labels and aliases in en, ru and ja. The vocabulary step only matches tags with the site and
/// sets the English side; the translate step sends the approved new tags to the backend's
/// /v1/tags/completion, the same prompt the admin uses for a single tag, so the labels read like
/// the rest of the site. Tags a person deleted in the review are never translated.
/// </summary>
internal sealed class Translator(BackendApi api, string cacheDir, int concurrency, TextWriter output)
{
	public static readonly string[] Languages = ["en", "ru", "ja"];

	/// <summary>
	/// A tag matches a site tag by slug, or by its name being a label or an alias there, so new photos
	/// of a filled site end up in the existing tags.
	/// </summary>
	public static void MatchExisting(IEnumerable<VocabTag> tags, IReadOnlyCollection<ExistingTag> existing)
	{
		var bySlug = existing.ToDictionary(e => e.Slug, StringComparer.OrdinalIgnoreCase);
		var byText = new Dictionary<string, ExistingTag>(StringComparer.OrdinalIgnoreCase);
		foreach (var tag in existing)
			foreach (var text in (tag.Translations ?? []).Concat(tag.Aliases ?? []))
				byText.TryAdd(text.Text.Trim(), tag);

		foreach (var tag in tags.Where(t => t.Keep))
			if (bySlug.TryGetValue(tag.Slug, out var match) || byText.TryGetValue(tag.Name, out match))
				tag.ExistingId = match.Id;
	}

	/// <summary>
	/// The English side of a tag for the review: the model's name is the label, the variants merged
	/// into the tag ("sakura" for "cherry blossom") are search aliases.
	/// </summary>
	public static void SetEnglish(VocabTag tag)
	{
		tag.Labels.Clear();
		tag.Aliases.Clear();
		tag.Labels["en"] = tag.Name;
		tag.Aliases["en"] = tag.Entries.Select(e => e.Text)
			.Where(text => text != tag.Name && text.Any(char.IsAsciiLetter))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>Asks the backend for every tag; answers are cached. Failures are listed in <paramref name="errors"/>.</summary>
	public async Task TranslateAsync(IReadOnlyCollection<TranslatedTag> tags, List<string> errors, CancellationToken cancellationToken)
	{
		int done = 0;
		output.WriteLine($"translate: {tags.Count} tags through {api.BaseAddress}v1/tags/completion");

		await Parallel.ForEachAsync(tags, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
			async (tag, ct) =>
			{
				try
				{
					var draft = await CompleteCachedAsync(tag.Labels["en"], ct);
					lock (tag)
						Fill(tag, draft.Translations, draft.Aliases);
				}
				catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
				{
					lock (errors)
						errors.Add($"'{tag.Slug}' was not translated: {ex.Message}");
				}

				int count = Interlocked.Increment(ref done);
				if (count % 25 == 0 || count == tags.Count)
					output.WriteLine($"translate: {count}/{tags.Count}");
			});
	}

	/// <summary>
	/// The approved English label stays: a person chose it. Other English wording of the backend
	/// becomes an alias. Aliases equal to a label are dropped, as the backend would reject them.
	/// </summary>
	private static void Fill(TranslatedTag tag, IEnumerable<TagText>? translations, IEnumerable<TagText>? aliases)
	{
		foreach (var text in translations ?? [])
		{
			string language = text.LanguageCode.ToLowerInvariant();
			string value = text.Text.Trim();
			if (language == "en")
				AddAlias(tag, "en", value.ToLowerInvariant());
			else if (Languages.Contains(language) && value.Length > 0)
				tag.Labels[language] = value;
		}

		foreach (var text in aliases ?? [])
			AddAlias(tag, text.LanguageCode.ToLowerInvariant(), text.Text.Trim());

		// An alias may have been added before the label it equals.
		foreach (var (_, list) in tag.Aliases)
			list.RemoveAll(alias => tag.Labels.Values.Contains(alias, StringComparer.OrdinalIgnoreCase));
	}

	private static void AddAlias(TranslatedTag tag, string language, string text)
	{
		if (!Languages.Contains(language) || text.Length == 0 || tag.Labels.Values.Contains(text, StringComparer.OrdinalIgnoreCase))
			return;
		var list = tag.Aliases[language];
		if (!list.Contains(text, StringComparer.OrdinalIgnoreCase))
			list.Add(text);
	}

	private async Task<TagDraft> CompleteCachedAsync(string name, CancellationToken cancellationToken)
	{
		string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"completion|{api.BaseAddress}|{name}"))).ToLowerInvariant();
		string path = Path.Combine(cacheDir, key + ".json");
		if (File.Exists(path))
			return JsonSerializer.Deserialize<TagDraft>(await File.ReadAllTextAsync(path, cancellationToken), BackendApi.Json)!;

		var draft = await api.CompleteTagAsync(name, cancellationToken);
		Directory.CreateDirectory(cacheDir);
		await File.WriteAllTextAsync(path, JsonSerializer.Serialize(draft, BackendApi.Json), cancellationToken);
		return draft;
	}
}

/// <summary>A new tag of the approved vocabulary with its labels and aliases in every language.</summary>
internal sealed class TranslatedTag
{
	public TranslatedTag(string slug, string en, IEnumerable<string> enAliases)
	{
		Slug = slug;
		Labels["en"] = en;
		foreach (string language in Translator.Languages)
			Aliases[language] = [];
		Aliases["en"].AddRange(enAliases);
	}

	public string Slug { get; }

	/// <summary>Main label per language.</summary>
	public Dictionary<string, string> Labels { get; } = [];

	/// <summary>Search aliases per language; every language is present.</summary>
	public Dictionary<string, List<string>> Aliases { get; } = [];
}
