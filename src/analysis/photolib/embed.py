"""Stage 5: SigLIP 2 multilingual image embeddings, stored as float32 blobs."""

import numpy as np

from . import db

DEFAULT_MODEL_ID = "google/siglip2-base-patch16-256"

_state = {}


def to_blob(vec):
    return np.asarray(vec, dtype=np.float32).tobytes()


def from_blob(blob):
    return np.frombuffer(blob, dtype=np.float32)


def _load(model_id=DEFAULT_MODEL_ID):
    if _state.get("id") != model_id:
        import torch
        from transformers import AutoModel, AutoProcessor

        device = "mps" if torch.backends.mps.is_available() else "cpu"
        _state.clear()
        _state["torch"] = torch
        _state["device"] = device
        _state["model"] = AutoModel.from_pretrained(model_id).to(device).eval()
        _state["proc"] = AutoProcessor.from_pretrained(model_id)
        _state["id"] = model_id
    return _state


def _normalize(vec):
    vec = np.asarray(vec, dtype=np.float32)
    return (vec / (np.linalg.norm(vec) + 1e-12)).astype(np.float32)


def embed_image(path, model_id=DEFAULT_MODEL_ID):
    from PIL import Image

    state = _load(model_id)
    torch = state["torch"]
    with Image.open(path) as im:
        im = im.convert("RGB")
        inputs = state["proc"](images=im, return_tensors="pt").to(state["device"])
        with torch.no_grad():
            # pooler_output is the single pooled image embedding (1, dim). Do NOT
            # use get_image_features: in transformers 5.x it returns a ModelOutput
            # whose [0] is the un-pooled (1, num_patches, dim) grid, which silently
            # stored ~256x too much data and broke image-text search.
            feats = state["model"].vision_model(**inputs).pooler_output
    return _normalize(feats[0].float().cpu().numpy())


def run_embed(conn, model_id=DEFAULT_MODEL_ID):
    for row in db.pending(conn, "embed"):
        try:
            vec = embed_image(row["path"], model_id)
            conn.execute(
                "INSERT OR REPLACE INTO embeddings(image_id, model, dim, vector)"
                " VALUES (?, ?, ?, ?)",
                (row["id"], model_id, len(vec), to_blob(vec)),
            )
            db.set_status(conn, row["id"], "embed", "done")
        except Exception as exc:  # noqa: BLE001 -- record and continue
            db.set_status(conn, row["id"], "embed", "error", str(exc))
        conn.commit()
