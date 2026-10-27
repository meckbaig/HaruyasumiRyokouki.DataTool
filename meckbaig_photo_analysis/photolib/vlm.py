"""Stage 4: Qwen2.5-VL via Ollama produces description, tags, and scene fields."""

import io
import json

from PIL import Image

from . import db

DEFAULT_MODEL = "qwen2.5vl:7b"

# Downscale each image before sending it to the model. Qwen2.5-VL turns a
# full-resolution phone photo (~12 MP) into ~4000+ image tokens, and that image
# token count dominates inference time (~25 s/image). Capping the long side to
# about 1024 px cuts it to ~1200 tokens and roughly 3x the throughput, with no
# loss of scene/tag quality (on-image text is read by the separate Apple Vision
# OCR stage, not the VLM). Set to 0 via the vlm --max-side flag to disable.
MAX_IMAGE_SIDE = 1024

# Context window for the Ollama request. Downscaled images are ~1200 tokens, so
# 4096 leaves a comfortable margin and avoids the 400 exceed_context_size_error.
# Raise it with the vlm --num-ctx flag if you disable resizing or feed unusually
# large images.
DEFAULT_NUM_CTX = 4096

PROMPT = (
    "You are analyzing one travel photo. Return STRICT JSON only, no prose.\n"
    "Keys and types:\n"
    '  "description": string, English, 1-3 factual sentences, no invented details.\n'
    '  "tags": array of lowercase English keywords (objects, scene, activities).\n'
    '  "scene_setting": "indoor" or "outdoor" or null.\n'
    '  "scene_category": short English label, for example temple, street, food.\n'
    '  "time_of_day": "day" or "night" or "dusk" or null.\n'
    '  "location_guess": object {"name": string or null,'
    ' "confidence": "low" or "medium" or "high", "reasoning": string}.\n'
    '  "people_present": boolean.\n'
    "If you are not sure of the specific city or landmark, set location_guess.name"
    " to null. Do not identify individuals."
)

REQUIRED = (
    "description", "tags", "scene_setting", "scene_category",
    "time_of_day", "location_guess", "people_present",
)


def parse_and_validate(raw):
    data = json.loads(raw)
    for key in REQUIRED:
        if key not in data:
            raise ValueError("missing key: " + key)
    if not isinstance(data["tags"], list):
        raise ValueError("tags must be a list")
    guess = data["location_guess"]
    if not isinstance(guess, dict) or "name" not in guess or "confidence" not in guess:
        raise ValueError("bad location_guess")
    return data


def _prepare_image(path, max_side=MAX_IMAGE_SIDE):
    """Return JPEG bytes for the image, downscaled so its long side is max_side."""
    with Image.open(path) as im:
        im = im.convert("RGB")
        im.thumbnail((max_side, max_side))
        buf = io.BytesIO()
        im.save(buf, format="JPEG", quality=90)
    return buf.getvalue()


def call_ollama(path, model=DEFAULT_MODEL, num_ctx=DEFAULT_NUM_CTX, max_side=MAX_IMAGE_SIDE):
    import ollama

    image = _prepare_image(path, max_side) if max_side else path
    resp = ollama.chat(
        model=model,
        messages=[{"role": "user", "content": PROMPT, "images": [image]}],
        format="json",
        options={"temperature": 0, "num_ctx": num_ctx},
    )
    return resp["message"]["content"]


def analyze(path, model=DEFAULT_MODEL, caller=call_ollama,
            num_ctx=DEFAULT_NUM_CTX, max_side=MAX_IMAGE_SIDE):
    raw = caller(path, model, num_ctx=num_ctx, max_side=max_side)
    try:
        return parse_and_validate(raw)
    except (ValueError, json.JSONDecodeError):
        raw = caller(path, model, num_ctx=num_ctx, max_side=max_side)  # one retry
        return parse_and_validate(raw)


def run_vlm(conn, model=DEFAULT_MODEL, num_ctx=DEFAULT_NUM_CTX, max_side=MAX_IMAGE_SIDE):
    done = 0
    errors = 0
    for row in db.pending(conn, "vlm"):
        try:
            data = analyze(row["path"], model, num_ctx=num_ctx, max_side=max_side)
            conn.execute(
                "INSERT OR REPLACE INTO vlm(image_id, description, tags_json,"
                " scene_setting, scene_category, time_of_day, location_guess, raw_json)"
                " VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                (
                    row["id"],
                    data["description"],
                    json.dumps(data["tags"], ensure_ascii=False),
                    data["scene_setting"],
                    data["scene_category"],
                    data["time_of_day"],
                    json.dumps(data["location_guess"], ensure_ascii=False),
                    json.dumps(data, ensure_ascii=False),
                ),
            )
            db.set_status(conn, row["id"], "vlm", "done")
            done += 1
        except Exception as exc:  # noqa: BLE001 -- record and continue
            db.set_status(conn, row["id"], "vlm", "error", str(exc))
            errors += 1
        conn.commit()
    print("vlm: " + str(done) + " ok, " + str(errors) + " error, "
          + str(done + errors) + " processed")
    if errors:
        print("  some images failed; run the report command for per-file errors")
    return {"done": done, "errors": errors, "processed": done + errors}
