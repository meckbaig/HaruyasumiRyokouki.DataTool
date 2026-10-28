# haru-analysis

The local part of the data tool: it indexes the trip folder, takes a frame from every video, asks a vision model for tags and computes image vectors for "similar media" on the site. Nothing here talks to the backend; it only fills `work.sqlite` in the workspace, which `haru-tags` reads later.

Normally the steps are started by `haru-data run`. Run them directly to repeat or debug one step:

```
uv-local run --project src/analysis haru-analysis scan   --workspace my-trip
uv-local run --project src/analysis haru-analysis frames --workspace my-trip
uv-local run --project src/analysis haru-analysis tag    --workspace my-trip
uv-local run --project src/analysis haru-analysis embed  --workspace my-trip
uv-local run --project src/analysis haru-analysis status --workspace my-trip
```

`uv-local` (`uv-local.cmd` on Windows, `./uv-local.sh` elsewhere, in the repository root) is plain `uv` that keeps everything it downloads in the repository.

Every step writes `reports/<step>.json` and exits with `0` (ok) or `1` (error). Each file is saved as soon as it is processed, so a step stopped with Ctrl+C or by an error continues where it left off on the next run.

## Setup

[uv](https://docs.astral.sh/uv/) installs the right Python (pinned in `.python-version`: torch has no builds for the newest one yet) and the dependencies on the first run.

Nothing is installed into the system or the user profile: `uv-local` points uv's package cache, its Python installs and the Hugging Face model cache to `.cache/` in the repository root. The project environment lives in `src/analysis/.venv`. Deleting these two folders removes everything the tool downloaded (about 2.5 GB, mostly torch and the SigLIP 2 model). Plain `uv` works too, but then the downloads land in the user profile.

`frames` needs `ffmpeg` in `PATH`. `tag` needs a running vision model server (below).

## Settings

The steps read `haru-data.toml` in the workspace folder. `haru-data init` creates it; until then, this is enough:

```toml
source = "D:/Photos/Japan"                   # trip folder, searched recursively; relative paths are resolved against the workspace

[providers.local]                            # any OpenAI-compatible server; the name is yours
type = "openai"                              # the only supported type
base_url = "http://localhost:1234/v1"        # LM Studio; llama.cpp server: http://localhost:8080/v1; Ollama: http://localhost:11434/v1
model = "qwen2.5-vl-7b-instruct"             # model name as the server lists it
# api_key_env = "OPENROUTER_API_KEY"         # only for servers that need a key; the key itself goes into .env

[usage]
vision = "local"                             # provider for the tag step

[vision]
max_side = 1024                              # photos are downscaled to this long side before sending: fewer image tokens, text still readable
concurrency = 1                              # parallel requests; 1 gives repeatable answers, more only helps a server with several GPUs

[embed]
model = "google/siglip2-base-patch16-256"   # must match the backend: it compares vectors of exactly this model, 768 floats
device = "auto"                             # auto | cuda | mps | cpu
```

## How the steps work

**scan.** Files are matched to the site by their key: the file name without the last extension, in lower case. The backend keeps that part of the name when it converts photos (`IMG_1.HEIC` becomes `IMG_1.webp`), so the key is the only reliable link between a local file and its `media_files` row. Two files with the same key (`IMG_1.jpg` and `img_1.png`, possibly in different subfolders) cannot be told apart on the site, so scan stops with the list of such files and writes nothing. File types are the ones the backend imports; everything else is ignored.

Running scan again is safe: new files are added, a moved folder only updates paths, a changed file (size or modification time) loses its frame and vector so they are computed again, and a vanished file is marked missing rather than deleted.

**frames.** The backend makes a video's preview from the frame at 1 second, so the tool takes the same frame (`frames/<key>.jpg`) and the later steps use it instead of the video. Videos shorter than a second get their first frame.

**tag.** Every photo (and video frame) goes to the vision model, which answers in three parts:

- `description`: one or two sentences about what is in the photo, including readable text. Writing it first makes the model look before it names things. It is stored as context for the vocabulary step and never shown on the site.
- `tags`: 5 to 12 general words a visitor might search for: the kind of place (also indoors: museum, shop, hotel room) and the main subjects (dishes, animals, landscape, pop-culture genre, activities). The minimum is part of the JSON schema, because a small model stops after one or two tags whatever the prompt says, and servers like LM Studio enforce the schema.
- `names`: proper names (landmarks, places, brands, franchises, characters), each with its kind and the evidence the model saw: "the sign reads dotonbori" or "the distinctive torii in the water". A name without evidence is dropped. Names are where a small model guesses most, so they are kept apart with their evidence, and the smarter model of the vocabulary step can keep what was read and drop what was guessed from a general look.

The prompt has no example tags or names on purpose: a small model copies examples into answers where they do not belong (an anime photo became "hololive", every night photo "shibuya").

Small models also ignore "do not" lists, so after the answer a stop list in `tag.py` removes words too broad to narrow a search (building, sky, people, japan), incidental objects (chopsticks, signs), seasons (the site knows every photo's date) and colors, and strips colors and materials from the front of a tag ("red torii" becomes "torii"). An invalid answer is asked again once before the file is marked as failed. The tags are raw material: `haru-tags vocab` later merges synonyms and plurals, fixes spelling, checks names and translates.

Requests go one at a time by default: with parallel requests LM Studio gave slightly different answers for the same photo, one at a time it is repeatable, and on one GPU it is not slower.

`tag --reset` drops all machine tags, names and descriptions and asks again, for trying another prompt or model.

`--vocabulary-hint FILE` adds the site's existing tags (one per line) to the prompt as preferred words, for adding new photos to a filled site.

The model runs in a separate server, so GPU acceleration (CUDA, Vulkan, Metal, ROCm) is that server's setting. The tool only needs its address.

**embed.** The vectors on the site were computed by this code: SigLIP 2 vision tower, `pooler_output`, L2 normalization, float32. Any change to that makes new vectors incomparable with the stored ones, which is why the integration test `tests/test_compat.py` recomputes vectors of known photos and compares them with the old database.

One difference is deliberate: photos are turned upright by their EXIF orientation before encoding. The first run of the site skipped that, so its vectors of portrait photos were computed from the sideways image; new vectors of such photos differ from those. Both still work for "similar media", the upright ones somewhat better.

Device `auto` picks CUDA (NVIDIA, or AMD with a ROCm build of torch on Linux), then Apple `mps`, then CPU. AMD GPUs on Windows run on CPU; for this model that is minutes per thousand photos.

All vectors in a workspace must come from one model. After changing `[embed] model`, run `embed --reset` to recompute everything.

## Tests

```
uv-local run --directory src/analysis --extra dev pytest                  # unit tests, no models are loaded
uv-local run --directory src/analysis --extra dev pytest -m integration   # downloads the model on first run
```

`--directory` matters: pytest reads its settings (including skipping integration tests by default) from `src/analysis/pyproject.toml` only when started there.

The integration test needs the old `photos.sqlite` and the original photos it was built from (by default both in `examples/` at the repository root, which git ignores); see the top of `tests/test_compat.py`.
