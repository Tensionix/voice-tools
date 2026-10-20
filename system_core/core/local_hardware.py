"""Lightweight local hardware detection for choosing local STT defaults."""

from __future__ import annotations

import importlib.util
import os
import platform
import re
import shutil
import subprocess
import ctypes
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path
from typing import Callable

from .editions import (
    EDITION_STUDIO,
    MODE_API,
    MODE_CUDA,
    MODE_VULKAN,
    current_edition,
    vulkan_pack_installed,
    whispercpp_cublas_backend_present,
    whispercpp_vulkan_backend_present,
)
from .paths import ProjectPaths, get_project_paths

LocalHardwareProgress = Callable[[int, int, str], None]
_DETECTION_STAGES = (
    "local_hw_stage_start",
    "local_hw_stage_nvidia",
    "local_hw_stage_windows_gpu",
    "local_hw_stage_onnx_runtime",
    "local_hw_stage_onnx_providers",
    "local_hw_stage_gigaam_runtime",
    "local_hw_stage_whispercpp",
    "local_hw_stage_recommendation",
)


@dataclass(frozen=True)
class LocalHardwareProfile:
    gpu_names: tuple[str, ...]
    has_nvidia: bool
    has_intel: bool
    has_amd: bool
    has_cuda_runtime: bool
    has_onnxruntime: bool
    onnx_providers: tuple[str, ...]
    has_gigaam_runtime: bool
    has_whispercpp: bool
    has_whispercpp_vulkan_backend: bool
    has_whispercpp_cublas_backend: bool = False
    # Why an NVIDIA card here is not offered its parts (a card too old, a driver too old, no driver); "" - none such.
    nvidia_note: str = ""

    @property
    def recommended_stack(self) -> str:
        providers = set(self.onnx_providers)
        if self.has_nvidia and "CUDAExecutionProvider" in providers:
            return "onnx_cuda"
        if (self.has_amd or self.has_intel or self.has_nvidia) and "DmlExecutionProvider" in providers:
            return "onnx_directml"
        if self.has_onnxruntime and "CPUExecutionProvider" in providers:
            return "onnx_cpu"
        if self.has_nvidia and self.has_whispercpp_cublas_backend:
            return "whispercpp_cublas"
        if self.has_whispercpp_vulkan_backend:
            return "whispercpp_vulkan"
        if self.has_whispercpp:
            return "whispercpp_cpu"
        return "cpu"


def _has(module: str) -> bool:
    try:
        return importlib.util.find_spec(module) is not None
    except (ImportError, ValueError):
        return False


def _run(command: list[str]) -> str:
    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            timeout=2.5,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except Exception:
        return ""
    return "\n".join(part for part in (result.stdout, result.stderr) if part).strip()


# The parts of an NVIDIA card - torch 2.8.0+cu128 (install\constraints_cuda.txt), onnxruntime-gpu and the cuBLAS pack of
# whisper.cpp, all of CUDA 12 - are offered only where they run: a card of compute capability 6.1 or newer (torch cu128
# carries code from sm_61 up) and a driver of 528.33 or newer, the first driver of Windows CUDA 12 runs on. An older card
# or driver is no NVIDIA card to the program: the processor, Vulkan and DirectML serve it (the owner, 20.10.2026: «модели
# и NVIDIA-части - при определении в системе нужного GPU/драйвера»).
NVIDIA_MIN_CAPABILITY = (6, 1)
NVIDIA_MIN_DRIVER = (528, 33)


def _version(text: str) -> tuple[int, ...] | None:
    parts = re.findall(r"\d+", text)
    return tuple(int(part) for part in parts[:2]) if parts else None


def nvidia_fit(told: str) -> tuple[bool, str]:
    """
    Whether an NVIDIA card here takes its parts, from `nvidia-smi --query-gpu=name,compute_cap,driver_version
    --format=csv,noheader`: (True, "") - one fits; (False, why) - none does; (False, "") - nvidia-smi named no card.
    A driver too old to know the field compute_cap answers with an error instead of a row: too old for CUDA 12 as well.
    """
    why = ""
    for line in told.splitlines():
        cells = [cell.strip() for cell in line.split(",")]
        if len(cells) < 3 or "NVIDIA" not in cells[0].upper():
            if "compute_cap" in line or "not a valid field" in line.lower():
                why = "the NVIDIA driver is too old for CUDA 12 (528.33 or newer is needed)"
            continue
        name, capability, driver = cells[0], _version(cells[1]), _version(cells[2])
        if capability is None or capability < NVIDIA_MIN_CAPABILITY:
            why = f"{name}: compute capability {cells[1]} - CUDA parts need 6.1 or newer"
        elif driver is None or driver < NVIDIA_MIN_DRIVER:
            why = f"{name}: driver {cells[2]} - CUDA parts need 528.33 or newer"
        else:
            return True, ""
    return False, why


