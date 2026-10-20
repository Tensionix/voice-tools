"""The keys of the services as the window of 3.0 shows them: what each is for, where it is got, whether it is there, and
a check of it by the service itself.

A key is never shown and never written into a line of a command: the window sends it on the standard input of
`voice_cli.cmd key set PROVIDER`, the list says only whether a key is there and its last four characters
(credentials.mask_api_key). Clearing a key writes its file empty: nothing of it is left on the disk, not even in the
Recycle Bin.
"""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from dataclasses import dataclass

from .credentials import _key_entry, mask_api_key, read_api_key
from .paths import ProjectPaths


@dataclass(frozen=True)
class KeyInfo:
    provider: str
    name: str
    why: tuple[str, str]  # ru, en
    url: str
    checkable: bool = True


KEYS: tuple[KeyInfo, ...] = (
    KeyInfo("huggingface", "Hugging Face",
            ("Разделение по говорящим на видеокарте (Community-1) и быстрая докачка моделей. Токен Read; условия "
             "pyannote/speaker-diarization-community-1 принять тем же аккаунтом.",
             "Speaker separation on the video card (Community-1) and fast downloads of models. A Read token; the terms "
             "of pyannote/speaker-diarization-community-1 accepted with the same account."),
            "https://huggingface.co/settings/tokens"),
    KeyInfo("openai", "OpenAI", ("Диктовка OpenAI Realtime и после остановки, облачная расшифровка файлов, очистка текста.",
                                 "Dictation by OpenAI Realtime and after the stop, cloud transcription of files, the cleanup of a text."),
            "https://platform.openai.com/api-keys"),
    KeyInfo("xai", "xAI", ("Потоковая диктовка и облачная расшифровка файлов.", "Streaming dictation and cloud transcription of files."), "https://console.x.ai"),
    KeyInfo("elevenlabs", "ElevenLabs", ("Потоковая диктовка (Scribe realtime).", "Streaming dictation (Scribe realtime)."),
            "https://elevenlabs.io/app/settings/api-keys"),
    KeyInfo("assemblyai", "AssemblyAI", ("Облачная расшифровка файлов с разделением по говорящим.", "Cloud transcription of files with speaker labels."),
            "https://www.assemblyai.com/app/api-keys"),
    KeyInfo("gemini", "Google Gemini", ("Облачная расшифровка файлов.", "Cloud transcription of files."), "https://aistudio.google.com/apikey"),
    KeyInfo("gigachat", "GigaChat", ("Облачная расшифровка файлов; ключ авторизации Сбера.", "Cloud transcription of files; the authorization key of Sber."),
            "https://developers.sber.ru/studio", checkable=False),
)

COMMUNITY_1 = "pyannote/speaker-diarization-community-1"


def info(provider: str) -> KeyInfo | None:
    return next((one for one in KEYS if one.provider == provider), None)


def describe(paths: ProjectPaths) -> list[dict]:
    """Every key of the window: there or not, its last four characters, where it comes from - never the key."""
    told = []
    for one in KEYS:
        filename, env_var, _ = _key_entry(one.provider)
        key_file = paths.config / filename
        in_file = key_file.exists() and bool(key_file.read_text(encoding="utf-8").strip())
        key = read_api_key(paths, one.provider)
        told.append({
            "id": one.provider, "name": one.name, "why": {"ru": one.why[0], "en": one.why[1]}, "url": one.url,
            "file": f"config\\{filename}", "present": bool(key), "fingerprint": mask_api_key(key),
            "source": "file" if in_file else ("env" if key and env_var and os.environ.get(env_var) else ""),
            "env": env_var, "checkable": one.checkable,
        })
    return told


def write(paths: ProjectPaths, provider: str, key: str) -> None:
    """The key into its file whole or not at all; an empty key empties the file."""
    from ..writers.atomic import write_text_atomic

    filename, _, _ = _key_entry(provider)
    paths.config.mkdir(parents=True, exist_ok=True)
    write_text_atomic(paths.config / filename, (key or "").strip() + ("\n" if key.strip() else ""))


def _ask(url: str, headers: dict[str, str], method: str = "GET") -> tuple[int, dict]:
    request = urllib.request.Request(url, headers={"User-Agent": "Audion-Voice-Tools", **headers}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=15) as answer:  # noqa: S310 - fixed https hosts of the services
            body = answer.read() if method == "GET" else b""
            try:
                return answer.status, json.loads(body.decode("utf-8")) if body else {}
            except ValueError:
                return answer.status, {}
    except urllib.error.HTTPError as error:
        return error.code, {}


def check(paths: ProjectPaths, provider: str) -> dict:
    """
    The key asked of its service by one short request: {"state": accepted | refused | no_access | unreachable | no_key |
    not_checkable, "detail": ...}. Hugging Face says the account, and whether the terms of Community-1 are accepted.
    """
    one = info(provider)
    if one is None or not one.checkable:
        return {"state": "not_checkable", "detail": ""}
    key = read_api_key(paths, provider)
    if not key:
        return {"state": "no_key", "detail": ""}
    bearer = {"Authorization": f"Bearer {key}"}
    try:
        if provider == "huggingface":
            code, said = _ask("https://huggingface.co/api/whoami-v2", bearer)
            if code != 200:
                return {"state": "refused", "detail": f"HTTP {code}"}
            account = str(said.get("name") or "")
            gated, _ = _ask(f"https://huggingface.co/{COMMUNITY_1}/resolve/main/config.yaml", bearer, method="HEAD")
            if gated != 200:
                return {"state": "no_access", "detail": account}
            return {"state": "accepted", "detail": account}
        if provider == "openai":
            code, _ = _ask("https://api.openai.com/v1/models", bearer)
        elif provider == "xai":
            code, _ = _ask("https://api.x.ai/v1/models", bearer)
        elif provider == "assemblyai":
            code, _ = _ask("https://api.assemblyai.com/v2/transcript?limit=1", {"Authorization": key})
        elif provider == "gemini":
            code, _ = _ask("https://generativelanguage.googleapis.com/v1beta/models", {"x-goog-api-key": key})
        elif provider == "elevenlabs":
            code, _ = _ask("https://api.elevenlabs.io/v1/user", {"xi-api-key": key})
        else:
            return {"state": "not_checkable", "detail": ""}
    except (urllib.error.URLError, OSError, TimeoutError) as error:
        return {"state": "unreachable", "detail": str(getattr(error, "reason", error))}
    if code == 200:
        return {"state": "accepted", "detail": ""}
    return {"state": "refused", "detail": f"HTTP {code}"}
