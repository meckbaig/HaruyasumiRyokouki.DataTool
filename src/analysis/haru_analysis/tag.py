"""Step tag: machine tags for every photo and video frame from a vision model.

Any OpenAI-compatible server works (LM Studio, llama.cpp server, Ollama, vLLM,
or a cloud API): the image goes as a base64 JPEG in /chat/completions. GPU
acceleration is chosen in that server, not here.

The model answers in three parts:

- description: one or two sentences about the photo. Writing it first makes
  the model look before naming things; it is stored for the vocabulary step
  as context, never shown on the site.
- tags: general words a visitor would search for (shrine, ramen, anime).
- names: proper names (landmarks, places, brands, franchises, characters),
  each with the evidence the model saw. Names are where a small model guesses
  most, so they are kept apart with their evidence: the vocabulary step's
  smarter model can drop "a bridge, so hokkaido" and keep "the sign reads
  dotonbori".

The prompt has no example names on purpose: a small model copies examples
into answers where they do not belong.
"""

import base64
import json
import re
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field

from . import db
from .images import jpeg_bytes
from .progress import Progress

PROMPT = """\
This is a photo from a trip to Japan. It will be found later through search on a travel diary site.

Answer in JSON only:
{"description": "...", "tags": ["..."], "names": [{"name": "...", "kind": "...", "evidence": "..."}]}

description: one or two plain sentences about what is actually in the photo, including any readable text.

tags: 5 to 12 general words a visitor might type into the search box to find this photo. Always include what kind of place it is (indoors too: a museum, a shop, a restaurant, a hotel room), then the main subjects: landscape, dishes and drinks, animals, plants, notable objects, pop-culture genre, events and activities; weather or time of day when they stand out. No proper names here. Prefer the common word a person would search over a narrower or decorated version of it. Leave out things that are just in the background or on the table, words too broad to narrow a search down, mood words, colors and materials, camera terms.

names: every proper name you can identify in the photo: a landmark, a city or area, a shop or brand, a franchise (series, game, band), a character. For each give its kind (landmark, place, brand, franchise, character) and the evidence: what in the photo shows it, such as text you can read or a design you recognize. List a name only when you can state such evidence; if it is only a guess from the general look, leave it out. An empty list is fine. Do not name real people.

Write in English, lowercase, singular, with standard spelling; names in their best-known short form.
"""

HINT = """
The site already uses these tags and names. Prefer them when they fit; add new ones only for what they do not cover:
{tags}
"""

NAME_KINDS = ("landmark", "place", "brand", "franchise", "character")

# Ask servers that support structured output to return exactly this shape;
# the answer is validated anyway.
RESPONSE_FORMAT = {
    "type": "json_schema",
    "json_schema": {
        "name": "photo_tags",
        "strict": True,
        "schema": {
            "type": "object",
            "properties": {
                "description": {"type": "string"},
                # A small model stops after one or two tags whatever the prompt
                # says; servers that enforce the schema make it go on.
                "tags": {"type": "array", "items": {"type": "string"}, "minItems": 5, "maxItems": 12},
                "names": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"},
                            "kind": {"type": "string", "enum": list(NAME_KINDS)},
                            "evidence": {"type": "string"},
                        },
                        "required": ["name", "kind", "evidence"],
                        "additionalProperties": False,
                    },
                },
            },
            "required": ["description", "tags", "names"],
            "additionalProperties": False,
        },
    },
}

_FENCE = re.compile(r"^```(?:json)?\s*|\s*```$")


# Removed from tags after the model answers, because small models do not follow
# "do not" lists reliably. Too broad to narrow a search, or incidental objects
# nobody searches for. The vocabulary step has its own stop list on top of this.
STOP_TAGS = {
    "japan", "japanese", "travel", "trip", "photo", "image", "picture", "tourism",
    "building", "sky", "sun", "tree", "trees", "water", "people", "person", "crowd",
    "indoor", "outdoor", "outdoors", "street", "city", "nature",
    "landscape", "scenery", "view", "daytime", "day", "light", "lighting",
    "chopsticks", "chopstick", "tray", "plate", "bowl", "cup", "glass", "table", "wall",
    "floor", "window", "door", "sign", "signage", "wire", "electric wire", "pole",
    "traffic light", "car", "road", "ground", "grass", "stand", "cupboard", "display",
    "beautiful", "serene", "peaceful", "kawaii", "cute", "colorful",
    "selfie", "tourist", "architecture", "asia", "urban", "religion", "religious building", "signs", "retro",
    "hand", "sightseeing", "landmark", "japan trip", "japan travel", "travel destination", "outdoor scenery",
    "food photography", "blurry", "blurry photo", "close-up", "close up",
    # The site knows the date of every photo; the model guesses seasons from light and colors.
    "spring", "summer", "autumn", "fall", "winter",
    # The model puts these on daytime photos with warm light; "china" may mean
    # porcelain or the country.
    "sunset", "dusk", "evening", "golden hour", "china",
}

