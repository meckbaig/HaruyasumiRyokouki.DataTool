# haru-tags

The part of the data tool that talks to the site: it turns the machine tags of `haru-analysis` into a reviewed vocabulary and imports it. It used to be `TagsTool`, a one-off migration tool; the CSV round trip through Excel, the translation through the backend's `/v1/tags/completion` and the hand-made `media-map.json` are gone.

```
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- status --workspace my-trip
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- vocab  --workspace my-trip
```

Normally `haru-data run` starts the steps. A typo in an option is an error, not a silently ignored flag.

## Settings and secrets

The workspace's `haru-data.toml` names the backend; credentials only come from the environment or `.env` in the workspace:

```toml
[backend]
api = "https://haru.example"   # or HARU_API; there is no default, so nothing is written to a server nobody chose
```

```
HARU_USER=admin                # Basic auth of the site's admin
HARU_PASSWORD=...
HARU_CONNECTION=Host=...;Database=...;Username=...;Password=...   # only for writing media_embeddings
```

## Building the vocabulary

`vocab` turns the machine tags of all photos into a vocabulary in `work.sqlite` (tables in [docs/formats.md](../../docs/formats.md)). It runs in three parts, chosen after testing several models (see [docs/models.md](../../docs/models.md)):

1. **Mechanical clean-up**, no model: case and separators, a plural merged into its singular when both occur. Names from the vision model keep their spelling.
2. **Finding duplicates**: one request with the whole list, because "sakura" and "cherry blossom" can be far apart. The model answers only with merges and spelling fixes; everything it does not mention stays. Pairs of tags whose photos look alike but rarely appear together are passed as hints. The code checks the answer: an entry the model invents is ignored, a plural or singular form is matched to the entry, an entry in two groups stays in the first.
3. **Keeping or dropping**, in batches of 150 tags: the model decides for each tag whether a visitor could search for it, with names judged by the evidence the vision model gave. A tag without a decision is kept.

The model comes from `[usage] vocabulary`; it has to be a strong one, local models were not good enough. For DeepSeek:

```toml
[providers.deepseek]
type = "openai"
base_url = "https://api.deepseek.com"
model = "deepseek-flash"
api_key_env = "DEEPSEEK_API_KEY"   # the key goes into .env
json_mode = "object"               # DeepSeek takes no JSON schema; the schema is then put into the prompt
max_tokens = 200000                # reasoning takes tens of thousands of tokens; a lower limit cuts the answer off

[usage]
vocabulary = "deepseek"
```

Every answer is cached in `cache/llm/` by a hash of the request, so a run stopped by an error or Ctrl+C does not pay again. An answer that cannot be parsed, or that is clearly incomplete, is asked once more and never cached. The report counts requests and tokens.

## How it works with the backend

Tags go only through the HTTP API, so the backend's own validation applies. Tags are added to files with `POST /v1/tags/{id}/media`, which only adds; `PATCH /v1/media` replaces a file's whole tag set and would wipe tags set by hand, so it is not used. The one table without an API, `media_embeddings`, is written to Postgres directly; `media_files` is only read, to link local files to their ids.

Machine tags are cleaned without a language model first: case and separators, and a plural merged into its singular only when both forms occur (`mountain` + `mountains`, while a lone `glasses` stays). Synonym candidates come from image vectors: tags whose photos look alike but almost never appear on the same photo. On the first run about two thirds of such candidates were right, so they are passed to the language model as hints, never merged by themselves.
