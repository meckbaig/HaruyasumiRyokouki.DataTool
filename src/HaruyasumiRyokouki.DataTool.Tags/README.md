# haru-tags

The part of the data tool that talks to the site: it turns the machine tags of `haru-analysis` into a reviewed vocabulary, uploads the files and imports the tags and image vectors. It used to be `TagsTool`, a one-off migration tool; the CSV round trip through Excel and the hand-made `media-map.json` are gone.

```
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- status --workspace my-trip
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- vocab  --workspace my-trip [--estimate]
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- review --workspace my-trip [--check | --approve | --ui [--no-browser]]
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- translate --workspace my-trip
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- push-tags --workspace my-trip [--yes]
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- upload --workspace my-trip [--yes]
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- sync --workspace my-trip [--yes]
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- assign --workspace my-trip [--yes]
dotnet run --project src/HaruyasumiRyokouki.DataTool.Tags -- push-embeddings --workspace my-trip [--yes] [--dry-run] [--replace]
```

Normally `haru-data run` starts the steps. A typo in an option is an error, not a silently ignored flag.

## Settings and secrets

The workspace's `haru-data.toml` names the backend; credentials only come from the environment or `.env` in the workspace:

```toml
[backend]
api = "https://haru.example"   # or HARU_API; there is no default, so nothing is written to a server nobody chose

[storage]                      # where the site imports media from: the backend's MediaStorage
type = "local"                 # local | webdav
path = "D:/Media"              # local: the backend's LocalStorageProvider:Path, or a network share of it
concurrency = 4                # parallel uploads
# type = "webdav"              # if the backend keeps media in Nextcloud
# url = "https://cloud.example/remote.php/dav/files/user/media/"   # or HARU_STORAGE_URL
```

```
HARU_USER=admin                # Basic auth of the site's admin
HARU_PASSWORD=...
HARU_STORAGE_USER=...          # only for a WebDAV storage
HARU_STORAGE_PASSWORD=...
HARU_CONNECTION=Host=...;Database=...;Username=...;Password=...   # reading media_files, writing media_embeddings
```

## Building the vocabulary

`vocab --estimate` prints how many requests and roughly how many input tokens a run would send, and sends nothing; `haru-data run` shows it and asks before the paid run. `vocab` turns the machine tags of all photos into an English vocabulary in `work.sqlite` (tables in [docs/formats.md](../../docs/formats.md)); nothing is translated before the review, so a tag the person deletes costs nothing. It runs in three parts, chosen after testing several models (see [docs/models.md](../../docs/models.md)):

1. **Mechanical clean-up**, no model: case and separators, a plural merged into its singular when both occur. Names from the vision model keep their spelling.
2. **Finding duplicates**: one request with the whole list, because "sakura" and "cherry blossom" can be far apart. The model answers only with merges and spelling fixes; everything it does not mention stays. Pairs of tags whose photos look alike but rarely appear together are passed as hints. The code checks the answer: an entry the model invents is ignored, a plural or singular form is matched to the entry, an entry in two groups stays in the first.
3. **Keeping or dropping**, in batches of 150 tags: the model decides for each tag whether a visitor could search for it, with names judged by the evidence the vision model gave. A tag without a decision is kept.
4. **Matching with the site**: a tag that is already on the site (same slug, or its name is a label or an alias there) is marked `existing` and is not created or translated again. The variants merged into a tag ("sakura" for "cherry blossom") become its English aliases.
5. **Checks** without a model: a slug for every tag, no slug twice, no text twice in one tag.

The result goes to `work.sqlite` and to `vocabulary.yaml` in the workspace, with the problems listed at the top of the file.

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

The backend must be running: its tags are matched; its address is `[backend] api` (or `HARU_API`), the admin's login goes into `HARU_USER` and `HARU_PASSWORD` in `.env`. The step checks the connection before it asks the model anything.

Every answer is cached in `cache/llm/` by a hash of the request, so a run stopped by an error or Ctrl+C does not pay again. An answer that cannot be parsed, or that is clearly incomplete, is asked once more and never cached. The report counts requests and tokens.

## Reviewing the vocabulary

`vocabulary.yaml` is the person's part: one English tag per entry with its aliases, `sources` (the machine tags that put it on a photo) and the number of photos, the tags the model dropped in a separate list with the reason, and the problems the checks found at the top. Names and aliases can be changed, tags deleted, sources moved between tags, and dropped tags moved back. A photo can be taken off a tag: its key goes into the tag's `excluded` list, and `assign` does not put the tag on it although the photo has one of the sources.

`review --ui` (what `haru-data review` starts) shows the same on a page in the browser: a card per tag with its photos, the problems while editing, Save and Approve. A small web server on `127.0.0.1` serves it with a random token in the address, writes the edits into the same YAML file and approves through the same code as `--approve`; it stops after the approval or on Ctrl+C. Photos in formats a browser cannot show (HEIC, TIFF) are listed by name; videos show their frame.

