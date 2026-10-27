"""Vectors computed now must match the ones already stored on the site.

Needs the real model (downloaded on first run) and two things that are not in
the repository: the old photolib database and the original photos it was built
from. Defaults are the author's working copy layout; override with
HARU_COMPAT_DB and HARU_COMPAT_PHOTOS. Run with: pytest -m integration

Only photos without EXIF rotation are compared: for rotated ones the new code
turns the photo upright and the old one did not, so they differ on purpose.
"""

import os
import sqlite3
from pathlib import Path

import numpy as np
import pytest

from haru_analysis import embed

REPO = Path(__file__).resolve().parents[3]
OLD_DB = Path(os.environ.get("HARU_COMPAT_DB", REPO / "examples" / "photos.sqlite"))
PHOTOS = Path(os.environ.get("HARU_COMPAT_PHOTOS", REPO / "examples"))
MIN_COSINE = 0.999


@pytest.mark.integration
def test_vectors_match_old_photolib():
    if not OLD_DB.is_file() or not PHOTOS.is_dir():
        pytest.skip(f"needs {OLD_DB} and {PHOTOS}")
    conn = sqlite3.connect(f"file:{OLD_DB}?mode=ro", uri=True)
    old = {
        name: (model, np.frombuffer(blob, dtype=np.float32))
        for name, model, blob in conn.execute(
            "SELECT i.filename, e.model, e.vector FROM images i JOIN embeddings e ON e.image_id = i.id"
        )
    }
    photos = sorted(p for p in PHOTOS.iterdir() if p.name in old and _orientation(p) == 1)
    assert len(photos) >= 10, "need at least 10 unrotated photos that are in the old database"

    worst = 1.0
    for path in photos:
        model, old_vec = old[path.name]
        new_vec = embed.embed_image(path, model, device="cpu")
        cosine = float(np.dot(old_vec, new_vec))
        worst = min(worst, cosine)
        assert cosine >= MIN_COSINE, f"{path.name}: cosine {cosine:.5f}"
    print(f"{len(photos)} photos, worst cosine {worst:.6f}")


def _orientation(path):
    from PIL import Image

    with Image.open(path) as im:
        return im.getexif().get(0x0112, 1)
