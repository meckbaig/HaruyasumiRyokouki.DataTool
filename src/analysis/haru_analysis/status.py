"""Per-step counts and recent errors from work.sqlite, for debugging."""

STAGES = ("frame", "embed")


def summary(conn):
    out = {
        "media": {
            row["type"]: row["n"]
            for row in conn.execute("SELECT type, COUNT(*) AS n FROM media WHERE missing = 0 GROUP BY type")
        },
        "missing": conn.execute("SELECT COUNT(*) FROM media WHERE missing = 1").fetchone()[0],
    }
    for stage in STAGES:
        rows = conn.execute(
            f"SELECT {stage}_status AS s, COUNT(*) AS n FROM media"
            f" WHERE missing = 0 AND {stage}_status IS NOT NULL GROUP BY {stage}_status"
        ).fetchall()
        out[stage] = {r["s"]: r["n"] for r in rows}
    return out


def errors(conn, limit=20):
    out = []
    for stage in STAGES:
        rows = conn.execute(
            f"SELECT key, path, {stage}_error AS error FROM media"
            f" WHERE missing = 0 AND {stage}_status = 'error' ORDER BY key LIMIT ?",
            (limit,),
        ).fetchall()
        out.extend((stage, r["key"], r["path"], r["error"]) for r in rows)
    return out
