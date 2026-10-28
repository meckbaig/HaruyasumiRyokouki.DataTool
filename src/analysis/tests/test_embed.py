import types

import numpy as np
import pytest

from haru_analysis import embed, images, scan
from haru_analysis.workspace import StepReport


def test_blob_round_trip_preserves_vector():
    vec = np.array([0.1, -0.2, 0.3], dtype=np.float32)
    restored = embed.from_blob(embed.to_blob(vec))
    assert np.allclose(restored, vec)
    assert restored.dtype == np.float32


def fake_torch(cuda, mps):
    return types.SimpleNamespace(
        cuda=types.SimpleNamespace(is_available=lambda: cuda),
        backends=types.SimpleNamespace(mps=types.SimpleNamespace(is_available=lambda: mps)),
    )


@pytest.mark.parametrize("cuda, mps, expected", [
    (True, True, "cuda"),
    (False, True, "mps"),
    (False, False, "cpu"),
])
def test_auto_device_order(cuda, mps, expected):
    assert embed.pick_device(fake_torch(cuda, mps), "auto") == expected


def test_explicit_device_is_kept():
    assert embed.pick_device(fake_torch(True, False), "cpu") == "cpu"


def test_photo_is_turned_upright_by_exif(tmp_path):
    from PIL import Image

    # Stored 40x20 (landscape) with orientation 6 = "rotate 90 to view": upright it is portrait.
    path = tmp_path / "rotated.jpg"
    exif = Image.Exif()
    exif[0x0112] = 6
    Image.new("RGB", (40, 20), "red").save(path, exif=exif)

    im = images.open_upright_rgb(path)
    assert im.size == (20, 40)
    assert im.mode == "RGB"


def unit(i):
    vec = np.zeros(4, dtype=np.float32)
    vec[i % 4] = 1.0
    return vec


def setup(make_workspace, extra_toml=""):
    ws, src = make_workspace(extra_toml)
    (src / "IMG_1.jpg").write_bytes(b"x")
    (src / "clip.mp4").write_bytes(b"x")
    conn = ws.connect()
    scan.run_scan(conn, src, StepReport("scan"))
    return ws, conn


def test_embeds_images_and_video_frames(make_workspace):
    ws, conn = setup(make_workspace)
    ws.frames_dir.mkdir()
    ws.frame_path("clip").write_bytes(b"jpeg")
    conn.execute("UPDATE media SET frame_status = 'done' WHERE key = 'clip'")
    conn.commit()

    seen = []
    report = StepReport("embed")
    embed.run_embed(conn, ws, report, embed_fn=lambda p: seen.append(str(p)) or unit(len(seen)))

    assert any(p.endswith("IMG_1.jpg") for p in seen)
    assert str(ws.frame_path("clip")) in seen
    rows = conn.execute("SELECT key, model, dim FROM embeddings ORDER BY key").fetchall()
    assert [tuple(r) for r in rows] == [
        ("clip", "google/siglip2-base-patch16-256", 4),
        ("img_1", "google/siglip2-base-patch16-256", 4),
    ]
    assert report.counts["done"] == 2


def test_video_without_frame_is_an_error(make_workspace):
    ws, conn = setup(make_workspace)
    report = StepReport("embed")
    embed.run_embed(conn, ws, report, embed_fn=lambda p: unit(0))
    assert report.errors == [{"key": "clip", "message": "no video frame, run the frames step first"}]
    assert conn.execute("SELECT embed_status FROM media WHERE key = 'img_1'").fetchone()[0] == "done"


def test_rerun_skips_done(make_workspace):
    ws, conn = setup(make_workspace)
    calls = []
    embed.run_embed(conn, ws, StepReport("embed"), embed_fn=lambda p: calls.append(p) or unit(0))
    embed.run_embed(conn, ws, StepReport("embed"), embed_fn=lambda p: calls.append(p) or unit(0))
    # img_1 once; the video errors (no frame) and is retried
    assert sum(str(p).endswith("IMG_1.jpg") for p in calls) == 1


def test_model_change_needs_reset(make_workspace):
    ws, conn = setup(make_workspace)
    embed.run_embed(conn, ws, StepReport("embed"), embed_fn=lambda p: unit(0))

    other = ws.root / "haru-data.toml"
    other.write_text(other.read_text(encoding="utf-8") + "[embed]\nmodel = 'other/model'\n", encoding="utf-8")
    ws = type(ws)(ws.root)
    with pytest.raises(embed.ModelMismatch, match="--reset"):
        embed.run_embed(conn, ws, StepReport("embed"), embed_fn=lambda p: unit(0))

    embed.run_embed(conn, ws, StepReport("embed"), reset=True, embed_fn=lambda p: unit(0))
    models = {r[0] for r in conn.execute("SELECT model FROM embeddings")}
    assert models == {"other/model"}
