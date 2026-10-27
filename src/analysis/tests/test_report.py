from photolib import db, report


def test_summary_counts_by_stage_and_status(tmp_path):
    conn = db.connect(str(tmp_path / "t.sqlite"))
    db.init_db(conn)
    db.upsert_image(conn, "/a.jpg", "a.jpg", "a", None, "unknown")
    db.upsert_image(conn, "/b.jpg", "b.jpg", "b", None, "unknown")
    conn.commit()
    row = db.pending(conn, "ocr")[0]
    db.set_status(conn, row["id"], "ocr", "done")
    conn.commit()

    summary = report.summary(conn)
    assert summary["ocr"]["done"] == 1
    assert summary["ocr"]["pending"] == 1


def test_errors_lists_failed_rows_with_message(tmp_path):
    conn = db.connect(str(tmp_path / "t.sqlite"))
    db.init_db(conn)
    db.upsert_image(conn, "/a.jpg", "a.jpg", "a", None, "unknown")
    conn.commit()
    image_id = db.pending(conn, "ocr")[0]["id"]
    db.set_status(conn, image_id, "ocr", "error", "vision crash")
    db.set_status(conn, image_id, "vlm", "done")
    conn.commit()
    errs = report.errors(conn)
    assert len(errs) == 1
    assert errs[0]["error_ocr"] == "vision crash"
