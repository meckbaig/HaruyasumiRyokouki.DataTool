using System.Text.Json;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Deciding for every tag whether a visitor could search for it. Needs no other tags, so it runs
/// in batches, which keeps every answer far below the output limits.
/// </summary>
internal static class Filter
{
	public const int BatchSize = 150;

	// The rules follow what the owner deleted in a review of the model's earlier choices: tags that are
	// true about the photo but not what it is about, and names of one particular place, which belong in
	// the photo's title that the owner writes. Examples of words are left out on purpose: the model
	// copies them.
	public const string Prompt = """
		You review the search vocabulary of a travel diary site about a trip to Japan. A vision model tagged every photo; duplicates are already merged. Each input item is a tag with the number of photos; items that are proper names also carry their kind and what the vision model saw as evidence.

		Visitors search the diary for what a photo is about: the sight, the place, the dish, the animal, the event, the activity. A tag is worth keeping when someone looking for exactly that would be glad to find these photos. Many tags the vision model gives are true about the photo but are not what it is about; those are noise in search. The owner adds missing tags while editing photos, so a wrong tag costs more than a missing one.

		A good tag recurs: it can apply to many photos of a whole trip and still narrows a search. The name of one particular thing is not a tag but the title of its photo, and the owner writes the titles: a particular temple, shrine, castle, bridge, tower, park, garden, station, museum, hotel, shop, café, restaurant or product line of one shop. Its photos are found by their kind instead, which the vision model tagged as well. Judge whether a tag can recur by what it names, not by its number of photos here: this list may come from a small part of the trip.

		Decide for every item:

		- "keep": the subject or the setting of a photo that a visitor could look for: a dish, a food or a drink, a kind of place, building or structure, a landscape or a natural feature, an animal, a vehicle, a kind of artwork or exhibit, the theme of a museum or an exhibition, an activity, a festival or event, a pop-culture genre or franchise the photo is about, a season or weather that shows. A category that covers many photos is still useful, because visitors browse by it. A kind of thing is not too general when such a thing can be what a photo shows.
		- "drop":
		  - a word that would fit almost any travel photo and names no kind of thing: buildings or a town in general, people in general, eating as such, celebrating, religion or history as such;
		  - an ingredient or topping that is only a part of a dish: keep the dish, drop what is in it. A food that is the subject by itself, such as a fruit or a dessert, is kept;
		  - an object that is in the frame but not what the photo is about: something a person wears or carries along, things lying on a table or a shelf, decorations, musical instruments, signs;
		  - a word that only describes how the photo looks, its mood, or where the camera is (inside or outside);
		  - the name of one particular place, venue or landmark (see above): it is a title, not a tag. Names that recur are kept: a city, prefecture, region, island or district, which many photos of the trip share;
		  - a brand, shop or company name, unless it is a chain or a brand that recurs across a trip as the subject of photos, such as a restaurant chain or a convenience store chain. A single shop, café or restaurant is a title. A name only printed on something in the background or on an incidental object is noise. Text you cannot identify is always noise;
		  - a character or franchise that only appears on a poster, a sign or something in the background. A franchise, a series or a widely known character is kept when the photo is about an item or a place featuring it: a figure, merchandise, a cup or a package the photo shows, an exhibition, a themed shop or café. A character known only from this one poster or package is not a tag;
		  - a city, region or district name whose evidence is only a guess from the general look rather than text in the photo or a one-of-a-kind sight.

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
