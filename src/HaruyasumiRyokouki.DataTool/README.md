# haru-data

The entry point of the data tool. It creates a workspace, runs the steps of `haru-analysis` and `haru-tags` in their order, stops where a person is needed and tells where the cycle is.

```
haru-data init my-trip --source D:/Photos/Japan   # workspace with haru-data.toml and .env
cd my-trip
haru-data doctor                                  # programs, models, servers, secrets
haru-data run                                     # the steps that are not done
haru-data status                                  # where the cycle is and what is next
haru-data review                                  # the review page in the browser: the tag list, then the translations
haru-data review --check | --approve              # the same in the terminal, on the YAML files
haru-data run embed                               # one step, even if it is done
```

`haru-data` is `src/HaruyasumiRyokouki.DataTool/bin/Debug/net8.0/haru-data` after `dotnet build`, or `dotnet run --project src/HaruyasumiRyokouki.DataTool --`. Every command takes `--workspace <folder>`; without it the current folder is the workspace.

## What run does

Each step is a separate process with the console of `haru-data`, so its progress and its questions appear as they are; the step writes `reports/<step>.json` and its exit code tells what happened (`0` done, `1` error, `2` a person is needed).

- **Local steps** (`scan`, `frames`, `tag`, `embed`) start every time. They skip the files that are done, so after new photos are added only those are processed, and with nothing new they finish in seconds. `tag` needs the vision model's server only when there are files to tag.
- **`vocab`** calls the paid cloud model, so it starts only when the set of machine tags changed since it last finished. Before it, `vocab --estimate` prints the number of requests and tokens, and `run` asks.
- **`review`** starts every time and stops the run until `vocabulary.yaml` is approved, and again while `translations.yaml` waits. `--no-review` approves both as they were made.
- **`translate`** starts when a new vocabulary was approved. It translates the new tags through the site's AI and stops the run only when the checks found problems in the translations.
- **Steps that write to the site** (`push-tags`, `upload`, `sync`, `assign`, `push-embeddings`) start only when their input changed: a new approved vocabulary, new files, new vectors, new ids on the site. Each prints its target and asks; `--yes` answers for them, the targets are still printed.

The run stops at the first error, at a declined question and at the review, and says what to do. Work that finished is kept; `haru-data run` continues from there. A second run of a finished cycle starts only the local steps and the review, which find nothing to do.

What changed since a step finished is kept in `work.sqlite` as a fingerprint of its input (see [docs/formats.md](../../docs/formats.md)); `status` shows a step whose input changed as "to run".

## Where things go

`haru-analysis` runs through `uv run --project src/analysis`; `haru-data` points `UV_CACHE_DIR`, `UV_PYTHON_INSTALL_DIR` and `HF_HOME` at `.cache/` of the repository unless they are set, so Python, the packages and the SigLIP model stay next to the tool and go away with it. `haru-tags` is built next to `haru-data`.
