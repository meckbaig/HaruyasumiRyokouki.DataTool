"""SQLite storage: schema, connection, and per-stage status helpers."""

import sqlite3

STAGES = ("meta", "ocr", "vlm", "embed")

SCHEMA = """
CREATE TABLE IF NOT EXISTS images (
  id            INTEGER PRIMARY KEY,
  path          TEXT UNIQUE NOT NULL,
  filename      TEXT NOT NULL,
  dup_group     TEXT,
  taken_at      TEXT,
  taken_source  TEXT,
  width         INTEGER,
  height        INTEGER,
  orientation   INTEGER,
  status_meta   TEXT NOT NULL DEFAULT 'pending',
  status_ocr    TEXT NOT NULL DEFAULT 'pending',
  status_vlm    TEXT NOT NULL DEFAULT 'pending',
  status_embed  TEXT NOT NULL DEFAULT 'pending',
  error_meta    TEXT,
  error_ocr     TEXT,
  error_vlm     TEXT,
  error_embed   TEXT
);
CREATE TABLE IF NOT EXISTS ocr (
  image_id      INTEGER PRIMARY KEY REFERENCES images(id),
  text          TEXT,
  blocks_json   TEXT,
  lang_hint     TEXT
);
CREATE TABLE IF NOT EXISTS vlm (
  image_id       INTEGER PRIMARY KEY REFERENCES images(id),
  description    TEXT,
  tags_json      TEXT,
  scene_setting  TEXT,
  scene_category TEXT,
  time_of_day    TEXT,
  location_guess TEXT,
  raw_json       TEXT
);
CREATE TABLE IF NOT EXISTS embeddings (
  image_id      INTEGER PRIMARY KEY REFERENCES images(id),
  model         TEXT NOT NULL,
  dim           INTEGER NOT NULL,
  vector        BLOB NOT NULL
);
CREATE TABLE IF NOT EXISTS translations (
  image_id      INTEGER REFERENCES images(id),
  lang          TEXT NOT NULL,
  description   TEXT,
  tags_json     TEXT,
  PRIMARY KEY (image_id, lang)
);
"""


def connect(db_path):
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    return conn


def init_db(conn):
    conn.executescript(SCHEMA)
    conn.commit()


def upsert_image(conn, path, filename, dup_group, taken_at, taken_source):
    conn.execute(
        "INSERT OR IGNORE INTO images(path, filename, dup_group, taken_at, taken_source)"
        " VALUES (?, ?, ?, ?, ?)",
        (path, filename, dup_group, taken_at, taken_source),
    )


def pending(conn, stage):
    col = "status_" + stage
    return conn.execute(
        "SELECT * FROM images WHERE " + col + " != 'done' ORDER BY id"
    ).fetchall()


def set_status(conn, image_id, stage, status, error=None):
    status_col = "status_" + stage
    error_col = "error_" + stage
    conn.execute(
        "UPDATE images SET " + status_col + " = ?, " + error_col + " = ? WHERE id = ?",
        (status, error, image_id),
    )
