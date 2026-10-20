"""Optional HuggingFace key: the file of the key, else its variable."""

from __future__ import annotations

from system_core.core.credentials import read_api_key, write_api_key
from system_core.core.paths import get_project_paths


def test_key_file_round_trip_and_env_fallback(tmp_path, monkeypatch):
    paths = get_project_paths(tmp_path)
    monkeypatch.delenv("HF_TOKEN", raising=False)
    assert read_api_key(paths, "huggingface") is None
    monkeypatch.setenv("HF_TOKEN", "hf_from_env")
    assert read_api_key(paths, "huggingface") == "hf_from_env"
    write_api_key(paths, "huggingface", "  hf_from_file  ")
    assert (paths.config / "api_key_huggingface.txt").read_text(encoding="utf-8").strip() == "hf_from_file"
    assert read_api_key(paths, "huggingface") == "hf_from_file"

