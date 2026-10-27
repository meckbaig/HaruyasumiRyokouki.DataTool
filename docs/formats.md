# Data formats

What the apps of the data tool exchange: the workspace folder, step reports and `work.sqlite`. Each app owns some tables and is the only one that writes them; the others only read. When a table changes, this file changes in the same commit.

## Workspace folder

```
my-trip/
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
  "message": "model google/siglip2-base-patch16-256 on cpu"
}
```

`status` is `error` when at least one error was recorded, even if most files succeeded: the orchestrator stops, and running the step again retries only the failed files.

## work.sqlite

### schema_versions (shared)

| column | |
|---|---|
| `owner` | app that owns a set of tables: `analysis`, `tags` |
| `version` | schema version of that owner's tables |

Each app creates its tables on first use and writes only its own row. An app refuses to open a workspace whose version for its tables differs from its own; there are no migrations, a new workspace is created instead.

### Owner: analysis (version 1)

**media** — one row per file of the source folder.

| column | |
|---|---|
| `key` | primary key: file name without the last extension, lower case. The same value is derived from `media_files.file_name` on the site |
| `path` | absolute path of the file |
| `type` | `image` or `video` |
| `size`, `mtime_ns` | used to detect changed files |
| `missing` | 1 if the file was not found by the last scan; such rows are skipped by all steps |
| `frame_status`, `frame_error` | `pending` / `done` / `error`; NULL for images |
| `embed_status`, `embed_error` | `pending` / `done` / `error` |

**embeddings** — one vector per file.

| column | |
|---|---|
| `key` | primary key, references `media.key` |
| `model` | Hugging Face id of the encoder; one model per workspace |
| `dim` | vector length (768 for the site's model) |
| `vector` | `dim` little-endian float32, L2-normalized |
