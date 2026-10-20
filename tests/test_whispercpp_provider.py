from __future__ import annotations

from pathlib import Path

import pytest

from system_core.core.jobs import ProcessResult
from system_core.core.paths import get_project_paths
from system_core.providers import whispercpp_provider as wc
from system_core.providers import gigaam_provider as gp
from system_core.providers import registry
from system_core.providers.base import TranscriptionOptions
from system_core.providers.gigaam_provider import DEFAULT_GIGAAM_FILE_MODEL, GigaAMLiveTranscriber, GigaAMTranscribeProvider
from system_core.providers.whispercpp_provider import WhisperCppProvider


def _stage_whispercpp_pack(tmp_path, *, vulkan: bool = False):
    paths = get_project_paths(tmp_path)
    (paths.tools / "whispercpp").mkdir(parents=True)
    (paths.models).mkdir(parents=True)
    binary = paths.tools / "whispercpp" / "whisper-cli.exe"
    model = paths.models / "ggml-large-v3-turbo.bin"
    binary.write_text("", encoding="utf-8")
    if vulkan:
        (paths.tools / "whispercpp" / "ggml-vulkan.dll").write_text("", encoding="utf-8")
    model.write_text("", encoding="utf-8")
    return paths


def test_whispercpp_provider_missing_pack_has_actionable_error(tmp_path):
    paths = get_project_paths(tmp_path)
    provider = WhisperCppProvider(paths)

    with pytest.raises(RuntimeError, match="Install-Live-Vulkan.cmd"):
        provider.transcribe(tmp_path / "x.wav", TranscriptionOptions())


def test_whispercpp_provider_cpu_backend_adds_no_gpu_flag(tmp_path, monkeypatch):
    paths = _stage_whispercpp_pack(tmp_path)
    wav = tmp_path / "x.wav"
    wav.write_bytes(b"data")
    commands = []

    def fake_run(command, *, cwd=None):
        commands.append(command)
        Path(command[command.index("-of") + 1]).with_suffix(".txt").write_text("hello", encoding="utf-8")
        return ProcessResult(0, ("ok",))

    monkeypatch.setattr(wc, "run_capture", fake_run)

    result = WhisperCppProvider(paths, backend="cpu").transcribe(
        wav,
        TranscriptionOptions(
            context="Urban-planning presentation",
            keyterms=("ИАС УГРТ", "XML"),
        ),
    )

    assert result.segments[0].text == "hello"
    assert "-ng" in commands[0]
    prompt = commands[0][commands[0].index("--prompt") + 1]
    assert prompt == "Urban-planning presentation\nExact spellings: ИАС УГРТ, XML"


def test_whispercpp_provider_auto_retries_cpu_on_gpu_failure(tmp_path, monkeypatch):
    paths = _stage_whispercpp_pack(tmp_path)
    wav = tmp_path / "x.wav"
    wav.write_bytes(b"data")
    commands = []

    def fake_run(command, *, cwd=None):
        commands.append(command)
        if len(commands) == 1:
            return ProcessResult(1, ("ggml_cuda: device failed",))
        Path(command[command.index("-of") + 1]).with_suffix(".txt").write_text("fallback", encoding="utf-8")
        return ProcessResult(0, ("ok",))

    monkeypatch.setattr(wc, "run_capture", fake_run)
    monkeypatch.setattr(wc, "recommended_whispercpp_backend", lambda _paths: "cublas")

    result = WhisperCppProvider(paths, backend="auto").transcribe(wav, TranscriptionOptions())

    assert result.segments[0].text == "fallback"
    assert "-ng" not in commands[0]
    assert "-ng" in commands[1]


def test_whispercpp_provider_forced_cublas_does_not_retry_cpu(tmp_path, monkeypatch):
    paths = _stage_whispercpp_pack(tmp_path)
    wav = tmp_path / "x.wav"
    wav.write_bytes(b"data")
    commands = []

    def fake_run(command, *, cwd=None):
        commands.append(command)
        return ProcessResult(1, ("ggml_cuda: device failed",))

    monkeypatch.setattr(wc, "run_capture", fake_run)
    monkeypatch.setattr(wc, "whispercpp_cublas_backend_present", lambda _paths: True)

    with pytest.raises(RuntimeError, match="ggml_cuda"):
        WhisperCppProvider(paths, backend="cublas").transcribe(wav, TranscriptionOptions())

    assert len(commands) == 1
    assert "-ng" not in commands[0]


def test_verbose_segments_keep_timestamps_and_rejoin_split_words(tmp_path):
    provider = WhisperCppProvider(_stage_whispercpp_pack(tmp_path))
    result = provider._result_from_payload(
        {
            "segments": [
                {"text": " аналитичес", "start": 1.0, "end": 1.5},
                {"text": "кую платформу", "start": 1.5, "end": 2.5},
                {"text": " для города", "start": 2.5, "end": 3.5},
            ],
        },
        language="ru",
        model_name="ggml-large-v2.bin",
    )
    assert [(seg.start, seg.end, seg.text) for seg in result.segments] == [
        (1.0, 2.5, "аналитическую платформу"),
        (2.5, 3.5, "для города"),
    ]


