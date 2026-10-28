# HaruyasumiRyokouki.DataTool

Tool that fills the [HaruyasumiRyokouki](https://github.com/meckbaig/HaruyasumiRyokouki.Backend) travelogue with data: a folder of trip photos and videos goes in; files in the site's storage, media records with search tags in three languages and image vectors for "similar media" come out. Everything a model can do is done by a model; a person only reviews the tag vocabulary and confirms every write to the site.

How the data flows:

1. **haru-analysis** (Python, runs locally) indexes the folder, takes a frame from every video, asks a vision model for tags and names on every photo and computes SigLIP 2 image vectors. See [src/analysis/README.md](src/analysis/README.md).
2. **haru-tags** (.NET) turns the machine tags into a vocabulary with a cloud model: merges duplicates and drops what nobody would search for. After a person has reviewed it, it translates the tags that are left through the site's AI, then creates the tags on the site, uploads the files, lets the site import them and attaches tags and vectors. See [src/HaruyasumiRyokouki.DataTool.Tags/README.md](src/HaruyasumiRyokouki.DataTool.Tags/README.md).
3. **haru-data** (.NET) is the entry point: it creates a workspace, runs the steps in order, stops for the review and for confirmations, and shows where a run stands. See [src/HaruyasumiRyokouki.DataTool/README.md](src/HaruyasumiRyokouki.DataTool/README.md).

All state of a trip lives in one workspace folder: settings, `work.sqlite` with the results of every step, `vocabulary.yaml` and `translations.yaml` for the review and a report per step. The formats are in [docs/formats.md](docs/formats.md).

## Requirements

- **.NET 8 SDK** (a newer SDK builds it too).
- **[uv](https://docs.astral.sh/uv/)**. It installs the right Python and the packages of the analysis on the first run.
- **ffmpeg** in `PATH`, for the frames of videos.
- **A vision model server** with an OpenAI-compatible API for tagging: [LM Studio](https://lmstudio.ai/) with `qwen3-vl-8b-instruct` by default (about 6 GB of video memory), or a cloud API. See [Choosing models](#choosing-models).
- **A cloud model** for the vocabulary: a DeepSeek API key by default.
- **Access to the site**: the API address and the admin login, write access to the folder the site imports media from (a local or network folder, or Nextcloud over WebDAV if the backend uses that) and a connection string to its Postgres that may read `media_files` and write `media_embeddings`.

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
| **Workspace** | The state of one trip: `haru-data.toml` (settings), `.env` (secrets), `work.sqlite` (results of every step), `vocabulary.yaml` (the tag list for the review), `translations.yaml` (the translations of the new tags), `reports/` (a report per step), `frames/` (frames of videos), `cache/llm/` (answers of the cloud model). Created by `haru-data init`; `run`, `status` and the other commands work in it. Keep it until the trip is on the site, and for adding photos later. | the argument of `init`, then `--workspace` or the current folder |
| **Site storage** | The folder the backend imports new media from (`LocalStorageProvider:Path` of the backend, or a Nextcloud folder if the backend uses WebDAV). The site only imports files that lie there, so `upload` copies the photos there, and `sync` asks the site to import them; the site converts them and keeps them there. | `[storage]` in `haru-data.toml` |

## Step-by-step guide

This is the whole way from a folder of photos to photos with tags on the site. You need no knowledge of how the tool works inside.

The idea in one paragraph: almost all the work is done by **one command, `haru-data run`, which you run several times**. Each time, it goes through the list of steps, skips what is done and does the rest. It stops when it needs you: to answer a question, to check the tag list it made, or when something went wrong. After you have done what it asked, you run `haru-data run` again, and it continues from where it stopped. Nothing is done twice.

### Before you start

Once per computer:

1. Install what the tool needs and build it, see [Requirements](#requirements) and [Installation](#installation).

Once per trip:

2. **Collect the photos and videos** of the trip into one folder on your computer, for example `D:/Photos/Japan`. Subfolders are fine. Keep the file names as the phone or camera wrote them (the site takes the date of a photo from its name), and copy them in a way that keeps the file dates.
3. **Make sure the site knows the dates of the trip**: the backend setting `MediaFormat:TravelDateOffsets` must cover them, otherwise the site skips the files.
4. **Back up the site's database**, so you can go back if something goes wrong (see [Rolling back](#rolling-back)):

   ```
   pg_dump -Fc -h <host> -U <user> -d <database> -f before-import.dump
   ```

### Step 1. Create a workspace

A workspace is a folder where the tool keeps everything about this trip: settings, passwords, results. It is not the photo folder and not the site's storage (see [Folders](#folders)).

```
haru-data init D:/Trips/japan --source D:/Photos/Japan
```

- `D:/Trips/japan` is the workspace to create; any new folder.
- `--source D:/Photos/Japan` is the folder with the photos from "Before you start".

The command creates the folder with two files to fill in, then checks your computer and prints a list; lines marked `MISSING` are what is not set up yet. That is expected now.

### Step 2. Fill in the settings

Open `haru-data.toml` in the workspace. Every line has a comment. Fill in:

- `api` under `[backend]`: the address of the site's API, such as `https://api.example.com`.
- `path` under `[storage]`: the folder the site takes new media from, the same as `LocalStorageProvider:Path` in the backend settings. If the backend runs on another computer, give the path to that folder as this computer sees it, such as a network share `//server/media`. (Only if the backend keeps media in Nextcloud: `type = "webdav"` and `url` instead, see the comments in the file.)

Open `.env` in the workspace and fill in the passwords:

| Line | What to put there |
|---|---|
| `DEEPSEEK_API_KEY` | the DeepSeek API key, for building the tag list |
| `HARU_USER`, `HARU_PASSWORD` | the site's admin login and password |
| `HARU_STORAGE_USER`, `HARU_STORAGE_PASSWORD` | only for Nextcloud (`type = "webdav"`): its login and password; otherwise leave them out |
| `HARU_CONNECTION` | the connection to the site's database, like `Host=db.example.com;Port=5432;Database=haru;Username=haru;Password=...` |

Start LM Studio, load the model `qwen3-vl-8b-instruct` and start its server (the "Developer" tab). Then go into the workspace and check everything:

```
cd D:/Trips/japan
haru-data doctor
```

Fix every line marked `MISSING` and run `haru-data doctor` again until it says `Everything is in place.` All the following commands are run in this folder.

### Step 3. First run: analyse the photos and build the tag list

```
haru-data run
```

What happens:

1. The tool looks at every photo and video in the photo folder (`--source` from step 1, nothing else) with the vision model in LM Studio. This is the long part: about 2 hours per 4,000 photos. The screen shows the progress (`tag 120/4200`). The computer must stay on; if you interrupt it with Ctrl+C or it is closed, just run `haru-data run` again later, finished photos are not done again.
2. Then it is going to ask the cloud model (DeepSeek) to turn the tags of all photos into one tag list. This costs money, so it first prints how much it will send and asks:

   ```
   Run vocab and send these requests?
   Continue? [y/N]
   ```

   Type `y` and Enter. This takes a few minutes and costs about $0.10 per 4,000 photos.
3. The run **stops** with the message `Your check is needed`. This is not an error: the tag list is ready and waits for your check.

### Step 4. Check the tag list

```
haru-data review
```

This opens a page in your browser (the terminal shows its address in case it does not open by itself; the page works only on this computer). The page explains at the top what to do. In short:

- Every card is a tag that goes to the site. The list is in English only: translations are made after your check, so nothing you delete is translated.
- Press **Photos** on a card to see the photos that get the tag. This is the quickest way to understand a tag the model made up. A photo that got the tag by mistake: press **×** on it, and it will not get this tag (**↺** gives it back). Click a photo to see it in full size; **Show more** shows the rest.
- Rename a tag in its name field, press **Delete** on a useless one, or **Bring back** a tag the model threw away (at the bottom of the page).
- Two tags that are really one (`dish` and `food`): **Merge into…** on one of them and pick the other. Its photos get the other tag, and its name becomes a search word of it.
- "Machine tags" on a card are what the vision model wrote on the photos; a photo gets the tag if it has any of them. **→** moves one to another tag, **×** removes it.
- Problems appear at the top and on the cards while you edit: red ones must be fixed, yellow ones are only hints. You may also change nothing.

Press **Approve**. The page says `Approved`; you can close it, and the command in the terminal ends. **Save** keeps your edits without approving, so you can close the page and continue later with `haru-data review` again.

### Step 5. Second run: translations

```
haru-data run
```

The same command again. The photo steps find nothing new and pass in seconds. Then the tool translates the new tags of your list into Russian and Japanese through the site's AI and checks the result: an empty translation, two tags with the same name in one language, or a text the site does not accept.

- **No problems:** the translations are approved by themselves and the run goes on to step 6 without stopping.
- **Problems found:** the run **stops** again and asks for your check. Run `haru-data review` once more: the same page now shows the translations, with only the tags that have problems. Fix the names in English, Russian and Japanese. Two tags with the same name (like `bun` and `bread`, both パン) can be merged right there: **Merge into…** on the card, or the merge button on the problem. A tag you do not want after all: **Delete**. **Photos** shows the photos of a tag. Press **Approve**, and run `haru-data run` again; it goes on to step 6.

A merge or a deletion on the translations page changes the approved tag list and approves it again; the translations of the other tags stay as they are. Other changes of the tag list (renaming, bringing a tag back) are made on its page: edit `vocabulary.yaml` or approve it again, and the translations are made again (from the cache, so it is quick) and checked again.

Without a browser, the same works in the terminal and a text editor: the tag list is `vocabulary.yaml` and the translations are `translations.yaml` in the workspace; `haru-data review --check` prints their problems with line numbers and `haru-data review --approve` approves them.

### Step 6. Put everything on the site

If the run stopped in step 5, this is the third `haru-data run`; otherwise the second run simply goes on. The tool writes to the site in five steps. **Before each step it prints where it is going to write and asks**:

```
Create 223 tags on the site:
  API: https://api.example.com
Continue? [y/N]
```

Check that the address is your site and type `y`. The steps are:

1. creating the new tags on the site;
2. copying the photos and videos into the site's storage;
3. the site imports the new files: converts photos, makes video previews. This takes long, about 3 seconds per file, so a few hours for thousands of files. Wait;
4. putting the tags on the photos;
5. saving the data for "similar photos" into the site's database.

At the end it prints `All steps are done.`

### Step 7. Restart the backend

Restart the site's backend so it loads the new data for "similar photos".

### Step 8. Approve the photos on the site

The new photos are visible only to the admin until they are approved. Open the admin panel, look through them, fix tags where the model was wrong, and approve them. Done.

### If something goes wrong

When a step fails, `haru-data run` stops with a message like `upload failed: see the messages above and reports/upload.json`. Read the message above it, fix the cause (often a wrong address or password in the settings, or a server that is not running) and run `haru-data run` again; it continues from the failed step.

At any moment, `haru-data status` shows which steps are done, which are not, what went wrong and what to run next.

If you answered `n` to a question, nothing was written; run `haru-data run` again to be asked again.

### Adding photos later

Add the new photos to the same photo folder and run `haru-data run` in the same workspace. Only the new photos are analysed. If they bring tags the list does not have yet, the run stops for the check again (step 4, and step 5 if the new translations have problems); then run `haru-data run` once more. Only the new files are uploaded.

## Commands

`haru-data` has five commands. Every command works in a workspace: the current folder, or the folder given with `--workspace <folder>`, which every command accepts. The exit code tells scripts what happened: `0` done, `1` error, `2` waits for a person (the vocabulary check, a question answered "no").

In the syntax below, `[...]` is optional and `<...>` is a value you put in.

### haru-data init

```
haru-data init <folder> --source <folder>
```

Creates a new workspace and checks the computer (the same as `doctor`).

| Part | Meaning |
|---|---|
| `<folder>` | The workspace to create, such as `D:/Trips/japan`. It is created if it does not exist; a folder that is a workspace already is refused. |
| `--source <folder>` | Required. The folder with the photos and videos of the trip. It is written into `haru-data.toml` and can be changed there. |

### haru-data doctor

```
haru-data doctor
```

Checks that everything the steps need is there and prints `ok` or `MISSING` for each: uv, ffmpeg, the photo folder, the vision model server, the API key, the site's API, the storage settings, the passwords (only whether they are set, never their values). Changes nothing.

### haru-data run

```
haru-data run [<step>] [--yes] [--no-review]
```

Without `<step>`: does all steps that are not done, in their order, and stops where a person is needed or at an error. Running it again continues from there. With `<step>`: does only that one step, even if it is done.

| Part | Meaning |
|---|---|
| `<step>` | Optional. The name of one step from [the list of steps](#steps), such as `sync`. |
| `--yes` | Optional. Answers "yes" to every question: before the paid cloud step and before every write to the site. What would be asked is still printed. Use it only when you are sure of the settings, or where nobody can answer. |
| `--no-review` | Optional. Approves the tag list and the translations as they were made, instead of stopping for your check. |

The options can be combined with each other and with `<step>`:

```
haru-data run                      # everything that is not done, asks before writing
haru-data run --yes                # the same without questions
haru-data run --yes --no-review    # everything without stopping
haru-data run sync                 # only the step sync, asks before writing
haru-data run sync --yes           # only the step sync, without the question
```

### haru-data status

```
haru-data status
```

Shows every step with its state (`done`, `to run`, `errors`, `waiting for you`, `not run`) and its numbers, the errors of a failed step and the command to run next. Changes nothing.

### haru-data review

```
haru-data review [--check | --approve]
```

Works with what waits for your check: the tag list (`vocabulary.yaml`) until it is approved, then the translations (`translations.yaml`) if they had problems. Use one option or none.

| Form | Meaning |
|---|---|
| `haru-data review` | Opens the review page in the browser: the tags with their photos, the problems, editing, Save and Approve. Runs until you approve on the page or press Ctrl+C. |
| `haru-data review --check` | In the terminal: checks the file and prints each problem with its line number. Changes nothing. |
| `haru-data review --approve` | In the terminal: checks the file and, if there are no errors, approves it: from now on it is what goes to the site. |

### Steps

`haru-data run` goes through these steps in this order. Normally you do not need their names; they appear on the screen while `run` works, in `status`, and are used with `haru-data run <step>`.

| Step | What it does | When `run` does it |
|---|---|---|
| `scan` | Finds the photos and videos in the photo folder, checks that names are unique | every time |
| `frames` | Takes a picture from every video at 1 second, for the analysis | every time; skips videos that are done |
| `tag` | Asks the vision model (LM Studio) which tags fit every photo | every time; skips photos that are done |
| `embed` | Computes the data for "similar photos" | every time; skips photos that are done |
| `vocab` | Makes one English tag list from the tags of all photos with the cloud model; writes `vocabulary.yaml` | when the photos brought new tags; asks first, because it costs money |
| `review` | Stops until you approve `vocabulary.yaml`, and `translations.yaml` if it waits | every time; passes at once when everything is approved |
| `translate` | Translates the new tags of the approved list through the site's AI and checks them; writes `translations.yaml` and stops only if it found problems | when a new tag list was approved |
| `push-tags` | Creates the new tags on the site, with their translations | when a new tag list or new translations were approved; asks first |
| `upload` | Copies the photos and videos the site does not have into its storage | when photos were added; asks first |
| `sync` | Lets the site import the new files | when photos were added; asks first |
| `assign` | Puts the tags on the photos on the site; never removes a tag | when the tag list or the files on the site changed; asks first |
| `push-embeddings` | Saves the data for "similar photos" into the site's database; keeps what is there already | when new photos have it; asks first |

### Running a step without haru-data

Each step is a command of one of two programs, which `haru-data run` starts for you. You can start them yourself to repeat or debug a step with options `run` does not have. Here `--workspace` is required:

```
uv-local run --project src/analysis haru-analysis <step> --workspace <folder>   # scan, frames, tag, embed, status
haru-tags <step> --workspace <folder>                                           # the other steps, status
```

`haru-tags` lies next to `haru-data`. `uv-local` is `uv-local.cmd` (Windows) or `./uv-local.sh` in the repository root.

| Command | What it does |
|---|---|
| `haru-analysis status` | Photos per step: done, to do, with errors, and the errors. |
| `haru-analysis tag --reset` | Deletes all tags from the model and asks it again, for example after changing the model. |
| `haru-analysis tag --vocabulary-hint <file>` | Gives the model a text file with tags, one per line, to prefer. |
| `haru-analysis embed --reset` | Deletes the "similar photos" data and computes it again. |
| `haru-tags status` | What the analysis found: photos, tagged photos, distinct tags. |
| `haru-tags translate` | Translates the approved new tags; a file that waits for review is not overwritten. |
| `haru-tags vocab --estimate` | Prints what `vocab` would send to the cloud model; sends nothing. |
| `haru-tags push-embeddings --dry-run` | Reports what would be saved; saves nothing. |
| `haru-tags push-embeddings --replace` | Also overwrites the "similar photos" data the site has already. |

The `haru-tags` steps that write to the site take `--yes`, like `run`.

## Rolling back

Imported files are not visible to visitors until they are approved, so a wrong import can be cleaned up before anyone sees it.

**With the backup made before you started**, restore the database and delete from the storage the files uploaded since; their names start with the keys in `work.sqlite` (`SELECT key FROM media WHERE missing = 0`):

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

**Building the vocabulary** needs a model that reasons over the whole list of tags at once: which entries are the same thing, which are noise. Local models tested so far cannot do it reliably: they merge different things into broad categories, drop useful tags or break the answer format. Use a cloud model: `deepseek-flash` (with reasoning on) does it for about $0.10 off-peak for a 4,200-photo trip. Translations go through the site's own `/v1/tags/completion` after the review, only for the new tags that are left, so they use whatever model the backend is configured with.

In total, a 4,200-photo trip costs about **$0.10 to $0.20** with local tagging, or **$1 to $2** with tagging by DeepSeek (prices of October 2026). Every step reports the tokens it used, so the cost of an actual run is known.

## Development

```
dotnet test                                                              # .NET tests
uv-local run --directory src/analysis --extra dev pytest                 # Python tests, no models are loaded
uv-local run --directory src/analysis --extra dev pytest -m integration  # with the SigLIP model
```

`uv-local.cmd` (Windows) and `uv-local.sh` run uv with its downloads in `.cache/`. Every step can also be started directly, without the orchestrator, to repeat or debug it; see the READMEs of the apps. The steps that write to the site were tested against a local backend with its own Postgres and a local storage folder, never against the production site.
