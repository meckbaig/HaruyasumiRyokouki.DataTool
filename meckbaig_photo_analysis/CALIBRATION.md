# Calibration Runbook

This runbook guides you through calibrating the photo analysis pipeline on a small sample of real photos before running against your full photo library. The calibration process validates all stages, measures performance, and helps you decide whether to adjust model selection based on runtime constraints.

## Prerequisites

Before starting calibration, ensure all setup steps are complete:

### 1. Create Python virtual environment

```bash
python3 -m venv .venv
source .venv/bin/activate
```

### 2. Install core dependencies (for calibration)

```bash
pip install -e ".[dev]"
```

### 3. Install model backends (required for real runs)

```bash
pip install -e ".[models]"
```

This installs Ollama, transformers, torch, and sentencepiece needed for OCR, VLM (Qwen2.5-VL), and embedding (SigLIP2) stages.

### 4. Install and set up Ollama

1. Download and install Ollama from https://ollama.com
2. Pull the default VLM model:

```bash
ollama pull qwen2.5vl:7b
```

This downloads a 7B parameter vision-language model (approx 5 GB) used for photo description and tagging.

### 5. Build Apple Vision OCR helper

The OCR stage uses an Apple Vision framework helper for efficient Japanese/English text extraction:

```bash
swiftc ocr_helper/vision_ocr.swift -o ocr_helper/vision_ocr
```

This creates a compiled binary that the ocr module calls via subprocess.

### 6. Verify embeddings model setup

The embedding stage uses SigLIP2 from Hugging Face. The model downloads automatically on first run; no action needed here. The default is `google/siglip2-base-patch16-256` (approx 1.3 GB).

## Step 1: Assemble a Sample

Collect 30-50 varied real photos from your trip into a new `sample/` folder. Include diverse categories to stress-test all pipeline stages:

- **Signage photos** (5-10): signs with Japanese/English text to test OCR
- **Food photos** (5-10): close-ups and wide shots to test description and tagging
- **Landscapes** (5-10): outdoor scenes, architecture to test location guessing and tags
- **Indoor photos** (5-10): rooms, museums, interiors
- **People photos** (3-5): crowds, street scenes (no individual identification)
- **Edited variants** (1-2): copy one photo and edit it (brightness, crop, filters) to verify metadata consistency

Create the folder and copy photos:

```bash
mkdir sample
cp /path/to/selected/photos/* sample/
```

## Step 2: Run Every Pipeline Stage on the Sample

Initialize the sample database and process it through all stages. The `--db` flag must come BEFORE the subcommand.

### Init

Create the schema:

```bash
python -m photolib.cli --db sample.sqlite init
```

### Index

Scan the sample folder and populate the images table:

```bash
python -m photolib.cli --db sample.sqlite index --root sample
```

### Metadata

Extract EXIF metadata (capture date, dimensions, orientation):

```bash
python -m photolib.cli --db sample.sqlite metadata
```

### OCR

Extract visible text (Japanese and English) from photos using Apple Vision:

```bash
python -m photolib.cli --db sample.sqlite ocr
```

### VLM (with timing)

Generate English descriptions, tags, and scene metadata using Qwen2.5-VL. This stage is the slowest; time it carefully:

```bash
time python -m photolib.cli --db sample.sqlite vlm
```

Note the real, user, and sys time from the output. The stage prints a summary
line (`vlm: N ok, M error, K processed`); divide the total real time by the
number of photos to estimate per-image cost.

Speed knobs (both optional, tune here before the full run):

- `--max-side N` (default 1024): each image is downscaled so its long side is
  at most N pixels before being sent to the model. A full-resolution phone photo
  becomes ~4000 image tokens (~25 s/image); at 1024 px it is ~1200 tokens
  (~8 s/image, roughly 3x faster) with no loss of scene/tag quality, since
  on-image text is read by the separate Apple Vision OCR stage. Use `--max-side 0`
  to disable resizing (then also raise `--num-ctx`).
- `--num-ctx N` (default 4096): Ollama context window. 4096 fits a downscaled
  image comfortably; raise it only if you disable resizing or feed unusually
  large images (the symptom of it being too small is a 400
  exceed_context_size_error recorded in error_vlm).

Example, smaller/faster model and a tighter image cap:

```bash
time python -m photolib.cli --db sample.sqlite vlm --model qwen2.5vl:3b --max-side 896
```

### Embed

Compute multilingual image embeddings using SigLIP2:

```bash
python -m photolib.cli --db sample.sqlite embed
```

### Report

Generate a summary of pipeline completion status:

```bash
python -m photolib.cli --db sample.sqlite report
```

Review the counts: all images should show status "done" for each stage. If any show "error" or "pending", note the image id and check the database for error messages.

## Step 3: Inspect Quality by Eye

Query the sample database to verify output quality. Use sqlite3 or your preferred tool to inspect the following:

### Check VLM outputs

```bash
sqlite3 sample.sqlite <<'EOF'
SELECT image_id, description, tags_json, scene_category, location_guess
FROM vlm LIMIT 10;
EOF
```

For each row, verify:

- **description**: English, factual, 1-3 sentences, no invented details (e.g. not hallucinating landmarks that are not in the photo)
- **tags_json**: Array of lowercase keywords (objects, scene type, activities) that match the photo content
- **scene_category**: Label like "temple", "street", "food", "nature"
- **location_guess.name**: Should be null when the model is unsure about specific location; only populated when confident

### Check OCR outputs

```bash
sqlite3 sample.sqlite <<'EOF'
SELECT image_id, text, lang_hint FROM ocr LIMIT 10;
EOF
```

For photos with signage:

- **text**: Captured text should be accurate (not corrupted)
- **lang_hint**: Should correctly identify "en", "ja", or "mixed" based on signage content