def _nvidia_fit(names: tuple[str, ...]) -> tuple[bool, str]:
    nvidia_smi = shutil.which("nvidia-smi")
    if not nvidia_smi:
        named = next((name for name in names if "nvidia" in name.lower()), "")
        return False, f"{named}: the NVIDIA driver is not installed (no nvidia-smi)" if named else ""
    return nvidia_fit(_run([nvidia_smi, "--query-gpu=name,compute_cap,driver_version", "--format=csv,noheader"]))


def _emit(progress: LocalHardwareProgress | None, step: int, key: str) -> None:
    if progress is not None:
        progress(step, len(_DETECTION_STAGES), key)


def _video_controller_names(progress: LocalHardwareProgress | None = None, root: str = "") -> tuple[str, ...]:
    names: list[str] = []
    nvidia_smi = shutil.which("nvidia-smi")
    _emit(progress, 2, "local_hw_stage_nvidia")
    if nvidia_smi:
        out = _run([nvidia_smi, "-L"])
        for line in out.splitlines():
            # «GPU 0: NVIDIA GeForce RTX 5070 (UUID: GPU-…)» - the name alone; Windows names the same card once more below.
            name = re.sub(r"\s*\(UUID:[^)]*\)\s*$", "", re.sub(r"^GPU\s+\d+:\s*", "", line.strip())).strip()
            if name and name.lower() not in {one.lower() for one in names}:
                names.append(name)

    _emit(progress, 3, "local_hw_stage_windows_gpu")
    if platform.system().lower() == "windows":
        # The PowerShell the program carries first (App\AudionPs.exe), then the one of Windows.
        own = Path(root) / "App" / "AudionPs.exe" if root else None
        ps = str(own) if own is not None and own.exists() else shutil.which("powershell") or shutil.which("pwsh")
        if ps:
            out = _run([
                ps,
                "-NoProfile",
                "-Command",
                "Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name",
            ])
            for line in out.splitlines():
                line = line.strip()
                if line and line.lower() not in {one.lower() for one in names}:
                    names.append(line)
    return tuple(names)


def _onnx_providers() -> tuple[str, ...]:
    try:
        import onnxruntime as ort  # type: ignore

        providers = []
        for item in ort.get_available_providers():
            provider = str(item)
            if provider in {"TensorrtExecutionProvider", "NvTensorRTRTXExecutionProvider", "NvTensorRtRtxExecutionProvider"}:
                continue
            if _onnx_provider_loadable(ort, provider):
                providers.append(provider)
        return tuple(providers)
    except Exception:
        return ()


def preload_cuda_libraries(ort) -> None:
    """The CUDA, cuDNN and MSVC libraries, loaded before a CUDA session.

    ort.preload_dlls takes them from torch\\lib when a torch of the same CUDA is installed (the speakers on the video
    card bring one), else from the NVIDIA wheels, and loads the cuDNN libraries of its own time. A newer cuDNN loads parts
    of itself only when a Conv runs (cuDNN 9.24: cudnn_engines_tensor_ir64_9.dll) and looks for them by name alone, or the
    first Conv fails and ONNX Runtime falls back to the processor (RTX 5070, 20.10.2026). So the rest of the cuDNN set is
    loaded from the folder ort took cudnn64_9.dll from - never from another folder: two cuDNN versions in one process
    fail with "the specified procedure could not be found"."""
    preload = getattr(ort, "preload_dlls", None)
    if callable(preload):
        try:
            preload(cuda=True, cudnn=True, msvc=True)
        except Exception:
            pass
    if platform.system().lower() != "windows":
        return
    folder = _loaded_library_folder("cudnn64_9.dll")
    if folder is None:
        return
    try:
        os.add_dll_directory(str(folder))
    except OSError:
        pass
    if str(folder) not in os.environ.get("PATH", ""):
        os.environ["PATH"] = str(folder) + os.pathsep + os.environ.get("PATH", "")
    for dll in sorted(folder.glob("cudnn*64_9.dll")):
        try:
            ctypes.WinDLL(str(dll))
        except OSError:
            pass


