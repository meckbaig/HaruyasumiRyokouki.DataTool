from photolib import cli, db


def test_cli_init_creates_db(tmp_path):
    dbpath = str(tmp_path / "t.sqlite")
    rc = cli.main(["--db", dbpath, "init"])
    assert rc == 0
    conn = db.connect(dbpath)
    names = {r["name"] for r in conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table'"
    )}
    assert "images" in names


def test_cli_index_counts_files(tmp_path, capsys):
    photos = tmp_path / "p"
    photos.mkdir()
    (photos / "PXL_20260115_090000123.jpg").write_bytes(b"x")
    dbpath = str(tmp_path / "t.sqlite")
    cli.main(["--db", dbpath, "init"])
    rc = cli.main(["--db", dbpath, "index", "--root", str(photos)])
    assert rc == 0
    assert "1" in capsys.readouterr().out


def test_cli_translate_runs_with_no_rows(tmp_path):
    dbpath = str(tmp_path / "t.sqlite")
    cli.main(["--db", dbpath, "init"])
    rc = cli.main(["--db", dbpath, "translate", "--lang", "ru"])
    assert rc == 0
