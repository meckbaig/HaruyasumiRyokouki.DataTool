from photolib import db, index


def test_index_inserts_rows_with_parsed_date(tmp_path):
    photos = tmp_path / "photos"
    photos.mkdir()
    (photos / "PXL_20260115_090000123.jpg").write_bytes(b"x")
    (photos / "PXL_20260115_090000123~2.jpg").write_bytes(b"x")
    (photos / "notes.txt").write_bytes(b"x")

    conn = db.connect(str(tmp_path / "t.sqlite"))
    db.init_db(conn)
    n = index.index_images(conn, str(photos))

    assert n == 2
    rows = conn.execute("SELECT filename, taken_at, dup_group FROM images ORDER BY filename").fetchall()
    assert rows[0]["taken_at"] == "2026-01-15T09:00:00"
    assert rows[0]["dup_group"] == rows[1]["dup_group"]


def test_index_is_idempotent(tmp_path):
    photos = tmp_path / "photos"
    photos.mkdir()
    (photos / "PXL_20260115_090000123.jpg").write_bytes(b"x")
    conn = db.connect(str(tmp_path / "t.sqlite"))
    db.init_db(conn)
    index.index_images(conn, str(photos))
    index.index_images(conn, str(photos))
    assert conn.execute("SELECT COUNT(*) AS n FROM images").fetchone()["n"] == 1
