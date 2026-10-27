"""Step embed: SigLIP 2 image embeddings, stored as float32 blobs.

The vectors already stored on the site were computed by this exact code
(model, pooler_output, L2 normalization). Changing any of it makes new vectors
incomparable with the old ones; the integration test test_compat.py checks that.

One deliberate difference: photos are turned upright by their EXIF orientation
first. The old code skipped that and encoded portrait photos lying on their side,
so for rotated photos the new vectors differ from the old ones on the site.
"""

import numpy as np

from . import db
from .progress import Progress

DEFAULT_MODEL_ID = "google/siglip2-base-patch16-256"

_state = {}


class ModelMismatch(Exception):
    pass


def to_blob(vec):
    return np.asarray(vec, dtype=np.float32).tobytes()


def from_blob(blob):
    return np.frombuffer(blob, dtype=np.float32)


def pick_device(torch, wanted="auto"):
    """auto picks cuda (NVIDIA, or AMD with a ROCm build of torch on Linux), then mps (Apple), then cpu."""
    if wanted != "auto":
        return wanted
    if torch.cuda.is_available():
        return "cuda"
    if torch.backends.mps.is_available():
        return "mps"
    return "cpu"


def _load(model_id=DEFAULT_MODEL_ID, device="auto"):
    if _state.get("id") != model_id or _state.get("wanted") != device:
        import torch
        from transformers import AutoModel, AutoProcessor

        actual = pick_device(torch, device)
        _state.clear()
        _state["torch"] = torch
        _state["device"] = actual
        _state["model"] = AutoModel.from_pretrained(model_id).to(actual).eval()
        _state["proc"] = AutoProcessor.from_pretrained(model_id)
        _state["id"] = model_id
        _state["wanted"] = device
    return _state


def _normalize(vec):
    vec = np.asarray(vec, dtype=np.float32)
    return (vec / (np.linalg.norm(vec) + 1e-12)).astype(np.float32)


def open_upright_rgb(path):
    """Open an image (HEIC included) as RGB, rotated as the camera's EXIF orientation says."""
    from PIL import Image, ImageOps
    from pillow_heif import register_heif_opener

    register_heif_opener()
    with Image.open(path) as im:
        return ImageOps.exif_transpose(im).convert("RGB")


def embed_image(path, model_id=DEFAULT_MODEL_ID, device="auto"):
    state = _load(model_id, device)
    torch = state["torch"]
    im = open_upright_rgb(path)
    inputs = state["proc"](images=im, return_tensors="pt").to(state["device"])
    with torch.no_grad():
        # pooler_output is the single pooled image embedding (1, dim). Do NOT
        # use get_image_features: in transformers 5.x it returns a ModelOutput
        # whose [0] is the un-pooled (1, num_patches, dim) grid, which silently
        # stored ~256x too much data and broke image-text search.
        feats = state["model"].vision_model(**inputs).pooler_output
    return _normalize(feats[0].float().cpu().numpy())


def run_embed(conn, workspace, report, reset=False, embed_fn=None):
    model_id = workspace.embed_model
    device = workspace.embed_device
    embed_fn = embed_fn or (lambda path: embed_image(path, model_id, device))

    if reset:
        conn.execute("DELETE FROM embeddings")
        conn.execute("UPDATE media SET embed_status = 'pending', embed_error = NULL")
        conn.commit()
    else:
        # Vectors of different models live in different spaces and cannot be compared.
        other = conn.execute("SELECT DISTINCT model FROM embeddings WHERE model != ?", (model_id,)).fetchall()
        if other:
            raise ModelMismatch(
                f"work.sqlite has vectors of {', '.join(r['model'] for r in other)}, settings say {model_id}."
                " Run embed with --reset to recompute all vectors with the new model."
            )

    rows = conn.execute(
        "SELECT key, path, type, frame_status FROM media"
        " WHERE missing = 0 AND embed_status != 'done' ORDER BY key"
    ).fetchall()
    report.count("skipped", conn.execute(
        "SELECT COUNT(*) FROM media WHERE missing = 0 AND embed_status = 'done'").fetchone()[0])
    progress = Progress("embed", len(rows))
    for row in rows:
        try:
            if row["type"] == "video":
                image = workspace.frame_path(row["key"])
                if row["frame_status"] != db.DONE or not image.is_file():
                    raise RuntimeError("no video frame, run the frames step first")
            else:
                image = row["path"]
            vec = embed_fn(image)
            conn.execute(
                "INSERT OR REPLACE INTO embeddings(key, model, dim, vector) VALUES (?, ?, ?, ?)",
                (row["key"], model_id, len(vec), to_blob(vec)),
            )
            db.set_status(conn, row["key"], "embed", db.DONE)
            report.count("done")
        except Exception as exc:  # noqa: BLE001 -- record and continue
            db.set_status(conn, row["key"], "embed", db.ERROR, str(exc))
            report.error(str(exc), row["key"])
        conn.commit()
        progress.step()
    progress.close()
    if "device" in _state:
        report.message = f"model {model_id} on {_state['device']}"
