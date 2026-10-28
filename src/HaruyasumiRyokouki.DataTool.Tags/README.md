# haru-tags

The part of the data tool that talks to the site: it turns the machine tags of `haru-analysis` into a reviewed vocabulary and imports it. It used to be `TagsTool`, a one-off migration tool; the CSV round trip through Excel, the translation through the backend's `/v1/tags/completion` and the hand-made `media-map.json` are gone.

```
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- status --workspace my-trip
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

## How it works with the backend

Tags go only through the HTTP API, so the backend's own validation applies. Tags are added to files with `POST /v1/tags/{id}/media`, which only adds; `PATCH /v1/media` replaces a file's whole tag set and would wipe tags set by hand, so it is not used. The one table without an API, `media_embeddings`, is written to Postgres directly; `media_files` is only read, to link local files to their ids.

Machine tags are cleaned without a language model first: case and separators, and a plural merged into its singular only when both forms occur (`mountain` + `mountains`, while a lone `glasses` stays). Synonym candidates come from image vectors: tags whose photos look alike but almost never appear on the same photo. On the first run about two thirds of such candidates were right, so they are passed to the language model as hints, never merged by themselves.
