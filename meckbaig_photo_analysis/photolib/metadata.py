"""Stage 2: image dimensions, orientation, and EXIF date fallback."""

from datetime import datetime

from PIL import Image

from . import db

_ORIENTATION = 274
_DATETIME_ORIGINAL = 36867
_EXIF_IFD = 0x8769


def read_meta(path):
    with Image.open(path) as im:
        width, height = im.size
        exif = im.getexif()
        orientation = exif.get(_ORIENTATION)
        try:
            sub = exif.get_ifd(_EXIF_IFD)
        except Exception:
            sub = {}
        raw_dt = sub.get(_DATETIME_ORIGINAL)
    taken = None
    if raw_dt:
        try:
            taken = datetime.strptime(raw_dt, "%Y:%m:%d %H:%M:%S").isoformat()
        except (ValueError, TypeError):
            taken = None
    return width, height, orientation, taken


def run_metadata(conn):
    for row in db.pending(conn, "meta"):
        try:
            width, height, orientation, exif_taken = read_meta(row["path"])
            taken_at = row["taken_at"]
            taken_source = row["taken_source"]
            if not taken_at and exif_taken:
                taken_at = exif_taken
                taken_source = "exif"
            conn.execute(
                "UPDATE images SET width=?, height=?, orientation=?, taken_at=?,"
                " taken_source=? WHERE id=?",
                (width, height, orientation, taken_at, taken_source, row["id"]),
            )
            db.set_status(conn, row["id"], "meta", "done")
        except Exception as exc:  # noqa: BLE001 -- record and continue
            db.set_status(conn, row["id"], "meta", "error", str(exc))
        conn.commit()
