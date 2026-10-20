from __future__ import annotations

from system_core.core.editions import (
    MODE_API,
    MODE_CUDA,
    MODE_VULKAN,
    current_edition,
    display_compute_mode,
    visible_compute_modes,
)
from system_core.core.paths import get_project_paths


def test_live_folder_exposes_openai_and_vulkan_modes():
    paths = get_project_paths()
    assert current_edition(paths, {"edition": "live"}) == "live"
    assert visible_compute_modes(paths, {"edition": "live"}) == [MODE_API, MODE_VULKAN]


def test_studio_exposes_cuda_mode():
    paths = get_project_paths()
    assert visible_compute_modes(paths, {"edition": "studio"}) == [MODE_API, MODE_VULKAN, MODE_CUDA]


def test_legacy_modes_display_as_new_product_modes():
    assert display_compute_mode("cpu") == MODE_VULKAN
    assert display_compute_mode("gpu") == MODE_CUDA
    assert display_compute_mode("openai") == MODE_API


# ---------------------------------------------------------------- the model of the whisper.cpp pack


def _pack(tmp_path, kind: str, *models: str):
    """A whisper.cpp pack of the kind (cpu / cublas) with its payloads, and the models named."""
    paths = get_project_paths(tmp_path)
    root = paths.tools / "whispercpp"
    root.mkdir(parents=True)
    paths.models.mkdir(parents=True)
    (root / "audion-whispercpp-pack.txt").write_text(f"kind={kind}\n", encoding="utf-8")
    payloads = ["whisper-cli.exe", "whisper-server.exe", "ggml.dll", "ggml-base.dll", "whisper.dll"]
    payloads += (["ggml-cuda.dll", "cudart64_12.dll", "cublas64_12.dll", "ggml-cpu-x64.dll"] if kind == "cublas"
                 else ["ggml-cpu-x64.dll"])
    for name in payloads:
        (root / name).write_text("", encoding="utf-8")
    for model in models:
        (paths.models / model).write_text("", encoding="utf-8")
    return paths


def test_the_cuda_pack_recognizes_files_with_large_v2_and_never_turbo(tmp_path):
    from system_core.core.editions import resolve_whispercpp_model, whispercpp_default_model, whispercpp_file_model

    paths = _pack(tmp_path, "cublas", "ggml-large-v2.bin", "ggml-large-v3-turbo.bin")
    assert whispercpp_default_model(paths) == "large-v2"
    assert [whispercpp_file_model(paths, asked) for asked in ("turbo", "large-v3-turbo", "", None, "large-v2")] == ["large-v2"] * 5
    assert resolve_whispercpp_model(paths).name == "ggml-large-v2.bin"


def test_the_cpu_pack_of_a_machine_without_nvidia_keeps_turbo(tmp_path):
    from system_core.core.editions import resolve_whispercpp_model, whispercpp_default_model, whispercpp_file_model

    paths = _pack(tmp_path, "cpu", "ggml-large-v3-turbo.bin")
    assert whispercpp_default_model(paths) == "turbo"
    assert whispercpp_file_model(paths, "turbo") == "turbo" and whispercpp_file_model(paths, None) == "turbo"
    assert whispercpp_file_model(paths, "large-v2") == "large-v2"
    assert resolve_whispercpp_model(paths).name == "ggml-large-v3-turbo.bin"
    # No pack at all: turbo, as before.
    assert whispercpp_default_model(get_project_paths(tmp_path / "bare")) == "turbo"


def test_setup_counts_whisper_on_cuda_without_turbo_and_whisper_for_files_by_the_cpu_pack(tmp_path):
    from system_core.core import modules

    by_id = {module.key: module for module in modules.window_modules()}
    cuda = _pack(tmp_path / "rtx", "cublas", "ggml-large-v2.bin")
    assert by_id["whispercpp_cuda"].is_installed(cuda) and by_id["whispercpp_models"].is_installed(cuda)
    # Whisper for files is the CPU pack with turbo: the CUDA pack is not it, a turbo left beside it or not.
    assert not by_id["vulkan"].is_installed(cuda)
    (cuda.models / "ggml-large-v3-turbo.bin").write_text("", encoding="utf-8")
    assert not by_id["vulkan"].is_installed(cuda)

    cpu = _pack(tmp_path / "carbon", "cpu", "ggml-large-v3-turbo.bin")
    assert by_id["vulkan"].is_installed(cpu) and not by_id["whispercpp_cuda"].is_installed(cpu)
    (cpu.models / "ggml-large-v3-turbo.bin").unlink()
    assert not by_id["vulkan"].is_installed(cpu)
