using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AudionVoiceTools.Views;

namespace AudionVoiceTools.Core;

/// <summary>
/// The hot path of dictation, alive all the time the program is (the probe of 3.0, E:\TOOLS\VOICE_AI_AVALONIA\
/// PROBE_HOT_PATH_RU.md): the engine of Handy (transcribe.cpp) with its model kept loaded, the microphone by WASAPI held
/// open with 250 ms before the press, the paste on an STA thread of its own, the automaton of the session, the capsule
/// at the top of the screen, the hotkey by Raw Input. The window thread waits for none of them; the manager only reads
/// what the session says. Beside it the recorder (Core\Recorder.cs): its own hotkey, the same capsule, a stream of its
/// own on the same microphone - a dictation goes on during a recording.
/// </summary>
public sealed class Dictation : IDisposable
{
    public Session Session { get; }
    public Capsule Capsule { get; }
    public Recorder Recorder { get; }
    readonly Engine _engine;
    readonly Mic _mic;
    readonly Hotkeys? _hotkeys;

    /// <summary>Where the recorder writes: library\recordings\YEAR\MONTH.</summary>
    public static string RecordingsRoot => Path.Combine(Journal.Library, "recordings");

    /// <summary>A recording is saved and in the library - the manager shows it, and takes it to the transcription. On the thread of the disk.</summary>
    public event Action<string>? Saved;

    /// <summary>The recorder changed its state - the manager and the tray follow. On any thread.</summary>
    public event Action<RecSnapshot>? RecordingChanged;

    /// <summary>What dictation recognizes with (config\dictation.json); a stand has its own, never saved.</summary>
    public DictationConfig Config { get; }
    readonly bool _stand;

    /// <summary>The engine of dictation changed - the tray, the capsule and SETTINGS follow. On the thread that changed it.</summary>
    public event Action<string>? EngineChanged;

    /// <summary>
    /// Anything of config\dictation.json changed - the engine, its model or language, the mode of the text, the microphone, the
    /// panel - from the tray, the capsule or a page of the manager: the pages that show it follow. On the thread that changed it.
    /// </summary>
    public event Action? Changed;

    /// <summary>The engine of this machine as it runs: its model and the backend it got («gigaam-v3-e2e-rnnt-Q8_0 · Vulkan0»); "" until loaded.</summary>
    public string LocalEngine => _engine.Ready ? $"{_engine.ModelName} · {_engine.Backend}" : "";

    public Dictation(Program.Options o)
    {
        Journal.Start(o.DataRoot);
        Journal.Log($"[GUI] Audion Voice Tools, root {Paths.Root}{(o.Stand ? ", stand" : "")}, microphone {(o.KeepMicOpen ? "kept open" : "opened at the press")}");
        UiWatchdog.Start();

        _engine = new Engine(Path.Combine(Paths.Root, "Tools", "transcribe"));
        _mic = new Mic { FakeFile = o.FakeMic, KeepOpen = o.KeepMicOpen };
        _stand = o.Stand;
        Config = o.Stand ? new DictationConfig { Engine = o.Cloud.Length > 0 ? o.Cloud : "local" } : DictationConfig.Load(Paths.Root);
        // The model of this machine: the one chosen on DICTATIONS when it is installed, else GigaAM; --model names one outright.
        var model = o.ModelGiven || o.Stand ? o.Model : DictationModels.Of(Config.LocalModel) is { Installed: true } chosen ? chosen.Path : o.Model;
        Session = new Session(_engine, _mic, new Paste(), model, o.Backend);
        if (o.Stand) Session.PasteInto = Stand.PasteInto;

        // A stand records from its WAV too: no microphone of the person is opened.
        Recorder = new Recorder { FakeFile = o.FakeMic };

        Microphones.Chosen = o.Stand ? "" : Config.Microphone;
        Session.Config = Config;
        if (!o.Stand) Session.Correct = text => Vocabulary.Replace(Paths.Root, text);
        if (o.CloudWait > 0) Session.CloudWait = TimeSpan.FromSeconds(o.CloudWait);
        Apply(Config.Engine, o.CloudEndpoint);

        Capsule = new Capsule(o.Scale, o.Stand) { Session = Session, Host = this };
        Session.Changed += Capsule.Post;
        _mic.Level += Capsule.Level;
        Recorder.Changed += snapshot =>
        {
            Capsule.PostRecording(snapshot);
            RecordingChanged?.Invoke(snapshot);
        };
        Recorder.Level += Capsule.RecordLevel;

        if (!o.Stand)
        {
            _hotkeys = new Hotkeys(acceptInjected: false);
            _hotkeys.Pressed += (key, ticks, foreground) =>
            {
                if (key == Hotkeys.Key.Chord) Session.Chord(ticks, foreground);
                else if (key == Hotkeys.Key.Record) ToggleRecording();
                else Session.Cancel();
            };
            Session.Changed += s => _hotkeys.EscapeWanted = s.State == State.Listening;
        }
    }

