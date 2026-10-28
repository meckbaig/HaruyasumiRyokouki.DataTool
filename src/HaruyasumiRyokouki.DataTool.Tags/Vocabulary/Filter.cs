using System.Text.Json;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Deciding for every tag whether a visitor could search for it. Needs no other tags, so it runs
/// in batches, which keeps every answer far below the output limits.
/// </summary>
internal static class Filter
{
	public const int BatchSize = 150;

	// "When unsure, keep" made the model keep unreadable packaging text; that case is named explicitly.
	public const string Prompt = """
		You review the search vocabulary of a travel diary site about a trip to Japan. A vision model tagged every photo; duplicates are already merged. Each input item is a tag with the number of photos; items that are proper names also carry their kind and what the vision model saw as evidence.

		The owner prefers a rich vocabulary and removes leftovers by hand, so keeping a doubtful tag is better than losing a useful one. When unsure, keep.

		Decide for every item:

		- "keep": a visitor could search for it. Categories are good tags: a kind of food or drink, a kind of place, a landscape, an animal, an activity, a pop-culture genre, a time of day or weather that shows in the photo. A category that covers many photos is still useful, because visitors browse by it.
		- "drop" only when it is clearly useless for search:
		  - a word that would fit almost any photo and says nothing about its content;
		  - an object that is only incidental in a photo, such as furniture or something lying around;
		  - a word that only describes how the photo looks, its mood, or where the camera is (inside or outside);
		  - text or a number read off packaging or signs that you cannot identify as a real place, shop, brand, franchise or character. "When unsure, keep" does not apply here: unidentified text is noise;
		  - a place or landmark name whose evidence is only a guess from the general look rather than text in the photo or a one-of-a-kind sight.

		Give a short "reason" for every drop. Return JSON only: one decision per input item, tags exactly as written in the input.
		""";

	/// <summary>The model's answer for one batch.</summary>
	public sealed record Answer(List<Decision> Decisions);

	/// <summary>Keep or drop one tag.</summary>
	public sealed record Decision(string Tag, string Action, string Reason);

	/// <summary>A tag of the batch, as the model sees it.</summary>
	public sealed record Item(string Tag, int Photos, List<NameInfo>? Names);

	/// <summary>A name merged into the tag, with the vision model's evidence.</summary>
	public sealed record NameInfo(string AsWritten, string Kind, List<string> Evidence);

	public static string BuildMessage(IEnumerable<Item> batch) => JsonSerializer.Serialize(batch, JsonSerializerOptions.Web);
}
