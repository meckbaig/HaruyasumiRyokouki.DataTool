import pytest

from haru_analysis.workspace import Workspace


@pytest.fixture
def make_workspace(tmp_path):
    """Create a workspace folder with a source folder and return (Workspace, source path)."""

    def make(extra_toml=""):
        source = tmp_path / "photos"
        source.mkdir(exist_ok=True)
        root = tmp_path / "trip"
        root.mkdir(exist_ok=True)
        (root / "haru-data.toml").write_text(
            f"source = '{source.as_posix()}'\n{extra_toml}", encoding="utf-8"
        )
        return Workspace(root), source

    return make
