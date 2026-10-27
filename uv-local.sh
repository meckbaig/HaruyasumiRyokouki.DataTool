#!/bin/sh
# Runs uv with everything it downloads (packages, Python, Hugging Face models)
# kept in .cache/ next to this file. Deleting .cache/ and src/analysis/.venv/
# removes all of it. Usage: ./uv-local.sh run --project src/analysis haru-analysis status --workspace my-trip
root="$(cd "$(dirname "$0")" && pwd)"
export UV_CACHE_DIR="${UV_CACHE_DIR:-$root/.cache/uv}"
export UV_PYTHON_INSTALL_DIR="${UV_PYTHON_INSTALL_DIR:-$root/.cache/python}"
export HF_HOME="${HF_HOME:-$root/.cache/huggingface}"
exec uv "$@"
