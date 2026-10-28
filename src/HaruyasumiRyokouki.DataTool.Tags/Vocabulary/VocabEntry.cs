namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// One line of the list the language model sees: a general tag after mechanical normalization,
/// or a proper name as the vision model wrote it, with the files it is on.
/// </summary>
internal sealed class VocabEntry(string text, string kind)
{
	/// <summary>The entry as the model sees it and must quote back.</summary>
	public string Text { get; } = text;

	/// <summary><c>tag</c>, or the kind of a name: landmark, place, brand, franchise, character, name.</summary>
	public string Kind { get; set; } = kind;

	/// <summary>Keys of the files that have it.</summary>
	public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

	/// <summary>raw_tags.tag values it was built from; the link from a photo to the final tag.</summary>
	public HashSet<string> RawTags { get; } = new(StringComparer.Ordinal);

	/// <summary>What the vision model saw for a name, a few examples.</summary>
	public List<string> Evidence { get; } = [];

	/// <summary>
	/// General tags are normalized and their plural merged into the singular when both occur;
	/// names are kept as written, since "senso-ji" must not become "senso ji". A name equal to a
	/// tag is one entry.
	/// </summary>
	public static List<VocabEntry> Build(IReadOnlyCollection<RawTag> rawTags)
	{
		var entries = new Dictionary<string, VocabEntry>(StringComparer.Ordinal);

		foreach (var group in TagNormalizer.Group(rawTags.Where(r => r.Kind == "tag").Select(r => (r.Tag, r.Key))))
		{
			var entry = new VocabEntry(group.Normalized, "tag");
			entry.Keys.UnionWith(group.Keys);
			entry.RawTags.UnionWith(group.RawForms);
			entries[entry.Text] = entry;
		}

		foreach (var name in rawTags.Where(r => r.Kind != "tag"))
		{
			if (!entries.TryGetValue(name.Tag, out var entry))
				entries[name.Tag] = entry = new VocabEntry(name.Tag, name.Kind);
			else if (entry.Kind == "tag")
				entry.Kind = name.Kind;

			entry.Keys.Add(name.Key);
			entry.RawTags.Add(name.Tag);
			if (name.Evidence is not null && entry.Evidence.Count < 2 && !entry.Evidence.Contains(name.Evidence))
				entry.Evidence.Add(name.Evidence);
		}

		return entries.Values
			.OrderByDescending(e => e.Keys.Count)
			.ThenBy(e => e.Text, StringComparer.Ordinal)
			.ToList();
	}
}
