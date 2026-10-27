"""Workspace folder: settings from haru-data.toml, paths, and step reports."""

import json
import tomllib
from datetime import datetime, timezone
from pathlib import Path

from . import db

CONFIG_NAME = "haru-data.toml"
DEFAULT_EMBED_MODEL = "google/siglip2-base-patch16-256"
DEVICES = ("auto", "cuda", "mps", "cpu")

# Exit codes shared by every step of every app (docs/formats.md).
EXIT_OK = 0
EXIT_ERROR = 1
EXIT_NEEDS_REVIEW = 2


class WorkspaceError(Exception):
    """A problem the user has to fix before the step can run."""


class Workspace:
    def __init__(self, root):
        self.root = Path(root).resolve()
        config_path = self.root / CONFIG_NAME
        if not config_path.is_file():
            raise WorkspaceError(
                f"{config_path} not found. Create the workspace with 'haru-data init'"
                " or write the settings file by hand (see README)."
            )
        try:
            with open(config_path, "rb") as f:
                self.config = tomllib.load(f)
        except tomllib.TOMLDecodeError as exc:
            raise WorkspaceError(f"{config_path}: {exc}") from exc

        self.db_path = self.root / "work.sqlite"
        self.frames_dir = self.root / "frames"
        self.reports_dir = self.root / "reports"

    @property
    def source(self):
        value = self.config.get("source")
        if not value:
            raise WorkspaceError(f"'source' is not set in {CONFIG_NAME}.")
        path = Path(value)
        if not path.is_absolute():
            path = self.root / path
        if not path.is_dir():
            raise WorkspaceError(f"Source folder {path} does not exist.")
        return path

    @property
    def embed_model(self):
        return self.config.get("embed", {}).get("model", DEFAULT_EMBED_MODEL)

    @property
    def embed_device(self):
        device = self.config.get("embed", {}).get("device", "auto")
        if device not in DEVICES:
            raise WorkspaceError(f"[embed] device must be one of {', '.join(DEVICES)}, got '{device}'.")
        return device

    def connect(self):
        conn = db.connect(self.db_path)
        db.ensure_schema(conn)
        return conn

    def frame_path(self, key):
        return self.frames_dir / (key + ".jpg")


class StepReport:
    """Collects counts and errors of one step and writes reports/<step>.json."""

    def __init__(self, step):
        self.step = step
        self.started_at = _now()
        self.counts = {}
        self.errors = []
        self.message = ""

    def count(self, name, n=1):
        self.counts[name] = self.counts.get(name, 0) + n

    def error(self, message, key=None):
        self.errors.append({"key": key, "message": message} if key else {"message": message})

    def finish(self, workspace, status=None):
        """Write the report and return the exit code. Status defaults to error if any were recorded."""
        if status is None:
            status = "error" if self.errors else "ok"
        report = {
            "step": self.step,
            "status": status,
            "startedAt": self.started_at,
            "finishedAt": _now(),
            "counts": self.counts,
            "errors": self.errors,
            "message": self.message,
        }
        workspace.reports_dir.mkdir(parents=True, exist_ok=True)
        path = workspace.reports_dir / (self.step + ".json")
        path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        return {"ok": EXIT_OK, "error": EXIT_ERROR, "needs_review": EXIT_NEEDS_REVIEW}[status]


def _now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")
