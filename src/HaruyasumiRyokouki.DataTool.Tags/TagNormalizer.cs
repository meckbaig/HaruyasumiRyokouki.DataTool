using System.Globalization;
using System.Text;

namespace HaruyasumiRyokouki.DataTool.Tags;

/// <summary>
/// Mechanical clean-up of machine tags before they go to a language model: case, separators,
/// singular and plural. It shrinks the list the model has to read and is free and repeatable.
/// Every raw tag remembers its normal form, so the link from a photo's tag to the final slug
/// survives every later step.
/// </summary>
internal static class TagNormalizer
{
	/// <summary>
	/// Tags that fit almost the whole archive and so do not narrow a search. The analysis step
	/// filters most of them already; this catches what an older run or another model let through.
	/// </summary>
	private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
	{
		"japan", "japanese", "travel", "tourism", "outdoor", "indoor",
		"architecture", "building", "traditional", "serene",
		"scenic", "beautiful", "view", "photo", "image"
	};

	/// <summary>True for tags that are dropped without asking anyone.</summary>
	public static bool IsStopWord(string normalized) => StopWords.Contains(normalized);

	/// <summary>Lower case, <c>_</c> and <c>-</c> as spaces, single spaces: "Cherry_Blossoms" becomes "cherry blossoms".</summary>
	public static string Normalize(string raw)
	{
		var builder = new StringBuilder(raw.Length);
		bool lastWasSpace = true;

		foreach (char c in raw.Trim().ToLowerInvariant())
		{
			char ch = c is '_' or '-' ? ' ' : c;
			if (ch == ' ')
			{
				if (lastWasSpace)
					continue;
				lastWasSpace = true;
			}
			else
			{
				lastWasSpace = false;
			}

			builder.Append(ch);
		}

		return builder.ToString().Trim();
	}

	/// <summary>Rough English singular. Deliberately conservative: it is only used when both forms occur.</summary>
	public static string Singularize(string word)
	{
		if (word.Length < 4 || !word.EndsWith('s') || word.EndsWith("ss") || word.EndsWith("us"))
			return word;

		if (word.EndsWith("ies") && word.Length > 4)
			return string.Concat(word.AsSpan(0, word.Length - 3), "y");

		if (word.EndsWith("ses") || word.EndsWith("shes") || word.EndsWith("ches") || word.EndsWith("xes"))
			return word[..^2];

		return word[..^1];
	}

	/// <summary>
	/// ASCII letters and digits joined by <c>_</c>: "cherry blossom" becomes "cherry_blossom".
	/// Diacritics are dropped first, so romanized names keep their letters: "tōdai-ji" becomes "todai_ji".
	/// </summary>
	public static string Slugify(string value)
	{
		var builder = new StringBuilder(value.Length);

		foreach (char c in value.ToLowerInvariant().Normalize(NormalizationForm.FormD))
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
				continue;

			if (char.IsAsciiLetterOrDigit(c))
				builder.Append(c);
			else if (builder.Length > 0 && builder[^1] != '_')
				builder.Append('_');
		}

		return builder.ToString().Trim('_');
	}

	/// <summary>
	/// Groups raw tags by normal form, then merges a plural into its singular only when both
	/// occur: "mountain" and "mountains" become one group, a lone "glasses" stays as it is.
	/// Stop words are left out. Groups come largest first.
	/// </summary>
	/// <param name="tagged">Pairs of raw tag and the key of a file that has it.</param>
	public static List<TagGroup> Group(IEnumerable<(string Raw, string Key)> tagged)
	{
		var groups = new Dictionary<string, TagGroup>(StringComparer.Ordinal);

		foreach (var (raw, key) in tagged)
		{
			string normalized = Normalize(raw);
			if (normalized.Length == 0 || IsStopWord(normalized))
				continue;

			if (!groups.TryGetValue(normalized, out var group))
				groups[normalized] = group = new TagGroup(normalized);

			group.RawForms.Add(raw);
			group.Keys.Add(key);
		}

		foreach (string plural in groups.Keys.ToList())
		{
			string singular = Singularize(plural);
			if (singular == plural || !groups.TryGetValue(singular, out var target) || !groups.TryGetValue(plural, out var source))
				continue;

			target.RawForms.UnionWith(source.RawForms);
			target.Keys.UnionWith(source.Keys);
			target.MergedForms.Add(plural);
			groups.Remove(plural);
		}

		return groups.Values
			.OrderByDescending(g => g.Keys.Count)
			.ThenBy(g => g.Normalized, StringComparer.Ordinal)
			.ToList();
	}
}

/// <summary>Raw tags that mean the same after normalization, and the files that have any of them.</summary>
internal sealed class TagGroup(string normalized)
{
	/// <summary>The normal form, also the base of the slug.</summary>
	public string Normalized { get; } = normalized;

	/// <summary>Tags exactly as the vision model wrote them.</summary>
	public HashSet<string> RawForms { get; } = new(StringComparer.Ordinal);

	/// <summary>Keys of the files with any of the raw forms.</summary>
	public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

	/// <summary>Plural forms merged into this group.</summary>
	public List<string> MergedForms { get; } = [];
}
