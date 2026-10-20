"""Speaker separation without a key on Sortformer (transcribe.cpp).

transcribe_cpp itself is faked: a module that answers like the binding. The tests pin the provider contract: the
pipeline's own 16 kHz WAV is given whole, the turns label the segments by overlap, the limit of four is said and not
hidden, Vulkan first and the CPU after, and the registry prefers Community-1 when it is installed."""

from __future__ import annotations

import sys
import types
import wave
from types import SimpleNamespace

import pytest

from system_core.core.models import Segment
from system_core.core.paths import get_project_paths
from system_core.providers import diarization_sortformer as ds
from system_core.providers import registry
from system_core.providers.base import DiarizationUnavailable


def _wav(path, seconds: float = 2.0, rate: int = 16000) -> None:
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(rate)
        handle.writeframes(b"\x00\x00" * int(seconds * rate))


def _segments():
    return [Segment(index=0, start=0.0, end=4.0, text="first"), Segment(index=1, start=4.5, end=9.0, text="second"),
            Segment(index=2, start=9.5, end=12.0, text="third")]


@pytest.fixture()
def fake_binding(tmp_path, monkeypatch):
    """A binding whose Vulkan may fail; the turns it gives back; what it was given."""
    # The audio goes to the binding as numpy arrays; the build brings no numpy - the speakers or GigaAM for files do.
    pytest.importorskip("numpy", reason="numpy comes with the speakers or GigaAM for files, not with the build")
    seen = {"backends": [], "samples": 0}
    turns = [(0, 4200, 0), (4300, 9100, 1), (9200, 12000, 0)]
    vulkan_fails = {"on": False}

    class Session:
        def run(self, pcm):
            seen["samples"] = len(pcm)
            seen["dtype"] = str(pcm.dtype)
            return [SimpleNamespace(t0_ms=a, t1_ms=b, speaker_id=s) for a, b, s in turns]

    class Model:
        def __init__(self, path, backend):
            seen["backends"].append(backend)
            if backend == "vulkan" and vulkan_fails["on"]:
                raise RuntimeError("no Vulkan device")

        def diarize_session(self):
            return Session()

    monkeypatch.setitem(sys.modules, "transcribe_cpp", types.SimpleNamespace(Model=Model))
    monkeypatch.setattr(ds, "sortformer_ready", lambda _paths: True)
    return seen, turns, vulkan_fails


def test_the_turns_label_the_segments_by_overlap(tmp_path, fake_binding):
    seen, _, _ = fake_binding
    _wav(tmp_path / "audio.wav")
    provider = ds.SortformerDiarizationProvider(get_project_paths(tmp_path))
    result = provider.diarize(tmp_path / "audio.wav", _segments())
    assert [seg.speaker for seg in result] == ["Speaker 00", "Speaker 01", "Speaker 00"]
    # The whole WAV, as float32, in one run: Sortformer finds the speakers over all of it.
    assert seen["samples"] == 32000 and seen["dtype"] == "float32"
    assert provider.backend == "vulkan" and provider.notice == ""


def test_the_cpu_when_vulkan_does_not_load(tmp_path, fake_binding):
    seen, _, vulkan_fails = fake_binding
    vulkan_fails["on"] = True
    _wav(tmp_path / "audio.wav")
    provider = ds.SortformerDiarizationProvider(get_project_paths(tmp_path))
    provider.diarize(tmp_path / "audio.wav", _segments())
    assert seen["backends"] == ["vulkan", "cpu"] and provider.backend == "cpu"


def test_four_places_taken_are_said_not_hidden(tmp_path, fake_binding):
    _, turns, _ = fake_binding
    turns[:] = [(0, 3000, 0), (3000, 6000, 1), (6000, 9000, 2), (9000, 12000, 3)]
    _wav(tmp_path / "audio.wav")
    provider = ds.SortformerDiarizationProvider(get_project_paths(tmp_path))
    provider.diarize(tmp_path / "audio.wav", _segments())
    assert "four" in provider.notice and "Community-1" in provider.notice
    # A fourth voice of a breath is no full place.
    turns[:] = [(0, 6000, 0), (6000, 11000, 1), (11000, 11900, 2), (11900, 12000, 3)]
    provider.diarize(tmp_path / "audio.wav", _segments())
    assert provider.notice == ""


def test_more_than_four_asked_is_said(tmp_path, fake_binding):
    _wav(tmp_path / "audio.wav")
    provider = ds.SortformerDiarizationProvider(get_project_paths(tmp_path), speakers=6)
    provider.diarize(tmp_path / "audio.wav", _segments())
    assert provider.notice.startswith("6 speakers were asked for")


def test_a_wav_of_another_rate_is_refused_in_words(tmp_path, fake_binding):
    _wav(tmp_path / "audio.wav", rate=48000)
    with pytest.raises(DiarizationUnavailable, match="16000 Hz"):
        ds.SortformerDiarizationProvider(get_project_paths(tmp_path)).diarize(tmp_path / "audio.wav", _segments())


def test_missing_install_is_explained(tmp_path):
    with pytest.raises(DiarizationUnavailable, match="SETUP of the window"):
        ds.SortformerDiarizationProvider(get_project_paths(tmp_path)).diarize(tmp_path / "audio.wav", _segments())


def test_ready_needs_the_binding_the_engine_and_the_model(tmp_path, monkeypatch):
    paths = get_project_paths(tmp_path)
    monkeypatch.setattr(ds.importlib.util, "find_spec", lambda name: object() if name == "transcribe_cpp" else None)
    assert not ds.sortformer_ready(paths)
    ds.sortformer_library(paths).parent.mkdir(parents=True)
    ds.sortformer_library(paths).write_bytes(b"dll")
    assert not ds.sortformer_ready(paths)
    ds.sortformer_model(paths).parent.mkdir(parents=True)
    ds.sortformer_model(paths).write_bytes(b"gguf")
    assert ds.sortformer_ready(paths)


def _local(engine: str = "auto", speakers: int = 0) -> dict:
    return {"compute_mode": "vulkan", "vulkan": {"engine": "gigaam"},
            "diarization": {"enabled": True, "engine": engine, "speakers": speakers}}


def test_the_registry_takes_sortformer_without_community_1(tmp_path, monkeypatch):
    from system_core.providers import diarization_pyannote

    monkeypatch.setattr(diarization_pyannote, "pyannote_ready", lambda _paths: False)
    provider = registry.get_diarization_provider(get_project_paths(tmp_path), _local())
    assert isinstance(provider, ds.SortformerDiarizationProvider)


def test_the_registry_prefers_community_1_when_installed(tmp_path, monkeypatch):
    from system_core.providers import diarization_pyannote

    monkeypatch.setattr(diarization_pyannote, "pyannote_ready", lambda _paths: True)
    paths = get_project_paths(tmp_path)
    assert isinstance(registry.get_diarization_provider(paths, _local()), diarization_pyannote.PyannoteDiarizationProvider)
    # An explicit choice wins; "sherpa" of an older config is the engine without a key, now Sortformer.
    for engine in ("sortformer", "sherpa"):
        assert isinstance(registry.get_diarization_provider(paths, _local(engine)), ds.SortformerDiarizationProvider)


def test_the_registry_skips_api_mode_and_disabled_separation(tmp_path):
    paths = get_project_paths(tmp_path)
    assert registry.get_diarization_provider(paths, {"compute_mode": "api", "diarization": {"enabled": True}}) is None
    assert registry.get_diarization_provider(paths, {"compute_mode": "vulkan", "diarization": {"enabled": False}}) is None
