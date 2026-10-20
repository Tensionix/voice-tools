"""Studio speaker separation on pyannote Community-1.

pyannote.audio and torch are not imported: a fake ``pyannote.audio`` module and
a fake waveform loader stand in. The tests pin the provider contract: the
default pipeline, the key handling, the exclusive annotation, clear access
errors, and that a failed separation never loses the transcript."""

from __future__ import annotations

import sys
import types
from types import SimpleNamespace

import pytest

from system_core.core.credentials import write_api_key
from system_core.core.models import Segment
from system_core.core.paths import get_project_paths
from system_core.providers import diarization_pyannote as dp
from system_core.providers.diarization_pyannote import (
    DiarizationUnavailable,
    PyannoteDiarizationProvider,
)


class _Annotation:
    def __init__(self, turns):
        self._turns = turns

    def itertracks(self, yield_label=True):
        for start, end, label in self._turns:
            yield SimpleNamespace(start=start, end=end), None, label


def _install_fake_pyannote(monkeypatch, factory):
    module = types.ModuleType("pyannote.audio")
    module.Pipeline = SimpleNamespace(from_pretrained=factory)
    package = types.ModuleType("pyannote")
    package.audio = module
    monkeypatch.setitem(sys.modules, "pyannote", package)
    monkeypatch.setitem(sys.modules, "pyannote.audio", module)
    monkeypatch.setattr(dp, "_load_waveform", lambda _path: ("waveform", 16000))
    monkeypatch.delenv("HF_TOKEN", raising=False)


def _segments():
    return [
        Segment(index=0, start=0.0, end=10.0, text="first"),
        Segment(index=1, start=10.0, end=20.0, text="second"),
    ]


def test_default_pipeline_is_community_1():
    assert dp._DEFAULT_PIPELINE == "pyannote/speaker-diarization-community-1"


def test_exclusive_annotation_labels_segments_and_token_is_passed(tmp_path, monkeypatch):
    paths = get_project_paths(tmp_path)
    write_api_key(paths, "huggingface", "hf_test")
    calls = {}

    def factory(name, token=None):
        calls["name"], calls["token"] = name, token

        def pipeline(payload):
            calls["payload"] = payload
            return SimpleNamespace(
                speaker_diarization=_Annotation([(0.0, 20.0, "WRONG")]),
                exclusive_speaker_diarization=_Annotation(
                    [(0.0, 9.0, "SPEAKER_00"), (9.0, 20.0, "SPEAKER_01")]
                ),
            )

        pipeline.to = lambda device: None
        return pipeline

    _install_fake_pyannote(monkeypatch, factory)
    result = PyannoteDiarizationProvider(paths).diarize(tmp_path / "audio.wav", _segments())

    assert calls["name"] == "pyannote/speaker-diarization-community-1"
    assert calls["token"] == "hf_test"
    assert calls["payload"] == {"waveform": "waveform", "sample_rate": 16000}
    assert [s.speaker for s in result] == ["Speaker 00", "Speaker 01"]


def test_pyannote_3_api_still_works(tmp_path, monkeypatch):
    paths = get_project_paths(tmp_path)
    write_api_key(paths, "huggingface", "hf_test")

    def factory(name, use_auth_token=None):  # 3.x signature: no `token`
        assert use_auth_token == "hf_test"
        return lambda payload: _Annotation([(0.0, 20.0, "A")])

    _install_fake_pyannote(monkeypatch, factory)
    result = PyannoteDiarizationProvider(paths).diarize(tmp_path / "audio.wav", _segments())
    assert [s.speaker for s in result] == ["Speaker A", "Speaker A"]


def test_missing_key_explains_what_to_do(tmp_path, monkeypatch):
    paths = get_project_paths(tmp_path)

    def factory(name, token=None):
        raise OSError("401 gated repo")

    _install_fake_pyannote(monkeypatch, factory)
    with pytest.raises(DiarizationUnavailable) as err:
        PyannoteDiarizationProvider(paths).diarize(tmp_path / "audio.wav", _segments())
    assert "Maintenance" in str(err.value) and "community-1" in str(err.value)


def test_refused_key_points_at_model_terms(tmp_path, monkeypatch):
    paths = get_project_paths(tmp_path)
    write_api_key(paths, "huggingface", "hf_test")
    _install_fake_pyannote(monkeypatch, lambda name, token=None: None)  # 3.x style refusal
    with pytest.raises(DiarizationUnavailable) as err:
        PyannoteDiarizationProvider(paths).diarize(tmp_path / "audio.wav", _segments())
    assert "accept the" in str(err.value)


def test_failed_separation_keeps_the_transcript(tmp_path, monkeypatch):
    from system_core.pipeline import orchestrator

    source = orchestrator.__file__
    text = open(source, encoding="utf-8").read()
    # The diarization call is guarded and only warns; the transcript is written afterwards.
    guarded = text.index("segments = diar_provider.diarize(wav_path, segments)")
    assert "try:" in text[guarded - 40 : guarded]
    assert "WARN: speaker separation skipped" in text[guarded : guarded + 400]
