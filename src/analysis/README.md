# haru-analysis

The offline part of the data tool: it indexes the trip folder, takes a frame from every video and computes image vectors for "similar media" on the site. Nothing here talks to the backend; it only fills `work.sqlite` in the workspace, which `haru-tags` reads later.

Normally the steps are started by `haru-data run`. Run them directly to repeat or debug one step:

```
uv-local run --project src/analysis haru-analysis scan   --workspace my-trip
uv-local run --project src/analysis haru-analysis frames --workspace my-trip
uv-local run --project src/analysis haru-analysis embed  --workspace my-trip
uv-local run --project src/analysis haru-analysis status --workspace my-trip
```

`uv-local` (`uv-local.cmd` on Windows, `./uv-local.sh` elsewhere, in the repository root) is plain `uv` that keeps everything it downloads in the repository.

Every step writes `reports/<step>.json` and exits with `0` (ok) or `1` (error). Each file is saved as soon as it is processed, so a step stopped with Ctrl+C or by an error continues where it left off on the next run.

## Setup

[uv](https://docs.astral.sh/uv/) installs the right Python (pinned in `.python-version`: torch has no builds for the newest one yet) and the dependencies on the first run.

Nothing is installed into the system or the user profile: `uv-local` points uv's package cache, its Python installs and the Hugging Face model cache to `.cache/` in the repository root. The project environment lives in `src/analysis/.venv`. Deleting these two folders removes everything the tool downloaded (about 2.5 GB, mostly torch and the SigLIP 2 model). Plain `uv` works too, but then the downloads land in the user profile.

`frames` needs `ffmpeg` in `PATH`.

## Settings

The steps read `haru-data.toml` in the workspace folder. `haru-data init` creates it; until then, this is enough:

```toml
source = "D:/Photos/Japan"                   # trip folder, searched recursively; relative paths are resolved against the workspace

[embed]
model = "google/siglip2-base-patch16-256"   # must match the backend: it compares vectors of exactly this model, 768 floats
device = "auto"                             # auto | cuda | mps | cpu
```

## How the steps work

**scan.** Files are matched to the site by their key: the file name without the last extension, in lower case. The backend keeps that part of the name when it converts photos (`IMG_1.HEIC` becomes `IMG_1.webp`), so the key is the only reliable link between a local file and its `media_files` row. Two files with the same key (`IMG_1.jpg` and `img_1.png`, possibly in different subfolders) cannot be told apart on the site, so scan stops with the list of such files and writes nothing. File types are the ones the backend imports; everything else is ignored.

Running scan again is safe: new files are added, a moved folder only updates paths, a changed file (size or modification time) loses its frame and vector so they are computed again, and a vanished file is marked missing rather than deleted.

**frames.** The backend makes a video's preview from the frame at 1 second, so the tool takes the same frame (`frames/<key>.jpg`) and the later steps use it instead of the video. Videos shorter than a second get their first frame.

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
