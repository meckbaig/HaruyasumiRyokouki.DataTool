"""Optional stage 6: translate English description and tags to ru/ja via Ollama."""

import json

DEFAULT_MODEL = "qwen2.5:7b"
LANGS = {"ru": "Russian", "ja": "Japanese"}

_PROMPT = (
    "Translate the JSON values into {lang_name}. Return STRICT JSON with the same"
    ' keys "description" (string) and "tags" (array of strings). Keep proper nouns'
    " sensible. Input:\n{payload}"
)


def parse_translation(raw):
    data = json.loads(raw)
    return data["description"], data["tags"]


def call_ollama(prompt, model):
    import ollama

    resp = ollama.chat(
        model=model,
        messages=[{"role": "user", "content": prompt}],
        format="json",
        options={"temperature": 0},
    )
    return resp["message"]["content"]


def translate_fields(description, tags, lang, model=DEFAULT_MODEL, caller=call_ollama):
    payload = json.dumps({"description": description, "tags": tags}, ensure_ascii=False)
    prompt = _PROMPT.format(lang_name=LANGS[lang], payload=payload)
    return parse_translation(caller(prompt, model))


def run_translate(conn, lang, model=DEFAULT_MODEL):
    rows = conn.execute(
        "SELECT v.image_id, v.description, v.tags_json FROM vlm v"
        " LEFT JOIN translations t ON t.image_id = v.image_id AND t.lang = ?"
        " WHERE t.image_id IS NULL",
        (lang,),
    ).fetchall()
    for row in rows:
        try:
            desc, tags = translate_fields(
                row["description"], json.loads(row["tags_json"]), lang, model
            )
            conn.execute(
                "INSERT OR REPLACE INTO translations(image_id, lang, description, tags_json)"
                " VALUES (?, ?, ?, ?)",
                (row["image_id"], lang, desc, json.dumps(tags, ensure_ascii=False)),
            )
        except Exception:  # noqa: BLE001 -- skip failed rows, rerun later
            pass
        conn.commit()
