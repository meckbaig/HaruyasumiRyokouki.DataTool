from PIL import Image

from photolib import db, metadata


def write_jpeg(path, size=(64, 48), exif_dt=None):
    im = Image.new("RGB", size, (10, 20, 30))
    if exif_dt:
        exif = im.getexif()
        ex = exif.get_ifd(0x8769)
        ex[36867] = exif_dt
        im.save(path, exif=exif)
    else:
        im.save(path)


def test_read_meta_dimensions(tmp_path):
    p = tmp_path / "a.jpg"
    write_jpeg(p, size=(64, 48))
    w, h, orient, taken = metadata.read_meta(str(p))
    assert (w, h) == (64, 48)
    assert taken is None


def test_run_metadata_fills_dims_and_exif_fallback(tmp_path):
    p = tmp_path / "PXL_bad_name.jpg"  # no parseable date
    write_jpeg(p, size=(30, 20), exif_dt="2026:01:15 09:00:00")

    conn = db.connect(str(tmp_path / "t.sqlite"))
    db.init_db(conn)
    db.upsert_image(conn, str(p), p.name, "g", None, "unknown")
    conn.commit()

    metadata.run_metadata(conn)

    row = conn.execute("SELECT * FROM images").fetchone()
    assert row["width"] == 30 and row["height"] == 20
    assert row["taken_at"] == "2026-01-15T09:00:00"
    assert row["taken_source"] == "exif"
    assert row["status_meta"] == "done"


def test_run_metadata_keeps_filename_date_over_exif(tmp_path):
    p = tmp_path / "PXL_20260115_090000123.jpg"
    write_jpeg(p, size=(40, 25), exif_dt="2020:05:05 05:05:05")

    conn = db.connect(str(tmp_path / "t.sqlite"))
    db.init_db(conn)
    db.upsert_image(conn, str(p), p.name, "g", "2026-01-15T09:00:00", "filename")
    conn.commit()

    metadata.run_metadata(conn)

    row = conn.execute("SELECT * FROM images").fetchone()
    assert row["taken_at"] == "2026-01-15T09:00:00"
    assert row["taken_source"] == "filename"
    assert row["width"] == 40 and row["height"] == 25
    assert row["status_meta"] == "done"
