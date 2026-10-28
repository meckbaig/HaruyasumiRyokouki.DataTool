namespace HaruyasumiRyokouki.DataTool.Tags.Tests;

public sealed class TagNormalizerTests
{
	[Theory]
	[InlineData("Cherry_Blossoms", "cherry blossoms")]
	[InlineData("  night--market ", "night market")]
	[InlineData("tokyo  skytree", "tokyo skytree")]
	[InlineData("___", "")]
	public void Normalize(string raw, string expected) => Assert.Equal(expected, TagNormalizer.Normalize(raw));

	[Theory]
	[InlineData("mountains", "mountain")]
	[InlineData("cherries", "cherry")]
	[InlineData("dishes", "dish")]
	[InlineData("glass", "glass")]
	[InlineData("bus", "bus")]
	[InlineData("tea", "tea")]
	public void Singularize(string word, string expected) => Assert.Equal(expected, TagNormalizer.Singularize(word));

	[Theory]
	[InlineData("cherry blossom", "cherry_blossom")]
	[InlineData("mcdonald's", "mcdonald_s")]
	[InlineData("hōō-ji", "hoo_ji")]
	[InlineData("kōchi prefecture", "kochi_prefecture")]
	[InlineData("七咲ナギ", "")]
	public void Slugify(string value, string expected) => Assert.Equal(expected, TagNormalizer.Slugify(value));

	[Fact]
	public void Plural_merges_into_singular_only_when_both_occur()
	{
		var groups = TagNormalizer.Group([
			("mountain", "a"), ("Mountains", "b"), ("mountains", "c"),
			("glasses", "a"),
			("Japan", "a"), ("travel", "b")
		]);

		Assert.Equal(["mountain", "glasses"], groups.Select(g => g.Normalized));
		var mountain = groups[0];
		Assert.Equal(["a", "b", "c"], mountain.Keys.Order());
		Assert.Equal(["Mountains", "mountain", "mountains"], mountain.RawForms.Order(StringComparer.Ordinal));
		Assert.Equal(["mountains"], mountain.MergedForms);
	}
}
