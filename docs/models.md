# Models: test results and recommendations

How the language models used by the tool were chosen. The numbers come from runs in October 2026 on photos of the actual trip; prices are DeepSeek's of the same month. Re-run the comparison before relying on it for other photos or newer models.

Test data:

- **92 files** (91 photos, 1 video) from the trip, each checked by eye. Used for tagging and, through their tags, for the vocabulary steps (282 distinct tags and names).
- **1,234 tags** of the site's first run on all 4,200 photos (after mechanical normalization). Used to check that the vocabulary step copes with a full trip in one request.

Local models ran in LM Studio on an AMD Radeon RX 9070 XT (16 GB, Vulkan); `qwen3.6-35b-a3b` did not fit completely and had 34 of 41 layers on the GPU. Times on other GPUs will differ, mostly in proportion to memory bandwidth.

## Tagging photos (`haru-analysis tag`)

Same prompt and settings for every model; local models without reasoning (`reasoning_effort = "none"`) and one request at a time.

| Model | 92 files | Per photo | Tags / names per photo | Notes |
|---|---|---|---|---|
| `qwen3-vl-8b-instruct` (Q4_K_M) | ~3 min | ~2 s | 4.3 / 0.9 | Reliable general tags. Names read off signs are almost always right; names guessed from the general look are often wrong (a park with deer named "nijo castle", a city view named "shibuya"). |
| `qwen3-vl-4b-instruct` (IQ4_XS) | 109 s | ~1.2 s | 5.4 / 0.6 | Faster, noticeably worse: "museum" on 16 photos where there is none, invented temples ("kiyomizu-dera" twice, "koyasan" for Itsukushima), junk names ("torii", "none"), names in Japanese script that cannot become a slug. Still right on sushiro, hatsune miku, himeji castle. |
| `gemma-4-e4b` (Q4_K_M) | ~3 min | ~2 s | 3.9 / 0.1 | Weakest: copies words of the prompt as tags ("dishes and drinks" on 14 photos, "notable objects", "pop-culture genre"), almost never names a place (no Itsukushima, Himeji, Dotonbori), many vague tags (objects, structure, indoors). |
| `qwen3.6-35b-a3b` (IQ3_S, ~16 GB) | 12.4 min | ~8 s | 6.6 / 0.6 | Knows more: "girls und panzer" where the 8B model said "girls' frontline", "551 horai", "hatsune miku" in full. Also more noise ("landmark", "japan trip", "food photography") and some invented names ("ayase nayuta from hololive"). |
| `deepseek-flash` (API) | 54 s with 4 requests in parallel, 10 s with 32 | — | 7.5 / 1.1 | Best: places (dotonbori, nikko toshogu, shinjuku gyoen, shirakawa-go with "gassho-zukuri"), franchises (bang dream with "poppin'party" and "roselia"), dishes (udon, miso ramen, yakisoba pan). Mistakes too: Matsumoto castle on a photo of Himeji, a "puppy" that is an aardvark. ~880 input and ~140 output tokens per photo. |

What the runs taught about the prompt and settings, all built into the tool:

- **No examples in the prompt.** Every example name was copied into unrelated photos: with "hololive" in the prompt every anime photo got "hololive", every night photo "shibuya", every red structure "torii".
- **"Do not" lists do not work on small models.** A stop list in the code removes broad words, incidental objects, seasons and colors after the answer.
- **The minimum number of tags goes into the JSON schema.** Asked in words for 4 to 10 tags, the 8B model often gave one or two; a schema with `minItems` makes the server keep it going.
- **Names come with evidence.** The model says what it saw ("the sign reads dotonbori", "the distinctive torii in the water"), so the vocabulary step can keep what was read and drop what was guessed.
- **One request at a time for a local server.** Two parallel requests gave different answers for 6 of 92 photos between identical runs; one at a time, three runs were identical.
- **No reasoning for tagging.** It multiplies the time per photo and does not improve tags.

## Building the vocabulary (`haru-tags vocab`)

The step has two model tasks. **Finding duplicates** needs the whole list at once, because "sakura" and "cherry blossom" may be far apart; the answer is only the merges and spelling fixes. **Keeping or dropping** each tag needs no neighbours, so it runs in batches.

Finding duplicates, same prompt for every model:

| Model | 282 entries | 1,234 entries | Notes |
|---|---|---|---|
| Claude Opus 5.5 | 14 merges, 11 fixes | 157 merges, 11 fixes, ~3 min | The only one to find every known case: akiba → akihabara, hirosima → hiroshima, ファミマ → familymart, 七咲ナギ → nanasaki nagi. |
| `deepseek-flash`, reasoning on | 18 merges, 5 fixes, 3.4 min | 106 merges, 7 fixes, 3.3 min | Also merged food, dishes and meal; missed akiba. Needs a high `max_tokens`: reasoning takes ~40,000 tokens and the first full-list run was cut off. |
| Claude Sonnet 5.5 | 13 merges, 13 fixes | 101 merges, 6 fixes, ~1 min | Missed a few fixes; once "fixed" a variety into its parent dish. |
| Claude Haiku 5.5 | 15 merges, 6 fixes | 158 merges, 7 fixes | Merges eagerly but put two entries into the answer that were not in the list. |

No model merged related but different things (shrine and temple, ramen and soba, castle and palace). Claude was tested as a coding agent, not through the API; through the API it would need Anthropic's OpenAI-compatible endpoint.

Keeping or dropping (`deepseek-flash`, 259 tags in batches of 150, 2.4 min): with the rule "when unsure, keep" the model kept unidentified text read off packaging ("155", "aaregi"); with the added rule "unidentified text is noise" it kept 223 tags and dropped 36 (furniture, things lying around, unreadable text, place names guessed from the look). A few doubtful tags remain for the review.

Local models were not good enough for either task:

| Model | Result |
|---|---|
| `qwen3.8-27b` (IQ2_S) | Kept almost everything as is, copied examples from the prompt, dropped useful tags and kept junk depending on the wording. |
| `qwen3.6-35b-a3b` (IQ3_S), no reasoning | Correct format, but merged different things into broad categories (dessert, snack, bun, noodles into "food") and dropped specific tags (airplane, fossil, kushikatsu). |
| `qwen3.6-35b-a3b` (IQ3_S), reasoning on | ~10 min for 282 entries; merged even more into categories (guitar and CDs into "music"). On the first prompt it broke the answer format. |

## Translations

The final tags are translated through the site's `/v1/tags/completion`, which already has a carefully written prompt for translations and search aliases and uses the model the backend is configured with (a cloud API or a local LM Studio server). Different tags getting the same label in a language (last time "магазин" for both shop and store) are caught by a check in the tool and shown in the review.

For comparison, translating 223 tags in batches of 50 directly took 50 s with `deepseek-flash` without reasoning (2 tags with the same label) and 7 minutes with `qwen3.6-35b-a3b` (5 tags with the same label).

## Cost of a 4,200-photo trip

DeepSeek prices per million tokens, October 2026: input $0.15 off-peak / $0.30 peak (cache hits much less), output $0.60 / $1.20.

| Step | Tokens | Off-peak | Peak |
|---|---|---|---|
| Tagging with `deepseek-flash` | ~3.7M input, ~0.6M output | ~$0.90 | ~$1.80 |
| Finding duplicates | ~7k input, ~41k output | ~$0.03 | ~$0.05 |
| Keeping or dropping | ~30k input, ~115k output | ~$0.07 | ~$0.15 |
| Translations | through the backend's model | depends on it | |

With local tagging the whole run costs about $0.10 to $0.20; with tagging by DeepSeek about $1 to $2.
