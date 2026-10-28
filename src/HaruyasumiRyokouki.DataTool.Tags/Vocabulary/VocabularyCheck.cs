using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Reads vocabulary.yaml and translations.yaml as edited by a person and checks them with the line
/// of every problem. The YAML is read node by node rather than into objects, so each tag keeps its
/// line number.
/// </summary>
internal static class VocabularyCheck
{
	/// <summary>A tag of the file with the line it starts on.</summary>
	/// <param name="Labels">Main label per language: only en in vocabulary.yaml, en, ru and ja in translations.yaml.</param>
	/// <param name="Aliases">Aliases per language, with an entry for every language of the file.</param>
	/// <param name="Excluded">Files that do not get the tag although they have a source.</param>
	public sealed record Tag(int Line, string Slug, Dictionary<string, string> Labels, Dictionary<string, List<string>> Aliases,
		List<string> Sources, string Kind, bool Existing, List<string> Excluded);

	/// <summary>A problem in the file. Errors block the approval, warnings are only shown.</summary>
	public sealed record Issue(int Line, string? Slug, string Message, bool IsError);

	public sealed record Result(List<Tag> Tags, List<Issue> Issues)
	{
		public bool HasErrors => Issues.Any(i => i.IsError);
	}

	/// <summary>vocabulary.yaml: English tags and the machine tags they are made of.</summary>
	/// <param name="knownSources">raw_tags.tag values of the workspace; a source outside them is a typo.</param>
	public static Result Run(string yaml, IReadOnlySet<string> knownSources)
	{
		var issues = new List<Issue>();
		var tags = Parse(yaml, ["en"], issues);
		if (tags is null)
			return new([], issues);

		CheckTags(tags, ["en"], issues);
		CheckSources(tags, knownSources, issues);
		return new(tags, issues.OrderBy(i => i.Line).ToList());
	}

	/// <summary>translations.yaml: the new tags of the approved vocabulary in every language.</summary>
	/// <param name="expectedSlugs">The new tags of the approved vocabulary; the file must have each exactly once.</param>
	public static Result RunTranslations(string yaml, IReadOnlyCollection<string> expectedSlugs)
	{
		var issues = new List<Issue>();
		var tags = Parse(yaml, Translator.Languages, issues);
		if (tags is null)
			return new([], issues);

		CheckTags(tags, Translator.Languages, issues);
		var expected = expectedSlugs.ToHashSet(StringComparer.Ordinal);
		foreach (var tag in tags.Where(t => t.Slug.Length > 0 && !expected.Contains(t.Slug)))
			issues.Add(new(tag.Line, tag.Slug, "not a new tag of the approved vocabulary; tags are added and deleted in vocabulary.yaml", true));
		foreach (string slug in expected.Where(s => !tags.Any(t => t.Slug == s)).Order(StringComparer.Ordinal))
			issues.Add(new(1, slug, $"the tag '{slug}' of the approved vocabulary is missing; tags are deleted in vocabulary.yaml", true));

		return new(tags, issues.OrderBy(i => i.Line).ToList());
	}

	/// <summary>The tags of the file, or null when it is not a valid file at all.</summary>
	private static List<Tag>? Parse(string yaml, IReadOnlyList<string> languages, List<Issue> issues)
	{
		YamlMappingNode root;
		try
		{
			var stream = new YamlStream();
			stream.Load(new StringReader(yaml));
			root = stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode
				?? throw new YamlException("the file must be a mapping with \"tags\"");
		}
		catch (YamlException ex)
		{
			issues.Add(new(checked((int)ex.Start.Line), null, "not valid YAML: " + ex.Message, true));
			return null;
		}

		if (Child(root, "tags") is not YamlSequenceNode sequence)
		{
			// An empty list is fine: all tags may be on the site already.
			if (Child(root, "tags") is YamlScalarNode { Value: null or "" or "[]" })
				return [];
			issues.Add(new(1, null, "no \"tags\" list", true));
			return null;
		}

		var tags = new List<Tag>();
		foreach (var node in sequence.Children)
		{
			int line = checked((int)node.Start.Line);
			if (node is not YamlMappingNode map)
			{
				issues.Add(new(line, null, "a tag must be a mapping with slug and en", true));
				continue;
			}

			var labels = languages.ToDictionary(l => l, l => Scalar(map, l)?.Trim() ?? "");
			// vocabulary.yaml has English aliases as a list, translations.yaml a list per language.
			var aliases = languages.ToDictionary(l => l, l => Child(map, "aliases") switch
			{
				YamlMappingNode byLanguage => Strings(Child(byLanguage, l)),
				YamlSequenceNode list when l == "en" => Strings(list),
				_ => []
			});
			tags.Add(new Tag(line, Scalar(map, "slug")?.Trim() ?? "", labels, aliases, Strings(Child(map, "sources")),
				Scalar(map, "kind") ?? "tag", Scalar(map, "existing") is "true" or "True", Strings(Child(map, "excluded"))));
		}

		return tags;
	}