def test_registry_file_vulkan_uses_file_vulkan_settings(tmp_path, monkeypatch):
    paths = get_project_paths(tmp_path)
    seen = {}

    class FakeWhisperCppProvider:
        def __init__(self, paths_arg, *, model=None, backend=None):
            seen["paths"] = paths_arg
            seen["model"] = model
            seen["backend"] = backend

    monkeypatch.setattr(wc, "WhisperCppProvider", FakeWhisperCppProvider)

    provider = registry.get_transcription_provider(
        paths,
        {
            "compute_mode": "vulkan",
            "vulkan": {"engine": "whispercpp", "model": "large-v2", "backend": "cpu"},
            "live": {"local": {"engine": "whispercpp", "model": "small", "backend": "vulkan"}},
        },
    )

    assert isinstance(provider, FakeWhisperCppProvider)
    assert seen == {"paths": paths, "model": "large-v2", "backend": "cpu"}


def test_registry_file_vulkan_routes_gigaam_local_model(tmp_path):
    paths = get_project_paths(tmp_path)

    provider = registry.get_transcription_provider(
        paths,
        {
            "ui_language": "ru",
            "compute_mode": "vulkan",
            "local": {"model": "large-v2"},
            "vulkan": {"engine": "gigaam", "model": "gigaam-v3-e2e-rnnt", "backend": "directml"},
        },
    )

    assert isinstance(provider, GigaAMTranscribeProvider)
    assert provider.model == "gigaam-v3-e2e-rnnt"
    assert provider.backend == "directml"


def test_registry_gigaam_ignores_legacy_whisper_model(tmp_path):
    paths = get_project_paths(tmp_path)

    provider = registry.get_transcription_provider(
        paths,
        {
            "ui_language": "ru",
            "compute_mode": "vulkan",
            "vulkan": {"engine": "gigaam", "model": "turbo", "backend": "auto"},
        },
    )

    assert isinstance(provider, GigaAMTranscribeProvider)
    assert provider.model == DEFAULT_GIGAAM_FILE_MODEL


def test_gigaam_live_warm_reuses_process_cache(tmp_path, monkeypatch):
    paths = get_project_paths(tmp_path)
    gp._MODEL_CACHE.clear()
    load_calls = []
    loaded = object()

    class FakeOnnxAsr:
        def load_model(self, model, **kwargs):
            load_calls.append((model, kwargs))
            return loaded

    monkeypatch.setattr(gp, "_load_onnx_asr", lambda: FakeOnnxAsr())
    monkeypatch.setattr(gp, "_provider_list", lambda _paths, _backend: ["CPUExecutionProvider"])

    first = GigaAMLiveTranscriber(paths, model="gigaam-v3-e2e-ctc", backend="cpu")
    second = GigaAMLiveTranscriber(paths, model="gigaam-v3-e2e-ctc", backend="cpu")

    first.warm()
    second.warm()

    assert load_calls == [
        ("gigaam-v3-e2e-ctc", {"providers": ["CPUExecutionProvider"]}),
    ]


def test_turbo_asked_on_the_cuda_pack_is_recognized_by_large_v2_and_said(tmp_path):
    paths = _stage_whispercpp_pack(tmp_path)
    (paths.tools / "whispercpp" / "audion-whispercpp-pack.txt").write_text("kind=cublas\n", encoding="utf-8")
    (paths.models / "ggml-large-v2.bin").write_text("", encoding="utf-8")

    provider = registry.get_transcription_provider(
        paths, {"compute_mode": "vulkan", "vulkan": {"engine": "whispercpp", "model": "turbo", "backend": "auto"}})

    assert isinstance(provider, WhisperCppProvider)
    assert provider.model == "large-v2" and provider._resolve()[1].name == "ggml-large-v2.bin"
    assert "not used on CUDA" in provider.notice
    # large-v2 asked is large-v2, nothing to say.
    assert WhisperCppProvider(paths, "large-v2").notice == ""


def test_turbo_stays_turbo_on_the_cpu_pack(tmp_path):
    paths = _stage_whispercpp_pack(tmp_path)
    (paths.tools / "whispercpp" / "audion-whispercpp-pack.txt").write_text("kind=cpu\n", encoding="utf-8")

    provider = WhisperCppProvider(paths, "turbo")
    assert provider.model == "turbo" and provider.notice == ""
    assert provider._resolve()[1].name == "ggml-large-v3-turbo.bin"
    assert WhisperCppProvider(paths).model == "turbo"


def test_the_cuda_pack_without_large_v2_points_at_its_installer(tmp_path):
    paths = _stage_whispercpp_pack(tmp_path)
    (paths.tools / "whispercpp" / "audion-whispercpp-pack.txt").write_text("kind=cublas\n", encoding="utf-8")

    with pytest.raises(RuntimeError, match="Install-WhisperCpp-Large-Models.cmd"):
        WhisperCppProvider(paths, "turbo").transcribe(tmp_path / "x.wav", TranscriptionOptions())
