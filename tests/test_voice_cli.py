"""voice_cli.cmd - the door of the window of 3.0 for files: describe, run with its lines ::voice::, --result,
--cancel-file and the exit codes 0 / 1 / 2 / 130.

The file is processed here by a stand-in for process_file: the contract of the door is tested, not the engines (those
have their own tests); the jobs of the run go into a store of the temporary folder, not the program's.
"""
from __future__ import annotations

import dataclasses
import json
import sys
from pathlib import Path

import pytest

from system_core import cli
from system_core.core.persistence import JobStore
from system_core.pipeline import queue
from system_core.pipeline.orchestrator import Cancelled


@pytest.fixture()
def paths(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    real = cli.get_project_paths()
    mine = dataclasses.replace(real, workspace=tmp_path / "workspace")
    mine.workspace.mkdir()
    monkeypatch.setattr(cli, "get_project_paths", lambda: mine)
    return mine


def clip(folder: Path, name: str = "talk.wav") -> Path:
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / name
    path.write_bytes(b"RIFF\0\0\0\0WAVE")
    return path


@pytest.fixture(autouse=True)
def no_github(monkeypatch: pytest.MonkeyPatch):
    """The newest releases as GitHub would answer: no network in a test, no cache written into the program's workspace."""
    from system_core.core import releases

    newest = {"checked": "2026-10-18T12:00:00+00:00", "transcribe": "0.4.0", "whispercpp:cpu": "b5454", "whispercpp:cublas": "b5454"}
    monkeypatch.setattr(releases, "newest_releases", lambda _paths, refresh=False: dict(newest))


def lines(out: str) -> list[dict]:
    return [json.loads(line[len(cli.CONTROL):]) for line in out.splitlines() if line.startswith(cli.CONTROL)]


def test_describe_names_the_program_and_holds_no_keys(capsys) -> None:
    assert cli.main(["describe"]) == 0
    told = json.loads(capsys.readouterr().out.strip().splitlines()[-1])
    assert told["program"]["name"] == "Audion Voice Tools" and told["program"]["version"]
    assert "run" in told["commands"] and {"json", "markdown", "srt", "vtt", "txt"} <= set(told["exports"])
    assert {cap["key"] for cap in told["capabilities"]} >= {"ffmpeg", "gigaam"}
    assert "api_key" not in json.dumps(told).lower()


def test_a_run_says_its_course_and_its_outcome(paths, tmp_path, capsys, monkeypatch) -> None:
    source = clip(tmp_path / "in")
    seen = {}

    def fake(_paths, file, settings, *, store, job_id, log, cancel, progress):
        seen["settings"] = settings
        progress(0.5)
        return {"json": str(file.with_suffix(".transcript.json"))}

    monkeypatch.setattr(queue, "process_file", fake)
    monkeypatch.setattr(queue, "outputs_exist", lambda *_args: False)
    result = tmp_path / "told.json"
    code = cli.main(["run", str(source), "--set", "language=ru", "--set", "exports.srt=false",
                     "--set", "pipeline.chunk_seconds=300", "--result", str(result)])
    assert code == 0
    events = lines(capsys.readouterr().out)
    assert list(events[0]) == ["started"] and events[-1]["finished"]["outcome"] == "done"
    shares = [event["progress"]["share"] for event in events if "progress" in event]
    assert shares == [0.0, 0.5]
    assert [event["file"]["state"] for event in events if "file" in event] == ["processing", "completed"]
    assert seen["settings"]["language"] == "ru" and seen["settings"]["exports"]["srt"] is False
    assert seen["settings"]["pipeline"]["chunk_seconds"] == 300
    told = json.loads(result.read_text(encoding="utf-8"))
    assert told["outcome"] == "done" and told["completed"] == [str(source.resolve())]


def test_a_failed_file_fails_the_run_and_the_rest_goes_on(paths, tmp_path, capsys, monkeypatch) -> None:
    first, second = clip(tmp_path / "in", "a.wav"), clip(tmp_path / "in", "b.wav")

    def fake(_paths, file, *_args, **_kwargs):
        if file.name == "a.wav":
            raise RuntimeError("the provider said no")
        return {}

    monkeypatch.setattr(queue, "process_file", fake)
    monkeypatch.setattr(queue, "outputs_exist", lambda *_args: False)
    result = tmp_path / "told.json"
    assert cli.main(["run", str(tmp_path / "in"), "--result", str(result)]) == 1
    told = json.loads(result.read_text(encoding="utf-8"))
    assert told["outcome"] == "failed" and told["completed"] == [str(second.resolve())]
    assert told["failed"][0]["path"] == str(first.resolve()) and "the provider said no" in told["failed"][0]["error"]


def test_nothing_to_process_is_a_wrong_call(paths, tmp_path, capsys) -> None:
    (tmp_path / "empty").mkdir()
    result = tmp_path / "told.json"
    assert cli.main(["run", str(tmp_path / "empty"), "--result", str(result)]) == 2
    assert json.loads(result.read_text(encoding="utf-8"))["outcome"] == "refused"


def test_a_stop_is_no_failure_and_the_next_run_takes_the_file_up(paths, tmp_path, capsys, monkeypatch) -> None:
    source = clip(tmp_path / "in")
    stop = tmp_path / "stop.flag"

    def stopped(_paths, _file, *_args, cancel, **_kwargs):
        stop.write_text("")
        assert cancel()
        raise Cancelled("Operation cancelled by user.")

    monkeypatch.setattr(queue, "process_file", stopped)
    monkeypatch.setattr(queue, "outputs_exist", lambda *_args: False)
    result = tmp_path / "told.json"
    assert cli.main(["run", str(source), "--cancel-file", str(stop), "--result", str(result)]) == 130
    assert json.loads(result.read_text(encoding="utf-8"))["outcome"] == "stopped"
    with JobStore(paths.db_file) as store:
        # Not failed: the next run turns it back into pending and goes on from its cached chunks.
        assert store.reset_stale_processing() == 1


def test_a_wrong_set_is_said(paths, tmp_path) -> None:
    source = clip(tmp_path / "in")
    with pytest.raises(SystemExit, match="KEY=VALUE"):
        cli.main(["run", str(source), "--set", "language"])


# ---------------------------------------------------------------- SETUP: components and install


def test_components_are_the_modules_of_the_window_with_advice_by_the_machine(capsys, monkeypatch) -> None:
    from system_core.core import local_hardware

    intel = local_hardware.LocalHardwareProfile(
        gpu_names=("Intel(R) Iris(R) Xe Graphics",), has_nvidia=False, has_intel=True, has_amd=False,
        has_cuda_runtime=False, has_onnxruntime=False, onnx_providers=(), has_gigaam_runtime=False,
        has_whispercpp=False, has_whispercpp_vulkan_backend=False)
    monkeypatch.setattr(local_hardware, "detect_local_hardware", lambda _paths: intel)
    assert cli.main(["components"]) == 0
    told = json.loads(capsys.readouterr().out.strip().splitlines()[-1])
    by_id = {one["id"]: one for one in told["components"]}
    assert list(by_id)[0] == "dictation"
    assert {"dictation", "ffmpeg", "gigaam", "vulkan", "gpu", "speakers"} <= set(by_id)
    # The builder's own steps are not components of the window.
    assert not {"wheel_cache", "live"} & set(by_id)
    assert all(by_id[key]["advice"] == "recommended" for key in ("dictation", "ffmpeg", "gigaam", "vulkan"))
    # Whisper for files without NVIDIA: the CPU pack with turbo, by its installer.
    assert by_id["vulkan"]["script"] == "install\\Install-Live-Vulkan.cmd"
    # The heavy stack of NVIDIA is not offered without an NVIDIA card.
    assert all(by_id[key]["advice"] == "not_needed" for key in ("gpu", "restore_rtx", "whispercpp_cuda", "whispercpp_models"))
    assert all(one["name"]["ru"] and one["name"]["en"] and one["about"]["ru"] and one["about"]["en"] for one in by_id.values())
    assert all(one["state"] in {"installed", "missing"} and one["script"].startswith("install\\") for one in by_id.values())
    assert told["machine"] == {"gpus": ["Intel(R) Iris(R) Xe Graphics"], "nvidia": False, "nvidia_note": "", "intel": True, "amd": False}


def test_an_nvidia_card_gets_whisper_large_v2_on_cuda_and_no_turbo(capsys, monkeypatch) -> None:
    from system_core.core import local_hardware

    rtx = local_hardware.LocalHardwareProfile(
        gpu_names=("NVIDIA GeForce RTX 5070",), has_nvidia=True, has_intel=False, has_amd=False,
        has_cuda_runtime=False, has_onnxruntime=False, onnx_providers=(), has_gigaam_runtime=False,
        has_whispercpp=False, has_whispercpp_vulkan_backend=False)
    monkeypatch.setattr(local_hardware, "detect_local_hardware", lambda _paths: rtx)
    assert cli.main(["components"]) == 0
    by_id = {one["id"]: one for one in json.loads(capsys.readouterr().out.strip().splitlines()[-1])["components"]}
    assert {"whispercpp_cuda", "whispercpp_models", "gpu"} <= set(by_id)
    # Whisper for files on the card is large-v2 on CUDA: the question of the start queues both, and the CPU pack with
    # turbo is not needed - turbo loops on CUDA.
    assert all(by_id[key]["advice"] == "recommended" for key in ("dictation", "ffmpeg", "gigaam", "whispercpp_cuda", "whispercpp_models"))
    assert by_id["vulkan"]["advice"] == "not_needed"
    # Community-1 is offered, never queued: it is gated behind a personal Hugging Face account.
    assert by_id["gpu"]["advice"] == "optional"
    assert by_id["restore_intel"]["advice"] == "not_needed"
    assert by_id["whispercpp_cuda"]["script"] == "install\\Install-WhisperCpp-CUDA.cmd"


@pytest.fixture()
def installer(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    """A program of the temporary folder with one module whose installer is the script the test writes."""
    from system_core.core import modules

    real = cli.get_project_paths()
    mine = dataclasses.replace(real, root=tmp_path)
    monkeypatch.setattr(cli, "get_project_paths", lambda: mine)
    (tmp_path / "install").mkdir()
    put = tmp_path / "put.flag"
    module = modules.ModuleInfo("fake", "Install-Fake.cmd", "", "", lambda _paths: put.exists())
    monkeypatch.setattr(modules, "list_modules", lambda *_args, **_kwargs: [module])

    def write(body: str) -> Path:
        script = tmp_path / "install" / "Install-Fake.cmd"
        script.write_bytes(("@echo off\r\n" + body.replace("\n", "\r\n") + "\r\n").encode("ascii"))
        return put

    return write


def test_an_install_passes_the_output_of_its_installer_and_says_its_outcome(installer, tmp_path, capsys) -> None:
    put = installer(f'echo [audion-step] 1/1 fake\necho done> "{tmp_path / "put.flag"}"\nexit /b 0')
    result = tmp_path / "told.json"
    assert cli.main(["install", "fake", "--result", str(result)]) == 0
    out = capsys.readouterr().out
    assert "[audion-step] 1/1 fake" in out and put.exists()
    events = lines(out)
    assert "started" in events[0] and events[-1]["finished"]["outcome"] == "done"
    assert events[-1]["finished"]["installed"] is True
    assert json.loads(result.read_text(encoding="utf-8"))["outcome"] == "done"


def test_the_progress_of_a_download_comes_as_service_lines_not_as_seventy_lines(installer, tmp_path, capsys) -> None:
    installer("echo [audion-step] 2/3 Fake model\n"
              "echo Fake model: 1,00 MB / 2,00 MB (50,0%%) @ 1,0 MB/s\n"
              "echo Fake model download complete\nexit /b 0")
    assert cli.main(["install", "fake"]) == 0
    out = capsys.readouterr().out
    # The step and the end stay in the journal; the line of the share does not.
    assert "[audion-step] 2/3 Fake model" in out and "Fake model download complete" in out
    assert "1,00 MB / 2,00 MB" not in out
    told = [event["progress"] for event in lines(out) if "progress" in event]
    assert told[0]["step"] == 2 and told[0]["steps"] == 3 and told[0]["share"] == -1
    share = next(one for one in told if one["share"] >= 0)
    assert share["share"] == 0.5 and share["bytes_total"] == 2 * 1024 * 1024 and share["eta"] == 1
    assert share["label"] == "Fake model" and share["item"] == "Fake model"


def test_a_failed_install_is_exit_code_1(installer, capsys) -> None:
    installer("echo [ERROR] no network\nexit /b 3")
    assert cli.main(["install", "fake"]) == 1
    finished = lines(capsys.readouterr().out)[-1]["finished"]
    assert finished["outcome"] == "failed" and finished["code"] == 3 and finished["installed"] is False


def test_a_stopped_install_ends_its_installer(installer, tmp_path, capsys) -> None:
    installer("ping -n 30 127.0.0.1 >nul\nexit /b 0")
    stop = tmp_path / "stop.flag"
    stop.write_text("")
    began = __import__("time").monotonic()
    assert cli.main(["install", "fake", "--cancel-file", str(stop)]) == 130
    assert __import__("time").monotonic() - began < 15
    assert lines(capsys.readouterr().out)[-1]["finished"]["outcome"] == "stopped"


def test_an_unknown_module_is_a_wrong_call(installer, capsys) -> None:
    assert cli.main(["install", "nothing"]) == 2
    assert "No such module" in capsys.readouterr().out


def test_components_say_the_version_and_whether_a_newer_release_is_out(capsys) -> None:
    assert cli.main(["components"]) == 0
    told = json.loads(capsys.readouterr().out.strip().splitlines()[-1])
    assert told["checked"] == "2026-10-18T12:00:00+00:00"
    for one in told["components"]:
        assert {"version", "latest", "fresh", "updatable"} <= set(one)
        assert one["fresh"] in {"fresh", "old", "own", ""}
    by_id = {one["id"]: one for one in told["components"]}
    # The engine of dictation is compared, never updated by a press: the window binds its C API.
    assert by_id["dictation"]["updatable"] is False and by_id["vulkan"]["updatable"] is True


def test_newer_compares_builds_by_number_and_versions_by_parts() -> None:
    from system_core.core.releases import _newer

    assert _newer("b4938", "b5130") and not _newer("b5130", "b4938") and not _newer("b4938", "b4938")
    assert _newer("0.4.0", "0.5.0") and not _newer("0.4.0", "0.4.0") and not _newer("0.10.0", "0.9.0")


def test_an_update_gives_the_installer_its_update_switch(installer, tmp_path, monkeypatch, capsys) -> None:
    from system_core.core import modules

    installer("echo the installer was given: %*\nexit /b 0")
    fake = modules.list_modules()[0]
    monkeypatch.setattr(modules, "list_modules", lambda *_args, **_kwargs: [dataclasses.replace(fake, update_args=("-Update",))])
    assert cli.main(["install", "fake", "--update"]) == 0
    assert "the installer was given: -Update" in capsys.readouterr().out
    assert cli.main(["install", "fake"]) == 0
    assert "-Update" not in capsys.readouterr().out


# ---------------------------------------------------------------- the keys of the services


@pytest.fixture()
def key_paths(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    """A config of the temporary folder: the keys of the program are never read or written by a test."""
    from system_core.core import keys

    real = cli.get_project_paths()
    mine = dataclasses.replace(real, config=tmp_path / "config")
    monkeypatch.setattr(cli, "get_project_paths", lambda: mine)
    for one in keys.KEYS:
        monkeypatch.delenv(keys._key_entry(one.provider)[1], raising=False)
    return mine


def test_keys_say_whether_a_key_is_there_never_the_key(key_paths, capsys, monkeypatch) -> None:
    import io

    assert cli.main(["keys"]) == 0
    told = {one["id"]: one for one in json.loads(capsys.readouterr().out.strip().splitlines()[-1])["keys"]}
    assert list(told)[0] == "huggingface" and {"openai", "xai"} <= set(told) and "notion" not in told
    assert not any(one["present"] for one in told.values())
    monkeypatch.setattr(sys, "stdin", io.StringIO("hf_a_secret_of_the_test_1234\n"))
    assert cli.main(["key", "set", "huggingface"]) == 0
    out = capsys.readouterr().out
    assert "hf_a_secret_of_the_test" not in out and json.loads(out.strip())["fingerprint"] == "…1234"
    assert (key_paths.config / "api_key_huggingface.txt").read_text(encoding="utf-8").strip() == "hf_a_secret_of_the_test_1234"
    assert cli.main(["keys"]) == 0
    out = capsys.readouterr().out
    told = {one["id"]: one for one in json.loads(out.strip().splitlines()[-1])["keys"]}
    assert told["huggingface"]["present"] and told["huggingface"]["fingerprint"] == "…1234" and told["huggingface"]["source"] == "file"
    assert "hf_a_secret_of_the_test" not in out
    assert cli.main(["key", "clear", "huggingface"]) == 0
    assert (key_paths.config / "api_key_huggingface.txt").read_text(encoding="utf-8") == ""


def test_a_key_comes_only_from_the_standard_input(key_paths, capsys, monkeypatch) -> None:
    import io

    monkeypatch.setattr(sys, "stdin", io.StringIO(""))
    assert cli.main(["key", "set", "openai"]) == 2
    assert cli.main(["key", "set", "nothing"]) == 2


def test_a_check_asks_the_service_and_tells_the_terms_of_community_1(key_paths, capsys, monkeypatch) -> None:
    import urllib.error

    from system_core.core import keys

    keys.write(key_paths, "huggingface", "hf_test")
    keys.write(key_paths, "openai", "sk_test")
    answers = {"whoami": (200, {"name": "tester"}), "gated": (403, {})}

    def fake(url, headers, method="GET"):
        assert headers.get("Authorization") in {"Bearer hf_test", "Bearer sk_test"}
        if "whoami" in url:
            return answers["whoami"]
        if "community-1" in url:
            return answers["gated"]
        return (401, {})

    monkeypatch.setattr(keys, "_ask", fake)
    assert keys.check(key_paths, "huggingface") == {"state": "no_access", "detail": "tester"}
    answers["gated"] = (200, {})
    assert keys.check(key_paths, "huggingface") == {"state": "accepted", "detail": "tester"}
    assert keys.check(key_paths, "openai")["state"] == "refused"
    assert keys.check(key_paths, "xai")["state"] == "no_key"
    assert keys.check(key_paths, "gigachat")["state"] == "not_checkable"

    def offline(*_args, **_kwargs):
        raise urllib.error.URLError("no route")

    monkeypatch.setattr(keys, "_ask", offline)
    assert keys.check(key_paths, "openai") == {"state": "unreachable", "detail": "no route"}
