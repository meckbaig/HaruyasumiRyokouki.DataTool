# Data formats

What the apps of the data tool exchange: the workspace folder, step reports and `work.sqlite`. Each app owns some tables and is the only one that writes them; the others only read. When a table changes, this file changes in the same commit.

## Workspace folder

```
my-trip/
  vocabulary.yaml  the vocabulary for review (haru-tags vocab)
  haru-data.toml   settings, no secrets
  .env             secrets (optional, environment variables work too)
  work.sqlite      state of all steps
  frames/          one frame per video, <key>.jpg (haru-analysis frames)
  reports/         <step>.json, one per step, overwritten on every run
```

## Step reports

Every step writes `reports/<step>.json` and exits with the matching code:

```jsonc
{
  "step": "embed",
  "status": "ok",                       // ok -> exit 0, error -> exit 1, needs_review -> exit 2
  "startedAt": "2026-10-28T10:00:00+00:00",
  "finishedAt": "2026-10-28T10:05:12+00:00",
  "counts": { "done": 120, "skipped": 4080 },   // step-specific
  "errors": [ { "key": "img_1", "message": "cannot identify image file" } ],
  "warnings": [ { "key": "img_2", "message": "not in the storage; run upload" } ],   // only when there are any
  "message": "model google/siglip2-base-patch16-256 on cpu"
}
```

Warnings are what a person should look at without stopping the next steps, such as files the site did not import. `status` is `error` when at least one error was recorded, even if most files succeeded: the orchestrator stops, and running the step again retries only the failed files.

## work.sqlite

### schema_versions (shared)

| column | |
|---|---|
| `owner` | app that owns a set of tables: `analysis`, `tags` |
| `version` | schema version of that owner's tables |

Each app creates its tables on first use and writes only its own row. An app refuses to open a workspace whose version for its tables differs from its own; there are no migrations, a new workspace is created instead.

### Owner: analysis (version 3)

**media** — one row per file of the source folder.

| column | |
|---|---|
| `key` | primary key: file name without the last extension, lower case. The same value is derived from `media_files.file_name` on the site |
| `path` | absolute path of the file |
| `type` | `image` or `video` |
| `size`, `mtime_ns` | used to detect changed files |
| `missing` | 1 if the file was not found by the last scan; such rows are skipped by all steps |
| `frame_status`, `frame_error` | `pending` / `done` / `error`; NULL for images |
| `tag_status`, `tag_error` | `pending` / `done` / `error` |
| `tag_description` | the vision model's one or two sentences about the photo; context for the vocabulary step |
| `embed_status`, `embed_error` | `pending` / `done` / `error` |

**raw_tags** — what the vision model found in a file, cleaned: lower case, single spaces, no duplicates, stop words removed.

| column | |
|---|---|
| `key` | references `media.key` |
| `tag` | a general tag or a proper name; primary key is (`key`, `tag`) |
| `kind` | `tag` for a general tag; for a name `landmark`, `place`, `brand`, `franchise`, `character`, or `name` if the model gave no valid kind |
| `evidence` | for a name, what the model saw in the photo ("the sign reads ..."); NULL for tags |

**embeddings** — one vector per file.

| column | |
|---|---|
| `key` | primary key, references `media.key` |
| `model` | Hugging Face id of the encoder; one model per workspace |
| `dim` | vector length (768 for the site's model) |
| `vector` | `dim` little-endian float32, L2-normalized |

### Owner: tags (version 3)

Written by `haru-tags vocab`, which replaces their content on every run (the model answers come from `cache/llm/`, so a rerun is cheap), and by `haru-tags review --approve`, which replaces them with the reviewed `vocabulary.yaml`. After the approval the tables hold exactly the file: kept tags only, named by their English label.

**vocab_tags** — the vocabulary: one row per final tag.

| column | |
|---|---|
| `tag` | primary key: English display name chosen by the model, lower case; the base of the slug |
| `kind` | `tag` for a general tag; for a name `landmark`, `place`, `brand`, `franchise`, `character` or `name` |
| `photos` | number of files with any of its sources |
| `action` | `keep` or `drop` |
| `reason` | why the model dropped it; NULL when kept |
| `slug` | slug for the site, from the English name; NULL for dropped tags, empty when the name has no Latin letters |
| `label_en`, `label_ru`, `label_ja` | main label per language; NULL when missing |
| `existing_id` | id of the same tag on the site; such a tag is not created again |

**vocab_aliases** — search aliases of a vocabulary tag.

| column | |
|---|---|
| `tag` | references `vocab_tags.tag` |
| `language` | `en`, `ru` or `ja` |
| `text` | the alias; primary key is (`tag`, `language`, `text`) |

**vocab_sources** — which raw tags make up a vocabulary tag; the link from a photo to its final tags.

| column | |
|---|---|
| `raw_tag` | primary key: a `raw_tags.tag` value as the analysis step stored it |
| `tag` | references `vocab_tags.tag` |

**vocab_problems** — what the review has to look at.

| column | |
|---|---|
| `kind` | `model` (an answer the code had to correct), `translation` (the backend could not translate), `slug`, `duplicate text` (a text twice in one tag, which the backend rejects), `same label` (two tags with one label in a language) |
| `subject` | the tag it is about, or NULL |
| `message` | what is wrong |

**vocab_approval** — at most one row: the vocabulary was approved. Steps that write to the site require it and the hash to match the current `vocabulary.yaml`, so an edit after the approval needs a new one.

| column | |
|---|---|
| `file_sha256` | SHA-256 of the approved `vocabulary.yaml` |
| `approved_at` | time of the approval, UTC |

### Owner: site (version 1)

Written by the haru-tags steps that write to the site: what the site calls the tags and files of this workspace.

**site_tags** — site ids of the approved tags, written by `push-tags`.

| column | |
|---|---|
| `slug` | primary key: `vocab_tags.slug` |
| `tag_id` | id of the tag on the site |
| `created` | 1 when `push-tags` created it, 0 when the site already had it |

**site_media** — site ids of the files, replaced by every `sync`; `assign` reads them.

| column | |
|---|---|
| `key` | primary key: `media.key` |
| `media_file_id` | `media_files.id` of the site |
| `file_name` | the name on the site, often with another extension (`.webp`) |
| `type` | `Image` or `Video`, as the site stores it |

### Owner: run (version 1)

Written by `haru-data run`. The vocabulary step and the steps that write to the site run again only when their input changed since they last finished; the input is summed up as a fingerprint of the tables of the other owners.

**run_steps** — one row per such step after it finished without errors.

| column | |
|---|---|
| `step` | primary key: step name |
| `fingerprint` | short hash of the input: the machine tags for `vocab`, the approved file for `push-tags`, the file keys for `upload` and `sync`, the approved file and the site ids for `assign`, the vectors and the site ids for `push-embeddings` |
| `finished_at` | time it finished, UTC |
