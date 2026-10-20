using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AudionVoiceTools.Core;

public enum State { Off, Arming, Ready, Listening, Finalizing, Pasting, Error }

/// <summary>
/// What the capsule draws: a copy, made on the control thread and read on the window thread. Cloud - the engine is a service;
/// Mode - the mode of the text works the text over (Core\TextModes.cs), its name.
/// </summary>
public sealed record Snapshot(State State, string Text, string Engine, long ListeningSince, string Error, bool Cloud = false, string Mode = "");

/// <summary>
/// The dictation itself: a machine of states on a control thread of its own. Every event - the chord from the input
/// thread, a click of the capsule, the end of the engine or of the paste - is a job in its queue, so the state changes
/// in one place and one at a time. The window thread is only told what to draw (Changed) and never waits: it reports
/// back when it has drawn "listening" (Shown), which closes the measure of the start.
///
/// The mode is the one of 2.3.0 (live.mode toggle): the chord starts, the chord again or ■ stops, × or Esc cancels.
///
/// With a stream of the cloud (Core\Cloud.cs) the take goes to the service as it is spoken, its words come back to the
/// capsule, and the stop waits for its text; with a request after the stop (BatchCloud) the take is sent whole. Whatever the
/// cloud does - no key, no network, silence - the take is recognized by the local engine instead, and the record says so.
/// </summary>
public sealed class Session : IDisposable
{
    public event Action<Snapshot>? Changed;

    /// <summary>Pastes the text into the window that was in front; a stand pastes into its own field.</summary>
    public Func<IntPtr, string, Task<(long Ms, string? Error)>> PasteInto { get; set; }

    readonly BlockingCollection<Action> _jobs = new();
    readonly Engine _engine;
    readonly Mic _mic;
    readonly string _backend;
    string _modelPath;
    /// <summary>Another model was chosen while a take went: it is loaded once the session is ready again.</summary>
    bool _reload;
    State _state = State.Off;
    string _text = "", _error = "";
    long _pressed, _stopped, _shownAt;
    IntPtr _target;
    string _targetTitle = "";
    DictationRecord? _record;
    int _generation;
    CloudStream? _streaming;
    string _working = "";

    /// <summary>The stream of the cloud dictation goes to; null - the local engine or a request after the stop.</summary>
    public CloudStream? Cloud { get; set; }

    /// <summary>The request after the stop to an endpoint compatible with OpenAI; null - none.</summary>
    public BatchCloud? Batch { get; set; }

    /// <summary>The settings of dictation: the mode of the text among them, read at each stop; null - the text as the engine gives it.</summary>
    public DictationConfig? Config { get; set; }

    /// <summary>The text of the engine put right before anything else - the replacements of the dictionary (Core\Vocabulary.cs); a stand keeps it as it is.</summary>
    public Func<string, string> Correct { get; set; } = text => text;

    /// <summary>How long the stop waits for the text of the cloud before the local engine takes the take.</summary>
    public TimeSpan CloudWait { get; set; } = TimeSpan.FromSeconds(8);

