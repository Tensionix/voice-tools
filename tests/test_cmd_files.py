"""Every .cmd of the project keeps CRLF.

cmd.exe finds a label of a file with bare LF only now and then: cleanup_project.cmd with LF lost :RemoveDir and
:AssertInside on every run, so ._runtime and system_core\\7zip stayed and the cleanup ended with errors.
"""
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
# What the builder and the program bring in: not the project's own files.
NOT_OURS = {"runtime", "Tools", "models", "wheelhouse", "workspace", "library", "release", "input", "output", "logs",
            "report", "work", "App", ".git"}


def test_every_cmd_file_keeps_crlf():
    files = [path for path in ROOT.rglob("*.cmd") if path.relative_to(ROOT).parts[0] not in NOT_OURS
             and "download" not in path.relative_to(ROOT).parts]
    assert any(path.name == "cleanup_project.cmd" for path in files)
    bare_lf = [str(path.relative_to(ROOT)) for path in files
               if path.read_bytes().count(b"\n") != path.read_bytes().count(b"\r\n")]
    assert bare_lf == []
