from __future__ import annotations

from system_core.core.paths import get_project_paths
from system_core.providers.whispercpp_server import WhisperCppServer, WhisperCppServerError


class _FakeProc:
    """Minimal Popen stand-in: exit_code None = alive, int = already exited."""

    def __init__(self, exit_code=None):
        self._exit = exit_code

    def poll(self):
        return self._exit

    def terminate(self):
        if self._exit is None:
            self._exit = -15

    def wait(self, timeout=None):
        return self._exit or 0

    def kill(self):
        self._exit = -9

    @property
    def returncode(self):
        return self._exit


def _stage_server_pack(tmp_path):
    paths = get_project_paths(tmp_path)
    (paths.tools / "whispercpp").mkdir(parents=True)
    paths.models.mkdir(parents=True)
    paths.workspace.mkdir(parents=True, exist_ok=True)
    (paths.tools / "whispercpp" / "whisper-server.exe").write_text("", encoding="utf-8")
    (paths.models / "ggml-large-v3-turbo.bin").write_text("", encoding="utf-8")
    wav = paths.workspace / "u.wav"
    wav.write_bytes(b"data")
    return paths, wav


def test_server_unavailable_without_binary(tmp_path):
    paths = get_project_paths(tmp_path)
    assert WhisperCppServer(paths).available() is False


def test_server_available_with_server_binary(tmp_path):
    paths, _ = _stage_server_pack(tmp_path)
    assert WhisperCppServer(paths).available() is True


def test_server_transcribes_via_warm_server(tmp_path):
    paths, wav = _stage_server_pack(tmp_path)
    commands = []
    server = WhisperCppServer(
        paths,
        launcher=lambda cmd: commands.append(cmd) or _FakeProc(exit_code=None),
        http_get=lambda url, timeout: 200,
        http_post=lambda url, wav_path, data, timeout: '{"text": "  hello world "}'
        if data.get("response_format") != "json"
        else "hello world",
    )

    text = server.transcribe_wav(wav, language="ru")

    assert text == "hello world"
    assert commands and "-ng" not in commands[0]   # auto tries GPU first
    server.close()


def test_server_joins_segments_cut_inside_a_word(tmp_path):
    paths, wav = _stage_server_pack(tmp_path)
    server = WhisperCppServer(
        paths,
        launcher=lambda cmd: _FakeProc(exit_code=None),
        http_get=lambda url, timeout: 200,
        http_post=lambda url, wav_path, data, timeout: "Привет!\n Посмотри после об\nновления.\n",
    )

    assert server.transcribe_wav(wav, language="ru") == "Привет! Посмотри после обновления."
    server.close()


def test_server_returns_verbose_timestamp_payload(tmp_path):
    paths, wav = _stage_server_pack(tmp_path)
    server = WhisperCppServer(
        paths,
        launcher=lambda _cmd: _FakeProc(exit_code=None),
        http_get=lambda _url, _timeout: 200,
        http_post=lambda _url, _wav, _data, _timeout: {
            "text": "hello",
            "segments": [{"text": " hello", "start": 1.25, "end": 2.5}],
        },
    )
    payload = server.transcribe_wav_verbose(wav, language="en")
    assert payload["segments"][0]["start"] == 1.25
    server.close()


def test_server_forced_cublas_does_not_demote_to_cpu(tmp_path):
    paths, wav = _stage_server_pack(tmp_path)
    commands = []
    server = WhisperCppServer(
        paths,
        backend="cublas",
        launcher=lambda cmd: (commands.append(cmd), _FakeProc(exit_code=1))[1],
        http_get=lambda _url, _timeout: 200,
        http_post=lambda _url, _wav, _data, _timeout: "ok",
    )
    try:
        server.transcribe_wav(wav)
        raised = False
    except WhisperCppServerError:
        raised = True
    assert raised
    assert len(commands) == 1
    assert "-ng" not in commands[0]
    server.close()


def test_server_auto_retries_cpu_when_gpu_dies(tmp_path):
    paths, wav = _stage_server_pack(tmp_path)
    commands = []
    procs = [_FakeProc(exit_code=1), _FakeProc(exit_code=None)]  # GPU dies, CPU alive

    server = WhisperCppServer(
        paths,
        launcher=lambda cmd: (commands.append(cmd), procs.pop(0))[1],
        http_get=lambda url, timeout: 200,
        http_post=lambda url, wav_path, data, timeout: "ok",
    )

    text = server.transcribe_wav(wav)

    assert text == "ok"
    assert len(commands) == 2
    assert "-ng" not in commands[0]
    assert "-ng" in commands[1]
    server.close()


def test_server_forced_vulkan_does_not_demote_to_cpu(tmp_path):
    paths, wav = _stage_server_pack(tmp_path)
    commands = []
    server = WhisperCppServer(
        paths,
        backend="vulkan",
        launcher=lambda cmd: (commands.append(cmd), _FakeProc(exit_code=1))[1],
        http_get=lambda url, timeout: 200,
        http_post=lambda url, wav_path, data, timeout: "ok",
    )

    try:
        server.transcribe_wav(wav)
        raised = False
    except WhisperCppServerError:
        raised = True

    assert raised
    assert len(commands) == 1          # no CPU retry when GPU is forced
    assert "-ng" not in commands[0]
    server.close()