    public Session(Engine engine, Mic mic, Paste paste, string modelPath, string backend)
    {
        _engine = engine;
        _mic = mic;
        _modelPath = modelPath;
        _backend = backend;
        PasteInto = paste.Into;
        _mic.Collected += (pcm, rate) => _streaming?.Feed(pcm, rate);
        var thread = new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) Safe(job); })
        { IsBackground = true, Name = "control" };
        thread.Start();
    }

    public State Current => _state;

    /// <summary>
    /// A text of the library pasted again (DICTATIONS of the manager): into the window that comes in front once the
    /// manager has stepped aside, by the same paste - the clipboard of the person comes back. Not while a dictation goes.
    /// </summary>
    public void PasteText(string text) => Post(() =>
    {
        if (_state is State.Listening or State.Finalizing or State.Pasting || text.Length == 0) return;
        Thread.Sleep(400);
        var target = Native.GetForegroundWindow();
        var (ms, error) = PasteInto(target, text).GetAwaiter().GetResult();
        Journal.Log(error is null ? $"[PASTE AGAIN] {text.Length} chars in {ms} ms" : $"[PASTE AGAIN] failed: {error}");
    });

    /// <summary>
    /// A text pasted into a given window - the last dictation from the menu of the tray, into the program that was in front
    /// before the click on the tray; the clipboard of the person comes back as after any paste. Not while a dictation goes.
    /// </summary>
    public void PasteTextInto(IntPtr target, string text) => Post(() =>
    {
        if (_state is State.Listening or State.Finalizing or State.Pasting || text.Length == 0) return;
        if (target != IntPtr.Zero) Native.SetForegroundWindow(target);
        Thread.Sleep(300);
        var into = target != IntPtr.Zero ? target : Native.GetForegroundWindow();
        var (ms, error) = PasteInto(into, text).GetAwaiter().GetResult();
        Journal.Log(error is null ? $"[PASTE LAST] {text.Length} chars in {ms} ms" : $"[PASTE LAST] failed: {error}");
    });

    /// <summary>Another microphone chosen: the stream kept open moves to it - not during a take.</summary>
    public void MicChanged() => Post(() => { if (_state is not (State.Listening or State.Finalizing)) _mic.Reopen(); });

    void Safe(Action job)
    {
        try { job(); }
        catch (Exception e)
        {
            Journal.Log($"[ERR] {e.GetType().Name}: {e.Message}");
            Fail(e.Message);
        }
    }

    void Post(Action job) => _jobs.Add(job);

    void Set(State state, string? text = null)
    {
        _state = state;
        if (text != null) _text = text;
        var (label, cloud) = Cloud is { } stream ? (stream.Title, true) : Batch is { } batch ? (batch.Title, true)
            : (_engine.Ready ? $"{_engine.ModelName} · {_engine.Backend}" : "", false);
        Changed?.Invoke(new Snapshot(state, _text, label, _pressed, _error, cloud, _working));
        if (state == State.Ready && _reload)
        {
            _reload = false;
            Post(() => { if (_state == State.Ready) { _state = State.Off; Arm(); } });
        }
    }

    /// <summary>
    /// Another model of this machine (DICTATIONS → ADVANCED → LOCAL MODELS): loaded on the thread of the engine in place of the
    /// one there. A take under way finishes on the old model; the new one comes once the session is ready again.
    /// </summary>
    public void LoadModel(string modelPath) => Post(() =>
    {
        if (modelPath == _modelPath && _engine.Ready) return;
        _modelPath = modelPath;
        if (_state is State.Listening or State.Finalizing or State.Pasting or State.Arming) { _reload = true; return; }
        _state = State.Off;
        Arm();
    });

    void Fail(string message)
    {
        _error = message;
        Journal.Log("[ERR] " + message);
        if (_record != null)
        {
            _record.Outcome = "error";
            _record.Error = message;
            Journal.Dictation(_record, Array.Empty<float>());
            _record = null;
        }
        Set(State.Error);
        int generation = _generation;
        // The error stays on the capsule three seconds, then the capsule is ready again.
        Task.Delay(3000).ContinueWith(_ => Post(() =>
        {
            if (_generation == generation && _state == State.Error && (_engine.Ready || Cloud != null || Batch != null)) { _error = ""; Set(State.Ready, ""); }
        }));
    }

    /// <summary>Loads the engine (on its thread) and makes the chord work.</summary>
    public void Arm() => Post(() =>
    {
        if (_state != State.Off && _state != State.Error) return;
        Set(State.Arming, "");
        Cloud?.Warm();
        _engine.Load(_modelPath, _backend).ContinueWith(t => Post(() =>
        {
            if (t.IsFaulted)
            {
                // The cloud dictates without the local engine - only its fallback is lost.
                if (Cloud != null || Batch != null)
                {
                    Journal.Log("[ERR] the local engine did not load, the cloud dictates without a fallback: " + t.Exception!.InnerException!.Message);
                    _mic.Prepare();
                    Set(State.Ready, "");
                    return;
                }
                Fail(t.Exception!.InnerException!.Message);
                return;
            }
            Journal.Log("[OK] " + t.Result);
            _mic.Prepare();
            Set(State.Ready, "");
        }));
    });

    /// <summary>The chord: start when ready, stop when listening.</summary>
    public void Chord(long ticks, IntPtr foreground) => Post(() =>
    {
        if (_state == State.Ready) Begin(ticks, foreground);
        else if (_state == State.Listening) End(ticks);
    });

    public void StopClicked() => Post(() => { if (_state == State.Listening) End(Stopwatch.GetTimestamp()); });

    /// <summary>The engine changed: the capsule is told what it now says.</summary>
    public void Touch() => Post(() => Set(_state));

    /// <summary>Words of the stream of the cloud while it listens: the capsule shows the last of them.</summary>
    public void Heard(string text) => Post(() => { if (_state == State.Listening) Set(State.Listening, text); });

    public void Cancel() => Post(() =>
    {
        if (_state != State.Listening) return;
        var (pcm, _, _) = _mic.Stop();
        _streaming?.Cancel();
        _streaming = null;
        _generation++;
        if (_record != null)
        {
            // A cancel does not lose the sound: the take is kept in the library with its outcome.
            _record.Outcome = "cancelled";
            _record.AudioSeconds = Math.Round(pcm.Length / 16000.0, 2);
            Journal.Dictation(_record, pcm);
            _record = null;
        }
        Journal.Log("[GUI] dictation cancelled");
        Set(State.Ready, "");
    });

    /// <summary>
    /// ↻ of the capsule: the take under way is dropped - its sound kept, as a cancel keeps it - and a new one begins at once, for the
    /// same window.
    /// </summary>
    public void Again()
    {
        var target = _target;
        Cancel();
        Post(() => { if (_state == State.Ready) Begin(Stopwatch.GetTimestamp(), target); });
    }

    /// <summary>The window thread drew "listening": the measure of the start ends here.</summary>
    public void Shown(long ticks) => Post(() =>
    {
        if (_record != null && _record.ListeningShownMs == 0 && ticks >= _pressed)
            _record.ListeningShownMs = Ms(_pressed, ticks);
        _shownAt = ticks;
    });

    void Begin(long ticks, IntPtr foreground)
    {
        _generation++;
        _pressed = ticks;
        _target = foreground;
        _targetTitle = Native.WindowTitle(foreground);
        _error = "";
        UiWatchdog.TakeWorst();
        _record = new DictationRecord
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"),
            Created = DateTime.Now,
            Engine = Cloud is { } cloud ? cloud.Title + " · " + Lang.T("облако") : Batch is { } batch ? batch.Title + " · " + Lang.T("облако")
                : $"{_engine.ModelName} · {_engine.Backend}",
            TargetTitle = _targetTitle,
        };
        // The stream of the cloud takes the take from its first block - the pre-roll of the kept microphone among them.
        _streaming = Cloud;
        _streaming?.Begin();
        // "Listening" is told before the stream opens: the capsule answers the press at once; the sound collected from
        // the start of the stream is not lost either way.
        Set(State.Listening, "");
        _mic.Start();
        _record.MicOpenMs = Ms(_pressed, Stopwatch.GetTimestamp());
        _record.Device = _mic.DeviceName;
    }

    void End(long ticks)
    {
        _stopped = ticks;
        Set(State.Finalizing);
        var (pcm, _, _) = _mic.Stop();
        var stream = _streaming;
        _streaming = null;
        var record = _record!;
        record.AudioReadyMs = Ms(_stopped, Stopwatch.GetTimestamp());
        record.AudioSeconds = Math.Round(pcm.Length / 16000.0, 2);
        int generation = _generation;
        if (pcm.Length < 16000 / 2)
        {
            stream?.Cancel();
            record.Outcome = "empty";
            Journal.Dictation(record, pcm);
            _record = null;
            Set(State.Ready, "");
            return;
        }
        Recognize(pcm, record, stream).ContinueWith(t => Post(() =>
        {
            if (generation != _generation) return;
            if (t.IsFaulted)
            {
                var message = t.Exception!.InnerException!.Message;
                record.Outcome = "error";
                record.Error = message;
                Journal.Dictation(record, pcm);
                _record = null;
                Fail(message);
                return;
            }
            var (heard, recognizeMs) = t.Result;
            var text = heard.Length > 0 ? Correct(heard) : heard;
            record.RecognizeMs = recognizeMs;
            record.Text = text;
            if (text.Length == 0)
            {
                record.Outcome = "empty";
                record.UiWorstLagMs = UiWatchdog.TakeWorst();
                Journal.Dictation(record, pcm);
                _record = null;
                Set(State.Ready, "");
                return;
            }
            var mode = Config?.Mode ?? "raw";
            if (mode == "raw" || !TextModes.Modes.Contains(mode))
            {
                Deliver(text);
                return;
            }
            // The mode of the text: the model works the text over while the capsule says so; whatever it does, the text of the
            // engine is pasted at the worst - the record says why.
            record.Mode = mode;
            _working = Lang.T(TextModes.TitleOf(mode));
            Set(State.Finalizing, text);
            long asked = Stopwatch.GetTimestamp();
            TextModes.Apply(Paths.Root, Config!, mode, text, _targetTitle).ContinueWith(m => Post(() =>
            {
                _working = "";
                if (generation != _generation) return;
                record.ModeMs = Ms(asked, Stopwatch.GetTimestamp());
                if (m.IsFaulted)
                {
                    var why = m.Exception!.InnerException!.Message;
                    Journal.Log($"[MODE] {mode}: {why} - the text of the engine is pasted");
                    var note = Lang.F("текст не обработан ({0}) - вставлен текст движка", why);
                    record.Error = record.Error.Length > 0 ? record.Error + "; " + note : note;
                    Deliver(text);
                    return;
                }
                record.CleanText = m.Result;
                Deliver(m.Result);
            }));
        }));

        void Deliver(string text)
        {
            Set(State.Pasting, text);
            long ready = Stopwatch.GetTimestamp();
            PasteInto(_target, text).ContinueWith(p => Post(() =>
            {
                var (pasteMs, error) = p.Result;
                record.PasteMs = pasteMs;
                record.StopToPasteMs = Ms(_stopped, ready) + pasteMs;
                record.UiWorstLagMs = UiWatchdog.TakeWorst();
                record.Outcome = error == null ? "pasted" : "error";
                // The note of a cloud that gave nothing stays; a paste that failed says so instead.
                if (error != null) record.Error = error;
                Journal.Dictation(record, pcm);
                Journal.Log($"[OK] {record.AudioSeconds:0.0} s -> {record.RecognizeMs} ms recognize{(record.Mode.Length > 0 ? $", {record.ModeMs} ms mode {record.Mode}" : "")}, stop->paste {record.StopToPasteMs} ms, " +
                            $"press->listening shown {record.ListeningShownMs} ms, mic open {record.MicOpenMs} ms, UI worst lag {record.UiWorstLagMs} ms");
                _record = null;
                if (error != null) Fail(error);
                else
                {
                    Set(State.Ready, text);
                    // The text stays on the capsule a moment (2.3.0: 1.5 s), then the capsule goes quiet.
                    Task.Delay(1500).ContinueWith(_ => Post(() =>
                    {
                        if (_generation == generation && _state == State.Ready) Set(State.Ready, "");
                    }));
                }
            }));
        }
    }

    /// <summary>
    /// The text of the take: the stream of the cloud or the request after the stop when one is chosen; the local engine
    /// when the cloud gives nothing - the record says why - or when none is chosen.
    /// </summary>
    async Task<(string Text, long Ms)> Recognize(float[] pcm, DictationRecord record, CloudStream? stream)
    {
        var watch = Stopwatch.StartNew();
        string? why = null;
        try
        {
            if (stream is not null)
            {
                var text = await stream.Finish(CloudWait);
                if (text.Length > 0) return (text, watch.ElapsedMilliseconds);
                why = Lang.T("облако вернуло пустой текст");
            }
            else if (Batch is { } batch)
            {
                var text = await batch.Recognize(pcm);
                if (text.Length > 0) return (text, watch.ElapsedMilliseconds);
                why = Lang.T("облако вернуло пустой текст");
            }
        }
        catch (Exception ex) { why = ex.Message; }
        if (why is not null)
        {
            if (!_engine.Ready) throw new InvalidOperationException(why);
            Journal.Log($"[CLOUD] {why}: the take is recognized locally");
            record.Engine = $"{_engine.ModelName} · {_engine.Backend}";
            record.Error = Lang.F("облако не дало текст ({0}) - распознано локально", why);
        }
        var (local, _) = await _engine.Recognize(pcm);
        return (local, watch.ElapsedMilliseconds);
    }

    static long Ms(long from, long to) => (long)((to - from) * 1000.0 / Stopwatch.Frequency);

    public void Dispose() => _jobs.CompleteAdding();
}
