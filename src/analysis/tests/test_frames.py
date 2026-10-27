import shutil
import subprocess
import types

import pytest

from haru_analysis import frames, scan
from haru_analysis.workspace import StepReport


def fake_ffmpeg(write_on):
    """A stand-in for subprocess.run that writes a frame only for the given attempt numbers."""
    calls = []

    def run(cmd, capture_output):
        calls.append(cmd)
        if len(calls) in write_on:
            with open(cmd[-1], "wb") as f:
                f.write(b"jpeg")
        return types.SimpleNamespace(returncode=0, stderr=b"")

    return run, calls


def video_workspace(make_workspace):
    ws, src = make_workspace()
    (src / "clip.mp4").write_bytes(b"x")
    (src / "IMG_1.jpg").write_bytes(b"x")
    conn = ws.connect()
    scan.run_scan(conn, src, StepReport("scan"))
    return ws, conn


def test_frame_is_taken_at_one_second(make_workspace):
    ws, conn = video_workspace(make_workspace)
    run, calls = fake_ffmpeg(write_on={1})
    report = StepReport("frames")
    frames.run_frames(conn, ws, report, ffmpeg="ffmpeg", run=run)

    assert len(calls) == 1
    assert calls[0][calls[0].index("-ss") + 1] == "00:00:01"
    assert ws.frame_path("clip").is_file()
    assert conn.execute("SELECT frame_status FROM media WHERE key = 'clip'").fetchone()[0] == "done"
    assert report.counts == {"skipped": 0, "done": 1}


def test_short_video_falls_back_to_first_frame(make_workspace):
    ws, conn = video_workspace(make_workspace)
    run, calls = fake_ffmpeg(write_on={2})
    frames.run_frames(conn, ws, StepReport("frames"), ffmpeg="ffmpeg", run=run)
    assert len(calls) == 2
    assert "-ss" not in calls[1]
    assert ws.frame_path("clip").is_file()


def test_failed_frame_is_recorded(make_workspace):
    ws, conn = video_workspace(make_workspace)
    run, _ = fake_ffmpeg(write_on=set())
    report = StepReport("frames")
    frames.run_frames(conn, ws, report, ffmpeg="ffmpeg", run=run)
    row = conn.execute("SELECT frame_status, frame_error FROM media WHERE key = 'clip'").fetchone()
    assert row["frame_status"] == "error"
    assert report.errors[0]["key"] == "clip"


def test_done_frames_are_skipped_unless_file_was_deleted(make_workspace):
    ws, conn = video_workspace(make_workspace)
    run, calls = fake_ffmpeg(write_on={1, 2})
    frames.run_frames(conn, ws, StepReport("frames"), ffmpeg="ffmpeg", run=run)
    frames.run_frames(conn, ws, StepReport("frames"), ffmpeg="ffmpeg", run=run)
    assert len(calls) == 1
    ws.frame_path("clip").unlink()
    frames.run_frames(conn, ws, StepReport("frames"), ffmpeg="ffmpeg", run=run)
    assert len(calls) == 2


def test_missing_ffmpeg_is_a_clear_error(monkeypatch):
    monkeypatch.setattr(shutil, "which", lambda name: None)
    with pytest.raises(frames.FfmpegNotFound, match="PATH"):
        frames.find_ffmpeg()


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not installed")
@pytest.mark.parametrize("seconds", ["2", "0.4"])
def test_real_ffmpeg_extracts_a_frame(tmp_path, seconds):
    ffmpeg = shutil.which("ffmpeg")
    video = tmp_path / "v.mp4"
    subprocess.run(
        [ffmpeg, "-v", "error", "-f", "lavfi", "-i", f"testsrc=duration={seconds}:size=64x64:rate=10",
         "-pix_fmt", "yuv420p", str(video)],
        check=True,
    )
    out = tmp_path / "v.jpg"
    frames.extract_frame(ffmpeg, video, out)
    assert out.stat().st_size > 0
