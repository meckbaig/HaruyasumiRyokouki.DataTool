# HaruyasumiRyokouki.DataTool

Tool that fills the [HaruyasumiRyokouki](https://github.com/meckbaig/HaruyasumiRyokouki.Backend) travelogue with data: a folder of trip photos and videos goes in; files in the site's storage, media records with search tags in three languages and image vectors for "similar media" come out. Everything a model can do is done by a model; a person only reviews the tag vocabulary and confirms every write to the site.

How the data flows:

1. **haru-analysis** (Python, runs locally) indexes the folder, takes a frame from every video, asks a vision model for tags and names on every photo and computes SigLIP 2 image vectors. See [src/analysis/README.md](src/analysis/README.md).
2. **haru-tags** (.NET) turns the machine tags into a vocabulary with a cloud model: merges duplicates, drops what nobody would search for, translates through the site's AI. After a person has reviewed it, it creates the tags on the site, uploads the files, lets the site import them and attaches tags and vectors. See [src/HaruyasumiRyokouki.DataTool.Tags/README.md](src/HaruyasumiRyokouki.DataTool.Tags/README.md).
3. **haru-data** (.NET) is the entry point: it creates a workspace, runs the steps in order, stops for the review and for confirmations, and shows where a run stands. See [src/HaruyasumiRyokouki.DataTool/README.md](src/HaruyasumiRyokouki.DataTool/README.md).

All state of a trip lives in one workspace folder: settings, `work.sqlite` with the results of every step, `vocabulary.yaml` for the review and a report per step. The formats are in [docs/formats.md](docs/formats.md).

## Requirements

- **.NET 8 SDK** (a newer SDK builds it too).
- **[uv](https://docs.astral.sh/uv/)**. It installs the right Python and the packages of the analysis on the first run.
- **ffmpeg** in `PATH`, for the frames of videos.
- **A vision model server** with an OpenAI-compatible API for tagging: [LM Studio](https://lmstudio.ai/) with `qwen3-vl-8b-instruct` by default (about 6 GB of video memory), or a cloud API. See [Choosing models](#choosing-models).
- **A cloud model** for the vocabulary: a DeepSeek API key by default.
- **Access to the site**: the API address and the admin login, the media storage (WebDAV login, or the folder of a local storage) and a connection string to its Postgres that may read `media_files` and write `media_embeddings`.

## Installation

```
git clone https://github.com/meckbaig/HaruyasumiRyokouki.DataTool.git
cd HaruyasumiRyokouki.DataTool
dotnet build
```

`haru-data` is then `src/HaruyasumiRyokouki.DataTool/bin/Debug/net8.0/haru-data` (`.exe` on Windows); put that folder into `PATH` or call it by its path. `haru-data doctor` shows what is still missing.

Everything the tool downloads stays in the repository: NuGet packages, Python, its packages and the SigLIP model go into `.cache/` (about 2.5 GB after the first run, mostly torch and the model), the analysis environment into `src/analysis/.venv`. Deleting the repository removes all of it.

## Folders

Three folders take part, and they are easy to mix up:

| Folder | What it is | Where it is set |
|---|---|---|
| **Source folder** | The photos and videos of the trip on your computer, collected by you. The tool only reads them and never changes them. | `--source` of `haru-data init`, then `source` in `haru-data.toml` |
| **Workspace** | The state of one trip: `haru-data.toml` (settings), `.env` (secrets), `work.sqlite` (results of every step), `vocabulary.yaml` (the vocabulary for the review), `reports/` (a report per step), `frames/` (frames of videos), `cache/llm/` (answers of the cloud model). Created by `haru-data init`; `run`, `status` and the other commands work in it. Keep it until the trip is on the site, and for adding photos later. | the argument of `init`, then `--workspace` or the current folder |
| **Site storage** | The folder the backend imports media from: a Nextcloud folder over WebDAV or a local folder of the server. `upload` copies the files there; the site converts them and keeps them there. | `[storage]` in `haru-data.toml` |

## Commands

`haru-data` is the command for everyday use. Every command works in a workspace: the current folder, or the one given with `--workspace <folder>`. Exit codes: `0` done, `1` error, `2` waits for a person (the review, a declined question).

| Command | What it does |
|---|---|
| `haru-data init <folder> --source <folder>` | Creates a workspace in `<folder>` (the folder is created if needed) with `haru-data.toml`, `.env` and `work.sqlite`. `--source` is the folder with the photos and videos of the trip, written into the settings. Then runs `doctor`. Refuses a folder that is a workspace already. |
| `haru-data doctor` | Checks what the steps need: uv, ffmpeg, the analysis packages and the SigLIP model, the source folder, the vision model server, the API key of the cloud model, the site's API, the storage settings and whether the secrets are set (their values are never printed). Changes nothing. |
| `haru-data run` | Runs the steps that are not done, in their order (see [the steps](#steps)), and stops at the vocabulary review, at a question answered "no" and at an error. Running it again continues where it stopped. |
| `haru-data run <step>` | Runs one step, even if it is done, for example `haru-data run sync`. |
| `haru-data run --yes` | Answers "yes" for the steps: before the paid vocabulary step and before every write to the site. What would be asked is still printed. Needed where nobody can answer, such as a scheduled run. |
| `haru-data run --no-review` | Approves `vocabulary.yaml` as the model wrote it instead of stopping for the review. |
| `haru-data status` | Shows every step with its state (`done`, `to run`, `errors`, `waiting for you`, `not run`) and its counts, the errors of the step that failed and the command to run next. Changes nothing. |
| `haru-data review` | A summary of the vocabulary: tags, new and existing, files left without a tag, the most used tags, and whether it is approved. |
| `haru-data review --check` | Checks the edited `vocabulary.yaml` and prints every problem with its line number. Changes nothing. |
| `haru-data review --approve` | Checks `vocabulary.yaml` and, if there are no errors, makes it the vocabulary that is written to the site. |

### Steps

`run` runs these steps in this order. Each is a command of one of the two apps; they can also be started directly (below).

| Step | App | What it does | When `run` starts it |
|---|---|---|---|
| `scan` | analysis | Finds the photos and videos of the source folder, checks that names are unique | every time |
| `frames` | analysis | Takes the frame at 1 second of every video (ffmpeg) | every time; skips videos that are done |
| `tag` | analysis | Asks the vision model for tags and names of every photo and frame | every time; skips files that are done, needs the model server only for new files |
| `embed` | analysis | Computes SigLIP image vectors | every time; skips files that are done |
| `vocab` | tags | Builds the vocabulary from the machine tags with the cloud model, translations through the site's AI, writes `vocabulary.yaml` | when the machine tags changed; prints an estimate and asks first |
| `review` | tags | Stops until `vocabulary.yaml` is approved | every time |
| `push-tags` | tags | Creates the tags the site does not have | when the approved vocabulary changed; asks first |
| `upload` | tags | Copies the files the storage does not have into the site storage | when files were added; asks first |
| `sync` | tags | Lets the site import the new files of its storage, reads their ids | when files were added; asks first |
| `assign` | tags | Adds the tags to the files on the site; never removes a tag | when the vocabulary or the files on the site changed; asks first |
| `push-embeddings` | tags | Writes the image vectors of files that have none yet into the site's database | when the vectors or the files on the site changed; asks first |

### Running a step directly

For repeating or debugging one step with options `run` does not pass. The workspace is required here:

```
uv-local run --project src/analysis haru-analysis <step> --workspace my-trip
haru-tags <step> --workspace my-trip
```

`haru-tags` lies next to `haru-data`. `uv-local` is `uv-local.cmd` (Windows) or `./uv-local.sh` in the repository root: uv with its downloads kept in `.cache/`.

| Command | What it does |
|---|---|
| `haru-analysis status` | Files per step: done, to do, with errors, and the errors. |
| `haru-analysis tag --reset` | Deletes all machine tags and asks the model again, for example after changing the model. |
| `haru-analysis tag --vocabulary-hint <file>` | Gives the model a text file with tags, one per line, to prefer. |
| `haru-analysis embed --reset` | Deletes all vectors and computes them again. |
| `haru-tags status` | What the analysis found: files, tagged files, files with vectors, distinct tags and names. |
| `haru-tags vocab --estimate` | Prints how many requests and tokens `vocab` would send; sends nothing. |
| `haru-tags push-embeddings --dry-run` | Matches the vectors with the files on the site and reports; writes nothing. |
| `haru-tags push-embeddings --replace` | Also overwrites the vectors the site has already; by default they are kept. |

The steps of `haru-tags` that write to the site take `--yes`, like `run`.

## Filling the site with a trip

The steps below are the whole cycle, including what only a person can do. `haru-data run` does the analysis, the vocabulary and the writes by itself and stops where a person is needed. It can be stopped with Ctrl+C at any moment and started again: finished work is kept.

### 1. Collect the photos and videos

Put all photos and videos of the trip into one folder; subfolders are fine.

- **Formats.** Photos: `.jpg .jpeg .png .bmp .webp .tif .tiff .heic .heif .avif`; videos: `.mp4 .mov .avi .mkv .wmv .flv .webm .m4v .3gp .mpeg .mpg`. Other files are ignored.
- **Unique names.** The site keeps a photo under its name with another extension (`.webp`), and its storage is one flat folder, so a file is identified by its name without the extension, ignoring case. Two files whose names differ only by extension or case, or that have the same name in different subfolders, stop the first step with a list; rename one of them.
- **Dates.** The site takes the time of shooting from the file name (`20260301_120000.jpg`, `PXL_20260301_120000123.jpg` and the like), otherwise from the modification time of the file. Keep the names as the phone or camera wrote them, and copy the files in a way that keeps the modification time. The site skips a file whose time falls outside the trip dates.
- **Already uploaded files.** The folder may contain files that are on the site already. Such a file is not uploaded again and keeps its vector, but it is analysed and gets the tags of the new vocabulary added to the ones it has.

### 2. Prepare the site

- The trip dates in the backend settings (`MediaFormat:TravelDateOffsets`) must cover all files; see `docs/features/dates.md` of the backend.
- **Back up the database** before the first write. It is the simple way back if something goes wrong (see [Rolling back](#rolling-back)):

  ```
  pg_dump -Fc -h <host> -U <user> -d <database> -f before-import.dump
  ```

### 3. Create a workspace

```
haru-data init my-trip --source D:/Photos/Japan
cd my-trip
```

`my-trip` is the new workspace and `--source` the folder with the photos and videos from step 1 (see [Folders](#folders)). The workspace can be anywhere, also outside the repository. Fill in `haru-data.toml`, where every setting has a comment: the address of the site's API in `[backend]`, the storage in `[storage]` (`webdav` with the URL of the media folder, or `local` with the folder of the backend's local storage), and the models if the defaults do not fit. Put the secrets into `.env` next to it: `DEEPSEEK_API_KEY`, `HARU_USER` and `HARU_PASSWORD` of the site's admin, `HARU_STORAGE_USER` and `HARU_STORAGE_PASSWORD` for WebDAV, `HARU_CONNECTION` for Postgres. There are no default addresses: until an address is set, nothing is written anywhere.

Start LM Studio, load the vision model, start its server, then check:

```
haru-data doctor
```

### 4. Analyse and build the vocabulary

```
haru-data run
```

The analysis runs locally: the index of the folder (`scan`), a frame of every video (`frames`), tags from the vision model (`tag`, about 2 hours per 4,200 photos with the default model) and image vectors (`embed`, which downloads the SigLIP model on its first run). Files that are done are skipped on every later run.

Then `run` prints what the vocabulary step will send to the cloud model (requests, tokens, number of translations) and asks. The step takes a few minutes and costs about $0.10 for 4,200 photos with DeepSeek; the translations go through the site's own AI. The answers are kept in `cache/llm` of the workspace, so running it again does not pay twice.

### 5. Review the vocabulary

`run` stops at the review. Open `vocabulary.yaml` in the workspace: the problems the checks found are at the top, then every tag with its labels in English, Russian and Japanese, search aliases, the machine tags it is made of (`sources`) and the number of photos, and at the end the tags the model dropped, with the reason.

Change labels and aliases, delete tags, move sources between tags or bring a dropped tag back. Then:

```
haru-data review --check     # the problems, with line numbers
haru-data review --approve   # make it the vocabulary written to the site
```

Errors block the approval: what the site would reject, or what would break the import. The rules are in the [haru-tags README](src/HaruyasumiRyokouki.DataTool.Tags/README.md#reviewing-the-vocabulary). Editing the file after the approval takes the approval back.

### 6. Write to the site

```
haru-data run
```

Five steps write to the site. Each prints where it is going to write (API, storage, database) and asks first:

1. `push-tags` creates the tags the site does not have yet.
2. `upload` copies the files the storage does not have.
3. `sync` lets the site import the new files and reads their ids. The site converts every photo and makes previews of videos: on the test machine about 3 seconds per file, so a few hours for thousands of files; the step waits.
4. `assign` adds the tags to the files. It only adds: tags put on a file by hand stay.
5. `push-embeddings` writes the image vectors of the files that have none yet and lists the site's files still without one. Vectors already on the site are kept.

Every step can run again without duplicating anything. `sync` warns about files the site did not import, with the likely reason; the details are in the backend log.

### 7. Restart the backend

The similarity search loads all vectors when the backend starts, so restart it after `push-embeddings`.

### 8. Look through the new files in the admin panel

Imported files are not approved: only the admin sees them until they are approved. Look through them, correct the tags where the model was wrong, and approve them.

### Adding photos later

Add the files to the same source folder and run `haru-data run` in the same workspace. Only the new files are analysed. If they bring machine tags the vocabulary does not have, the vocabulary step runs again (earlier answers come from the cache) and the run stops at the review again; tags that are on the site already are matched, not created twice. Then only the new files are uploaded and tagged.

### When something stops

- `haru-data status` shows every step, the errors of the step that failed and what to run next. Each step also writes `reports/<step>.json`.
- An error stops the run with a message and a hint. Fix the cause and run `haru-data run` again; it continues where it stopped.
- `haru-data run <step>` runs one step again even if it is done, for example `haru-data run sync`.
- `--yes` answers the questions of the steps (the targets are still printed); `--no-review` approves the vocabulary as the model wrote it.

## Rolling back

Imported files are not visible to visitors until they are approved, so a wrong import can be cleaned up before anyone sees it.

**With the backup from step 2**, restore the database and delete from the storage the files uploaded since; their names start with the keys in `work.sqlite` (`SELECT key FROM media WHERE missing = 0`):

```
pg_restore --clean -h <host> -U <user> -d <database> before-import.dump
```

**Without a backup:**

- **Files.** Delete them in the admin panel (`DELETE /v1/media/{id}`): the site removes the files from the storage, and their tags and vectors go with them. The site ids of the files of the workspace are in `work.sqlite`: `SELECT media_file_id FROM site_media`.
- **Tags.** The site has no API to delete a tag. The tags this tool created are in `work.sqlite`: `SELECT tag_id FROM site_tags WHERE created = 1`. Deleting them in Postgres also deletes their translations and their links to files:

  ```sql
  DELETE FROM tags WHERE id IN (...);
  ```

- **Vectors only.** `DELETE FROM media_embeddings WHERE media_file_id IN (...)`, then restart the backend.

## Choosing models

Two steps use language models, and they need different ones. All numbers below come from test runs on photos of the actual trip, local models on an AMD Radeon RX 9070 XT (16 GB) in LM Studio; the details are in [docs/models.md](docs/models.md).

**Tagging photos** needs a vision model and runs once per photo, so speed and price per photo matter most.

| Model | Where | 4,200 photos | Cost | Quality |
|---|---|---|---|---|
| `qwen3-vl-8b-instruct` (Q4_K_M) | LM Studio, a mainstream GPU (about 6 GB for the model) | ~2-2.5 hours | free | Good general tags; reads names off signs, guesses famous sights by look, sometimes wrongly. **Default.** |
| `qwen3-vl-4b-instruct` (IQ4_XS) | LM Studio, an older or smaller GPU | ~1.5 hours | free | Usable, but noticeably worse: puts a wrong kind of place on many photos and invents temple names. |
| `gemma-4-e4b` (Q4_K_M) | LM Studio | ~2.5 hours | free | Not recommended: copies words of the prompt as tags and almost never names a place. |
| `qwen3.6-35b-a3b` (IQ3_S) | LM Studio, ~16 GB video memory | ~9 hours | free | Knows more franchises and dishes, but adds more noise and invents some names. Not worth the hardware for most people. |
| `deepseek-flash` | DeepSeek API, many requests in parallel | minutes (92 photos in 10 s with 32 parallel requests) | ~$0.9 off-peak, ~$1.8 peak | Best: recognizes noticeably more places, dishes and franchises. |

**Building the vocabulary** needs a model that reasons over the whole list of tags at once: which entries are the same thing, which are noise. Local models tested so far cannot do it reliably: they merge different things into broad categories, drop useful tags or break the answer format. Use a cloud model: `deepseek-flash` (with reasoning on) does it for about $0.10 off-peak for a 4,200-photo trip. Translations of the final tags go through the site's own `/v1/tags/completion`, so they use whatever model the backend is configured with.

In total, a 4,200-photo trip costs about **$0.10 to $0.20** with local tagging, or **$1 to $2** with tagging by DeepSeek (prices of October 2026). Every step reports the tokens it used, so the cost of an actual run is known.

## Development

```
dotnet test                                                              # .NET tests
uv-local run --directory src/analysis --extra dev pytest                 # Python tests, no models are loaded
uv-local run --directory src/analysis --extra dev pytest -m integration  # with the SigLIP model
```

`uv-local.cmd` (Windows) and `uv-local.sh` run uv with its downloads in `.cache/`. Every step can also be started directly, without the orchestrator, to repeat or debug it; see the READMEs of the apps. The steps that write to the site were tested against a local backend with its own Postgres and a local storage folder, never against the production site.