    /// <summary>The capsule comes up and the model is loaded and warmed, off the window thread; a recording a fall left is recovered.</summary>
    public void Start()
    {
        Capsule.Show();
        Session.Arm();
        Journal.Post(() =>
        {
            foreach (var file in Recorder.Recover(RecordingsRoot))
            {
                Library.Current?.PutRecording(file, recovered: true);
                Saved?.Invoke(file);
            }
        });
    }

    /// <summary>
    /// Another engine of dictation, from the capsule, the tray or SETTINGS: the stream of the cloud before it is closed, the
    /// new one opened ahead at once. Saved in config\dictation.json (a stand saves nothing).
    /// </summary>
    public void SetEngine(string engine)
    {
        if (!DictationConfig.Engines.Contains(engine)) return;
        Config.Engine = engine;
        if (!_stand) Config.Save(Paths.Root);
        Apply(engine, "");
        Journal.Log($"[GUI] dictation engine: {DictationConfig.TitleOf(engine)} ({engine})");
        EngineChanged?.Invoke(engine);
        Changed?.Invoke();
    }

    /// <summary>
    /// Another model of this machine (DICTATIONS → ADVANCED → LOCAL MODELS): an installed one only; loaded in place of the one
    /// there, and dictation goes to this machine. Saved in config\dictation.json.
    /// </summary>
    public bool SetLocalModel(string id)
    {
        var model = DictationModels.Of(id);
        if (!model.Installed) return false;
        Config.LocalModel = model.Id;
        if (!_stand) Config.Save(Paths.Root);
        Session.LoadModel(model.Path);
        Journal.Log($"[GUI] local model: {model.Title} ({model.File})");
        if (Config.Engine != "local") SetEngine("local");
        else Changed?.Invoke();
        return true;
    }

    /// <summary>The model of this machine chosen, by its name with the engine: «Parakeet V3 · transcribe.cpp».</summary>
    public string LocalTitle => DictationModels.Of(Config.LocalModel).Title + " · transcribe.cpp";

    /// <summary>The settings of the cloud changed (a model, a language, an endpoint): the stream is made again with them.</summary>
    public void ConfigChanged()
    {
        if (!_stand) Config.Save(Paths.Root);
        Apply(Config.Engine, "");
        Changed?.Invoke();
    }

    /// <summary>A key was saved: a stream that waited for it opens now.</summary>
    public void KeysChanged() => Session.Cloud?.Warm();

    /// <summary>Another microphone from the menu of the tray: dictation and the recorder take it from now on.</summary>
    public void SetMicrophone(string id)
    {
        Config.Microphone = id;
        Microphones.Chosen = id;
        if (!_stand) Config.Save(Paths.Root);
        Session.MicChanged();
        Journal.Log($"[GUI] microphone: {(id.Length == 0 ? "the one of Windows by default" : Microphones.NameInUse())}");
        Changed?.Invoke();
    }

    /// <summary>The mode of the text from ⌄ of the capsule or SETTINGS: it works on the next stop - the one under way too.</summary>
    public void SetMode(string mode)
    {
        if (!TextModes.Modes.Contains(mode)) return;
        Config.Mode = mode;
        if (!_stand) Config.Save(Paths.Root);
        Journal.Log($"[GUI] mode of the text: {mode}");
        Changed?.Invoke();
    }

