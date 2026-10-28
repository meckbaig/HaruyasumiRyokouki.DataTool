using HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

namespace HaruyasumiRyokouki.DataTool.Tags.Tests;

public sealed class VocabularyTests
{
	private static List<VocabEntry> Entries() => VocabEntry.Build([
		new RawTag("a", "mountain", "tag", null),
		new RawTag("b", "mountains", "tag", null),
		new RawTag("a", "plaques", "tag", null),
		new RawTag("c", "senso-ji", "landmark", "the sign reads senso-ji"),
		new RawTag("c", "tokyo", "tag", null),
		new RawTag("d", "tokyo", "place", "the skyline looks like tokyo"),
		new RawTag("d", "cherry blossom", "tag", null),
		new RawTag("e", "sakura", "tag", null),
	]);

	[Fact]
	public void Entries_normalize_tags_but_keep_names_and_join_a_name_with_the_same_tag()
	{
		var entries = Entries().ToDictionary(e => e.Text);

		Assert.Equal(["a", "b"], entries["mountain"].Keys.Order());
		Assert.Equal(["mountain", "mountains"], entries["mountain"].RawTags.Order());
		Assert.Equal("landmark", entries["senso-ji"].Kind);     // not "senso ji"
		Assert.Equal("place", entries["tokyo"].Kind);
		Assert.Equal(["c", "d"], entries["tokyo"].Keys.Order());
		Assert.Equal(["the skyline looks like tokyo"], entries["tokyo"].Evidence);
	}

	[Fact]
	public void Answer_is_applied_with_tolerance_for_the_model_s_mistakes()
	{
		var entries = Entries();
		var warnings = new List<string>();
		var answer = new Dedupe.Answer(
			[
				new Dedupe.Merge("Cherry Blossom", ["cherry blossom", "sakura"]),
				new Dedupe.Merge("plaque", ["plaque", "prayer board"]),        // singular of an entry, and an invented one
				new Dedupe.Merge("blossom", ["sakura"])                        // second group for the same entry
			],
			[new Dedupe.Fix("senso-ji", "sensoji")]);

		var nameOf = Dedupe.Apply(entries, answer, warnings).ToDictionary(kv => kv.Key.Text, kv => kv.Value);

		Assert.Equal("cherry blossom", nameOf["cherry blossom"]);
		Assert.Equal("cherry blossom", nameOf["sakura"]);
		Assert.Equal("plaque", nameOf["plaques"]);
		Assert.Equal("sensoji", nameOf["senso-ji"]);
		Assert.Equal("mountain", nameOf["mountain"]);                   // not mentioned: keeps its name
		Assert.Contains(warnings, w => w.Contains("prayer board"));
		Assert.Contains(warnings, w => w.Contains("'sakura' is in more than one group"));
		Assert.Equal(2, warnings.Count);
	}

	[Fact]
	public void Message_lists_entries_with_counts_and_each_hint_pair_once()
	{
		var entries = Entries();
		var hints = new Dictionary<string, List<(string Tag, double Cosine)>>
		{
			["cherry blossom"] = [("sakura", 0.97)],
			["sakura"] = [("cherry blossom", 0.97)]
		};

		string message = Dedupe.BuildMessage(entries, hints);

		Assert.Contains("mountain (2)\n", message);
		Assert.Contains("senso-ji (1)\n", message);
		Assert.Single(message.Split('\n'), line => line == "cherry blossom ~ sakura");
	}
}
