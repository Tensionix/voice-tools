using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AudionVoiceTools.Core;

/// <summary>
/// What is written to disk, on a thread of its own: the journal of the program (logs\voicetools-DATE.log) and the
/// dictations into the library (Core\Library.cs, library\library.sqlite3; the sound of each in
/// library\audio\YEAR-MONTH\ID.wav, 16 kHz mono). 2.3.0 kept 200 texts without sound in config\ and lost the sound of a
/// failed batch or local dictation; here every dictation keeps its sound for its term, and the time of every stage.
/// </summary>
public static class Journal
{
    static readonly BlockingCollection<Action> Jobs = new();
    static readonly UTF8Encoding NoMark = new(false);
    static string _root = "";
    static Timer? _sweep;

    public static string Library => Path.Combine(_root, "library");

    public static void Start(string root)
    {
        _root = root;
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Library);
        var thread = new Thread(() => { foreach (var job in Jobs.GetConsumingEnumerable()) job(); })
        { IsBackground = true, Name = "disk" };
        thread.Start();
        // The library opens here, off the window thread: what 2.3 kept comes in, the old sound is swept - and once a day after.
        Jobs.Add(() => Core.Library.Start(root));
        _sweep = new Timer(_ => Post(() => Core.Library.Current?.Sweep()), null, TimeSpan.FromDays(1), TimeSpan.FromDays(1));
    }

    /// <summary>Work for the thread of the disk: the library writes there.</summary>
    public static void Post(Action job)
    {
        if (_root.Length > 0) Jobs.Add(job);
    }

    public static event Action<string>? Line;

    public static void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {text}";
        Line?.Invoke(line);
        if (_root.Length == 0) return;
        Jobs.Add(() => File.AppendAllText(Path.Combine(_root, "logs", $"voicetools-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine, NoMark));
    }

    public static void Dictation(DictationRecord record, float[] pcm16k)
    {
        Jobs.Add(() =>
        {
            var library = Core.Library.Current;
            // "Not kept": the sound of a pasted dictation is not written; a cancelled or failed one keeps it until the sweep.
            if (library is null || library.AudioDays != 0 || record.Outcome != "pasted")
            {
                var folder = Path.Combine(Library, "audio", record.Created.ToString("yyyy-MM"));
                Directory.CreateDirectory(folder);
                var wav = Path.Combine(folder, record.Id + ".wav");
                WriteWav(wav, pcm16k, 16000);
                record.Audio = Path.GetRelativePath(_root, wav);
            }
            try
            {
                if (library is null) throw new InvalidOperationException("the library is not open");
                library.Put(record);
            }
            catch (Exception ex)
            {
                // A library that does not take it loses nothing: the line goes where the probe kept them and comes in later.
                Log(@"[ERR] the dictation went to library\dictations.jsonl: " + ex.Message);
                var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                File.AppendAllText(Path.Combine(Library, "dictations.jsonl"), json + "\n", NoMark);
            }
            Recorded?.Invoke(record);
        });
    }

    /// <summary>A dictation is in the library, its sound beside it: the manager shows it. Called on the thread of the disk.</summary>
    public static event Action<DictationRecord>? Recorded;

    public static void Flush()
    {
        using var done = new ManualResetEventSlim();
        Jobs.Add(done.Set);
        done.Wait(3000);
    }

    static void WriteWav(string path, float[] pcm, int rate)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        int bytes = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in pcm) w.Write((short)Math.Clamp((int)Math.Round(s * 32767f), short.MinValue, short.MaxValue));
    }
}

/// <summary>One dictation in the library: its text, where it went, and how long each stage took.</summary>
public sealed class DictationRecord
{
    public string Id { get; set; } = "";
    public DateTime Created { get; set; }
    public string Outcome { get; set; } = ""; // pasted, cancelled, empty, error
    public string Text { get; set; } = "";
    /// <summary>The text worked over by the mode of the text (Core\TextModes.cs) and pasted; "" - the text of the engine was.</summary>
    public string CleanText { get; set; } = "";
    /// <summary>The mode of the text asked for; "" - none.</summary>
    public string Mode { get; set; } = "";
    public string Error { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Device { get; set; } = "";
    public string TargetTitle { get; set; } = "";
    public double AudioSeconds { get; set; }
    public string Audio { get; set; } = "";
    /// <summary>Press of the chord to the stream open.</summary>
    public long MicOpenMs { get; set; }
    /// <summary>Press of the chord to the first frame of the capsule saying "listening".</summary>
    public long ListeningShownMs { get; set; }
    /// <summary>The stop to the take resampled for the engine.</summary>
    public long AudioReadyMs { get; set; }
    public long RecognizeMs { get; set; }
    /// <summary>The language model of the mode: the text of the engine to the text worked over.</summary>
    public long ModeMs { get; set; }
    /// <summary>Text ready to Ctrl+V sent.</summary>
    public long PasteMs { get; set; }
    /// <summary>The stop to Ctrl+V sent: what a person waits for.</summary>
    public long StopToPasteMs { get; set; }
    /// <summary>The longest the window thread kept a message waiting during this dictation.</summary>
    public long UiWorstLagMs { get; set; }
    /// <summary>What was pasted: the text worked over, else the text of the engine.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Pasted => CleanText.Length > 0 ? CleanText : Text;
    /// <summary>Kept on top of the list, and its sound past its term.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Pinned { get; set; }
    /// <summary>Where it came from: "3.0", or "history-2.3" - the texts of 2.3, without sound.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Origin { get; set; } = "3.0";
}
