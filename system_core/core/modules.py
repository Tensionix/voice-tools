"""Optional Voice Tools modules: the opt-in installers exposed in the GUI.

The base (API-mode) env stays light; heavier stacks (GigaAM/ONNX,
local Whisper, GPU diarization), the mic capture deps and the portable FFmpeg are installed on
demand via the `install/*.cmd` scripts. This catalog is the single source of
truth the UI renders: each entry knows its installer script and how to detect
whether it's already present, so the "Install Voice Tools modules" section can show
live status and run the right script.
"""

from __future__ import annotations

import importlib.util
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from .editions import (
    EDITION_LIVE,
    EDITION_STUDIO,
    current_edition,
    whispercpp_cuda_ready,
    whispercpp_runtime_ready,
)
from .live_dependencies import check_live_dependencies
from .paths import ProjectPaths


def _has(module: str) -> bool:
    """True if an import would resolve `module` (without importing it)."""
    try:
        return importlib.util.find_spec(module) is not None
    except (ImportError, ValueError):
        return False


def _ffmpeg_installed(paths: ProjectPaths) -> bool:
    bin_dir = paths.tools / "ffmpeg" / "bin"
    return (bin_dir / "ffmpeg.exe").exists() or (bin_dir / "ffmpeg").exists()


def _wheel_cache_installed(paths: ProjectPaths) -> bool:
    # The installers (Install-GigaAM-ONNX.ps1, Install-Live-Deps.cmd) and
    # Rebuild-Wheel-Cache.ps1 all read/write the root-level `wheelhouse`.
    wheel_root = paths.root / "wheelhouse"
    required = ["live", "common", "directml", "cpu"]
    if current_edition(paths) == EDITION_STUDIO:
        required.append("cuda")
    return all(any((wheel_root / name).glob("*.whl")) for name in required)


def _live_installed(paths: ProjectPaths) -> bool:
    return check_live_dependencies().ready


def _gigaam_installed(paths: ProjectPaths) -> bool:
    marker = paths.tools / "gigaam" / "audion-gigaam-onnx-pack.txt"
    return _has("onnx_asr") and _has("onnxruntime") and marker.exists()


def _marker_value(path: Path, key: str) -> str:
    if not path.exists():
        return ""
    prefix = f"{key}="
    try:
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            if line.startswith(prefix):
                return line[len(prefix):].strip().lower()
    except OSError:
        return ""
    return ""


def _onnx_providers() -> set[str]:
    try:
        import onnxruntime as ort  # type: ignore

        return set(ort.get_available_providers())
    except Exception:
        return set()


def _gigaam_provider(paths: ProjectPaths, kind: str, providers: tuple[str, ...]) -> bool:
    marker = paths.tools / "gigaam" / "audion-gigaam-onnx-pack.txt"
    if not (_has("onnx_asr") and _has("onnxruntime") and marker.exists()):
        return False
    if _marker_value(marker, "kind") != kind:
        return False
    available = _onnx_providers()
    return any(provider in available for provider in providers)


def _gigaam_directml_installed(paths: ProjectPaths) -> bool:
    return _gigaam_provider(paths, "directml", ("DmlExecutionProvider",))


def _gigaam_cuda_installed(paths: ProjectPaths) -> bool:
    return _gigaam_provider(
        paths,
        "cuda",
        ("CUDAExecutionProvider",),
    )


def _gpu_installed(paths: ProjectPaths) -> bool:
    return _has("torch") and _has("pyannote")


def _vulkan_installed(paths: ProjectPaths) -> bool:
    """The processor's pack with turbo - the Whisper of files of a machine without NVIDIA."""
    return whispercpp_runtime_ready(
        paths, model="turbo", backend="cpu", require_server=True
    )


def _whispercpp_cuda_installed(paths: ProjectPaths) -> bool:
    """The CUDA pack alone: its model is large-v2, a component of its own; turbo is not used on CUDA."""
    return whispercpp_cuda_ready(paths, model=None, require_server=True)