# Colors and materials: dropped from the front of a tag ("red torii" -> "torii")
# and as tags of their own.
_DECOR = {
    "red", "orange", "yellow", "green", "blue", "purple", "pink", "white", "black", "gray", "grey",
    "brown", "golden", "gold", "silver", "wooden", "stone", "metal", "concrete", "glass",
}
STOP_TAGS |= _DECOR


class ServerUnavailable(Exception):
    pass


@dataclass
class Answer:
    description: str = ""
    tags: list = field(default_factory=list)
    names: list = field(default_factory=list)  # dicts: name, kind, evidence


def build_prompt(hint_tags=None):
    if not hint_tags:
        return PROMPT
    return PROMPT + HINT.format(tags=", ".join(hint_tags))


def _clean(text):
    return " ".join(text.strip().lower().split())


def parse_answer(raw):
    """Return the cleaned answer or raise ValueError if it has the wrong shape."""
    data = json.loads(_FENCE.sub("", raw.strip()))
    if not isinstance(data, dict) or not isinstance(data.get("tags"), list):
        raise ValueError('expected {"description": ..., "tags": [...], "names": [...]}')
    answer = Answer(description=str(data.get("description") or "").strip())

    for item in data["tags"]:
        if not isinstance(item, str):
            raise ValueError("tags must be strings")
        words = _clean(item).split()
        while len(words) > 1 and words[0] in _DECOR:
            words = words[1:]
        tag = " ".join(words)
        if tag and tag not in STOP_TAGS and tag not in answer.tags:
            answer.tags.append(tag)

    seen = set(answer.tags)
    for item in data.get("names") or []:
        if not isinstance(item, dict) or not isinstance(item.get("name"), str):
            raise ValueError("names must be objects with a name")
        name = _clean(item["name"])
        evidence = str(item.get("evidence") or "").strip()
        kind = item.get("kind") if item.get("kind") in NAME_KINDS else None
        # A name without evidence is the guess the prompt asked to leave out.
        if not name or not evidence or name in seen:
            continue
        seen.add(name)
        answer.names.append({"name": name, "kind": kind, "evidence": evidence})
    # Empty tags and names are a valid answer: some photos have nothing worth searching for.
    return answer


class VisionClient:
    """Minimal /chat/completions client for one image and one prompt."""

    def __init__(self, provider, timeout=300, transport=None):
        import httpx

        self.model = provider["model"]
        # "none" turns off thinking of reasoning models in LM Studio; tagging does not need it
        # and it multiplies the time per photo.
        self.reasoning_effort = provider.get("reasoning_effort")
        # Some APIs (DeepSeek) take only {"type": "json_object"}; the prompt already describes the shape.
        self.response_format = {"type": "json_object"} if provider.get("json_mode") == "object" else RESPONSE_FORMAT
        headers = {"Authorization": "Bearer " + provider["api_key"]} if provider["api_key"] else {}
        self._http = httpx.Client(base_url=provider["base_url"], headers=headers, timeout=timeout, transport=transport)
        self._httpx = httpx
        self._lock = threading.Lock()
        self.usage = {"prompt tokens": 0, "completion tokens": 0}

    def ask(self, prompt, jpeg):
        image_url = "data:image/jpeg;base64," + base64.b64encode(jpeg).decode("ascii")
        body = {
            "model": self.model,
            "temperature": 0,
            "response_format": self.response_format,
            "messages": [{
                "role": "user",
                "content": [
                    {"type": "text", "text": prompt},
                    {"type": "image_url", "image_url": {"url": image_url}},
                ],
            }],
        }
        if self.reasoning_effort:
            body["reasoning_effort"] = self.reasoning_effort
        try:
            resp = self._http.post("/chat/completions", json=body)
        except self._httpx.ConnectError as exc:
            raise ServerUnavailable(f"cannot connect to {self._http.base_url}: {exc}") from exc
        if resp.status_code >= 400:
            raise RuntimeError(f"HTTP {resp.status_code}: {resp.text[:300]}")
        data = resp.json()
        usage = data.get("usage") or {}
        with self._lock:
            self.usage["prompt tokens"] += usage.get("prompt_tokens", 0)
            self.usage["completion tokens"] += usage.get("completion_tokens", 0)
        return data["choices"][0]["message"]["content"]

    def close(self):
        self._http.close()