	/// <summary>The backend's rules for a tag (CreateTagDto.Validator) and labels unique between tags.</summary>
	private static void CheckTags(List<Tag> tags, IReadOnlyList<string> languages, List<Issue> issues)
	{
		foreach (var tag in tags)
		{
			string? slug = tag.Slug.Length > 0 ? tag.Slug : null;
			if (slug is null)
				issues.Add(new(tag.Line, null, "slug is empty", true));
			else if (TagNormalizer.Slugify(slug) != slug)
				issues.Add(new(tag.Line, slug, $"slug may contain only lowercase Latin letters, digits and '_', like '{TagNormalizer.Slugify(slug)}'", true));

			foreach (string language in languages.Where(l => tag.Labels[l].Length == 0))
				issues.Add(new(tag.Line, slug, $"no {language} label", true));

			var texts = tag.Labels.Values.Where(t => t.Length > 0).Concat(tag.Aliases.Values.SelectMany(a => a));
			foreach (var duplicate in texts.GroupBy(t => t, StringComparer.Ordinal).Where(g => g.Count() > 1))
				issues.Add(new(tag.Line, slug, $"'{duplicate.Key}' is used twice in this tag; the site keeps every text of a tag unique, give one language its own spelling", true));
			foreach (var (language, list) in tag.Aliases)
				foreach (string alias in list.Where(a => tag.Labels.Values.Contains(a, StringComparer.OrdinalIgnoreCase) && !tag.Labels.Values.Contains(a, StringComparer.Ordinal)))
					issues.Add(new(tag.Line, slug, $"{language} alias '{alias}' equals a label", true));
		}

		foreach (var group in tags.Where(t => t.Slug.Length > 0).GroupBy(t => t.Slug, StringComparer.Ordinal).Where(g => g.Count() > 1))
			foreach (var tag in group.Skip(1))
				issues.Add(new(tag.Line, tag.Slug, $"slug is also used on line {group.First().Line}", true));

		foreach (string language in languages)
		{
			foreach (var group in tags.Where(t => t.Labels[language].Length > 0)
				.GroupBy(t => t.Labels[language], StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			{
				// The English label names the tag in work.sqlite, so it must be unique; the site itself
				// accepts shared labels, and a person may keep one on purpose.
				bool error = language == "en";
				foreach (var tag in group.Skip(1))
					issues.Add(new(tag.Line, tag.Slug, $"{language} label '{group.Key}' is also used on line {group.First().Line}; visitors cannot tell the tags apart", error));
			}
		}
	}

	private static void CheckSources(List<Tag> tags, IReadOnlySet<string> knownSources, List<Issue> issues)
	{
		foreach (var tag in tags)
		{
			string? slug = tag.Slug.Length > 0 ? tag.Slug : null;
			if (tag.Sources.Count == 0)
				issues.Add(new(tag.Line, slug, "no sources: no photo would get this tag", false));
			foreach (string source in tag.Sources.Where(s => !knownSources.Contains(s)))
				issues.Add(new(tag.Line, slug, $"source '{source}' is not a tag of any photo", true));
		}

		var bySource = tags.SelectMany(t => t.Sources.Select(s => (Source: s, Tag: t))).GroupBy(x => x.Source, StringComparer.Ordinal);
		foreach (var group in bySource.Where(g => g.Count() > 1))
			foreach (var (_, tag) in group.Skip(1))
				issues.Add(new(tag.Line, tag.Slug, $"source '{group.Key}' is also in the tag on line {group.First().Tag.Line}; a source can belong to one tag", true));
	}

	private static YamlNode? Child(YamlMappingNode map, string key)
		=> map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

	private static string? Scalar(YamlMappingNode map, string key) => (Child(map, key) as YamlScalarNode)?.Value;

	private static List<string> Strings(YamlNode? node)
		=> node is YamlSequenceNode list
			? list.Children.OfType<YamlScalarNode>().Select(s => s.Value?.Trim() ?? "").Where(s => s.Length > 0).ToList()
			: [];
}
