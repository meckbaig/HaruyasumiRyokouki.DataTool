using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// vocabulary.yaml: the English vocabulary for a person to review and edit, before anything is
/// translated. One tag per entry, short lists on one line, the problems the checks found as
/// comments at the top.
/// </summary>
internal sealed class VocabularyFile
{
	public List<Entry> Tags { get; set; } = [];
	public List<DroppedEntry> Dropped { get; set; } = [];

	/// <summary>A tag that goes to the site.</summary>
	public sealed class Entry
	{
		public string Slug { get; set; } = "";
		public string En { get; set; } = "";
		public List<string> Aliases { get; set; } = [];
		public string Kind { get; set; } = "tag";
		public List<string> Sources { get; set; } = [];
		public int Photos { get; set; }
		public bool Existing { get; set; }
	}

	/// <summary>A tag the model dropped; moving it to tags brings it back.</summary>
	public sealed class DroppedEntry
	{
		public string Tag { get; set; } = "";
		public string? Reason { get; set; }
		public List<string> Sources { get; set; } = [];
		public int Photos { get; set; }
	}

	private const string Header = """
		# Tag vocabulary for review, in English. The translations come after the approval and only
		# for the tags that are left, so deleting a tag here costs nothing.
		#
		# Every entry under "tags" becomes a tag on the site:
		#   slug     the tag's id on the site: lowercase Latin letters, digits and _
		#   en       the English name visitors see
		#   aliases  other English words a visitor may search for
		#   sources  the machine tags of the photos: a photo gets the tag if it has any of them
		#   photos   how many photos get it
		#   existing true: the site has this tag already, it is not created again
		#
		# Change names and aliases, delete an entry, move sources from one tag to another, or move an
		# entry from "dropped" back to "tags" (give it slug and en).
		#
		# Check the file with 'haru-data review --check', approve it with 'haru-data review --approve'.
		""";

	public static VocabularyFile From(IEnumerable<VocabTag> tags) => new()
	{
		Tags = tags.Where(t => t.Keep).Select(t => new Entry
		{
			Slug = t.Slug,
			En = t.Labels.GetValueOrDefault("en", t.Name),
			Aliases = t.Aliases.GetValueOrDefault("en") ?? [],
			Kind = t.Kind,
			Sources = t.RawTags.Order(StringComparer.Ordinal).ToList(),
			Photos = t.Keys.Count,
			Existing = t.ExistingId is not null
		}).ToList(),
		Dropped = tags.Where(t => !t.Keep).Select(t => new DroppedEntry
		{
			Tag = t.Name,
			Reason = t.Reason,
			Sources = t.RawTags.Order(StringComparer.Ordinal).ToList(),
			Photos = t.Keys.Count
		}).ToList()
	};

	public void Write(string path, IEnumerable<string> problems) => YamlText.Write(path, Header, problems, this);

	public static VocabularyFile Read(string path) => YamlText.Deserializer.Deserialize<VocabularyFile>(File.ReadAllText(path)) ?? new VocabularyFile();
}

/// <summary>
/// translations.yaml: labels and aliases in every language of the new tags of the approved
/// vocabulary, for a review when the checks found problems.
/// </summary>
internal sealed class TranslationsFile
{
	public List<Entry> Tags { get; set; } = [];

	public sealed class Entry
	{
		public string Slug { get; set; } = "";
		public string En { get; set; } = "";
		public string Ru { get; set; } = "";
		public string Ja { get; set; } = "";
		public Dictionary<string, List<string>> Aliases { get; set; } = [];
	}

	private const string Header = """
		# Translations of the new tags, made by the site's AI. Tags the site has already are not here.
		#
		#   en, ru, ja   the name visitors see in each language
		#   aliases      other words a visitor may search for, per language
		#
		# Fix the translations and aliases. Tags are not added or deleted here: that is done in
		# vocabulary.yaml, and editing it starts the translations again.
		#
		# Check the file with 'haru-data review --check', approve it with 'haru-data review --approve'.
		""";

	public static TranslationsFile From(IEnumerable<TranslatedTag> tags) => new()
	{
		Tags = tags.Select(t => new Entry
		{
			Slug = t.Slug,
			En = t.Labels.GetValueOrDefault("en", ""),
			Ru = t.Labels.GetValueOrDefault("ru", ""),
			Ja = t.Labels.GetValueOrDefault("ja", ""),
			Aliases = Translator.Languages.ToDictionary(l => l, l => t.Aliases.GetValueOrDefault(l) ?? [])
		}).ToList()
	};

	/// <summary>The text of the file, with the problems as comments at the top.</summary>
	public string Render(IEnumerable<string> problems) => YamlText.Render(Header, problems, this);
}

/// <summary>Writing of the review files: header, problems as comments, short lists on one line.</summary>
internal static class YamlText
{
	public static void Write(string path, string header, IEnumerable<string> problems, object content)
		=> File.WriteAllText(path, Render(header, problems, content), new UTF8Encoding(false));

	public static string Render(string header, IEnumerable<string> problems, object content)
	{
		var text = new StringBuilder(header).Append('\n');
		var list = problems.ToList();
		if (list.Count > 0)
		{
			text.Append("#\n# Problems to look at:\n");
			foreach (string problem in list)
				text.Append("#   ").Append(problem.Replace('\n', ' ')).Append('\n');
		}
		return text.Append('\n').Append(Serializer.Serialize(content)).ToString();
	}

	private static readonly ISerializer Serializer = new SerializerBuilder()
		.WithNamingConvention(UnderscoredNamingConvention.Instance)
		.WithEventEmitter(next => new ShortListsOnOneLine(next))
		.Build();

	internal static readonly IDeserializer Deserializer = new DeserializerBuilder()
		.WithNamingConvention(UnderscoredNamingConvention.Instance)
		.Build();

	/// <summary>Lists of strings and the aliases map in flow style: "sources: [temple, temples]".</summary>
	private sealed class ShortListsOnOneLine(IEventEmitter next) : ChainedEventEmitter(next)
	{
		public override void Emit(SequenceStartEventInfo eventInfo, IEmitter emitter)
		{
			if (eventInfo.Source.Type == typeof(List<string>))
				eventInfo.Style = SequenceStyle.Flow;
			base.Emit(eventInfo, emitter);
		}

		public override void Emit(MappingStartEventInfo eventInfo, IEmitter emitter)
		{
			if (eventInfo.Source.Type == typeof(Dictionary<string, List<string>>))
				eventInfo.Style = MappingStyle.Flow;
			base.Emit(eventInfo, emitter);
		}
	}
}
