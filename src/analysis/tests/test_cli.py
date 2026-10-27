import json

from haru_analysis import cli


def test_scan_writes_report_and_returns_zero(make_workspace, capsys):
    ws, src = make_workspace()
    (src / "IMG_1.jpg").write_bytes(b"x")
    assert cli.main(["scan", "--workspace", str(ws.root)]) == 0
    report = json.loads((ws.reports_dir / "scan.json").read_text(encoding="utf-8"))
    assert report["status"] == "ok"
    assert report["counts"]["added"] == 1
    assert "added 1" in capsys.readouterr().out


def test_collision_returns_one(make_workspace):
    ws, src = make_workspace()
    (src / "a.jpg").write_bytes(b"x")
    (src / "A.png").write_bytes(b"x")
    assert cli.main(["scan", "--workspace", str(ws.root)]) == 1


def test_missing_workspace_returns_one(tmp_path, capsys):
    assert cli.main(["scan", "--workspace", str(tmp_path)]) == 1
    assert "haru-data.toml" in capsys.readouterr().err


def test_status_prints_counts(make_workspace, capsys):
    ws, src = make_workspace()
    (src / "IMG_1.jpg").write_bytes(b"x")
    cli.main(["scan", "--workspace", str(ws.root)])
    capsys.readouterr()
    assert cli.main(["status", "--workspace", str(ws.root)]) == 0
    out = json.loads(capsys.readouterr().out)
    assert out["media"] == {"image": 1}
    assert out["embed"] == {"pending": 1}
