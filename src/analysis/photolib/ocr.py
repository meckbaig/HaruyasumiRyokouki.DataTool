"""Stage 3: Apple Vision OCR via a compiled Swift helper."""

import json
import subprocess
from pathlib import Path

from . import db

HELPER = str(Path(__file__).resolve().parent.parent / "ocr_helper" / "vision_ocr")

_JA_RANGES = (("぀", "ヿ"), ("一", "鿿"))


def lang_hint(text):
    has_ja = any(lo <= ch <= hi for ch in text for lo, hi in _JA_RANGES)
    has_en = any("a" <= ch.lower() <= "z" for ch in text)
    if has_ja and has_en:
        return "mixed"
    if has_ja:
        return "ja"
    if has_en:
        return "en"
    return None


def parse_ocr(raw):
    data = json.loads(raw)
    text = data.get("text", "")
    blocks = data.get("blocks", [])
    return text, blocks, lang_hint(text)


def recognize(path, helper=HELPER):
    proc = subprocess.run([helper, path], capture_output=True, timeout=120)
    if proc.returncode != 0:
        msg = proc.stderr.decode("utf-8", "replace").strip()
        raise RuntimeError(msg or ("exit " + str(proc.returncode)))
    return parse_ocr(proc.stdout.decode("utf-8"))


def run_ocr(conn, helper=HELPER):
    for row in db.pending(conn, "ocr"):
        try:
            text, blocks, lang = recognize(row["path"], helper)
            conn.execute(
                "INSERT OR REPLACE INTO ocr(image_id, text, blocks_json, lang_hint)"
                " VALUES (?, ?, ?, ?)",
                (row["id"], text, json.dumps(blocks, ensure_ascii=False), lang),
            )
            db.set_status(conn, row["id"], "ocr", "done")
        except Exception as exc:  # noqa: BLE001 -- record and continue
            db.set_status(conn, row["id"], "ocr", "error", str(exc))
        conn.commit()