def _whispercpp_large_models_installed(paths: ProjectPaths) -> bool:
    return (paths.models / "ggml-large-v2.bin").exists()


def _speakers_installed(paths: ProjectPaths) -> bool:
    from ..providers.diarization_sortformer import sortformer_ready

    return sortformer_ready(paths)


def _dictation_engine_in_place(paths: ProjectPaths) -> bool:
    """transcribe.cpp put in place by Install-Dictation-Engine.ps1 (its marker)."""
    tools = paths.tools / "transcribe"
    return _marker_value(tools / "audion-transcribe-pack.txt", "version") == "0.4.0" and (tools / "transcribe.dll").exists()


def _dictation_installed(paths: ProjectPaths) -> bool:
    """The engine of dictation and the GigaAM GGUF the window loads by default."""
    return _dictation_engine_in_place(paths) and (paths.models / "transcribe" / "gigaam-v3-e2e-rnnt-Q8_0.gguf").exists()


@dataclass(frozen=True)
class ModuleInfo:
    key: str            # stable id
    script: str         # filename under install/
    name_key: str       # i18n key (display name)
    desc_key: str       # i18n key (one-line description)
    _check: Callable[[ProjectPaths], bool]
    editions: tuple[str, ...] = (EDITION_LIVE, EDITION_STUDIO)
    install_key: str = "mod_install"
    reinstall_key: str = "mod_reinstall"
    # Approximate network download in megabytes (models, packs, wheels not
    # shipped in the distribution). Shown in the first-run setup prompt.
    download_mb: int = 0
    # The window of 3.0 (SETUP, voice_cli.cmd components): the name and what it is for, (ru, en). A module the window
    # does not list - the builder's own steps - has window=False.
    title: tuple[str, str] = ("", "")
    about: tuple[str, str] = ("", "")
    window: bool = True
    # What `voice_cli.cmd install MODULE --update` gives the installer: the newest release, nothing reused.
    update_args: tuple[str, ...] = ()
    # What the installer is always given: a model of dictation names itself to the engine's installer (-ModelId).
    args: tuple[str, ...] = ()

    def script_path(self, paths: ProjectPaths) -> Path:
        return paths.root / "install" / self.script

    def is_installed(self, paths: ProjectPaths) -> bool:
        try:
            return bool(self._check(paths))
        except Exception:
            return False


RECOMMENDED = "recommended"
OPTIONAL = "optional"
NOT_NEEDED = "not_needed"

# Modules every installation of the edition should have. Restore rows and the
# Studio GPU stack depend on the detected hardware (see module_recommendation).
_ALWAYS_RECOMMENDED = {"ffmpeg", "live", "wheel_cache", "gigaam", "vulkan", "whispercpp_models"}
# "speakers" (keyless sherpa-onnx separation) stays OPTIONAL on purpose: it is coarse
# (similar voices merge), so the first-run prompt must not install it for everyone.


def module_recommendation(mod: ModuleInfo, paths: ProjectPaths, profile=None) -> str:
    """Classify a module for this computer: RECOMMENDED / OPTIONAL / NOT_NEEDED.

    `profile` is a `LocalHardwareProfile` (or None while detection runs / after
    it failed, which yields the safe non-GPU recommendations)."""
    edition = current_edition(paths)
    has_nvidia = bool(getattr(profile, "has_nvidia", False))
    has_windows_gpu = bool(
        has_nvidia
        or getattr(profile, "has_amd", False)
        or getattr(profile, "has_intel", False)
    )
    if mod.key in _ALWAYS_RECOMMENDED:
        return RECOMMENDED
    if mod.key == "gpu":
        # Opt-in even on NVIDIA: the pyannote weights are gated behind a personal
        # Hugging Face account, so the first-run prompt must not queue them.
        return OPTIONAL if edition == EDITION_STUDIO and has_nvidia else NOT_NEEDED
    if mod.key == "restore_rtx":
        if mod.is_installed(paths):
            return NOT_NEEDED
        return OPTIONAL if edition == EDITION_STUDIO and has_nvidia else NOT_NEEDED
    if mod.key == "restore_intel":
        if mod.is_installed(paths):
            return NOT_NEEDED
        return OPTIONAL if has_windows_gpu and not has_nvidia else NOT_NEEDED
    return OPTIONAL


