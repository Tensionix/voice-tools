"""Speaker separation without a key: Sortformer on the engine of Handy.

`diar_streaming_sortformer_4spk-v2.1` (NVIDIA Open Model License, the GGUF of handy-computer) in transcribe.cpp 0.4.0 -
the same `Tools\\transcribe\\transcribe.dll` dictation loads, through its Python binding (`transcribe_cpp`), on Vulkan
or on the CPU. It finds the number of speakers itself, four at most, and is used exactly as its authors ship it: no
threshold is tuned here, so it behaves the same on any recording. Installed by install\\Install-Diarization-Local.cmd.

On a ten-minute talk of two it agrees with Community-1 on 99 % of the time and takes 20 s where Community-1 takes
11 minutes on a laptop without NVIDIA (E:\\TOOLS\\VOICE_AI_AVALONIA\\PROBE_DIARIZATION_RU.md). Its limit of four is said,
not hidden: when all four places are taken by speech that matters, `notice` tells that there may be more people and
where Community-1 is.
"""

from __future__ import annotations

import importlib.util
import os
import wave
from pathlib import Path
from typing import Optional

from ..core.models import Segment
from ..core.paths import ProjectPaths
from .base import DiarizationProvider, DiarizationUnavailable

MODEL_FILE = "diar_streaming_sortformer_4spk-v2.1-Q8_0.gguf"
MAX_SPEAKERS = 4
RATE = 16000
# A speaker holding less than this share of the speech is no full place of the four.
FULL_PLACE = 0.05


def sortformer_library(paths: ProjectPaths) -> Path:
    return paths.tools / "transcribe" / "transcribe.dll"


def sortformer_model(paths: ProjectPaths) -> Path:
    return paths.models / "transcribe" / MODEL_FILE


def sortformer_ready(paths: ProjectPaths) -> bool:
    """The binding importable, the engine of dictation and the model of Sortformer in their places."""
    try:
        if importlib.util.find_spec("transcribe_cpp") is None:
            return False
    except (ImportError, ValueError):
        return False
    return sortformer_library(paths).is_file() and sortformer_model(paths).is_file()


def _load_pcm(audio_path: Path):
    """The pipeline's own 16 kHz PCM16 WAV as one float32 array, the channels mixed down."""
    import numpy as np

    with wave.open(str(audio_path), "rb") as handle:
        if handle.getsampwidth() != 2:
            raise DiarizationUnavailable(f"unsupported WAV sample width: {handle.getsampwidth()}")
        if handle.getframerate() != RATE:
            raise DiarizationUnavailable(f"Sortformer takes {RATE} Hz, the audio is {handle.getframerate()} Hz")
        channels = handle.getnchannels()
        data = np.frombuffer(handle.readframes(handle.getnframes()), dtype=np.int16)
    if channels > 1:
        data = data.reshape(-1, channels).mean(axis=1)
    return np.ascontiguousarray(data.astype(np.float32) / 32768.0)


class SortformerDiarizationProvider(DiarizationProvider):
    name = "sortformer"
    title = "Sortformer"

    def __init__(self, paths: ProjectPaths, *, speakers: int = 0) -> None:
        self.paths = paths
        # A count given by the person is not passed on: Sortformer finds it itself. More than four is said.
        self.speakers = int(speakers or 0)
        self.notice = ""
        self._session = None
        self.backend = ""

    def _get_session(self):
        if self._session is not None:
            return self._session
        if not sortformer_ready(self.paths):
            raise DiarizationUnavailable(
                "Speaker separation is not installed. Install 'Speakers without a key' in SETUP of the window "
                "(or 'Speaker separation (no key)' on the Maintenance page of 2.3)."
            )
        os.environ.setdefault("TRANSCRIBE_LIBRARY", str(sortformer_library(self.paths)))
        import transcribe_cpp as tc  # type: ignore

        last: Optional[Exception] = None
        for backend in ("vulkan", "cpu"):
            try:
                model = tc.Model(str(sortformer_model(self.paths)), backend=backend)
                self._session = model.diarize_session()
                self._model = model  # the session lives while its model does
                self.backend = backend
                return self._session
            except Exception as exc:  # noqa: BLE001 - no Vulkan device: the CPU
                last = exc
        raise DiarizationUnavailable(f"Sortformer did not load: {last}")

    def diarize(self, audio_path: Path, segments: list[Segment]) -> list[Segment]:
        session = self._get_session()
        pcm = _load_pcm(Path(audio_path))
        turns = sorted(
            (s.t0_ms / 1000.0, s.t1_ms / 1000.0, f"Speaker {int(s.speaker_id):02d}") for s in session.run(pcm)
        )
        for seg in segments:
            seg.speaker = _best_speaker(seg, turns) or seg.speaker
        self.notice = _notice(turns, self.speakers)
        return segments


def _notice(turns: list[tuple[float, float, str]], asked: int) -> str:
    """What Sortformer cannot do, said in the journal: more people than its four places."""
    spoken: dict[str, float] = {}
    for start, end, label in turns:
        spoken[label] = spoken.get(label, 0.0) + max(0.0, end - start)
    total = sum(spoken.values())
    full = [label for label, seconds in spoken.items() if total > 0 and seconds / total >= FULL_PLACE]
    if len(full) >= MAX_SPEAKERS:
        return ("Sortformer separates four speakers at most, and all four places are taken: there may be more people. "
                "Community-1 has no such limit - SETUP of the window, on an NVIDIA card with a Hugging Face key.")
    if asked > MAX_SPEAKERS:
        return (f"{asked} speakers were asked for; Sortformer separates four at most. "
                "Community-1 has no such limit - SETUP of the window, on an NVIDIA card with a Hugging Face key.")
    return ""


def _best_speaker(seg: Segment, turns: list[tuple[float, float, str]]) -> Optional[str]:
    best_label: Optional[str] = None
    best_overlap = 0.0
    for start, end, label in turns:
        overlap = max(0.0, min(seg.end, end) - max(seg.start, start))
        if overlap > best_overlap:
            best_overlap = overlap
            best_label = label
    return best_label
