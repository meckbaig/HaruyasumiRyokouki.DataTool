"""Brute-force cosine search over stored embeddings, plus a text query encoder."""

import numpy as np

from . import embed


def cosine_search(conn, query_vec, k=20):
    rows = conn.execute("SELECT image_id, vector FROM embeddings").fetchall()
    if not rows:
        return []
    matrix = np.vstack([embed.from_blob(r["vector"]) for r in rows])
    ids = [r["image_id"] for r in rows]
    query = np.asarray(query_vec, dtype=np.float32)
    scores = matrix @ query
    order = np.argsort(-scores)[:k]
    return [(ids[i], float(scores[i])) for i in order]


def embed_text(query, model_id=embed.DEFAULT_MODEL_ID):
    state = embed._load(model_id)
    torch = state["torch"]
    inputs = state["proc"](
        text=[query], return_tensors="pt", padding="max_length"
    ).to(state["device"])
    with torch.no_grad():
        # pooler_output is the single pooled text embedding (1, dim), matching the
        # image side. get_text_features returns an un-pooled grid in transformers 5.x.
        feats = state["model"].text_model(**inputs).pooler_output
    vec = feats[0].float().cpu().numpy()
    return (vec / (np.linalg.norm(vec) + 1e-12)).astype(np.float32)
