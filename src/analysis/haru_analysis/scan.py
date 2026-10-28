"""Step scan: index the source folder into the media table.

A file is identified by its key: the file name without the last extension, in
lower case. The backend keeps that part of the name when it converts photos
(IMG_1.HEIC becomes IMG_1.webp), so the key is what links a local file to its
media_files row later.
"""

from pathlib import Path

from . import db

# Same lists as the backend (Common/Helpers/MediaFileNaming.cs); everything else is skipped.
IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif"}
VIDEO_EXTS = {".mp4", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".webm", ".m4v", ".3gp", ".mpeg", ".mpg"}


def media_key(filename):
    return Path(filename).stem.lower()


def media_type(path):
    ext = path.suffix.lower()
    if ext in IMAGE_EXTS:
        return "image"
    if ext in VIDEO_EXTS:
        return "video"
    return None


def find_media(root):
    """Return {key: [paths]} for supported files under root; more than one path is a collision."""
    found = {}
    for path in sorted(Path(root).rglob("*")):
        if path.is_file() and media_type(path):
            found.setdefault(media_key(path.name), []).append(path.resolve())
    return found


def run_scan(conn, root, report):
    found = find_media(root)
    collisions = {key: paths for key, paths in found.items() if len(paths) > 1}
    if collisions:
        for key, paths in sorted(collisions.items()):
            report.error("same key for " + ", ".join(str(p) for p in paths), key)
        report.message = (
            f"{len(collisions)} file names differ only by extension or case; the site"
            " cannot tell them apart. Rename or remove the extra files and run scan again."
        )
        return

    known = {row["key"]: row for row in conn.execute("SELECT * FROM media")}
    for key, (path,) in found.items():
        stat = path.stat()
        kind = media_type(path)
        row = known.get(key)
        if row is None:
            conn.execute(
                "INSERT INTO media(key, path, type, size, mtime_ns, frame_status)"
                " VALUES (?, ?, ?, ?, ?, ?)",
                (key, str(path), kind, stat.st_size, stat.st_mtime_ns,
                 db.PENDING if kind == "video" else None),
            )
            report.count("added")
        elif row["size"] != stat.st_size or row["mtime_ns"] != stat.st_mtime_ns or row["type"] != kind:
            # The content changed: everything computed from the old file is stale.
            conn.execute(
                "UPDATE media SET path = ?, type = ?, size = ?, mtime_ns = ?, missing = 0,"
                " frame_status = ?, frame_error = NULL, tag_status = 'pending', tag_error = NULL, tag_description = NULL,"
                " embed_status = 'pending', embed_error = NULL"
                " WHERE key = ?",
                (str(path), kind, stat.st_size, stat.st_mtime_ns,
                 db.PENDING if kind == "video" else None, key),
            )
            conn.execute("DELETE FROM raw_tags WHERE key = ?", (key,))
            conn.execute("DELETE FROM embeddings WHERE key = ?", (key,))
            report.count("changed")
        elif row["path"] != str(path) or row["missing"]:
            conn.execute("UPDATE media SET path = ?, missing = 0 WHERE key = ?", (str(path), key))
            report.count("moved")
        else:
            report.count("unchanged")

    gone = [key for key, row in known.items() if key not in found and not row["missing"]]
    for key in gone:
        conn.execute("UPDATE media SET missing = 1 WHERE key = ?", (key,))
    report.count("missing", len(gone))
    conn.commit()

    totals = conn.execute(
        "SELECT type, COUNT(*) AS n FROM media WHERE missing = 0 GROUP BY type"
    ).fetchall()
    for row in totals:
        report.counts[row["type"] + "s"] = row["n"]