    /// <summary>The capsule at the top or at the bottom of the screen.</summary>
    public void SetPanel(string where)
    {
        Config.Panel = where;
        if (!_stand) Config.Save(Paths.Root);
        Capsule.Refresh();
        Changed?.Invoke();
    }

    /// <summary>The strip of the capsule at rest shown or not.</summary>
    public void SetShowPanel(bool show)
    {
        Config.ShowPanel = show;
        if (!_stand) Config.Save(Paths.Root);
        Capsule.Refresh();
        Changed?.Invoke();
    }

    /// <summary>The text of the newest dictation of the library that has one; "" - none.</summary>
    public static string LastText() => Library.Current?.Dictations(most: 50).Where(one => one.Pasted.Length > 0)
        .OrderByDescending(one => one.Created).Select(one => one.Pasted).FirstOrDefault() ?? "";

    void Apply(string engine, string endpoint)
    {
        var old = Session.Cloud;
        Session.Cloud = null;
        Session.Batch = null;
        old?.Dispose();
        CloudStream? stream = engine switch
        {
            "xai" => new XaiStream(Paths.Root, Config),
            "openai_realtime" => new OpenAiRealtimeStream(Paths.Root, Config),
            "elevenlabs" => new ElevenLabsStream(Paths.Root, Config),
            _ => null,
        };
        if (stream is not null)
        {
            stream.Endpoint = endpoint;
            stream.Partial += Session.Heard;
            Session.Cloud = stream;
            stream.Warm();
        }
        if (engine == "openai_batch") Session.Batch = new BatchCloud(Paths.Root, Config);
        Session.Touch();
    }

    /// <summary>
    /// A take of the library recognized again (DICTATIONS: «распознать заново») by the engine of this machine, on the thread
    /// of the engine beside dictation; the name of the engine comes back with the text.
    /// </summary>
    public async Task<(string Text, string Engine)> RecognizeAgain(float[] pcm16k)
    {
        if (!_engine.Ready) throw new InvalidOperationException(Lang.T("движок диктовки этой машины не загружен"));
        var (text, _) = await _engine.Recognize(pcm16k);
        return (text, $"{_engine.ModelName} · {_engine.Backend}");
    }

    /// <summary>The hotkey of the recorder, its menu of the tray: a recording begins, or the one under way is saved.</summary>
    public void ToggleRecording()
    {
        if (Recorder.State == RecState.Idle) StartRecording();
        else StopRecording(keep: true);
    }

    public string StartRecording() => Recorder.Start(RecordingsRoot);

    public void PauseRecording()
    {
        if (Recorder.State == RecState.Recording) Recorder.Pause();
        else if (Recorder.State == RecState.Paused) Recorder.Resume();
    }

    /// <summary>
    /// The end of the recording off the window thread - the stream closes, the file is written whole; a kept one goes to
    /// the library. <paramref name="keep"/> false: the × of the capsule, the file to the Recycle Bin.
    /// </summary>
    public void StopRecording(bool keep) => Task.Run(() =>
    {
        if (Recorder.Stop(keep) is not { } file) return;
        Journal.Post(() =>
        {
            Library.Current?.PutRecording(file);
            Saved?.Invoke(file);
        });
    });

    /// <summary>The scenario of the stand: dictations from the WAV, then the summary; the program ends after it.</summary>
    public Task RunStand(Program.Options o) => Stand.Run(Session, o.Cycles, o.HoldSeconds, o.Summary, o.Record ? this : null);

    public void Dispose()
    {
        _hotkeys?.Dispose();
        // A recording under way at the end of the program is saved, not lost: the file is whole before the library hears of it.
        if (Recorder.State is RecState.Recording or RecState.Paused && Recorder.Stop() is { } file)
            Journal.Post(() => Library.Current?.PutRecording(file));
        Recorder.Dispose();
        Session.Cloud?.Dispose();
        _engine.Dispose();
        _mic.Dispose();
        Journal.Flush();
    }
}