def window_advice(mod: ModuleInfo, paths: ProjectPaths, profile=None) -> str:
    """The advice of SETUP in the window of 3.0: one program for every machine, so what a module needs is the machine,
    not an edition - the heavy stacks of NVIDIA are offered where an NVIDIA card is."""
    has_nvidia = bool(getattr(profile, "has_nvidia", False))
    has_other_gpu = bool(getattr(profile, "has_amd", False) or getattr(profile, "has_intel", False))
    if mod.key in {"dictation", "ffmpeg", "gigaam"}:
        return RECOMMENDED
    # The Whisper of files by the machine: with an NVIDIA card it is large-v2 on CUDA - turbo loops there and is not
    # downloaded; without one it is turbo on the processor, the one Whisper a processor carries.
    if mod.key == "vulkan":
        return NOT_NEEDED if has_nvidia else RECOMMENDED
    if mod.key in {"whispercpp_cuda", "whispercpp_models"}:
        return RECOMMENDED if has_nvidia else NOT_NEEDED
    if mod.key == "gpu":
        # Offered on an NVIDIA card, never queued: Community-1 is gated behind a personal Hugging Face account.
        return OPTIONAL if has_nvidia else NOT_NEEDED
    if mod.key == "restore_rtx":
        return OPTIONAL if has_nvidia and not mod.is_installed(paths) else NOT_NEEDED
    if mod.key == "restore_intel":
        return OPTIONAL if has_other_gpu and not has_nvidia and not mod.is_installed(paths) else NOT_NEEDED
    return OPTIONAL


def window_modules() -> list[ModuleInfo]:
    """The modules SETUP of the window lists, in the order INSTALL ALL puts them in place."""
    return [module for module in _MODULES if module.window]


def missing_recommended_modules(paths: ProjectPaths, profile=None) -> list[ModuleInfo]:
    """Recommended modules that are not installed yet, in install order.

    This is the list the first-run setup prompt offers to download so the
    readiness matrix on the Maintenance page turns fully green."""
    return [
        mod
        for mod in list_modules(paths)
        if module_recommendation(mod, paths, profile) == RECOMMENDED
        and not mod.is_installed(paths)
    ]