### Record Quality Findings

In a text file or notes, document your observations:

```
Sample Quality Assessment:
- VLM descriptions: [PASS/CONCERN] (e.g. "mostly accurate, one hallucinated landmark")
- VLM tags: [PASS/CONCERN] (e.g. "useful and specific")
- VLM location_guess: [PASS/CONCERN] (e.g. "correctly null when unsure")
- OCR text accuracy: [PASS/CONCERN] (e.g. "Japanese kanji captured correctly")
- OCR language detection: [PASS/CONCERN] (e.g. "mixed English-Japanese correctly marked")
- Metadata EXIF: [PASS/CONCERN] (e.g. "dates and orientation correct")
```

## Step 4: Decide on Model and Runtime

### Extrapolate VLM Performance

From the `time` output in Step 2, calculate the per-image cost:

```
Per-image VLM time = total_real_time / number_of_photos
```

For example, with the default 1024 px downscaling, a warm Qwen2.5-VL 7B runs at
roughly 8 to 9 seconds per image on an M4 Pro:
```
Per-image time = 8.5 seconds
Full library estimate = 8.5 * 10000 = 85000 seconds = ~24 hours
```

### Assess Runtime Acceptability

The VLM stage is resumable, so a multi-hour run can be interrupted and continued.

- **Acceptable**: proceed to the full run (overnight and into the next day is
  normal for ~10000 photos at 7B).
- **Too slow**: apply these levers, biggest win first, and re-time on the sample:
  - Confirm image downscaling is on (it is by default, `--max-side 1024`); this
    is already the largest speedup (~3x vs full resolution).
  - Try the smaller same-family model: `--model qwen2.5vl:3b` (pull it first with
    `ollama pull qwen2.5vl:3b`). Roughly 1.5 to 2x faster; compare quality on the
    sample before committing.
  - Run concurrent requests: set `OLLAMA_NUM_PARALLEL` and drive the stage with
    multiple workers (needs more memory; measure the actual gain).
  - Switch the backend to MLX-VLM (same Qwen2.5-VL family, tuned for Apple
    Silicon) behind the call_ollama seam; measure before adopting.

### Embedding Model Decision

The default SigLIP2 (google/siglip2-base-patch16-256) is balanced for speed and quality. Alternative:
- If embedding quality is critical, try `google/siglip2-so400m-patch16-384` (larger, slower, better semantic understanding)
- Speed overhead: roughly 2-3x slower per image

To use an alternative, pass `--model-id` to the embed stage:

```bash
python -m photolib.cli --db sample.sqlite embed --model-id google/siglip2-so400m-patch16-384
```

### Record Your Decision

Document the decision for reference during full run:

```
Calibration Decision:
- VLM model: [qwen2.5vl:7b] or [qwen2.5vl:3b] or [mlx version]
- VLM max-side: [1024] (or chosen value)
- VLM per-image time: X seconds
- Estimated full run time (10000 photos): Y hours
- Embedding model: [google/siglip2-base-patch16-256] or [google/siglip2-so400m-patch16-384]
- Rationale: [e.g. "current setup acceptable within overnight window" or "switching to phi3.5 reduces runtime to 6 hours"]
```

## Step 5: Commit the Calibration Results

Once you have completed all steps and validated the pipeline works:

```bash
git add CALIBRATION.md
git commit -m "docs: calibration runbook and recorded results"
```

## Step 6: Full Run (Post-Calibration)

After calibration is complete and validated, run the full pipeline against your complete photo library using the model and settings you chose.

### Initialize full database

```bash
python -m photolib.cli --db photos.sqlite init
```

### Index all photos

Replace `/path/to/all/photos` with the actual directory containing your full photo library:

```bash
python -m photolib.cli --db photos.sqlite index --root /path/to/all/photos
```

### Run all stages

Execute the remaining stages in order:

```bash
python -m photolib.cli --db photos.sqlite metadata
python -m photolib.cli --db photos.sqlite ocr
python -m photolib.cli --db photos.sqlite vlm
python -m photolib.cli --db photos.sqlite embed
python -m photolib.cli --db photos.sqlite report
```

### Resuming interrupted runs

If the pipeline is interrupted (crash, Ollama shutdown, etc.), simply re-run the same command. Each stage:
- Skips photos already marked "done"
- Retries photos marked "pending" (partial failure, no output recorded)
- Retries photos marked "error" and records a new attempt

No data is lost; run the same command to continue.

### Investigating errors

After a full run, check for leftover errors:

```bash
python -m photolib.cli --db photos.sqlite report
```

If the report shows images in "error" status, query the database to see error messages:

```bash
sqlite3 photos.sqlite <<'EOF'
SELECT id, path, status_ocr, error_ocr, status_vlm, error_vlm
FROM images
WHERE status_meta='error' OR status_ocr='error' OR status_vlm='error' OR status_embed='error';
EOF
```

Or simply run `python -m photolib.cli --db sample.sqlite report`, which now lists each failed file with its stage and error message.

Common issues:
- **VLM timeout**: Ollama service not running; restart with `ollama serve`
- **OCR failure**: Vision framework error; try re-running (transient) or inspect the image (corrupted)
- **Embedding failure**: Torch/GPU memory; reduce batch size (code change required) or run on CPU only
- **Index failure**: File moved/deleted between index and processing; re-run index to update paths

## Summary

Calibration validates:
1. All pipeline dependencies are installed and working
2. Model outputs are reasonable (no hallucination, good tags, accurate OCR)
3. Runtime is acceptable for your photo library size
4. Appropriate model choices are made for your speed/quality trade-off

After calibration passes, the full run uses the same commands and is expected to complete without intervention, resuming automatically if interrupted.
