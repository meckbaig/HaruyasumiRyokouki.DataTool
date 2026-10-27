import json

import pytest

from haru_analysis import db
from haru_analysis.workspace import StepReport, Workspace, WorkspaceError


def test_missing_config_is_a_clear_error(tmp_path):
    with pytest.raises(WorkspaceError, match="haru-data.toml"):
        Workspace(tmp_path)


def test_relative_source_is_resolved_against_workspace(tmp_path):
    (tmp_path / "pics").mkdir()
    (tmp_path / "haru-data.toml").write_text("source = 'pics'\n", encoding="utf-8")
    assert Workspace(tmp_path).source == tmp_path / "pics"


def test_missing_source_folder_is_an_error(tmp_path):
    (tmp_path / "haru-data.toml").write_text("source = 'nowhere'\n", encoding="utf-8")
    with pytest.raises(WorkspaceError, match="does not exist"):
        Workspace(tmp_path).source


def test_embed_defaults_and_device_validation(make_workspace):
    ws, _ = make_workspace()
    assert ws.embed_model == "google/siglip2-base-patch16-256"
    assert ws.embed_device == "auto"
    ws, _ = make_workspace("[embed]\ndevice = 'gpu'\n")
    with pytest.raises(WorkspaceError, match="device"):
        ws.embed_device


def test_report_file_and_exit_codes(make_workspace):
    ws, _ = make_workspace()
    report = StepReport("scan")
    report.count("added", 3)
    assert report.finish(ws) == 0
    data = json.loads((ws.reports_dir / "scan.json").read_text(encoding="utf-8"))
    assert data["step"] == "scan"
    assert data["status"] == "ok"
    assert data["counts"] == {"added": 3}
    assert data["startedAt"] and data["finishedAt"]

    report = StepReport("scan")
    report.error("boom", "img_1")
    assert report.finish(ws) == 1
    data = json.loads((ws.reports_dir / "scan.json").read_text(encoding="utf-8"))
    assert data["status"] == "error"
    assert data["errors"] == [{"key": "img_1", "message": "boom"}]


def test_schema_version_mismatch_is_refused(make_workspace):
    ws, _ = make_workspace()
    conn = ws.connect()
    conn.execute("UPDATE schema_versions SET version = 99 WHERE owner = 'analysis'")
    conn.commit()
    with pytest.raises(db.SchemaError, match="new workspace"):
        ws.connect()


def test_other_owners_rows_are_left_alone(make_workspace):
    ws, _ = make_workspace()
    conn = ws.connect()
    conn.execute("INSERT INTO schema_versions(owner, version) VALUES ('tags', 7)")
    conn.commit()
    conn = ws.connect()
    rows = dict(conn.execute("SELECT owner, version FROM schema_versions").fetchall())
    assert rows == {"analysis": db.SCHEMA_VERSION, "tags": 7}
