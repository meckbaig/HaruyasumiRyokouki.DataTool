"""work.sqlite: the tables owned by haru-analysis and status helpers.

Other apps read these tables but never write them. The layout is described in
docs/formats.md; change both together.
"""

import sqlite3

OWNER = "analysis"
SCHEMA_VERSION = 3

PENDING = "pending"
DONE = "done"
ERROR = "error"

SCHEMA = """
CREATE TABLE IF NOT EXISTS schema_versions (
  owner         TEXT PRIMARY KEY,
  version       INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS media (
  key           TEXT PRIMARY KEY,
  path          TEXT NOT NULL,
  type          TEXT NOT NULL CHECK (type IN ('image', 'video')),
  size          INTEGER NOT NULL,
  mtime_ns      INTEGER NOT NULL,
  missing       INTEGER NOT NULL DEFAULT 0,
  frame_status  TEXT,
  frame_error   TEXT,
  tag_status    TEXT NOT NULL DEFAULT 'pending',
  tag_error     TEXT,
  tag_description TEXT,
  embed_status  TEXT NOT NULL DEFAULT 'pending',
  embed_error   TEXT
);
CREATE TABLE IF NOT EXISTS raw_tags (
  key           TEXT NOT NULL REFERENCES media(key),
  tag           TEXT NOT NULL,
  kind          TEXT NOT NULL DEFAULT 'tag',
  evidence      TEXT,
  PRIMARY KEY (key, tag)
);
CREATE TABLE IF NOT EXISTS embeddings (
  key           TEXT PRIMARY KEY REFERENCES media(key),
  model         TEXT NOT NULL,
  dim           INTEGER NOT NULL,
  vector        BLOB NOT NULL
);
"""


class SchemaError(Exception):
    pass


def connect(db_path):
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    return conn


def ensure_schema(conn):
    """Create the tables on first use; refuse to work with a different schema version."""
    conn.executescript(SCHEMA)
    row = conn.execute("SELECT version FROM schema_versions WHERE owner = ?", (OWNER,)).fetchone()
    if row is None:
        conn.execute("INSERT INTO schema_versions(owner, version) VALUES (?, ?)", (OWNER, SCHEMA_VERSION))
        conn.commit()
    elif row["version"] != SCHEMA_VERSION:
        raise SchemaError(
            f"work.sqlite has {OWNER} schema version {row['version']}, this version of"
            f" haru-analysis needs {SCHEMA_VERSION}. Create a new workspace."
        )


def set_status(conn, key, stage, status, error=None):
    conn.execute(
        f"UPDATE media SET {stage}_status = ?, {stage}_error = ? WHERE key = ?",
        (status, error, key),
    )
