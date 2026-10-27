@echo off
rem Runs uv with everything it downloads (packages, Python, Hugging Face models)
rem kept in .cache\ next to this file. Deleting .cache\ and src\analysis\.venv\
rem removes all of it. Usage: uv-local run --project src\analysis haru-analysis status --workspace my-trip
setlocal
set "ROOT=%~dp0"
if not defined UV_CACHE_DIR set "UV_CACHE_DIR=%ROOT%.cache\uv"
if not defined UV_PYTHON_INSTALL_DIR set "UV_PYTHON_INSTALL_DIR=%ROOT%.cache\python"
if not defined HF_HOME set "HF_HOME=%ROOT%.cache\huggingface"
uv %*
