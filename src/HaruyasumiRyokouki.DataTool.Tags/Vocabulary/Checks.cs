namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Checks without a model, so problems show up in the review and not as a rejected request when
/// the tags are created. They repeat the backend's validation of a new tag (CreateTagDto.Validator)
/// and add what the backend does not check but the site needs.
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

			var missing = Translator.Languages.Where(l => !tag.Labels.TryGetValue(l, out var label) || string.IsNullOrWhiteSpace(label)).ToList();
			if (missing.Count > 0)
				problems.Add(new("translation", tag.Name, "no label in " + string.Join(", ", missing)));

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

		// Two tags with the same label look like one tag to a visitor: last time 56 pairs like
		// "магазин" for both shop and store. Merge them or give one a more precise label.
		foreach (string language in Translator.Languages)
		{
			var same = kept
				.Where(t => t.Labels.ContainsKey(language))
				.GroupBy(t => t.Labels[language].Trim(), StringComparer.OrdinalIgnoreCase)
				.Where(g => g.Count() > 1);
			foreach (var group in same)
				problems.Add(new("same label", null, $"{language} label '{group.Key}' is used by {string.Join(", ", group.Select(t => $"'{t.Name}'"))}"));
		}

		return problems;
	}
}
