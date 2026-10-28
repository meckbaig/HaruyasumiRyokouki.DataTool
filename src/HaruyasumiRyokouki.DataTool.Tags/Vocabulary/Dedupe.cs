using System.Text;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Finding entries that are the same thing for a visitor. The model sees the whole list at once,
/// because duplicates ("sakura", "cherry blossom") can be far apart, and answers only with the
/// changes, which keeps the answer short even for a thousand entries. Tested on DeepSeek, Claude and
/// local models; see docs/models.md.
/// </summary>
internal static class Dedupe
{
	// No examples on purpose: models copy them into the answer.
	public const string Prompt = """
		You clean up the search vocabulary of a travel diary site about a trip to Japan. The input lists every tag and name a vision model put on the photos, one per line, with the number of photos. Read the whole list and find entries that a visitor would search as the same thing, and entries whose spelling should be fixed. Everything you do not mention stays as it is; another step decides later what to drop.

		Return two lists:

		"merge": groups of two or more entries that mean the same thing for a visitor: synonyms, singular and plural, spelling variants and typos, short and long forms of the same name, the same word in another language or script, a word with and without a generic addition. Things that are related but different stay separate: a kind and its sub-kind, a place and a landmark in it, two different dishes. For each group give "tag", the name visitors will see (English, lowercase, singular, standard spelling, the usual romanized form for Japanese names, without macrons), and "members", the entries exactly as written in the input, without the photo count.

		"fix": single entries whose name should be written differently: a typo, a non-Latin script, an unusual form of a well-known name, a plural with no singular in the list. Give "entry" and "tag".

		An entry may appear only once in the whole answer. Return JSON only.
		""";

	/// <summary>The model's answer.</summary>
	public sealed record Answer(List<Merge> Merge, List<Fix> Fix);

	/// <summary>Entries joined into one tag.</summary>
	public sealed record Merge(string Tag, List<string> Members);

	/// <summary>One entry renamed.</summary>
	public sealed record Fix(string Entry, string Tag);

	/// <summary>The user message: the list, then the vector hints, which the model may ignore.</summary>
	public static string BuildMessage(IReadOnlyList<VocabEntry> entries, IReadOnlyDictionary<string, List<(string Tag, double Cosine)>> hints)
	{
		var text = new StringBuilder();
		foreach (var entry in entries)
			text.Append(entry.Text).Append(" (").Append(entry.Keys.Count).Append(")\n");

		var pairs = hints
			.SelectMany(h => h.Value.Select(o => string.CompareOrdinal(h.Key, o.Tag) < 0 ? (h.Key, o.Tag) : (o.Tag, h.Key)))
			.Distinct()
			.ToList();
		if (pairs.Count > 0)
		{
			text.Append("\nPairs whose photos look alike but rarely share a photo. Only a hint, about a third are not the same thing:\n");
			foreach (var (a, b) in pairs)
				text.Append(a).Append(" ~ ").Append(b).Append('\n');
		}

		return text.ToString();
	}

	/// <summary>
	/// Applies the answer: the name every entry ends up under. Members are matched exactly, then by
	/// normal form and by singular or plural, since models write "plaque" for an entry "plaques".
	/// An entry the answer does not mention keeps its own name; one mentioned twice keeps the first.
	/// </summary>
	public static Dictionary<VocabEntry, string> Apply(IReadOnlyList<VocabEntry> entries, Answer answer, List<string> warnings)
	{
		var byText = entries.ToDictionary(e => e.Text, StringComparer.Ordinal);
		var nameOf = new Dictionary<VocabEntry, string>();

		void Assign(string member, string tag)
		{
			var entry = Find(byText, member);
			if (entry is null)
			{
				warnings.Add($"The model named an entry that is not in the list: '{member}' (for '{tag}').");
				return;
			}
			if (!nameOf.TryAdd(entry, Clean(tag)))
				warnings.Add($"'{entry.Text}' is in more than one group; kept in '{nameOf[entry]}'.");
		}

		foreach (var merge in answer.Merge ?? [])
			foreach (string member in merge.Members ?? [])
				Assign(member, merge.Tag);
		foreach (var fix in answer.Fix ?? [])
			Assign(fix.Entry, fix.Tag);

		foreach (var entry in entries)
			nameOf.TryAdd(entry, entry.Text);
		return nameOf;
	}

	private static VocabEntry? Find(Dictionary<string, VocabEntry> byText, string member)
	{
		string text = Clean(member);
		if (byText.TryGetValue(text, out var entry) || byText.TryGetValue(TagNormalizer.Normalize(text), out entry))
			return entry;

		string singular = TagNormalizer.Singularize(text);
		if (singular != text && byText.TryGetValue(singular, out entry))
			return entry;
		return byText.Values.FirstOrDefault(e => TagNormalizer.Singularize(e.Text) == text);
	}

	private static string Clean(string text) => string.Join(' ', text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
