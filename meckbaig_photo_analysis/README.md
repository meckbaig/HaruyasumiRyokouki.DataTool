# meckbaig photo analysis

One-time local pipeline that turns a folder of trip JPEGs into searchable
metadata (English descriptions and tags, Japanese/English OCR text,
multilingual embeddings) stored in one SQLite database.

## Setup

1. Python env (unit tests): `python3 -m venv .venv && source .venv/bin/activate && pip install -e ".[dev]"`
2. Model backends (real runs only): `pip install -e ".[models]"`
3. Ollama: install from https://ollama.com, then `ollama pull qwen2.5vl:7b`
4. Apple Vision OCR helper: `swiftc ocr_helper/vision_ocr.swift -o ocr_helper/vision_ocr`
5. Embeddings model downloads automatically on first run.

## Usage

```
python -m photolib.cli --db photos.sqlite init
python -m photolib.cli --db photos.sqlite index --root /path/to/photos
python -m photolib.cli --db photos.sqlite metadata
python -m photolib.cli --db photos.sqlite ocr
python -m photolib.cli --db photos.sqlite vlm
python -m photolib.cli --db photos.sqlite embed
python -m photolib.cli --db photos.sqlite report
python -m photolib.cli --db photos.sqlite translate --lang ru   # optional
```

Run the calibration procedure on a small sample before the full run.
