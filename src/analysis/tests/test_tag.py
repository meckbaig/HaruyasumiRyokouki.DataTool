import base64
import io
import json

import httpx
import pytest
from PIL import Image

from haru_analysis import scan, tag
from haru_analysis.workspace import StepReport, WorkspaceError

PROVIDER_TOML = """
[providers.local]
type = "openai"
base_url = "http://vlm.test/v1"
model = "qwen-vl"

[usage]
vision = "local"

[vision]
max_side = 64
concurrency = 2
"""


def test_parse_cleans_and_dedupes():
    raw = '```json\n{"tags": [" Torii ", "torii", "Cherry  Blossom", ""]}\n```'
    assert tag.parse_answer(raw).tags == ["torii", "cherry blossom"]


@pytest.mark.parametrize("raw", ['{"tag": []}', '{"tags": "torii"}', '{"tags": [1]}', "not json"])
def test_parse_rejects_bad_answers(raw):
    with pytest.raises(ValueError):
        tag.parse_answer(raw)


def test_stop_words_and_decorations_are_removed():
    raw = '{"tags": ["Japan", "chopsticks", "Red Torii", "golden statue", "Green Tea", "red", "sky"]}'
    assert tag.parse_answer(raw).tags == ["torii", "statue", "tea"]


def test_nothing_worth_searching_is_a_valid_answer():
    assert tag.parse_answer('{"tags": ["japan", "sky"]}').tags == []
    assert tag.parse_answer('{"tags": []}').tags == []


def test_hint_is_appended_to_prompt():
    assert "shrine, temple" in tag.build_prompt(["shrine", "temple"])
    assert tag.build_prompt(None) == tag.PROMPT


class FakeClient:
    def __init__(self, answers):
        self.answers = answers  # file stem -> list of raw answers, consumed in order
        self.calls = []
        self.closed = False

    def ask(self, prompt, jpeg):
        stem = Image.open(io.BytesIO(jpeg)).info.get("comment", b"").decode()
        self.calls.append(stem)
        return self.answers[stem].pop(0)

    def close(self):
        self.closed = True


def photo(path, name, size=(200, 100)):
    # The stem goes into the JPEG comment so the fake client knows which file it got.
    im = Image.new("RGB", size, "blue")
    im.save(path, comment=name.encode())


def setup(make_workspace):
    ws, src = make_workspace(PROVIDER_TOML)
    photo(src / "a.jpg", "a")
    photo(src / "b.jpg", "b")
    (src / "clip.mp4").write_bytes(b"x")
    conn = ws.connect()
    scan.run_scan(conn, src, StepReport("scan"))
    return ws, conn


def raw_tags(conn):
    out = {}
    for key, t in conn.execute("SELECT key, tag FROM raw_tags ORDER BY key, tag"):
        out.setdefault(key, []).append(t)
    return out


def test_tags_are_saved_and_bad_answer_is_retried_once(make_workspace):
    ws, conn = setup(make_workspace)
    client = FakeClient({
        "a": ['{"tags": ["torii", "shrine"]}'],
        "b": ["sorry, I cannot", '{"tags": ["ramen"]}'],
    })
    report = StepReport("tag")
    tag.run_tag(conn, ws, report, client=client)

    assert raw_tags(conn) == {"a": ["shrine", "torii"], "b": ["ramen"]}
    assert client.calls.count("b") == 2
    assert client.closed
    assert report.counts["done"] == 2
    assert report.errors == [{"key": "clip", "message": "no video frame, run the frames step first"}]


def test_two_bad_answers_mark_the_file_as_error_and_rerun_retries_it(make_workspace):
    ws, conn = setup(make_workspace)
    client = FakeClient({"a": ['{"tags": ["torii"]}'], "b": ["nope", "nope again"]})
    report = StepReport("tag")
    tag.run_tag(conn, ws, report, client=client)
    assert conn.execute("SELECT tag_status FROM media WHERE key = 'b'").fetchone()[0] == "error"
    assert any(e["key"] == "b" for e in report.errors)

    client = FakeClient({"b": ['{"tags": ["harbor"]}']})
    tag.run_tag(conn, ws, StepReport("tag"), client=client)
    assert client.calls == ["b"]
    assert raw_tags(conn)["b"] == ["harbor"]


