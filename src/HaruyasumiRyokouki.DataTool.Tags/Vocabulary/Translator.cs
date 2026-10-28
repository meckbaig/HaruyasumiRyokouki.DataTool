using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Labels and aliases in en, ru and ja for the kept tags. A tag the site already has takes its
/// labels from there; the others go to the backend's /v1/tags/completion, the same prompt the
/// admin uses for a single tag, so the vocabulary reads like the rest of the site.
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
		{
			if (!bySlug.TryGetValue(tag.Slug, out var match) && !byText.TryGetValue(tag.Name, out match))
				continue;

			tag.ExistingId = match.Id;
			Fill(tag, match.Translations, match.Aliases);
		}
	}

	/// <summary>Asks the backend for every kept tag that is not on the site yet; answers are cached.</summary>
	public async Task TranslateAsync(IReadOnlyCollection<VocabTag> tags, List<string> errors, CancellationToken cancellationToken)
	{
		var todo = tags.Where(t => t.Keep && t.ExistingId is null).ToList();
		int done = 0;
		output.WriteLine($"vocab: translating {todo.Count} tags through {api.BaseAddress}v1/tags/completion");

		await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
			async (tag, ct) =>
			{
				try
				{
					var draft = await CompleteCachedAsync(tag.Name, ct);
					lock (tag)
						Fill(tag, draft.Translations, draft.Aliases);
				}
				catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
				{
					lock (errors)
						errors.Add($"'{tag.Name}': {ex.Message}");
				}

				int count = Interlocked.Increment(ref done);
				if (count % 25 == 0 || count == todo.Count)
					output.WriteLine($"vocab: translated {count}/{todo.Count}");
			});
	}

	/// <summary>
	/// The model's English name stays the main English label: it was chosen with the whole
	/// vocabulary in view. Other English wording of the backend becomes an alias, as do the variants
	/// merged into the tag ("sakura" for "cherry blossom"). Aliases equal to a label are dropped,
	/// as the backend would reject them.
	/// </summary>
	private static void Fill(VocabTag tag, IEnumerable<TagText>? translations, IEnumerable<TagText>? aliases)
	{
		tag.Labels.Clear();
		tag.Aliases.Clear();
		tag.Labels["en"] = tag.Name;
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
		foreach (var entry in tag.Entries.Where(e => e.Text != tag.Name && e.Text.Any(char.IsAsciiLetter)))
			AddAlias(tag, "en", entry.Text);

		// An alias may have been added before the label it equals.
		foreach (var (language, list) in tag.Aliases)
			list.RemoveAll(alias => tag.Labels.Values.Contains(alias, StringComparer.OrdinalIgnoreCase));
	}

	private static void AddAlias(VocabTag tag, string language, string text)
	{
		if (!Languages.Contains(language) || text.Length == 0 || tag.Labels.Values.Contains(text, StringComparer.OrdinalIgnoreCase))
			return;
		if (!tag.Aliases.TryGetValue(language, out var list))
			tag.Aliases[language] = list = [];
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
