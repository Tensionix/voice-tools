"""install\\Install-Live-Vulkan.ps1 puts the model the pack goes with in place: turbo with the CPU pack - a machine
without NVIDIA - and none with the CUDA pack, where turbo loops and large-v2 is the model. The installer runs on a copy
of itself in a temporary program with a local pack; the turbo URL of the copy points at a closed port, so nothing is
downloaded and an attempt shows as its first line."""

from __future__ import annotations

import os
import shutil
import subprocess
import zipfile
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
INSTALLER = ROOT / "install" / "Install-Live-Vulkan.ps1"
TURBO_URL = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin"


def _powershell() -> str:
    # PowerShell 7 alone: the PowerShell the program carries, or one of the machine.
    carried = ROOT / "App" / "AudionPs.exe"
    if carried.exists():
        return str(carried)
    found = shutil.which("pwsh")
    if found:
        return found
    pytest.skip("no PowerShell 7 to run the installer on")


def _program(tmp_path: Path, pack: str, *, turbo: bool) -> Path:
    root = tmp_path / "program"
    install = root / "install"
    install.mkdir(parents=True)
    script = INSTALLER.read_text(encoding="utf-8")
    assert script.count(f'"{TURBO_URL}"') == 1
    (install / INSTALLER.name).write_text(script.replace(TURBO_URL, "http://127.0.0.1:9/ggml-large-v3-turbo.bin"),
                                          encoding="utf-8")
    download = install / "download" / "whispercpp"
    download.mkdir(parents=True)
    name = "whisper-cublas-12.4.0-bin-x64.zip" if pack == "cublas" else "whisper-bin-x64.zip"
    with zipfile.ZipFile(download / name, "w") as archive:
        for file in ("whisper-server.exe", "whisper-cli.exe", "ggml.dll"):
            archive.writestr(f"Release/{file}", "")
    (root / "models").mkdir()
    if turbo:
        (root / "models" / "ggml-large-v3-turbo.bin").write_bytes(b"turbo")
    return root


def _install(root: Path, pack: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [_powershell(), "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
         str(root / "install" / INSTALLER.name), "-Pack", pack],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180,
        env={**os.environ, "NO_COLOR": "1"},
    )


def test_the_cpu_pack_of_a_machine_without_nvidia_still_gets_turbo(tmp_path) -> None:
    root = _program(tmp_path, "cpu", turbo=False)
    run = _install(root, "cpu")
    out = run.stdout + run.stderr
    assert "kind=cpu" in (root / "Tools" / "whispercpp" / "audion-whispercpp-pack.txt").read_text(encoding="utf-8-sig")
    # The download of turbo begins - and fails on the closed port of the copy.
    assert "Turbo model download started" in out
    assert run.returncode != 0


def test_the_cpu_pack_keeps_a_turbo_in_place(tmp_path) -> None:
    root = _program(tmp_path, "cpu", turbo=True)
    run = _install(root, "cpu")
    out = run.stdout + run.stderr
    assert run.returncode == 0, out
    assert "Turbo model already present." in out and (root / "models" / "ggml-large-v3-turbo.bin").exists()


def test_the_cuda_pack_downloads_no_turbo_and_names_large_v2(tmp_path) -> None:
    root = _program(tmp_path, "cublas", turbo=False)
    run = _install(root, "cublas")
    out = run.stdout + run.stderr
    assert run.returncode == 0, out
    assert "kind=cublas" in (root / "Tools" / "whispercpp" / "audion-whispercpp-pack.txt").read_text(encoding="utf-8-sig")
    assert "Turbo model download started" not in out and "not used on CUDA, not downloaded" in out
    assert "ggml-large-v2.bin - missing, install\\Install-WhisperCpp-Large-Models.cmd" in out
    assert not (root / "models" / "ggml-large-v3-turbo.bin").exists()