def _loaded_library_folder(name: str) -> Path | None:
    """The folder a DLL already in this process was loaded from; None when it is not loaded."""
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
    kernel32.GetModuleHandleW.restype = ctypes.c_void_p
    kernel32.GetModuleFileNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_uint32]
    kernel32.GetModuleFileNameW.restype = ctypes.c_uint32
    handle = kernel32.GetModuleHandleW(name)
    if not handle:
        return None
    buffer = ctypes.create_unicode_buffer(32768)
    if not kernel32.GetModuleFileNameW(handle, buffer, len(buffer)):
        return None
    return Path(buffer.value).parent


def _onnx_provider_loadable(ort, provider: str) -> bool:
    if platform.system().lower() != "windows":
        return True
    dll_names = {
        "CUDAExecutionProvider": "onnxruntime_providers_cuda.dll",
    }
    dll_name = dll_names.get(provider)
    if not dll_name:
        return True
    if provider == "CUDAExecutionProvider":
        preload_cuda_libraries(ort)
    try:
        capi_dir = Path(ort.__file__).resolve().parent / "capi"
        ctypes.WinDLL(str(capi_dir / dll_name))
        return True
    except Exception:
        return False


def _build_profile(root: str, progress: LocalHardwareProgress | None = None) -> LocalHardwareProfile:
    _emit(progress, 1, "local_hw_stage_start")
    paths = get_project_paths(Path(root))
    names = _video_controller_names(progress, root)
    text = " ".join(names).lower()
    _emit(progress, 4, "local_hw_stage_onnx_runtime")
    has_onnxruntime = _has("onnxruntime")
    _emit(progress, 5, "local_hw_stage_onnx_providers")
    onnx_providers = _onnx_providers()
    _emit(progress, 6, "local_hw_stage_gigaam_runtime")
    has_gigaam_runtime = _has("onnx_asr") or _has("gigaam")
    _emit(progress, 7, "local_hw_stage_whispercpp")
    has_cuda_runtime = _has("torch") or whispercpp_cublas_backend_present(paths)
    has_whispercpp = vulkan_pack_installed(paths)
    has_whispercpp_vulkan_backend = whispercpp_vulkan_backend_present(paths)
    has_whispercpp_cublas_backend = whispercpp_cublas_backend_present(paths)
    _emit(progress, 8, "local_hw_stage_recommendation")
    fit, note = _nvidia_fit(names)
    return LocalHardwareProfile(
        gpu_names=names,
        # An NVIDIA card counts when its parts run on it: the card and its driver new enough (nvidia_fit).
        has_nvidia=fit,
        nvidia_note=note,
        has_intel=("intel" in text) or ("iris" in text) or ("arc" in text),
        has_amd=("amd" in text) or ("radeon" in text),
        has_cuda_runtime=has_cuda_runtime,
        has_onnxruntime=has_onnxruntime,
        onnx_providers=onnx_providers,
        has_gigaam_runtime=has_gigaam_runtime,
        has_whispercpp=has_whispercpp,
        has_whispercpp_vulkan_backend=has_whispercpp_vulkan_backend,
        has_whispercpp_cublas_backend=has_whispercpp_cublas_backend,
    )


@lru_cache(maxsize=16)
def _cached_profile(root: str) -> LocalHardwareProfile:
    return _build_profile(root)


def detect_local_hardware(
    paths: ProjectPaths,
    progress: LocalHardwareProgress | None = None,
) -> LocalHardwareProfile:
    if progress is None:
        return _cached_profile(str(paths.root))
    return _build_profile(str(paths.root), progress)


def recommended_compute_mode(paths: ProjectPaths, settings: dict | None = None) -> str:
    """Pick a conservative file-engine default for `compute_mode: auto`."""
    profile = detect_local_hardware(paths)
    if profile.has_gigaam_runtime and profile.has_onnxruntime:
        return MODE_VULKAN
    if current_edition(paths, settings) == EDITION_STUDIO and profile.has_nvidia and profile.has_cuda_runtime:
        return MODE_CUDA
    if profile.has_whispercpp:
        return MODE_VULKAN
    return MODE_API


def recommended_gigaam_backend(paths: ProjectPaths) -> str:
    profile = detect_local_hardware(paths)
    stack = profile.recommended_stack
    if stack == "onnx_cuda":
        return "cuda"
    if stack == "onnx_directml":
        return "directml"
    return "cpu"


def recommended_whispercpp_backend(paths: ProjectPaths) -> str:
    profile = detect_local_hardware(paths)
    if profile.has_whispercpp_cublas_backend and profile.has_nvidia:
        return "cublas"
    return "cpu"
