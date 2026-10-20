"""Shared term-dictionary helpers for Live, file STT and cleanup."""

from __future__ import annotations

import re
from collections.abc import Iterable, Mapping


def normalize_keyterms(
    value: object,
    *,
    limit: int = 100,
    max_length: int = 50,
) -> tuple[str, ...]:
    """Normalize exact spellings while preserving user order."""
    raw: Iterable[object]
    if isinstance(value, (list, tuple)):
        raw = value
    else:
        raw = re.split(r"[,;\n]+", str(value or ""))

    result: list[str] = []
    seen: set[str] = set()
    for item in raw:
        term = " ".join(str(item or "").split()).strip()
        folded = term.casefold()
        if not term or len(term) > max_length or folded in seen:
            continue
        seen.add(folded)
        result.append(term)
        if len(result) >= limit:
            break
    return tuple(result)


def read_dictionary_file(path: object) -> tuple[str, ...] | None:
    """The dictionary of SETTINGS of the window of 3.0 (config/dictionary.txt, Source/Core/Vocabulary.cs) as terms.

    A line is a term; "said -> written" is a replacement of the window, and what it writes is a term when it has a
    letter in it; "#" is a note. None - no file: the settings keep their own term_dictionary.
    """
    from pathlib import Path

    file = Path(str(path))
    if not file.is_file():
        return None
    try:
        text = file.read_text(encoding="utf-8-sig")
    except OSError:
        return None
    terms: list[str] = []
    for raw in text.splitlines():
        line = " ".join(raw.split())
        if not line or line.startswith("#"):
            continue
        cuts = [(line.find(arrow), arrow) for arrow in ("->", "\u2192", "=>") if line.find(arrow) >= 0]
        if cuts:
            at, arrow = min(cuts)
            said, written = line[:at].strip(), line[at + len(arrow):].strip()
            if said and written and any(char.isalpha() for char in written):
                terms.append(written)
        else:
            terms.append(line)
    return normalize_keyterms(terms)


def build_stt_prompt(
    description: str,
    keyterms: object,
    *,
    max_chars: int = 1000,
) -> str:
    """Combine narrative recording context with an exact-spelling vocabulary."""
    parts: list[str] = []
    narrative = " ".join(str(description or "").split()).strip()
    if narrative:
        parts.append(narrative)
    terms = normalize_keyterms(keyterms)
    if terms:
        parts.append("Exact spellings: " + ", ".join(terms))
    return "\n".join(parts)[: max(0, int(max_chars))].rstrip()


def shared_keyterms(settings: object) -> tuple[str, ...]:
    """Read the canonical dictionary with fallback for pre-migration configs."""
    if not isinstance(settings, Mapping):
        return ()
    value = settings.get("term_dictionary")
    if value:
        return normalize_keyterms(value)
    postprocessing = settings.get("postprocessing")
    if not isinstance(postprocessing, Mapping):
        return ()
    cleanup = postprocessing.get("cleanup")
    if not isinstance(cleanup, Mapping):
        return ()
    return normalize_keyterms(cleanup.get("hotwords", ()))