def tag_image(client, prompt, image, max_side):
    """Ask the model; one retry if the answer is not valid."""
    jpeg = jpeg_bytes(image, max_side)
    try:
        return parse_answer(client.ask(prompt, jpeg))
    except (ValueError, json.JSONDecodeError):
        return parse_answer(client.ask(prompt, jpeg))


def run_tag(conn, workspace, report, client=None, hint_tags=None, reset=False):
    vision = workspace.vision
    if reset:
        conn.execute("DELETE FROM raw_tags")
        conn.execute("UPDATE media SET tag_status = 'pending', tag_error = NULL, tag_description = NULL")
        conn.commit()
    rows = conn.execute(
        "SELECT key, path, type, frame_status FROM media"
        " WHERE missing = 0 AND tag_status != 'done' ORDER BY key"
    ).fetchall()
    report.count("skipped", conn.execute(
        "SELECT COUNT(*) FROM media WHERE missing = 0 AND tag_status = 'done'").fetchone()[0])
    if not rows:
        return

    if client is None:
        provider = workspace.provider("vision")
        client = VisionClient(provider)
        report.message = f"{provider['name']}: {provider['model']} at {provider['base_url']}"
    prompt = build_prompt(hint_tags)

    jobs = []
    for row in rows:
        if row["type"] == "video":
            image = workspace.frame_path(row["key"])
            if row["frame_status"] != db.DONE or not image.is_file():
                _save_error(conn, report, row["key"], "no video frame, run the frames step first")
                continue
        else:
            image = row["path"]
        jobs.append((row["key"], image))

    progress = Progress("tag", len(jobs))
    stop = threading.Event()

    def work(image):
        if stop.is_set():
            return None
        return tag_image(client, prompt, image, vision["max_side"])

    # Requests may run in parallel; results are written here, in the main thread.
    pool = ThreadPoolExecutor(max_workers=vision["concurrency"])
    try:
        futures = {pool.submit(work, image): key for key, image in jobs}
        for future in as_completed(futures):
            key = futures[future]
            try:
                answer = future.result()
            except ServerUnavailable:
                stop.set()
                raise
            except Exception as exc:  # noqa: BLE001 -- record and continue
                _save_error(conn, report, key, str(exc))
            else:
                if answer is not None:
                    _save_answer(conn, key, answer)
                    report.count("done")
            progress.step()
    except BaseException:
        stop.set()
        pool.shutdown(wait=True, cancel_futures=True)
        raise
    finally:
        progress.close()
        client.close()
        # Token use for cost estimates of cloud providers; also written when the step is interrupted.
        for name, value in getattr(client, "usage", {}).items():
            report.counts[name] = value
    pool.shutdown()
    report.counts["distinct tags"] = conn.execute(
        "SELECT COUNT(DISTINCT tag) FROM raw_tags WHERE kind = 'tag'").fetchone()[0]
    report.counts["distinct names"] = conn.execute(
        "SELECT COUNT(DISTINCT tag) FROM raw_tags WHERE kind != 'tag'").fetchone()[0]


def _save_answer(conn, key, answer):
    conn.execute("DELETE FROM raw_tags WHERE key = ?", (key,))
    conn.executemany(
        "INSERT INTO raw_tags(key, tag, kind, evidence) VALUES (?, ?, 'tag', NULL)",
        [(key, t) for t in answer.tags],
    )
    conn.executemany(
        "INSERT INTO raw_tags(key, tag, kind, evidence) VALUES (?, ?, ?, ?)",
        [(key, n["name"], n["kind"] or "name", n["evidence"]) for n in answer.names],
    )
    conn.execute("UPDATE media SET tag_description = ? WHERE key = ?", (answer.description, key))
    db.set_status(conn, key, "tag", db.DONE)
    conn.commit()


def _save_error(conn, report, key, message):
    db.set_status(conn, key, "tag", db.ERROR, message)
    conn.commit()
    report.error(message, key)


def read_hint(path):
    """Tags of the site, one per line, for --vocabulary-hint."""
    with open(path, encoding="utf-8") as f:
        return [line.strip() for line in f if line.strip()]
