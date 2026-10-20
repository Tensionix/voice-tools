"""Merged application settings: built-in defaults <- app_settings.yaml <- providers.yaml."""

from __future__ import annotations

from typing import Any

from .core.config import deep_merge, load_yaml_or_json
from .core.paths import ProjectPaths

DEFAULT_SETTINGS: dict[str, Any] = {
    "edition": "live",              # live | studio
    "compute_mode": "api",          # api | auto | local models | cuda
    "language": "auto",             # transcription language; auto-detect by default
    "ui_language": "ru",            # application chrome language
    "ui_theme": "dark_blue",
    "context": "",
    "term_dictionary": [],
    # vad: where GigaAM cuts a long file - in the pauses Silero VAD finds (True), or at the quietest frame of its window;
    # nothing is discarded either way.
    "transcription": {"diarize": False, "vad": True},
    "stt": {
        "provider": "openai",        # openai | xai | gemini | gigachat | assemblyai
        "model": "gpt-transcribe",
        "xai_model": "grok-transcribe",
        "gemini_model": "gemini-3.5-flash",
        "gigachat_model": "GigaChat",
        "gigachat_scope": "GIGACHAT_API_PERS",
        "gigachat_base_url": "https://gigachat.devices.sberbank.ru/api/v1",
        "gigachat_verify_ssl": False,
    },
    "postprocess": {"model": "gpt-6-luna"},
    "local": {
        "model": "large-v2",
        "device": "cpu",
        "compute_type": None,
        "batched": False,
        "batch_size": 16,
    },
    "vulkan": {
        "engine": "gigaam",             # gigaam | whispercpp
        "model": "gigaam-v3-e2e-rnnt",
        "backend": "directml",          # GigaAM: auto/cuda/directml/cpu
    },
    "diarization": {
        "enabled": False,
        "engine": "auto",   # auto (Community-1 where installed, Sortformer otherwise) | pyannote | sortformer
        "speakers": 0,      # 0 = found by the engine; Community-1 takes a known count, Sortformer finds up to 4 itself
    },
    "assemblyai": {"enabled": False},
    "exports": {
        "json": True,
        "markdown": True,
        "txt": False,
        "srt": True,
        "vtt": True,
    },
    "subtitles": {
        "max_chars_per_line": 42,
        "max_lines": 2,
        "min_duration": 1.0,
        "max_duration": 7.0,
        "include_speakers": False,
    },
    "postprocessing": {
        "generate_title": True,
        "generate_summary": True,
        "generate_tags": True,
        "generate_action_items": False,
        # Transcript formatting: separate toggle, model and library template.
        "cleanup": {
            "enabled": False,
            "model": "gpt-6-luna",   # GPT-6 Luna: the cleanup at a low reasoning effort (_openai_common)
            "prompt_id": "builtin.cleanup.default",
        },
    },
    "pipeline": {
        "skip_existing": True,
        "force": False,
        "save_next_to_source": True,
        "recursive": False,
        "chunk_seconds": 600,
        "overlap_seconds": 0,
        "normalize_audio": True,
    },
    # Live dictation from the tray. Local layout routing is the clean-build
    # default: Russian -> GigaAM, English -> whisper.cpp.
    "live": {
        "enabled": True,
        "prewarm_local": True,
        "minimize_to_tray": True,
        "hotkey": "ralt+f12",
        "toggle_hotkey": "",
        "mode": "toggle",             # start gesture; overlay Stop ends session
        "paste_method": "clipboard",  # clipboard | type
        "show_overlay": True,
        "overlay_scale_percent": 70,
        "language": "layout",
        "model": "gpt-4o-mini-transcribe",
        "source": "local",            # local routes by target-window layout
        "engine": "vulkan",           # batch | realtime | vulkan
        "api": {
            "provider": "openai",     # openai | xai | elevenlabs
            "mode": "batch",          # OpenAI only: batch | realtime
            "model": "gpt-4o-mini-transcribe",
            "models": {
                "openai": "gpt-4o-mini-transcribe",
                "xai": "grok-transcribe",
                "elevenlabs": "scribe_v2_realtime",
            },
        },
        "local": {
            "engine": "gigaam",       # gigaam | whispercpp
            "model": "gigaam-v3-e2e-ctc",
            "backend": "cpu",         # Live stays on CPU; GPU is reserved for file transcription
            "idle_unload_seconds": 0,    # resident until model change/app exit
        },
        "layout_routing": {
            "enabled": True,
            "ru_model": "gigaam-v3-e2e-ctc",
            "en_model": "turbo",
        },
        "cleanup": {
            "enabled": False,
            "model": "gpt-5.4-mini",
            "prompt_id": "builtin.cleanup.default",
            "sentence_threshold": 12,
        },
    },
}


def load_settings(paths: ProjectPaths) -> dict[str, Any]:
    settings = dict(DEFAULT_SETTINGS)
    settings = deep_merge(settings, load_yaml_or_json(paths.config / "app_settings.yaml"))
    settings = deep_merge(settings, load_yaml_or_json(paths.config / "providers.yaml"))
    # The dictionary of SETTINGS of the window of 3.0 (Source\Core\Vocabulary.cs) takes the place of term_dictionary once written.
    from .core.terms import read_dictionary_file

    dictionary = read_dictionary_file(paths.config / "dictionary.txt")
    if dictionary is not None:
        settings["term_dictionary"] = list(dictionary)
    if str(settings.get("compute_mode", "")).strip().lower() == "auto":
        from .core.local_hardware import recommended_compute_mode

        settings["compute_mode"] = recommended_compute_mode(paths, settings)
    return settings
