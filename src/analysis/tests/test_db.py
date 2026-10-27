from photolib import db


def make_db(tmp_path):
    conn = db.connect(str(tmp_path / "t.sqlite"))
    db.init_db(conn)
    return conn


def test_init_creates_images_table(tmp_path):
    conn = make_db(tmp_path)
    names = {r["name"] for r in conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table'"
    )}
    assert {"images", "ocr", "vlm", "embeddings", "translations"} <= names


def test_upsert_is_idempotent_on_path(tmp_path):
    conn = make_db(tmp_path)
    db.upsert_image(conn, "/p/a.jpg", "a.jpg", "a", "2026-01-15T09:00:00", "filename")
    db.upsert_image(conn, "/p/a.jpg", "a.jpg", "a", "2026-01-15T09:00:00", "filename")
    conn.commit()
    assert conn.execute("SELECT COUNT(*) AS n FROM images").fetchone()["n"] == 1


def test_pending_and_set_status(tmp_path):
    conn = make_db(tmp_path)
    db.upsert_image(conn, "/p/a.jpg", "a.jpg", "a", None, "unknown")
    conn.commit()
    row = db.pending(conn, "ocr")[0]
    assert row["status_ocr"] == "pending"
    db.set_status(conn, row["id"], "ocr", "done")
    conn.commit()
    assert db.pending(conn, "ocr") == []


def test_pending_returns_errored_rows_for_retry(tmp_path):
    conn = make_db(tmp_path)
    db.upsert_image(conn, "/p/a.jpg", "a.jpg", "a", None, "unknown")
    conn.commit()
    image_id = db.pending(conn, "ocr")[0]["id"]
    db.set_status(conn, image_id, "ocr", "error", "boom")
    conn.commit()
    rows = db.pending(conn, "ocr")
    assert [r["id"] for r in rows] == [image_id]
    assert rows[0]["status_ocr"] == "error"
    db.set_status(conn, image_id, "ocr", "done")
    conn.commit()
    assert db.pending(conn, "ocr") == []


def test_stage_error_column_survives_other_stage_success(tmp_path):
    conn = make_db(tmp_path)
    db.upsert_image(conn, "/p/a.jpg", "a.jpg", "a", None, "unknown")
    conn.commit()
    image_id = db.pending(conn, "ocr")[0]["id"]
    db.set_status(conn, image_id, "ocr", "error", "boom")
    db.set_status(conn, image_id, "vlm", "done")
    conn.commit()
    row = conn.execute("SELECT * FROM images WHERE id=?", (image_id,)).fetchone()
    assert row["status_ocr"] == "error"
    assert row["error_ocr"] == "boom"        # earlier-stage error preserved
    assert row["status_vlm"] == "done"
    assert row["error_vlm"] is None