def test_video_uses_its_frame(make_workspace):
    ws, conn = setup(make_workspace)
    ws.frames_dir.mkdir()
    photo(ws.frame_path("clip"), "clip")
    conn.execute("UPDATE media SET frame_status = 'done' WHERE key = 'clip'")
    conn.commit()
    client = FakeClient({"a": ['{"tags": ["x"]}'], "b": ['{"tags": ["y"]}'], "clip": ['{"tags": ["train"]}']})
    tag.run_tag(conn, ws, StepReport("tag"), client=client)
    assert raw_tags(conn)["clip"] == ["train"]


def test_unreachable_server_stops_the_step(make_workspace):
    ws, conn = setup(make_workspace)

    class Down(FakeClient):
        def ask(self, prompt, jpeg):
            raise tag.ServerUnavailable("cannot connect")

    with pytest.raises(tag.ServerUnavailable):
        tag.run_tag(conn, ws, StepReport("tag"), client=Down({}))
    assert conn.execute("SELECT COUNT(*) FROM media WHERE tag_status = 'error' AND type = 'image'").fetchone()[0] == 0


def test_client_sends_openai_request_with_downscaled_image(make_workspace, tmp_path):
    ws, _ = make_workspace(PROVIDER_TOML)
    seen = {}

    def handler(request):
        seen["url"] = str(request.url)
        seen["auth"] = request.headers.get("authorization")
        seen["body"] = json.loads(request.content)
        return httpx.Response(200, json={"choices": [{"message": {"content": '{"tags": ["torii"]}'}}]})

    client = tag.VisionClient(ws.provider("vision"), transport=httpx.MockTransport(handler))
    path = tmp_path / "big.jpg"
    photo(path, "big", size=(400, 200))
    assert tag.tag_image(client, tag.PROMPT, path, max_side=64).tags == ["torii"]

    assert seen["url"] == "http://vlm.test/v1/chat/completions"
    assert seen["auth"] is None
    body = seen["body"]
    assert body["model"] == "qwen-vl"
    content = body["messages"][0]["content"]
    assert content[0]["text"] == tag.PROMPT
    url = content[1]["image_url"]["url"]
    assert url.startswith("data:image/jpeg;base64,")
    im = Image.open(io.BytesIO(base64.b64decode(url.split(",", 1)[1])))
    assert max(im.size) == 64


def test_api_key_comes_from_env_file(make_workspace, monkeypatch):
    monkeypatch.delenv("VLM_KEY", raising=False)
    ws, _ = make_workspace(PROVIDER_TOML.replace('model = "qwen-vl"', 'model = "qwen-vl"\napi_key_env = "VLM_KEY"'))
    with pytest.raises(WorkspaceError, match="VLM_KEY"):
        ws.provider("vision")
    (ws.root / ".env").write_text("# secrets\nVLM_KEY='sk-test'\n", encoding="utf-8")
    ws.load_env()
    assert ws.provider("vision")["api_key"] == "sk-test"
    monkeypatch.delenv("VLM_KEY")


def test_provider_errors_are_clear(make_workspace):
    ws, _ = make_workspace()
    with pytest.raises(WorkspaceError, match=r"\[usage\] vision"):
        ws.provider("vision")
    ws, _ = make_workspace("[usage]\nvision = 'x'\n")
    with pytest.raises(WorkspaceError, match=r"\[providers.x\]"):
        ws.provider("vision")