`review` works on the file that waits for a person: `vocabulary.yaml` until it is approved, then `translations.yaml` while the translations wait (below). Its output starts with the name of the file.

- `review` prints a summary (new and existing tags, machine tags not used, files left without any tag, the most used tags) and exits with `2` while the file waits for approval, so `haru-data run` stops here.
- `review --check` checks the file and names the line of every problem. Errors are what the site would reject or what would break the import: an empty or malformed slug, a missing label, a text twice in one tag, an alias equal to a label, the same slug or English label twice, a source in two tags, a source that is no machine tag at all. Two tags with the same Russian or Japanese label are a warning: visitors cannot tell them apart, but the site accepts it.
- `review --approve` checks the file and, without errors, approves it. The hash of the approved file is stored; editing the file afterwards takes the approval back, and running `vocab` again does too. A new approval of `vocabulary.yaml` starts the translations again.

A deleted tag is simply not created or translated, and its sources put no tag on the photos; the summary shows how many machine tags end up unused.

## Translating

`translate` runs after the vocabulary is approved. Every new tag of it (not the ones the site has) goes to the site's own `POST /v1/tags/completion`: the same prompt the admin uses for a single tag, with whatever model the backend is configured with. The approved English name stays the English label; the backend's other English wording becomes an alias. Answers are cached in `cache/llm/`, so translating again costs nothing.

```toml
[vocabulary]
translate_concurrency = 4          # parallel requests to /v1/tags/completion; a free tier of the backend's model allows few per minute
```

Then the checks of `review --check` run on the translations: the backend's rules for a new tag (one label per language, no text twice in one tag, no alias equal to a label) and no two tags with the same label in a language. Last time the old tool produced 56 such pairs, like "магазин" for both shop and store; names often get the same Latin spelling in every language, which the backend does not accept.

The result goes to `translations.yaml`, with the problems at the top. Without problems the translations are approved at once and the next steps go on. With problems the step exits with `2` and the file waits for `review --check` and `review --approve`, like the vocabulary. A waiting file is never overwritten by `translate`; tags are added and deleted only in `vocabulary.yaml`.

## Writing to the site

Each of these steps prints where it is going to write (API host, storage, database host and name, never credentials) and asks before writing; `--yes` skips the question but still prints the target. A declined question ends the step with exit code `2`. Every step can be run again: it does only what is not done yet.

1. `push-tags` needs the approved vocabulary and its approved translations, both unchanged since the approval. It creates the tags the site does not have and stores the id of every tag; a tag the site already has, matched by `vocab` or by slug, is not created again. A tag the site refuses is an error with the site's answer, the other tags are still created, and the step ends with exit code `1` so the next steps do not run.
2. `upload` copies the files of the source folder into the storage. A file whose key the storage already has with any extension is skipped: after a sync the site keeps a photo as `.webp` and deletes the original. The modification time goes along (on WebDAV through the `X-OC-MTime` header of Nextcloud and ownCloud), because the site dates a file without a date in its name by that time. A failed file is tried three times.
3. `sync` calls `PUT /v1/media/sync` and waits until the site has imported the new files, which can take a long time for videos; while another sync runs, it waits and asks again. Then it reads `media_files` and stores the site id of every local file by key. A file the site did not import is a warning with the likely reason (not uploaded, or skipped by the site: no date, failed conversion); two site files with one key are an error.
4. `assign` adds every approved tag to the site files with one of its sources, 500 files per request. It reports how many links were added and how many were already there.
5. `push-embeddings` checks the vectors (one model, length 768, unit norm), matches them to `media_files` by key, photos and videos alike, and writes them into `media_embeddings` in one transaction with a binary `COPY`. A file that has a vector on the site already keeps it, so the vectors of earlier trips stay as they were computed; `--replace` overwrites them. It reports the site files left without a vector: they never show up among similar files. `--dry-run` stops before writing. The site loads its similarity index at start, so restart the backend afterwards.

## How it works with the backend

Tags go only through the HTTP API, so the backend's own validation applies. Tags are added to files with `POST /v1/tags/{id}/media`, which only adds; `PATCH /v1/media` replaces a file's whole tag set and would wipe tags set by hand, so it is not used. The one table without an API, `media_embeddings`, is written to Postgres directly; `media_files` is only read, to link local files to their ids.

Machine tags are cleaned without a language model first: case and separators, and a plural merged into its singular only when both forms occur (`mountain` + `mountains`, while a lone `glasses` stays). Synonym candidates come from image vectors: tags whose photos look alike but almost never appear on the same photo. On the first run about two thirds of such candidates were right, so they are passed to the language model as hints, never merged by themselves.
