"""The console command of Audion Voice Tools: voice_cli.cmd. The window of 3.0 runs files through it.

    voice_cli.cmd describe                         the program, what is ready, the settings of a run - one line of JSON
    voice_cli.cmd run FILE_OR_FOLDER... [--set KEY=VALUE ...] [--result FILE] [--cancel-file FILE]
    voice_cli.cmd version
    voice_cli.cmd components                       SETUP: the modules, their state and advice, the machine - one line of JSON
    voice_cli.cmd install MODULE [--result FILE] [--cancel-file FILE]   a module put in place by its installer
    voice_cli.cmd keys                             the keys of the services: there or not, the last four characters
    voice_cli.cmd key set|clear|check SERVICE      a key from the standard input, emptied, asked of its service
    voice_cli.cmd process | list-models | regen | rename   as before 3.0

`run` writes the sidecars next to each source, as `process` does; whoever reads its output through a pipe (the window)
also gets the lines `::voice::{json}` - started, every file with its state, the share done, finished. `--set` takes a
dotted key of the settings: `language=ru`, `exports.srt=false`, `diarization.enabled=true`. The run stops when the file
of `--cancel-file` appears: the file under way is not failed, its cached chunks resume it on the next run.

Exit codes: 0 done, 1 a file failed, 2 a wrong call or nothing to process, 130 stopped.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from pathlib import Path

# Allow `python system_core/cli.py` as well as `python -m system_core.cli`.
SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR.parent) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR.parent))

from system_core.core.paths import ensure_project_dirs, get_project_paths  # noqa: E402
from system_core.providers import model_catalog  # noqa: E402
from system_core.settings import load_settings  # noqa: E402
from system_core.writers.json_writer import read_json  # noqa: E402


def _log(message: str) -> None:
    print(message, flush=True)


CONTROL = "::voice::"
EXIT_OK, EXIT_FAILED, EXIT_USAGE, EXIT_STOPPED = 0, 1, 2, 130


def _control(event: dict) -> None:
    """A line for whoever reads the output through a pipe - the window: ::voice::{json}. A person at the console does
    not get it."""
    if not sys.stdout.isatty():
        print(CONTROL + json.dumps(event, ensure_ascii=False), flush=True)


def _value(raw: str):
    """A value of --set as the settings hold it: true/false, a number, or the text."""
    lowered = raw.strip().lower()
    if lowered in {"true", "yes", "on"}:
        return True
    if lowered in {"false", "no", "off"}:
        return False
    for kind in (int, float):
        try:
            return kind(raw)
        except ValueError:
            pass
    return raw


def _apply_sets(settings: dict, pairs: list[str]) -> dict:
    for pair in pairs or []:
        if "=" not in pair:
            raise SystemExit(f"--set wants KEY=VALUE, got {pair!r}")
        key, raw = pair.split("=", 1)
        node = settings
        parts = [part for part in key.strip().split(".") if part]
        if not parts:
            raise SystemExit(f"--set wants KEY=VALUE, got {pair!r}")
        for part in parts[:-1]:
            node = node.setdefault(part, {})
            if not isinstance(node, dict):
                raise SystemExit(f"--set {key}: {part} is not a group of settings")
        node[parts[-1]] = _value(raw)
    return settings


def _version(paths) -> str:
    try:
        return str(json.loads((paths.root / "config" / "version.json").read_text(encoding="utf-8")).get("version") or "")
    except (OSError, ValueError):
        return ""


def _write_result(path: str | None, data: dict) -> None:
    if not path:
        return
    target = Path(path).resolve()
    target.parent.mkdir(parents=True, exist_ok=True)
    draft = target.with_name(target.name + ".writing")
    draft.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(draft, target)


def cmd_version(args: argparse.Namespace) -> int:
    print(_version(get_project_paths()))
    return EXIT_OK


# What a run takes from the settings - the window shows and changes these; keys of services are never here.
RUN_SETTINGS = ("language", "compute_mode", "vulkan", "stt", "transcription", "diarization", "exports", "pipeline", "subtitles",
                "postprocess", "postprocessing")


def cmd_describe(args: argparse.Namespace) -> int:
    from system_core.core.capabilities import list_capabilities

    paths = get_project_paths()
    settings = load_settings(paths)
    capabilities = [{"key": cap.key, "state": cap.state, "module": cap.module_key,
                     "missing": [check.key for check in cap.checks if check.required and not check.ok]}
                    for cap in list_capabilities(paths)]
    print(json.dumps({
        "program": {"id": "audion_voice_tools", "name": "Audion Voice Tools", "version": _version(paths)},
        "commands": ["describe", "run", "version", "components", "install", "keys", "key", "process", "list-models", "regen", "rename"],
        "exports": ["json", "markdown", "srt", "vtt", "txt"],
        "capabilities": capabilities,
        "settings": {key: settings[key] for key in RUN_SETTINGS if key in settings},
    }, ensure_ascii=False, default=str))
    return EXIT_OK


def cmd_components(args: argparse.Namespace) -> int:
    """SETUP of the window: every module it lists with its state, the advice for this machine, the size of its download,
    and the machine itself - one line of JSON."""
    from system_core.core.local_hardware import detect_local_hardware
    from system_core.core.modules import window_advice, window_modules
    from system_core.core.releases import newest_releases, version_of

    paths = get_project_paths()
    newest = newest_releases(paths, refresh=bool(getattr(args, "refresh", False)))
    try:
        profile = detect_local_hardware(paths)
    except Exception:  # noqa: BLE001 - no profile is the safe advice of a machine without a video card
        profile = None
    components = []
    for module in window_modules():
        components.append({
            "id": module.key,
            "name": {"ru": module.title[0], "en": module.title[1]},
            "about": {"ru": module.about[0], "en": module.about[1]},
            "state": "installed" if module.is_installed(paths) else "missing",
            "advice": window_advice(module, paths, profile),
            "action": "restore" if module.install_key == "mod_restore" else "install",
            "download_mb": module.download_mb,
            "script": f"install\\{module.script}",
            **version_of(module.key, paths, newest),
        })
    print(json.dumps({
        "components": components,
        "checked": newest.get("checked", ""),
        "release_error": newest.get("error", "") if newest.get("failed") else "",
        "machine": {
            "gpus": list(getattr(profile, "gpu_names", ()) or ()),
            "nvidia": bool(getattr(profile, "has_nvidia", False)),
            # An NVIDIA card its parts are not offered to, and why (local_hardware.nvidia_fit); "" - none such.
            "nvidia_note": str(getattr(profile, "nvidia_note", "") or ""),
            "intel": bool(getattr(profile, "has_intel", False)),
            "amd": bool(getattr(profile, "has_amd", False)),
        },
    }, ensure_ascii=False))
    return EXIT_OK


_NUMBER = r"\d[\d\s  .,]*"
_UNIT = r"\s*[kKMGT]?i?B?"


def _bytes(text: str | None) -> float:
    """1,25 GB, 1 023,5 KB, 9.20MB, 380M - in bytes; 0 when it does not read. A comma alone is the decimal point of
    Russian; beside a point it divides thousands."""
    hit = re.match(rf"\s*({_NUMBER})\s*([kKMGT]?)", text or "")
    if not hit:
        return 0.0
    number = re.sub(r"[\s  ]", "", hit.group(1))
    if "," in number and "." in number:
        number = number.replace(",", "") if number.index(",") < number.index(".") else number.replace(".", "").replace(",", ".")
    else:
        number = number.replace(",", ".")
    try:
        value = float(number)
    except ValueError:
        return 0.0
    return value * 1024 ** ("KMGT".find(hit.group(2).upper()) + 1) if hit.group(2) else value


class InstallCourse:
    """The course of an installer read from its lines: its step ([audion-step] N/M), and the share of the download
    under way - the lines of the downloads of the program's own installers, the progress of pip (--progress-bar raw), the
    bars of tqdm (the downloads of Hugging Face). Told to the window as lines ::voice::{"progress"}, twice a second at
    most."""

    STEP = re.compile(r"^\[audion-step\]\s+(\d+)\s*/\s*(\d+)\s+(.*)$")
    OWN = re.compile(rf"^(?P<item>.+?):\s+(?P<done>{_NUMBER}{_UNIT})\s*/\s*(?P<total>{_NUMBER}{_UNIT}|unknown)\s*"
                     rf"\((?:{_NUMBER}%|\?)\)\s*@\s*(?P<speed>{_NUMBER}{_UNIT})/s")
    PIP = re.compile(r"^Progress\s+(\d+)\s+of\s+(\d+)\s*$")
    TQDM = re.compile(rf"(?P<pct>\d{{1,3}})%\|[^|]*\|\s*(?P<done>{_NUMBER}{_UNIT})\s*/\s*(?P<total>{_NUMBER}{_UNIT})\s*"
                      rf"\[[^<\]]*<(?P<eta>[\d:?]+)(?:,\s*(?P<speed>{_NUMBER}{_UNIT})/s)?")

    def __init__(self, module: str) -> None:
        self.module = module
        self.step = 0
        self.steps = 0
        self.label = ""
        self._last = 0.0
        self._said = ("", False)

    def read(self, text: str) -> bool:
        """True when the line is a progress of a download - told as a service line, not printed."""
        line = text.strip()
        if not line:
            return False
        hit = self.STEP.match(line)
        if hit:
            self.step, self.steps, self.label = int(hit.group(1)), int(hit.group(2)), hit.group(3).strip()
            self._tell(-1.0, force=True)
            return False
        hit = self.OWN.match(line)
        if hit:
            done, total = _bytes(hit.group("done")), _bytes(hit.group("total"))
            self._tell(done / total if total else -1.0, done, total, _bytes(hit.group("speed")), item=hit.group("item").strip())
            return True
        hit = self.PIP.match(line)
        if hit:
            done, total = float(hit.group(1)), float(hit.group(2))
            self._tell(done / total if total else -1.0, done, total)
            return True
        hit = self.TQDM.search(line)
        if hit:
            eta = None
            if "?" not in hit.group("eta"):
                parts = [int(part) for part in hit.group("eta").split(":")]
                eta = sum(part * 60 ** index for index, part in enumerate(reversed(parts)))
            item = line[:hit.start()].strip().rstrip(":").strip()
            self._tell(int(hit.group("pct")) / 100, _bytes(hit.group("done")), _bytes(hit.group("total")),
                       _bytes(hit.group("speed")), item=item, eta=eta)
            return True
        return False

    def _tell(self, share: float, done: float = 0, total: float = 0, speed: float = 0, *, item: str = "",
              eta: float | None = None, force: bool = False) -> None:
        now = time.monotonic()
        # The first share of a file is told at once, the rest twice a second at most; the end of a file always.
        said = (item, share >= 0)
        if not force and said == self._said and now - self._last < 0.5 and share < 0.999:
            return
        self._last, self._said = now, said
        if eta is None and speed > 0 and total > done:
            eta = (total - done) / speed
        _control({"progress": {
            "module": self.module, "step": self.step, "steps": self.steps, "label": self.label, "item": item,
            "share": round(min(1.0, share), 4) if share >= 0 else -1, "bytes_done": int(done), "bytes_total": int(total),
            "speed": int(speed), "eta": int(round(eta)) if eta is not None else -1,
        }})


def cmd_install(args: argparse.Namespace) -> int:
    """A module put in place by its own installer, install\\*.cmd: its output as it comes, the lines ::voice:: around
    it. The file of --cancel-file stops it: the installer and everything it started are ended."""
    import subprocess
    import threading

    from system_core.core.modules import list_modules

    paths = get_project_paths()
    module = next((one for one in list_modules() if one.key == args.module), None)
    if module is None:
        _log(f"[ERROR] No such module: {args.module}. Modules: {', '.join(one.key for one in list_modules())}")
        return EXIT_USAGE
    script = module.script_path(paths)
    if not script.exists():
        _log(f"[ERROR] The installer is missing: {script}")
        return EXIT_USAGE
    cancel_file = Path(args.cancel_file).resolve() if args.cancel_file else None
    began = time.monotonic()
    _control({"started": {"module": module.key, "script": str(script)}})
    environment = dict(os.environ, AUDION_NO_PAUSE="1")
    extra = list(module.args) + (list(module.update_args) if getattr(args, "update", False) else [])
    child = subprocess.Popen(["cmd.exe", "/d", "/c", str(script), *extra], cwd=str(paths.root), env=environment,
                             stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    course = InstallCourse(module.key)

    def pump() -> None:
        # A person at the console gets the bytes of the installer as they are, its own progress bars among them. Whoever
        # reads through a pipe (the window) gets every line but those of the progress of a download, which come as lines
        # ::voice::{"progress"} instead - a share, the bytes, the speed, the time left - not seventy lines of a model.
        if sys.stdout.isatty():
            for chunk in iter(lambda: child.stdout.read1(65536), b""):
                sys.stdout.buffer.write(chunk)
                sys.stdout.buffer.flush()
            return
        pending = b""
        for chunk in iter(lambda: child.stdout.read1(65536), b""):
            pending += chunk
            # A bar of tqdm moves by \r, a line ends by \n: both end a piece of the output.
            *pieces, pending = re.split(rb"\r\n|\r|\n", pending)
            for piece in pieces:
                if not course.read(piece.decode("utf-8", errors="replace")):
                    sys.stdout.buffer.write(piece + b"\n")
            sys.stdout.buffer.flush()
        if pending and not course.read(pending.decode("utf-8", errors="replace")):
            sys.stdout.buffer.write(pending + b"\n")
            sys.stdout.buffer.flush()

    reader = threading.Thread(target=pump, daemon=True)
    reader.start()
    stopped = False
    while child.poll() is None:
        if cancel_file is not None and cancel_file.exists():
            stopped = True
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(child.pid)], stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            break
        time.sleep(0.25)
    child.wait()
    reader.join(timeout=5)
    code = child.returncode
    outcome = "stopped" if stopped else "done" if code == 0 else "failed"
    seconds = round(time.monotonic() - began, 1)
    installed = module.is_installed(paths)
    _write_result(args.result, {"outcome": outcome, "module": module.key, "code": code, "installed": installed,
                                "seconds": seconds})
    _control({"finished": {"outcome": outcome, "seconds": seconds, "module": module.key, "code": code,
                           "installed": installed}})
    return {"done": EXIT_OK, "failed": EXIT_FAILED, "stopped": EXIT_STOPPED}[outcome]


def cmd_keys(args: argparse.Namespace) -> int:
    """The keys of the services: there or not, the last four characters - one line of JSON, never a key."""
    from system_core.core.keys import describe

    print(json.dumps({"keys": describe(get_project_paths())}, ensure_ascii=False))
    return EXIT_OK


def cmd_key(args: argparse.Namespace) -> int:
    """set - the key from the standard input into its file (never from the line of the command); clear - the file
    emptied; check - the key asked of its service. One line of JSON."""
    from system_core.core import keys
    from system_core.core.credentials import mask_api_key

    if keys.info(args.provider) is None:
        _log(f"[ERROR] No such service: {args.provider}. Services: {', '.join(one.provider for one in keys.KEYS)}")
        return EXIT_USAGE
    paths = get_project_paths()
    if args.action == "set":
        key = sys.stdin.readline().strip()
        if not key:
            _log("[ERROR] No key on the standard input.")
            return EXIT_USAGE
        keys.write(paths, args.provider, key)
        print(json.dumps({"saved": args.provider, "fingerprint": mask_api_key(key)}, ensure_ascii=False))
    elif args.action == "clear":
        keys.write(paths, args.provider, "")
        print(json.dumps({"cleared": args.provider}, ensure_ascii=False))
    else:
        print(json.dumps({"checked": args.provider, **keys.check(paths, args.provider)}, ensure_ascii=False))
    return EXIT_OK


def cmd_run(args: argparse.Namespace) -> int:
    from system_core.pipeline.queue import run_queue

    paths = get_project_paths()
    ensure_project_dirs(paths)
    settings = _apply_sets(_apply_overrides(load_settings(paths), args), args.set)
    cancel_file = Path(args.cancel_file).resolve() if args.cancel_file else None
    began = time.monotonic()
    inputs = [Path(item) for item in args.inputs]
    _control({"started": {"inputs": [str(item) for item in inputs]}})

    def on_status(source: Path, status: str) -> None:
        _control({"file": {"path": str(source), "state": status}})

    def on_progress(source: Path, index: int, count: int, share: float) -> None:
        share = max(0.0, min(1.0, share))
        _control({"progress": {"path": str(source), "index": index, "count": count, "share": round(share, 4),
                               "total": round((index + share) / max(1, count), 4)}})

    summary = run_queue(paths, inputs, settings, log=_log,
                        cancel=(lambda: cancel_file.exists()) if cancel_file else (lambda: False),
                        on_status=on_status, on_progress=on_progress)
    outcome = ("stopped" if summary.stopped else "refused" if not summary.found
               else "failed" if summary.failed else "done")
    seconds = round(time.monotonic() - began, 1)
    _write_result(args.result, {
        "outcome": outcome, "found": summary.found, "completed": summary.completed, "skipped": summary.skipped,
        "failed": [{"path": path, "error": error} for path, error in summary.failed], "seconds": seconds,
    })
    _control({"finished": {"outcome": outcome, "seconds": seconds, "completed": len(summary.completed),
                           "skipped": len(summary.skipped), "failed": len(summary.failed)}})
    return {"done": EXIT_OK, "failed": EXIT_FAILED, "refused": EXIT_USAGE, "stopped": EXIT_STOPPED}[outcome]


def _apply_overrides(settings: dict, args: argparse.Namespace) -> dict:
    if args.lang:
        settings["language"] = args.lang
    if args.compute_mode:
        settings["compute_mode"] = args.compute_mode
    if args.stt_model:
        settings.setdefault("stt", {})["model"] = args.stt_model
    if args.postprocess_model:
        settings.setdefault("postprocess", {})["model"] = args.postprocess_model
    if args.diarize:
        settings.setdefault("transcription", {})["diarize"] = True
    if args.assemblyai:
        settings.setdefault("assemblyai", {})["enabled"] = True
    pipeline = settings.setdefault("pipeline", {})
    if args.recursive:
        pipeline["recursive"] = True
    if args.force:
        pipeline["force"] = True
    if args.no_skip:
        pipeline["skip_existing"] = False
    if args.exports:
        wanted = {fmt.strip().lower() for fmt in args.exports.split(",") if fmt.strip()}
        settings["exports"] = {
            "json": "json" in wanted,
            "markdown": "markdown" in wanted or "md" in wanted,
            "srt": "srt" in wanted,
            "vtt": "vtt" in wanted,
            "txt": "txt" in wanted,
        }
    return settings


def cmd_list_models(args: argparse.Namespace) -> int:
    paths = get_project_paths()
    role = model_catalog.ROLE_STT if args.role == "stt" else model_catalog.ROLE_CHAT
    result = model_catalog.list_models(paths, role, refresh=args.refresh)
    print(f"# {args.role} models (source: {result.source})")
    for model in result.models:
        print(model)
    return 0


def cmd_regen(args: argparse.Namespace) -> int:
    from system_core.writers.markdown_writer import write_markdown
    from system_core.writers.srt_writer import write_srt
    from system_core.writers.vtt_writer import write_vtt
    from system_core.pipeline.orchestrator import _subtitle_settings

    paths = get_project_paths()
    settings = load_settings(paths)
    sub = _subtitle_settings(settings)
    for json_arg in args.inputs:
        json_path = Path(json_arg).resolve()
        doc = read_json(json_path)
        stem = json_path.name.replace(".transcript.json", "")
        base = json_path.parent / stem
        write_markdown(base.with_suffix(".md"), doc)
        write_srt(base.with_suffix(".srt"), doc.transcription.segments, sub)
        write_vtt(base.with_suffix(".vtt"), doc.transcription.segments, sub)
        _log(f"Regenerated MD/SRT/VTT from {json_path.name}")
    return 0


def cmd_rename_speakers(args: argparse.Namespace) -> int:
    from system_core.pipeline.speaker_rename import rename_speakers_in_file

    paths = get_project_paths()
    settings = load_settings(paths)

    mapping: dict[str, str] = {}
    for pair in args.mapping:
        if "=" not in pair:
            _log(f"Ignoring '{pair}' (expected OLD=NEW)")
            continue
        old, new = pair.split("=", 1)
        mapping[old.strip()] = new.strip()
    if not mapping:
        _log("No OLD=NEW pairs given.")
        return 2

    json_path = Path(args.input).resolve()
    written = rename_speakers_in_file(json_path, mapping, settings)
    for path in written:
        _log(f"Updated {path.name}")
    return 0


def cmd_process(args: argparse.Namespace) -> int:
    from system_core.pipeline.queue import run_queue

    paths = get_project_paths()
    ensure_project_dirs(paths)
    settings = _apply_overrides(load_settings(paths), args)

    inputs = [Path(item) for item in args.inputs]
    summary = run_queue(paths, inputs, settings, log=_log)

    if summary.failed:
        _log("\nFailed files:")
        for path, error in summary.failed:
            _log(f"  - {path}: {error}")
        return 1
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="audion-voice", description="Audion Voice Tools pipeline")
    sub = parser.add_subparsers(dest="command")

    proc = sub.add_parser("process", help="Process files/folders (default)")
    _add_process_args(proc)

    run = sub.add_parser("run", help="Process files/folders: lines ::voice:: for the window, --result, --cancel-file")
    _add_process_args(run)
    run.add_argument("--set", action="append", default=[], metavar="KEY=VALUE", help="A dotted key of the settings")
    run.add_argument("--result", help="Write the outcome here as JSON")
    run.add_argument("--cancel-file", dest="cancel_file", help="Stop when this file appears")
    run.set_defaults(func=cmd_run)

    sub.add_parser("describe", help="The program, what is ready, the settings of a run: one line of JSON").set_defaults(func=cmd_describe)
    sub.add_parser("version", help="The version number").set_defaults(func=cmd_version)
    components = sub.add_parser("components", help="SETUP: the modules, their state, version and advice, the machine - one line of JSON")
    components.add_argument("--refresh", action="store_true", help="Ask GitHub for the newest releases now, not once a day")
    components.set_defaults(func=cmd_components)
    sub.add_parser("keys", help="The keys of the services: there or not, the last four characters - never a key").set_defaults(func=cmd_keys)
    key = sub.add_parser("key", help="A key of a service: set (from the standard input), clear, check")
    key.add_argument("action", choices=["set", "clear", "check"])
    key.add_argument("provider", help="The service, as keys names it")
    key.set_defaults(func=cmd_key)
    install = sub.add_parser("install", help="Put a module in place by its installer: lines ::voice::, --result, --cancel-file")
    install.add_argument("module", help="The id of the module, as components names it")
    install.add_argument("--update", action="store_true", help="The newest release, nothing reused (the modules that can)")
    install.add_argument("--result", help="Write the outcome here as JSON")
    install.add_argument("--cancel-file", dest="cancel_file", help="Stop when this file appears")
    install.set_defaults(func=cmd_install)

    models = sub.add_parser("list-models", help="List available models")
    models.add_argument("role", choices=["stt", "chat"], nargs="?", default="stt")
    models.add_argument("--refresh", action="store_true", help="Force refresh from API")
    models.set_defaults(func=cmd_list_models)

    regen = sub.add_parser("regen", help="Regenerate MD/SRT/VTT from existing .transcript.json")
    regen.add_argument("inputs", nargs="+")
    regen.set_defaults(func=cmd_regen)

    rename = sub.add_parser("rename", help="Rename speakers in a .transcript.json + regen sidecars")
    rename.add_argument("input", help="Path to a .transcript.json")
    rename.add_argument("mapping", nargs="+", help='OLD=NEW pairs, e.g. "Speaker 0=Иван"')
    rename.set_defaults(func=cmd_rename_speakers)

    return parser


def _add_process_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("inputs", nargs="+", help="Files or folders to process")
    parser.add_argument("--lang", help="Language code or 'auto'")
    parser.add_argument(
        "--compute-mode",
        choices=["api", "vulkan", "cuda", "cpu", "gpu"],
        dest="compute_mode",
    )
    parser.add_argument("--stt-model", dest="stt_model")
    parser.add_argument("--postprocess-model", dest="postprocess_model")
    parser.add_argument("--diarize", action="store_true", help="Request speaker labels")
    parser.add_argument("--assemblyai", action="store_true", help="Use AssemblyAI provider")
    parser.add_argument("--recursive", action="store_true")
    parser.add_argument("--force", action="store_true", help="Reprocess even if outputs exist")
    parser.add_argument("--no-skip", action="store_true", help="Do not skip existing outputs")
    parser.add_argument("--exports", help="Comma list: json,markdown,srt,vtt,txt")
    parser.set_defaults(func=cmd_process)


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    # Default to the `process` subcommand when the first arg isn't a known command.
    known = {"process", "run", "describe", "version", "components", "install", "keys", "key", "list-models", "regen", "rename",
             "-h", "--help"}
    if argv and argv[0] not in known:
        argv = ["process", *argv]

    parser = build_parser()
    args = parser.parse_args(argv)
    if not getattr(args, "func", None):
        parser.print_help()
        return 2
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
