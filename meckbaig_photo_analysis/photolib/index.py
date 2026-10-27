"""Stage 1: walk the photo folder and insert one image row per JPEG."""

from pathlib import Path

from . import db, naming

_EXTS = (".jpg", ".jpeg")


def iter_images(root):
    root = Path(root)
    for path in sorted(root.rglob("*")):
        if path.is_file() and path.suffix.lower() in _EXTS:
            yield path


def index_images(conn, root):
    count = 0
    for path in iter_images(root):
        taken = naming.parse_capture_datetime(path.name)
        source = "filename" if taken else "unknown"
        db.upsert_image(
            conn,
            str(path.resolve()),
            path.name,
            naming.dup_group_key(path.name),
            taken,
            source,
        )
        count += 1
    conn.commit()
    return count
