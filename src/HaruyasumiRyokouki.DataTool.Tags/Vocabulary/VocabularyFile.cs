using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// vocabulary.yaml: the vocabulary for a person to review and edit. One tag per entry, short
/// lists on one line, the problems the checks found as comments at the top.
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
		public string Ru { get; set; } = "";
		public string Ja { get; set; } = "";
		public string Kind { get; set; } = "tag";
		public Dictionary<string, List<string>> Aliases { get; set; } = [];
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
		# Tag vocabulary for review.
		#
		# Every entry under "tags" becomes a tag on the site. You can change labels and aliases,
		# delete an entry, move "sources" from one tag to another, or move an entry from "dropped"
		# back to "tags" (give it slug, en, ru and ja). "sources" are the machine tags of the photos:
		# a photo gets the tag if it has any of them. "existing: true" means the tag is already on
		# the site and will not be created again.
		#
		# Check the file with 'haru-tags review --check', accept it with 'haru-tags review --approve'.
		""";

	public static VocabularyFile From(IEnumerable<VocabTag> tags) => new()
	{
		Tags = tags.Where(t => t.Keep).Select(t => new Entry
		{
			Slug = t.Slug,
			En = t.Labels.GetValueOrDefault("en", t.Name),
			Ru = t.Labels.GetValueOrDefault("ru", ""),
			Ja = t.Labels.GetValueOrDefault("ja", ""),
			Kind = t.Kind,
			Aliases = Translator.Languages.ToDictionary(l => l, l => t.Aliases.GetValueOrDefault(l) ?? []),
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

	public void Write(string path, IEnumerable<Checks.Problem> problems)
	{
		var text = new StringBuilder(Header).Append('\n');
		var list = problems.ToList();
		if (list.Count > 0)
		{
			text.Append("#\n# Problems to look at:\n");
			foreach (var problem in list)
				text.Append("#   ").Append(problem.Tag is null ? "" : $"{problem.Tag}: ").Append(problem.Message.Replace('\n', ' ')).Append('\n');
		}
		text.Append('\n').Append(Serializer.Serialize(this));
		File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
	}

	public static VocabularyFile Read(string path) => Deserializer.Deserialize<VocabularyFile>(File.ReadAllText(path)) ?? new VocabularyFile();

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
