# HaruyasumiRyokouki.DataTool

Tool that fills the [HaruyasumiRyokouki](https://github.com/meckbaig/HaruyasumiRyokouki.Backend) travelogue with data: a folder of trip photos and videos goes in; files in the site's storage, media records with search tags in three languages and image vectors for "similar media" come out. Everything a model can do is done by a model; a person only reviews the tag vocabulary and confirms every write to the site.

How the data flows:

1. **haru-analysis** (Python, runs locally) indexes the folder, takes a frame from every video, asks a vision model for tags and names on every photo and computes SigLIP 2 image vectors. Results go to `work.sqlite` in a workspace folder. See [src/analysis/README.md](src/analysis/README.md).
2. **haru-tags** (.NET) turns the machine tags into a vocabulary: merges duplicates, drops what nobody would search for, translates to English, Russian and Japanese, and, after a person has reviewed it, creates the tags on the site, uploads the files, syncs them and attaches tags and vectors through the site's API. See [src/HaruyasumiRyokouki.DataTool.Tags/README.md](src/HaruyasumiRyokouki.DataTool.Tags/README.md).
3. **haru-data** (.NET) runs the steps in order, stops for review and confirmations, and shows where a run stands.

The tool is under development: the analysis steps work; vocabulary, import and the orchestrator are being built. The file formats shared by the apps are in [docs/formats.md](docs/formats.md).

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