# GUI runs from an already prepared portable Python app. Mirror the builder's
# user-facing install flow from the first step that can be launched inside GUI.
_MODULES: list[ModuleInfo] = [
    # transcribe.cpp 0.4.0 CPU + Vulkan pack (17 MB) + GigaAM v3 RNN-T Q8_0 GGUF (261 MB). The window of 3.0 alone
    # dictates on it: no edition of the former window lists it.
    ModuleInfo("dictation", "Install-Dictation-Engine.cmd",
               "", "", _dictation_installed, editions=(), download_mb=280,
               title=("Движок диктовки", "Dictation engine"),
               about=("Распознаёт диктовку на этой машине: transcribe.cpp на процессоре и Vulkan с моделью на выбор; GigaAM v3 - лучшая модель для русского языка.",
                      "Recognizes dictation on this machine: transcribe.cpp on the CPU and Vulkan with a model to choose; GigaAM v3 is the best model for Russian.")),
    ModuleInfo("ffmpeg", "Install-Portable-FFmpeg-BtbN.cmd",
               "mod_ffmpeg", "mod_ffmpeg_desc", _ffmpeg_installed, download_mb=150,
               title=("FFmpeg", "FFmpeg"),
               about=("Достаёт звук из записей и видео: без него файлы не обработать.",
                      "Takes the sound out of recordings and videos: files are not processed without it.")),
    ModuleInfo("wheel_cache", "Rebuild-Wheel-Cache.cmd",
               "mod_wheel_cache", "mod_wheel_cache_desc", _wheel_cache_installed, download_mb=60,
               title=("Кэш пакетов Python", "Python package cache"),
               about=("Пакеты движков, скачанные заранее: установки после него идут без сети.",
                      "The packages of the engines, downloaded ahead: the installs after it need no network."),
               window=False),
    ModuleInfo("live", "Install-Live-Deps.cmd",
               "mod_live", "mod_live_desc", _live_installed, download_mb=1,
               title=("Пакеты диктовки 2.3", "Dictation packages of 2.3"),
               about=("Микрофон и облачные потоки прежнего окна: sounddevice и websockets.",
                      "The microphone and the cloud streams of the former window: sounddevice and websockets."),
               window=False),
    # GigaAM v3 CTC + RNN-T ONNX payloads (~845 MB each) from Hugging Face.
    ModuleInfo("gigaam", "Install-GigaAM-ONNX.cmd",
               "mod_gigaam", "mod_gigaam_desc", _gigaam_installed, download_mb=1750,
               title=("GigaAM для файлов", "GigaAM for files"),
               about=("Русская речь в записях: GigaAM v3 на ONNX Runtime, длинные записи режутся по паузам.",
                      "Russian speech in recordings: GigaAM v3 on ONNX Runtime, long recordings cut at pauses.")),
    # whisper.cpp CPU pack (8 MB) + ggml-large-v3-turbo.bin (1549 MB): a machine without NVIDIA.
    ModuleInfo("vulkan", "Install-Live-Vulkan.cmd",
               "mod_vulkan", "mod_vulkan_desc", _vulkan_installed, download_mb=1560, update_args=("-Update",),
               title=("Whisper для файлов", "Whisper for files"),
               about=("Английская и смешанная речь в записях на процессоре: whisper.cpp с моделью large-v3-turbo. С видеокартой NVIDIA вместо него - Whisper на CUDA и Whisper large-v2.",
                      "English and mixed speech in recordings on the CPU: whisper.cpp with the large-v3-turbo model. With an NVIDIA card, Whisper on CUDA and Whisper large-v2 instead.")),
    # whisper.cpp cuBLAS pack (~450 MB) in place of the CPU pack; no turbo - it loops on CUDA, a turbo left from the
    # processor goes to the Recycle Bin.
    ModuleInfo("whispercpp_cuda", "Install-WhisperCpp-CUDA.cmd",
               "", "", _whispercpp_cuda_installed, editions=(), download_mb=640, update_args=("-Update",),
               title=("Whisper на CUDA", "Whisper on CUDA"),
               about=("whisper.cpp на видеокарте NVIDIA вместо процессора, с моделью Whisper large-v2: записи с английской и смешанной речью идут в разы быстрее. Модели turbo здесь нет - на CUDA она повторяет фразы.",
                      "whisper.cpp on the NVIDIA card instead of the CPU, with the Whisper large-v2 model: recordings of English and mixed speech go several times faster. No turbo model here - on CUDA it repeats phrases.")),
    # ggml-large-v2.bin (2951 MB).
    ModuleInfo("whispercpp_models", "Install-WhisperCpp-Large-Models.cmd",
               "mod_whispercpp_models", "mod_whispercpp_models_desc",
               _whispercpp_large_models_installed, editions=(), download_mb=2960,
               title=("Whisper large-v2", "Whisper large-v2"),
               about=("Модель whisper.cpp для файлов на видеокарте NVIDIA: английская и смешанная речь, на CUDA так же быстра, как turbo, и без повторов.",
                      "The whisper.cpp model for files on an NVIDIA card: English and mixed speech, as fast on CUDA as turbo and without repeats.")),
    # torch cu128 (3273 MB) + torchaudio + pyannote.audio 4 and the Community-1 weights.
    ModuleInfo("gpu", "Install-Diarization-GPU.cmd",
               "mod_gpu", "mod_gpu_desc", _gpu_installed, (EDITION_STUDIO,), download_mb=3700,
               title=("Говорящие на видеокарте", "Speakers on the video card"),
               about=("Разделение по говорящим pyannote Community-1 на видеокарте NVIDIA: точнее, но нужен ключ Hugging Face и согласие с условиями модели.",
                      "Speaker separation by pyannote Community-1 on an NVIDIA card: more precise, but needs a Hugging Face key and the model terms accepted.")),
    # The binding of transcribe.cpp (47 KB) + Sortformer Q8_0 GGUF (133 MB); the engine is that of dictation.
    ModuleInfo("speakers", "Install-Diarization-Local.cmd",
               "mod_speakers", "mod_speakers_desc", _speakers_installed, download_mb=135,
               title=("Говорящие без ключа", "Speakers without a key"),
               about=("Sortformer на движке диктовки: сам находит говорящих, до четырёх, без ключа Hugging Face; Vulkan или процессор, десять минут записи - около 20 секунд.",
                      "Sortformer on the engine of dictation: finds the speakers itself, four at most, without a Hugging Face key; Vulkan or the CPU, ten minutes of a recording in about 20 seconds.")),
    ModuleInfo("restore_intel", "Restore-GigaAM-DirectML.cmd",
               "mod_restore_intel", "mod_restore_intel_desc",
               _gigaam_directml_installed, install_key="mod_restore", reinstall_key="mod_restore",
               title=("GigaAM на DirectML", "GigaAM on DirectML"),
               about=("Вернуть GigaAM для файлов к видеокартам Intel и AMD - после переноса программы с машины с NVIDIA.",
                      "Return GigaAM for files to Intel and AMD cards - after the program moved from a machine with NVIDIA.")),
    ModuleInfo("restore_rtx", "Restore-GigaAM-CUDA.cmd",
               "mod_restore_rtx", "mod_restore_rtx_desc",
               _gigaam_cuda_installed, install_key="mod_restore", reinstall_key="mod_restore",
               title=("GigaAM на CUDA", "GigaAM on CUDA"),
               about=("Перевести GigaAM для файлов на видеокарту NVIDIA через CUDA.",
                      "Move GigaAM for files onto the NVIDIA card through CUDA.")),
]


