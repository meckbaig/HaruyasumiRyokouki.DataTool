"""Status summary and error listing across stages."""

from . import db


def summary(conn):
    out = {}
    for stage in db.STAGES:
        col = "status_" + stage
        rows = conn.execute(
            "SELECT " + col + " AS s, COUNT(*) AS n FROM images GROUP BY " + col
        ).fetchall()
        out[stage] = {r["s"]: r["n"] for r in rows}
    return out


def errors(conn):
    return conn.execute(
        "SELECT id, path, status_meta, error_meta, status_ocr, error_ocr,"
        " status_vlm, error_vlm, status_embed, error_embed FROM images"
        " WHERE status_meta = 'error' OR status_ocr = 'error'"
        " OR status_vlm = 'error' OR status_embed = 'error'"
    ).fetchall()
