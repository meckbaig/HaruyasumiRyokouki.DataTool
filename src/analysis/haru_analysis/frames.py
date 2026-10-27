"""Step frames: one still frame per video, used by later steps instead of the video.

The backend makes the video preview from the frame at 1 second
(appsettings.json, VideoThumbnailPrefix "first_frame": -ss 00:00:01 -frames:v 1),
so tags and vectors here describe the same picture the site shows.
"""

import shutil
import subprocess

from . import db
from .progress import Progress


class FfmpegNotFound(Exception):
    pass


def find_ffmpeg():
    path = shutil.which("ffmpeg")
    if not path:
        raise FfmpegNotFound("ffmpeg is not found in PATH. Install it and make sure 'ffmpeg -version' works.")
    return path


def extract_frame(ffmpeg, video, out, run=subprocess.run):
    """Write the frame at 1 s to out; fall back to the first frame for videos shorter than that."""
    for seek in (["-ss", "00:00:01"], []):
        out.unlink(missing_ok=True)
        proc = run(
            [ffmpeg, "-y", "-v", "error", "-i", str(video), *seek, "-frames:v", "1", "-q:v", "2", str(out)],
            capture_output=True,
        )
        if proc.returncode == 0 and out.is_file() and out.stat().st_size > 0:
            return
    msg = proc.stderr.decode("utf-8", "replace").strip() if proc.stderr else ""
    raise RuntimeError(msg or f"ffmpeg exit {proc.returncode}, no frame written")


def run_frames(conn, workspace, report, ffmpeg=None, run=subprocess.run):
    rows = conn.execute(
        "SELECT key, path, frame_status FROM media WHERE type = 'video' AND missing = 0 ORDER BY key"
    ).fetchall()
    # A frame deleted by hand is made again even if the status says done.
    todo = [r for r in rows if r["frame_status"] != db.DONE or not workspace.frame_path(r["key"]).is_file()]
    report.count("skipped", len(rows) - len(todo))
    if not todo:
        return

    ffmpeg = ffmpeg or find_ffmpeg()
    workspace.frames_dir.mkdir(parents=True, exist_ok=True)
    progress = Progress("frames", len(todo))
    for row in todo:
        try:
            extract_frame(ffmpeg, row["path"], workspace.frame_path(row["key"]), run)
            db.set_status(conn, row["key"], "frame", db.DONE)
            report.count("done")
        except Exception as exc:  # noqa: BLE001 -- record and continue
            db.set_status(conn, row["key"], "frame", db.ERROR, str(exc))
            report.error(str(exc), row["key"])
        conn.commit()
        progress.step()
    progress.close()