def test_reset_asks_again(make_workspace):
    ws, conn = setup(make_workspace)
    tag.run_tag(conn, ws, StepReport("tag"), client=FakeClient({"a": ['{"tags": ["x"]}'], "b": ['{"tags": ["y"]}']}))
    client = FakeClient({"a": ['{"tags": ["torii"]}'], "b": ['{"tags": ["ramen"]}']})
    tag.run_tag(conn, ws, StepReport("tag"), client=client, reset=True)
    assert sorted(client.calls) == ["a", "b"]
    assert raw_tags(conn) == {"a": ["torii"], "b": ["ramen"]}


def test_names_need_evidence_and_keep_kind():
    raw = json.dumps({
        "description": "A canal at night with a big sign.",
        "tags": ["canal", "night"],
        "names": [
            {"name": "Dotonbori", "kind": "place", "evidence": "sign reads Dotonbori"},
            {"name": "Shibuya", "kind": "place", "evidence": ""},
            {"name": "night", "kind": "place", "evidence": "dark"},
            {"name": "Glico", "kind": "mascot", "evidence": "running man sign"},
        ],
    })
    answer = tag.parse_answer(raw)
    assert answer.description == "A canal at night with a big sign."
    assert answer.tags == ["canal", "night"]
    assert answer.names == [
        {"name": "dotonbori", "kind": "place", "evidence": "sign reads Dotonbori"},
        {"name": "glico", "kind": None, "evidence": "running man sign"},
    ]


def test_names_and_description_are_stored(make_workspace):
    ws, conn = setup(make_workspace)
    answer = json.dumps({
        "description": "Figures of a character.",
        "tags": ["anime", "figure"],
        "names": [{"name": "Hatsune Miku", "kind": "character", "evidence": "teal twin tails"}],
    })
    tag.run_tag(conn, ws, StepReport("tag"), client=FakeClient({"a": [answer], "b": ['{"tags": ["x"]}']}))
    rows = conn.execute("SELECT tag, kind, evidence FROM raw_tags WHERE key = 'a' ORDER BY tag").fetchall()
    assert [tuple(r) for r in rows] == [
        ("anime", "tag", None), ("figure", "tag", None), ("hatsune miku", "character", "teal twin tails")]
    assert conn.execute("SELECT tag_description FROM media WHERE key = 'a'").fetchone()[0] == "Figures of a character."


def test_reasoning_effort_is_sent_only_when_set(make_workspace, tmp_path):
    bodies = []

    def handler(request):
        bodies.append(json.loads(request.content))
        return httpx.Response(200, json={"choices": [{"message": {"content": '{"tags": ["torii"]}'}}]})

    path = tmp_path / "p.jpg"
    photo(path, "p")
    for extra in ("", 'reasoning_effort = "none"\n'):
        ws, _ = make_workspace(PROVIDER_TOML.replace('model = "qwen-vl"\n', 'model = "qwen-vl"\n' + extra))
        client = tag.VisionClient(ws.provider("vision"), transport=httpx.MockTransport(handler))
        tag.tag_image(client, tag.PROMPT, path, max_side=64)
    assert "reasoning_effort" not in bodies[0]
    assert bodies[1]["reasoning_effort"] == "none"


def test_json_object_mode_and_token_usage(make_workspace, tmp_path):
    bodies = []

    def handler(request):
        bodies.append(json.loads(request.content))
        return httpx.Response(200, json={
            "choices": [{"message": {"content": '{"tags": ["torii"]}'}}],
            "usage": {"prompt_tokens": 700, "completion_tokens": 40},
        })

    ws, _ = make_workspace(PROVIDER_TOML.replace('model = "qwen-vl"', 'model = "qwen-vl"\njson_mode = "object"'))
    client = tag.VisionClient(ws.provider("vision"), transport=httpx.MockTransport(handler))
    path = tmp_path / "p.jpg"
    photo(path, "p")
    tag.tag_image(client, tag.PROMPT, path, max_side=64)
    tag.tag_image(client, tag.PROMPT, path, max_side=64)

    assert bodies[0]["response_format"] == {"type": "json_object"}
    assert client.usage == {"prompt tokens": 1400, "completion tokens": 80}
