using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AudionVoiceTools.Core;

/// <summary>
/// The stand of the hot path: the program runs a dictation scenario by itself, with nothing sent to the windows of the
/// person - no keys are injected into the system (a chord sent by SendInput goes to whatever window is in front) and the
/// clipboard is not touched. The chord comes to the control as the input thread would tell it; the microphone is a WAV
/// fed at the speed of real time; the paste goes into an EDIT window of the stand, beyond the screens, by
/// EM_REPLACESEL. What it measures: the press to "listening" drawn, the stream open, the stop to the paste, the worst
/// delay of the window thread. The raw input itself and Ctrl+V into a real program are checked by a person.
/// </summary>
public static class Stand
{
    static IntPtr _edit;

    /// <summary>The field the stand pastes into: a native EDIT on a thread of its own, at -30000.</summary>
    public static IntPtr Target()
    {
        if (_edit != IntPtr.Zero) return _edit;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _edit = Native.CreateWindowEx(0, "EDIT", "", 0x00CF0000 | 0x0004 /* WS_OVERLAPPEDWINDOW | ES_MULTILINE */,
                -30000, -30000, 400, 200, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
            ready.Set();
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }) { IsBackground = true, Name = "stand-target" };
        thread.Start();
        ready.Wait(3000);
        return _edit;
    }

    public static Task<(long, string?)> PasteInto(IntPtr target, string text) => Task.Run(() =>
    {
        var watch = Stopwatch.StartNew();
        Native.SendMessage(target, Native.EM_REPLACESEL, (IntPtr)1, text + " ");
        return (watch.ElapsedMilliseconds, (string?)null);
    });

    static string TargetText()
    {
        int length = (int)Native.SendMessage(_edit, Native.WM_GETTEXTLENGTH, IntPtr.Zero, "");
        var buffer = new char[length + 1];
        Native.SendMessage(_edit, Native.WM_GETTEXT, (IntPtr)buffer.Length, buffer);
        return new string(buffer, 0, length);
    }

    /// <summary>
    /// N dictations of the WAV, each held for its length plus a little; then the summary and the end. With the recorder
    /// (--record) a recording runs over them, paused for a second after the first; saved, it is measured against what it
    /// should hold; then a second one is left at its seventh second as a fall leaves it, and recovered as the next start
    /// recovers it.
    /// </summary>
    public static async Task Run(Session session, int cycles, double holdSeconds, string summaryFile, Dictation? owner = null)
    {
        var target = Target();
        for (int wait = 0; session.Current != State.Ready && session.Current != State.Error && wait < 600; wait++) await Task.Delay(100);
        if (session.Current != State.Ready)
        {
            Journal.Log("[STAND] the engine did not become ready");
            return;
        }
        Journal.Log($"[STAND] {cycles} dictations of {holdSeconds:0.0} s{(owner is null ? "" : ", over a recording")}");
        if (owner is not null && owner.StartRecording() is { Length: > 0 } refused) Journal.Log("[STAND] the recording did not start: " + refused);
        for (int i = 0; i < cycles; i++)
        {
            if (owner is not null && i == 1)
            {
                owner.PauseRecording();
                await Task.Delay(1000);
                owner.PauseRecording();
            }
            session.Chord(Stopwatch.GetTimestamp(), target);
            await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
            session.Chord(Stopwatch.GetTimestamp(), target);
            for (int wait = 0; wait < 600; wait++)
            {
                await Task.Delay(50);
                if (session.Current is State.Ready or State.Error) break;
            }
            await Task.Delay(400);
        }
        object? recording = null;
        if (owner is not null) recording = await Recordings(owner);
        Journal.Flush();
        var records = Library.Current?.Dictations() ?? new System.Collections.Generic.List<DictationRecord>();
        // What recognized each, and why the cloud gave way - the text itself is not written into the journal.
        foreach (var record in records)
            Journal.Log($"[STAND] {record.Id}: {record.Outcome}, {record.Engine}{(record.Error.Length > 0 ? " - " + record.Error : "")}");
        var summary = new
        {
            cycles,
            holdSeconds,
            records,
            recording,
            uiWorstEverMs = UiWatchdog.WorstEverMs,
            uiLateOver50 = UiWatchdog.LateCount,
            targetText = TargetText(),
        };
        File.WriteAllText(summaryFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));
        Journal.Log($"[STAND] done: {summaryFile}");
    }

    /// <summary>
    /// The cloud of a stand: a server of this machine that speaks as xAI speaks (wss://api.x.ai/v1/stt) - "transcript.created"
    /// at once, the binary sound counted, at "audio.done" a final and "transcript.done" that say how much sound came;
    /// "mute" - it takes the sound and says nothing, as a service that hangs. The address of its stream.
    /// </summary>
    public static string FakeCloud(string mode)
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                if (!context.Request.IsWebSocketRequest) { context.Response.StatusCode = 400; context.Response.Close(); continue; }
                _ = Task.Run(() => ServeCloud(context, mode));
            }
        });
        Journal.Log($"[STAND] a cloud of the stand ({mode}) at ws://localhost:{port}/v1/stt");
        return $"ws://localhost:{port}/v1/stt";
    }

    static async Task ServeCloud(System.Net.HttpListenerContext context, string mode)
    {
        var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        Task Say(string json) => socket.SendAsync(Encoding.UTF8.GetBytes(json), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        await Say("{\"type\":\"transcript.created\"}");
        long bytes = 0;
        var buffer = new byte[1 << 16];
        try
        {
            while (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var got = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (got.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                if (got.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary) { bytes += got.Count; continue; }
                if (!Encoding.UTF8.GetString(buffer, 0, got.Count).Contains("audio.done") || mode == "mute") continue;
                var text = $"Cloud of the stand: {(bytes / 32000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s of sound.";
                await Say("{\"type\":\"transcript.partial\",\"text\":\"" + text + "\",\"is_final\":true,\"speech_final\":true}");
                await Say("{\"type\":\"transcript.done\",\"text\":\"" + text + "\"}");
                await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
        }
        catch { }
    }

    /// <summary>What the header of a recording says it holds - not the length of the file: a fall is told by the two differing.</summary>
    static double HeaderSeconds(string file)
    {
        using var stream = File.OpenRead(file);
        var header = new byte[Wav.HeaderBytes];
        if (stream.Read(header, 0, header.Length) < header.Length) return 0;
        return BitConverter.ToUInt32(header, Wav.HeaderBytes - 4) / (double)(Recorder.Rate * 2);
    }

    static async Task<object> Recordings(Dictation owner)
    {
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Saved += file => saved.TrySetResult(file);
        var expected = owner.Recorder.Elapsed.TotalSeconds;
        owner.StopRecording(keep: true);
        var file = await saved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        double seconds = Library.WavSeconds(file);
        bool inLibrary = Library.Current?.Items("recording").Any(item => string.Equals(item.Source, file, StringComparison.OrdinalIgnoreCase)) == true;
        Journal.Log($"[STAND] recording {seconds:0.00} s against {expected:0.00} s recorded, paused 1 s, in the library: {inLibrary}");

        owner.StartRecording();
        await Task.Delay(6500);
        var fallen = owner.Recorder.AbandonForStand();
        bool marker = File.Exists(fallen + ".recording");
        double before = HeaderSeconds(fallen);
        var recovered = Recorder.Recover(Dictation.RecordingsRoot);
        double after = HeaderSeconds(fallen);
        Journal.Log($"[STAND] a fall at 6.5 s: the header said {before:0.00} s, the marker {(marker ? "was" : "was not")} there; recovered {after:0.00} s ({recovered.Count} file)");
        return new { file, seconds, expected, inLibrary, fallen, markerLeft = marker, headerBefore = before, recoveredSeconds = after, recovered = recovered.Count };
    }
}
