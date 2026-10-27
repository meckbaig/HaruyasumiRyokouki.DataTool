import json

from photolib import translate


def test_parse_translation_reads_fields():
    raw = json.dumps({"description": "krasnye vorota", "tags": ["torii", "svyatilishche"]})
    desc, tags = translate.parse_translation(raw)
    assert desc == "krasnye vorota"
    assert tags == ["torii", "svyatilishche"]


def test_translate_fields_uses_caller():
    def fake(prompt, model):
        return json.dumps({"description": "d", "tags": ["t"]})

    desc, tags = translate.translate_fields("x", ["y"], "ru", "m", caller=fake)
    assert (desc, tags) == ("d", ["t"])
