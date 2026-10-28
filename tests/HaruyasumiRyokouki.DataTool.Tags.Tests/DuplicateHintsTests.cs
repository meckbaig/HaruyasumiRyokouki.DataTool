namespace HaruyasumiRyokouki.DataTool.Tags.Tests;

public sealed class DuplicateHintsTests
{
	private static float[] Unit(params float[] values)
	{
		double norm = Math.Sqrt(values.Sum(v => (double)v * v));
		return values.Select(v => (float)(v / norm)).ToArray();
	}

	private static TagGroup Group(string name, params string[] keys)
	{
		var group = new TagGroup(name);
		group.Keys.UnionWith(keys);
		return group;
	}

	[Fact]
	public void Similar_photos_that_never_share_a_tag_are_candidates()
	{
		var vectors = new Dictionary<string, float[]>();
		for (int i = 0; i < 10; i++)
			vectors["p" + i] = Unit(1, 0.01f * i, 0);   // all alike
		for (int i = 0; i < 5; i++)
			vectors["q" + i] = Unit(0, 0, 1);            // something else

		var city = Group("city", "p0", "p1", "p2", "p3", "p4");
		var cityscape = Group("cityscape", "p5", "p6", "p7", "p8", "p9");
		var bridge = Group("bridge", "p0", "p1", "p2", "p5", "p6");   // alike, but shares photos with both
		var forest = Group("forest", "q0", "q1", "q2", "q3", "q4");   // different photos

		var hints = DuplicateHints.Build([city, cityscape, bridge, forest], vectors);

		Assert.Equal(["cityscape"], hints["city"].Select(h => h.Tag));
		Assert.Equal(["city"], hints["cityscape"].Select(h => h.Tag));
		Assert.False(hints.ContainsKey("bridge"));
		Assert.False(hints.ContainsKey("forest"));
	}

	[Fact]
	public void Tags_on_too_few_photos_or_without_vectors_are_skipped()
	{
		var vectors = new Dictionary<string, float[]> { ["a"] = Unit(1, 0), ["b"] = Unit(1, 0) };
		var hints = DuplicateHints.Build([Group("x", "a"), Group("y", "b"), Group("z", "c", "d", "e", "f", "g")], vectors);
		Assert.Empty(hints);
	}
}