def _dictation_model_modules() -> list[ModuleInfo]:
    """
    The other models of dictation (config/dictation_models.json, pinned by revision and SHA-256): each a module of its own -
    the engine's installer with -ModelId, in place when the engine is and the file has its size. GigaAM is the module
    "dictation" itself. DICTATIONS chooses among the models installed and installs the rest from its list.
    """
    catalog = Path(__file__).resolve().parents[2] / "config" / "dictation_models.json"
    try:
        models = json.loads(catalog.read_text(encoding="utf-8"))["models"]
    except (OSError, ValueError, KeyError):
        return []
    found: list[ModuleInfo] = []
    for model in models:
        if model.get("module") in (None, "", "dictation"):
            continue
        file, size = str(model["file"]), int(model["bytes"])

        def check(paths: ProjectPaths, file: str = file, size: int = size) -> bool:
            target = paths.models / "transcribe" / file
            return _dictation_engine_in_place(paths) and target.exists() and target.stat().st_size == size

        found.append(ModuleInfo(
            str(model["module"]), "Install-Dictation-Engine.cmd", "", "", check, editions=(),
            download_mb=round(size / 2 ** 20),
            title=(f"Модель диктовки: {model['title']}", f"Dictation model: {model['title']}"),
            about=(str(model["about"]["ru"]), str(model["about"]["en"])),
            args=("-ModelId", str(model["id"])),
        ))
    return found


_MODULES.extend(_dictation_model_modules())


def list_modules(paths: ProjectPaths | None = None, settings: dict | None = None) -> list[ModuleInfo]:
    if paths is None:
        return list(_MODULES)
    edition = current_edition(paths, settings)
    return [module for module in _MODULES if edition in module.editions]
