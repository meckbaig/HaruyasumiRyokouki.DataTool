import os

from haru_analysis import scan
from haru_analysis.workspace import StepReport


def media(conn):
    return {r["key"]: r for r in conn.execute("SELECT * FROM media")}


def test_key_is_lowercase_name_without_last_extension():
    assert scan.media_key("PXL_20260318_111609555.jpg") == "pxl_20260318_111609555"
    assert scan.media_key("PXL_20260318_111609555.NIGHT.jpg") == "pxl_20260318_111609555.night"
    assert scan.media_key("IMG_1.HEIC") == "img_1"


def test_scan_indexes_supported_files_recursively(make_workspace):
    ws, src = make_workspace()
    (src / "day1").mkdir()
    (src / "day1" / "IMG_1.HEIC").write_bytes(b"x")
    (src / "clip.MOV").write_bytes(b"x")
    (src / "notes.txt").write_bytes(b"x")
    conn = ws.connect()
    report = StepReport("scan")
    scan.run_scan(conn, src, report)

    rows = media(conn)
    assert set(rows) == {"img_1", "clip"}
    assert rows["img_1"]["type"] == "image"
    assert rows["img_1"]["frame_status"] is None
    assert rows["clip"]["type"] == "video"
    assert rows["clip"]["frame_status"] == "pending"
    assert report.counts["added"] == 2
    assert not report.errors


def test_key_collision_is_an_error_and_writes_nothing(make_workspace):
    ws, src = make_workspace()
    (src / "a").mkdir()
    (src / "IMG_1.jpg").write_bytes(b"x")
    (src / "a" / "img_1.png").write_bytes(b"x")
    (src / "IMG_2.jpg").write_bytes(b"x")
    conn = ws.connect()
    report = StepReport("scan")
    scan.run_scan(conn, src, report)

    assert media(conn) == {}
    assert len(report.errors) == 1
    assert report.errors[0]["key"] == "img_1"
    assert "IMG_1.jpg" in report.errors[0]["message"]
    assert "img_1.png" in report.errors[0]["message"]


def test_moved_folder_updates_path_and_keeps_results(make_workspace):
    ws, src = make_workspace()
    (src / "old").mkdir()
    (src / "old" / "IMG_1.jpg").write_bytes(b"x")
    conn = ws.connect()
    scan.run_scan(conn, src, StepReport("scan"))
    conn.execute("UPDATE media SET embed_status = 'done'")
    conn.commit()

    (src / "old").rename(src / "new")
    report = StepReport("scan")
    scan.run_scan(conn, src, report)

    row = media(conn)["img_1"]
    assert "new" in row["path"]
    assert row["embed_status"] == "done"
    assert report.counts["moved"] == 1


def test_changed_file_resets_its_results(make_workspace):
    ws, src = make_workspace()
    f = src / "IMG_1.jpg"
    f.write_bytes(b"x")
    conn = ws.connect()
    scan.run_scan(conn, src, StepReport("scan"))
    conn.execute("UPDATE media SET embed_status = 'done'")
    conn.execute("INSERT INTO embeddings(key, model, dim, vector) VALUES ('img_1', 'm', 1, x'00000000')")
    conn.commit()

    f.write_bytes(b"longer content")
    report = StepReport("scan")
    scan.run_scan(conn, src, report)

    assert media(conn)["img_1"]["embed_status"] == "pending"
    assert conn.execute("SELECT COUNT(*) FROM embeddings").fetchone()[0] == 0
    assert report.counts["changed"] == 1


def test_vanished_file_is_marked_not_deleted_and_comes_back(make_workspace):
    ws, src = make_workspace()
    f = src / "IMG_1.jpg"
    f.write_bytes(b"x")
    conn = ws.connect()
    scan.run_scan(conn, src, StepReport("scan"))
    stat = f.stat()

    f.rename(src.parent / "aside.jpg")
    report = StepReport("scan")
    scan.run_scan(conn, src, report)
    assert media(conn)["img_1"]["missing"] == 1
    assert report.counts["missing"] == 1

    (src.parent / "aside.jpg").rename(f)
    os.utime(f, ns=(stat.st_atime_ns, stat.st_mtime_ns))
    scan.run_scan(conn, src, StepReport("scan"))
    assert media(conn)["img_1"]["missing"] == 0
