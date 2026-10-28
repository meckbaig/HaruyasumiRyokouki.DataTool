namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Checks of the English vocabulary without a model, written at the top of vocabulary.yaml so the
/// review sees them. The rules that need the translations run on translations.yaml
/// (<see cref="VocabularyCheck.RunTranslations"/>).
/// </summary>
internal static class Checks
{
	/// <summary>A problem found in the vocabulary.</summary>
	public sealed record Problem(string Kind, string? Tag, string Message);

	public static List<Problem> Run(IReadOnlyCollection<VocabTag> tags)
	{
		var problems = new List<Problem>();
		var kept = tags.Where(t => t.Keep).ToList();

		foreach (var tag in kept)
		{
			if (tag.Slug.Length == 0)
				problems.Add(new("slug", tag.Name, "the name has no Latin letters, so it gives no slug; give it a romanized name"));

			// The backend keeps every text of a tag unique, labels and aliases together, case-sensitive.
			var duplicates = tag.Labels.Values.Concat(tag.Aliases.Values.SelectMany(a => a))
				.GroupBy(t => t, StringComparer.Ordinal)
				.Where(g => g.Count() > 1)
				.Select(g => g.Key)
				.ToList();
			if (duplicates.Count > 0)
				problems.Add(new("duplicate text", tag.Name, "the same text twice in one tag: " + string.Join(", ", duplicates)));
		}

		foreach (var group in kept.Where(t => t.Slug.Length > 0).GroupBy(t => t.Slug, StringComparer.Ordinal).Where(g => g.Count() > 1))
			problems.Add(new("slug", null, $"tags {string.Join(", ", group.Select(t => $"'{t.Name}'"))} get the same slug '{group.Key}'"));

		return problems;
	}
}
