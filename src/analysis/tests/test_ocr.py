from photolib import ocr


def test_parse_ocr_extracts_text_and_blocks():
    raw = '{"text": "Tokyo Station", "blocks": [{"text": "Tokyo Station", "confidence": 0.9, "bbox": [0,0,1,1]}]}'
    text, blocks, lang = ocr.parse_ocr(raw)
    assert text == "Tokyo Station"
    assert blocks[0]["confidence"] == 0.9
    assert lang == "en"


def test_lang_hint_detects_japanese():
    assert ocr.lang_hint("東京駅") == "ja"


def test_lang_hint_detects_mixed():
    assert ocr.lang_hint("東京 Tokyo") == "mixed"


def test_lang_hint_none_for_empty():
    assert ocr.lang_hint("") is None
