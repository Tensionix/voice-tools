"""What is installed and what is out: the versions of the components of SETUP and the newest releases of those compared.

Compared are the engines whose releases a component follows:
  - transcribe.cpp (the engine of dictation) - shown, never updated by a press: the window binds its C API, 0.4.0;
  - whisper.cpp - its Windows packs come with the builds (tags bNNNN) since v1.9.3: the newest release that is no
    pre-release and carries the pack of the kind in place, the CPU one or the cuBLAS one.
Not compared, only shown: FFmpeg (its build follows the NVIDIA driver, Install-Portable-FFmpeg-BtbN.ps1), the models and
the Python packages - pinned by their installers.

GitHub is asked at most once a day, the answer kept in workspace\\releases.json; no network - the last answer stays.
"""

from __future__ import annotations

import json
import re
import subprocess
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

from .paths import ProjectPaths

DAY = 24 * 3600
_HEADERS = {"User-Agent": "Audion-Voice-Tools", "Accept": "application/vnd.github+json"}
_WHISPER_ASSETS = {"cpu": "whisper-bin-x64.zip", "cublas": "whisper-cublas-12.4.0-bin-x64.zip"}


def _get(url: str):
    request = urllib.request.Request(url, headers=_HEADERS)
    with urllib.request.urlopen(request, timeout=15) as answer:  # noqa: S310 - fixed https hosts
        return json.loads(answer.read().decode("utf-8"))


def _ask_github() -> dict:
    newest: dict[str, str] = {}
    release = _get("https://api.github.com/repos/handy-computer/transcribe.cpp/releases/latest")
    newest["transcribe"] = str(release.get("tag_name") or "").lstrip("v")
    for one in _get("https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=30"):
        if one.get("draft") or one.get("prerelease"):
            continue
        names = {asset.get("name") for asset in one.get("assets") or []}
        for kind, asset in _WHISPER_ASSETS.items():
            if asset in names and f"whispercpp:{kind}" not in newest:
                newest[f"whispercpp:{kind}"] = str(one.get("tag_name") or "")
    return newest


def newest_releases(paths: ProjectPaths, refresh: bool = False) -> dict:
    """{"checked": ISO time, "transcribe": "0.4.0", "whispercpp:cpu": "b4938", ...}; {} when never asked successfully."""
    cache = paths.workspace / "releases.json"
    try:
        kept = json.loads(cache.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        kept = {}
    now = time.time()
    fresh_enough = now - float(kept.get("asked", 0) or 0) < DAY
    # A refusal of GitHub (no network, its limit of 60 questions an hour) is not asked again for an hour.
    refused_lately = now - float(kept.get("failed", 0) or 0) < 3600
    if (kept.get("asked") and fresh_enough and not refresh) or (refused_lately and not refresh):
        return kept
    try:
        newest = _ask_github()
    except Exception as error:  # noqa: BLE001 - no network, a limit of GitHub: the last answer stays
        kept["error"] = str(error)
        kept["failed"] = now
        _keep(cache, kept)
        return kept
    newest["asked"] = now
    newest["checked"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
    _keep(cache, newest)
    return newest


def _keep(cache: Path, said: dict) -> None:
    try:
        cache.parent.mkdir(parents=True, exist_ok=True)
        cache.write_text(json.dumps(said, ensure_ascii=False, indent=2), encoding="utf-8")
    except OSError:
        pass


def _marker(path: Path) -> dict[str, str]:
    said: dict[str, str] = {}
    try:
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            if "=" in line:
                key, value = line.split("=", 1)
                said[key.strip().lower()] = value.strip()
    except OSError:
        pass
    return said


def _package(name: str) -> str:
    try:
        from importlib.metadata import version

        return version(name)
    except Exception:  # noqa: BLE001 - not installed
        return ""


def _ffmpeg(paths: ProjectPaths) -> str:
    exe = paths.tools / "ffmpeg" / "bin" / "ffmpeg.exe"
    if not exe.exists():
        return ""
    try:
        first = subprocess.run([str(exe), "-hide_banner", "-version"], capture_output=True, text=True, timeout=10,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout.splitlines()[:1]
    except (OSError, subprocess.SubprocessError):
        return ""
    hit = re.match(r"ffmpeg version (\S+)", first[0]) if first else None
    return hit.group(1).split("-")[0] if hit else ""


def _newer(installed: str, newest: str) -> bool:
    """bNNNN against bNNNN by the number, 0.4.0 against 0.5.0 by the parts; anything else - when they differ."""
    builds = re.fullmatch(r"b(\d+)", installed), re.fullmatch(r"b(\d+)", newest)
    if all(builds):
        return int(builds[1].group(1)) > int(builds[0].group(1))
    parts = [tuple(int(part) for part in re.findall(r"\d+", text)) for text in (installed, newest)]
    if all(parts):
        return parts[1] > parts[0]
    return bool(newest) and newest != installed


def version_of(key: str, paths: ProjectPaths, newest: dict) -> dict:
    """{"version", "latest", "fresh": fresh | old | own | "", "updatable"} of a module of the window."""
    if key == "dictation":
        installed = _marker(paths.tools / "transcribe" / "audion-transcribe-pack.txt").get("version", "")
        latest = str(newest.get("transcribe") or "")
        fresh = "" if not (installed and latest) else "old" if _newer(installed, latest) else "fresh"
        return {"version": installed, "latest": latest, "fresh": fresh, "updatable": False}
    if key in {"vulkan", "whispercpp_cuda"}:
        said = _marker(paths.tools / "whispercpp" / "audion-whispercpp-pack.txt")
        installed, kind = said.get("tag", ""), said.get("kind", "")
        if key == "whispercpp_cuda" and kind != "cublas":
            return {"version": "", "latest": str(newest.get("whispercpp:cublas") or ""), "fresh": "", "updatable": True}
        latest = str(newest.get(f"whispercpp:{'cublas' if kind == 'cublas' else 'cpu'}") or "")
        fresh = "" if not (installed and latest) else "old" if _newer(installed, latest) else "fresh"
        return {"version": installed, "latest": latest, "fresh": fresh, "updatable": True}
    own = {
        "ffmpeg": lambda: _ffmpeg(paths),
        "gigaam": lambda: _marker(paths.tools / "gigaam" / "audion-gigaam-onnx-pack.txt").get("kind", "").upper(),
        "speakers": lambda: "Sortformer · " + _package("transcribe-cpp") if _package("transcribe-cpp") else "",
        "gpu": lambda: _package("pyannote.audio"),
        "whispercpp_models": lambda: "large-v2" if (paths.models / "ggml-large-v2.bin").exists() else "",
    }
    installed = own[key]() if key in own else ""
    return {"version": installed, "latest": "", "fresh": "own" if installed else "", "updatable": False}
