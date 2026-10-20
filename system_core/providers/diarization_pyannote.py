"""pyannote diarization — Studio file mode, opt-in (TZ decision 4).

Heavy (torch + pyannote.audio + gated HF weights). Installed via
install/Install-Diarization-GPU.cmd. Assigns each transcript segment the
speaker whose diarization turn overlaps it most.

The pipeline is used exactly as its authors ship it: no thresholds or
clustering parameters are tuned here, so behaviour stays the same on any
recording.
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import Optional

from ..core.credentials import read_api_key
from ..core.models import Segment
from ..core.paths import ProjectPaths
from .base import DiarizationProvider, DiarizationUnavailable

# Community-1 replaces 3.1: better on every published benchmark, no limit on
# the number of speakers, and an "exclusive" output made for transcripts.
_DEFAULT_PIPELINE = "pyannote/speaker-diarization-community-1"


def _configure_hf_cache(paths: ProjectPaths) -> None:
    """Keep the weights in the project's own Hugging Face cache (portable)."""
    cache_root = paths.models / "huggingface"
    os.environ.setdefault("HF_HOME", str(cache_root))
    os.environ.setdefault("HF_HUB_CACHE", str(cache_root / "hub"))
    cache_root.mkdir(parents=True, exist_ok=True)


def _load_waveform(audio_path: Path):
    """Read the pipeline's own 16-bit PCM WAV into a (channel, time) tensor.

    Passing audio from memory keeps pyannote.audio 4 away from torchcodec and
    its FFmpeg DLL lookup, which is fragile on Windows."""
    import wave

    import numpy as np
    import torch  # type: ignore

    with wave.open(str(audio_path), "rb") as handle:
        if handle.getsampwidth() != 2:
            raise ValueError(f"unsupported WAV sample width: {handle.getsampwidth()}")
        rate = handle.getframerate()
        channels = handle.getnchannels()
        data = np.frombuffer(handle.readframes(handle.getnframes()), dtype=np.int16)
    if channels > 1:
        data = data.reshape(-1, channels).mean(axis=1)
    waveform = torch.from_numpy(data.astype(np.float32) / 32768.0).unsqueeze(0)
    return waveform, rate


class PyannoteDiarizationProvider(DiarizationProvider):
    name = "pyannote"
    title = "Community-1"

    def __init__(
        self, paths: ProjectPaths, pipeline: str = _DEFAULT_PIPELINE, *, speakers: int = 0
    ) -> None:
        self.paths = paths
        self.pipeline_name = pipeline
        # 0 = let the pipeline decide (it is calibrated for that); a known
        # count from the user is simply passed through.
        self.speakers = int(speakers or 0)
        self._pipeline = None
        # The processor instead of the card is said, not hidden: the orchestrator logs a notice.
        self.notice = ""

    def _get_pipeline(self):
        if self._pipeline is not None:
            return self._pipeline
        try:
            import warnings

            with warnings.catch_warnings():
                # pyannote.audio 4 warns that torchcodec cannot find FFmpeg DLLs.
                # Audio is passed from memory, so torchcodec is never used.
                warnings.simplefilter("ignore")
                from pyannote.audio import Pipeline  # type: ignore
        except Exception as exc:  # pragma: no cover - optional dependency
            raise DiarizationUnavailable(
                "pyannote.audio is not installed. Install 'Speakers on the video card' in SETUP of the window "
                "(or 'GPU diarization' on the Maintenance page of 2.3)."
            ) from exc

        token = read_api_key(self.paths, "huggingface") or None
        _configure_hf_cache(self.paths)
        try:
            try:
                pipeline = Pipeline.from_pretrained(self.pipeline_name, token=token)
            except TypeError:  # pyannote.audio 3.x
                pipeline = Pipeline.from_pretrained(self.pipeline_name, use_auth_token=token)
        except Exception as exc:
            raise DiarizationUnavailable(_access_hint(self.pipeline_name, token, exc)) from exc
        if pipeline is None:  # 3.x returns None instead of raising on a gated repo
            raise DiarizationUnavailable(_access_hint(self.pipeline_name, token, None))

        on_card = False
        try:
            import torch  # type: ignore

            if torch.cuda.is_available():
                pipeline.to(torch.device("cuda"))
                on_card = True
        except Exception:
            pass  # CPU still works, only slower
        if not on_card:
            self.notice = "Community-1 ran on the processor: CUDA did not come up, it is several times slower"
        self._pipeline = pipeline
        return pipeline

    def diarize(self, audio_path: Path, segments: list[Segment]) -> list[Segment]:
        pipeline = self._get_pipeline()
        waveform, rate = _load_waveform(audio_path)
        extra = {"num_speakers": self.speakers} if self.speakers > 0 else {}
        output = pipeline({"waveform": waveform, "sample_rate": rate}, **extra)

        # 4.x: prefer the exclusive (one speaker at a time) annotation, which is
        # made for aligning with transcripts. 3.x returns the annotation itself.
        annotation = (
            getattr(output, "exclusive_speaker_diarization", None)
            or getattr(output, "speaker_diarization", None)
            or output
        )
        turns = [
            (turn.start, turn.end, _speaker_name(label))
            for turn, _, label in annotation.itertracks(yield_label=True)
        ]

        for seg in segments:
            seg.speaker = _best_speaker(seg, turns) or seg.speaker
        return segments


def _speaker_name(label: object) -> str:
    """pyannote labels are 'SPEAKER_00'; the app shows 'Speaker 00' for every engine."""
    text = str(label)
    if text.upper().startswith("SPEAKER_"):
        text = text[len("SPEAKER_"):]
    return f"Speaker {text}"


def pyannote_ready(paths: ProjectPaths) -> bool:
    """torch + pyannote.audio importable and the Community-1 weights in the project cache."""
    import importlib.util

    try:
        if importlib.util.find_spec("torch") is None or importlib.util.find_spec("pyannote") is None:
            return False
    except (ImportError, ValueError):
        return False
    repo = "models--" + _DEFAULT_PIPELINE.replace("/", "--")
    return (paths.models / "huggingface" / "hub" / repo / "snapshots").is_dir()


def _access_hint(pipeline_name: str, token: Optional[str], exc: Optional[BaseException]) -> str:
    detail = f" ({exc.__class__.__name__}: {exc})" if exc is not None else ""
    if not token:
        return (
            "Speaker separation needs a HuggingFace key: enter it in SETUP of the window, KEYS OF THE SERVICES, "
            "or on the Maintenance page of 2.3 "
            f"(the key row's tooltip explains how) and accept the terms of {pipeline_name}.{detail}"
        )
    return (
        f"HuggingFace refused {pipeline_name}. Open its page with the same account, accept the "
        f"model terms, and check that the key is a valid Read token.{detail}"
    )


def _best_speaker(seg: Segment, turns: list[tuple[float, float, str]]) -> Optional[str]:
    best_label: Optional[str] = None
    best_overlap = 0.0
    for start, end, label in turns:
        overlap = max(0.0, min(seg.end, end) - max(seg.start, start))
        if overlap > best_overlap:
            best_overlap = overlap
            best_label = label
    return best_label
