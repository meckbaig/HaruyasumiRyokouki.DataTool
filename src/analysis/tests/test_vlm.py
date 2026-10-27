import io
import json
import sys
import types

import pytest
from PIL import Image

from photolib import db, vlm

GOOD = json.dumps({
    "description": "A red torii gate on a hill at dusk.",
    "tags": ["torii", "shrine", "hill"],
    "scene_setting": "outdoor",
    "scene_category": "shrine",
    "time_of_day": "dusk",
    "location_guess": {"name": None, "confidence": "low", "reasoning": "no clear sign"},
    "people_present": False,
})


def test_parse_and_validate_accepts_good_json():
    data = vlm.parse_and_validate(GOOD)
    assert data["tags"] == ["torii", "shrine", "hill"]


def test_parse_and_validate_rejects_missing_key():
    bad = json.dumps({"description": "x"})
    with pytest.raises(ValueError):
        vlm.parse_and_validate(bad)


def test_analyze_retries_once_then_succeeds():
    calls = {"n": 0}

    def flaky(path, model, num_ctx=None, max_side=None):
        calls["n"] += 1
        return "not json" if calls["n"] == 1 else GOOD

    data = vlm.analyze("x.jpg", vlm.DEFAULT_MODEL, caller=flaky)
    assert calls["n"] == 2
    assert data["people_present"] is False


def test_analyze_raises_after_second_failure():
    def always_bad(path, model, num_ctx=None, max_side=None):
        return "still not json"

    with pytest.raises(Exception):
        vlm.analyze("x.jpg", vlm.DEFAULT_MODEL, caller=always_bad)


def test_prepare_image_downscales_long_side(tmp_path):
    big = tmp_path / "big.jpg"
    Image.new("RGB", (4000, 3000), (20, 40, 60)).save(big)
    data = vlm._prepare_image(str(big), max_side=1024)
    with Image.open(io.BytesIO(data)) as out:
        assert max(out.size) == 1024
        assert out.size == (1024, 768)


def test_call_ollama_resizes_image_and_passes_num_ctx(tmp_path, monkeypatch):
    src = tmp_path / "src.jpg"
    Image.new("RGB", (4000, 3000), (10, 20, 30)).save(src)

    captured = {}

    def fake_chat(**kwargs):
        captured.update(kwargs)
        return {"message": {"content": GOOD}}

    fake = types.ModuleType("ollama")
    fake.chat = fake_chat
    monkeypatch.setitem(sys.modules, "ollama", fake)

    vlm.call_ollama(str(src), vlm.DEFAULT_MODEL, num_ctx=4096, max_side=1024)

    assert captured["options"]["num_ctx"] == 4096
    assert captured["options"]["temperature"] == 0
    sent = captured["messages"][0]["images"][0]
    assert isinstance(sent, bytes)  # downscaled JPEG bytes, not the raw path
    with Image.open(io.BytesIO(sent)) as out:
        assert max(out.size) == 1024


def test_run_vlm_reports_counts_and_marks_status(tmp_path, monkeypatch):
    conn = db.connect(str(tmp_path / "t.sqlite"))
    db.init_db(conn)
    db.upsert_image(conn, "/ok.jpg", "ok.jpg", "g", None, "unknown")
    db.upsert_image(conn, "/bad.jpg", "bad.jpg", "g", None, "unknown")
    conn.commit()

    good = json.loads(GOOD)

    def fake_analyze(path, model, num_ctx=vlm.DEFAULT_NUM_CTX, max_side=vlm.MAX_IMAGE_SIDE):
        if path == "/bad.jpg":
            raise RuntimeError("boom")
        return good

    monkeypatch.setattr(vlm, "analyze", fake_analyze)

    result = vlm.run_vlm(conn)
    assert result == {"done": 1, "errors": 1, "processed": 2}
    rows = {r["path"]: r for r in conn.execute("SELECT * FROM images").fetchall()}
    assert rows["/ok.jpg"]["status_vlm"] == "done"
    assert rows["/bad.jpg"]["status_vlm"] == "error"
    assert rows["/bad.jpg"]["error_vlm"] == "boom"
    assert conn.execute("SELECT COUNT(*) AS n FROM vlm").fetchone()["n"] == 1
