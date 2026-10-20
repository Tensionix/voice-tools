using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace AudionVoiceTools.Core;

/// <summary>The recorder as it is: idle, recording, paused, saving.</summary>
public enum RecState { Idle, Recording, Paused, Saving }

/// <summary>What the recorder says of itself: its state, the file, how long it has recorded, a trouble.</summary>
public sealed record RecSnapshot(RecState State, string File, TimeSpan Elapsed, string Error);

/// <summary>
/// The recorder: a recording of the microphone, apart from dictation and from any section of the window - its own
/// hotkey, the capsule, the tray and the manager start and stop it. WASAPI in the shared mode, beside the stream of
/// dictation (Windows gives one microphone to both), mono, 48 kHz, PCM16, into
/// library\recordings\YEAR\MONTH\Audion_Recording_DATE_TIME.wav. A pause keeps the stream open and writes nothing of it.
///
/// What a fall of the program must not cost: the header is written again every 5 s and the file flushed to the disk,
/// and a marker beside the file (.recording) says that it is being written - the next start finds the marker, writes
/// the header by the length of the file and gives the recording to the library (<see cref="Recover"/>). Past 4 GB
/// (about 12 hours) the header becomes RF64 (EBU Tech 3306): the JUNK chunk kept for it at the start becomes ds64.
/// </summary>
public sealed class Recorder : IDisposable
{
    public const int Rate = 48000;
    /// <summary>Shorter than this a recording is no recording: it goes to the Recycle Bin, not to the library.</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(1);

    public event Action<RecSnapshot>? Changed;
    public event Action<float>? Level;

    /// <summary>A WAV of a stand in place of the microphone, fed at its own pace.</summary>
    public string? FakeFile { get; init; }

    readonly object _lock = new();
    RecState _state = RecState.Idle;
    string _file = "", _error = "";
    WasapiCapture? _capture;
    Thread? _fake;
    volatile bool _feeding;
    WdlResampler? _resampler;
    int _inRate;
    // A queue of its own for every recording: an end left by a start that failed does not end the next one.
    BlockingCollection<short[]> _blocks = new();
    Thread? _writer;
    long _frames;            // written to the file
    volatile bool _abandon;  // a stand: the writer leaves the file as a fall of the program would
    float _peak;
    int _peakFrames;

    public RecSnapshot Snapshot
    {
        get { lock (_lock) return new RecSnapshot(_state, _file, Elapsed, _error); }
    }

    public RecState State { get { lock (_lock) return _state; } }

    /// <summary>The length of what is recorded so far: the frames written, paused ones not among them.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(Interlocked.Read(ref _frames) / (double)Rate);

    void Tell() => Changed?.Invoke(Snapshot);

    // ------------------------------------------------------------ start, pause, stop

    /// <summary>A recording begins in the folder of recordings of the library; the reason when it could not.</summary>
    public string Start(string recordingsRoot)
    {
        lock (_lock)
        {
            if (_state != RecState.Idle) return "";
            var now = DateTime.Now;
            var folder = Path.Combine(recordingsRoot, now.ToString("yyyy"), now.ToString("MM"));
            Directory.CreateDirectory(folder);
            var stem = "Audion_Recording_" + now.ToString("yyyy-MM-dd_HH-mm-ss");
            var file = Path.Combine(folder, stem + ".wav");
            for (int i = 2; File.Exists(file); i++) file = Path.Combine(folder, $"{stem}_{i}.wav");
            try
            {
                File.WriteAllText(file + ".recording", DateTime.Now.ToString("O"), new UTF8Encoding(false));
                var stream = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
                stream.Write(Wav.Header(0, Rate));
                Interlocked.Exchange(ref _frames, 0);
                _file = file;
                _error = "";
                var blocks = _blocks = new BlockingCollection<short[]>();
                _writer = new Thread(() => Write(stream, blocks)) { IsBackground = true, Name = "recorder disk" };
                _writer.Start();
                OpenSource();
                _state = RecState.Recording;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                CloseSource();
                _blocks.Add(Array.Empty<short>()); // the writer, if it started, ends
                _state = RecState.Idle;
                Journal.Log("[ERR] the recording did not start: " + ex.Message);
                return ex.Message;
            }
        }
        Journal.Log("[REC] recording into " + _file);
        Tell();
        return "";
    }

    public void Pause()
    {
        lock (_lock) { if (_state != RecState.Recording) return; _state = RecState.Paused; }
        Journal.Log($"[REC] paused at {Elapsed:hh\\:mm\\:ss}");
        Tell();
    }

    public void Resume()
    {
        lock (_lock) { if (_state != RecState.Paused) return; _state = RecState.Recording; }
        Journal.Log("[REC] resumed");
        Tell();
    }

    /// <summary>
    /// The end of a recording: the stream closed, the header written whole, the marker taken off. The file of a recording
    /// worth keeping; null when it was too short or failed - then it is in the Recycle Bin. <paramref name="keep"/> false
    /// (the × of the capsule) sends it to the Recycle Bin too: a cancelled recording is not lost, it is put aside.
    /// </summary>
    public string? Stop(bool keep = true)
    {
        string file;
        lock (_lock)
        {
            if (_state is RecState.Idle or RecState.Saving) return null;
            _state = RecState.Saving;
            file = _file;
        }
        Tell();
        CloseSource();
        _blocks.Add(Array.Empty<short>());
        _writer?.Join();
        var length = Elapsed;
        bool worth = keep && length >= Shortest && _error.Length == 0;
        if (!worth)
        {
            if (Bin.HasBin(file)) Bin.ToBin(new[] { file });
            Journal.Log(keep ? $"[REC] {length.TotalSeconds:0.0} s - too short, the file is in the Recycle Bin" : "[REC] cancelled, the file is in the Recycle Bin");
        }
        else Journal.Log($"[REC] saved {length:hh\\:mm\\:ss}: {file}");
        lock (_lock) { _state = RecState.Idle; _file = ""; }
        Tell();
        return worth ? file : null;
    }

    // ------------------------------------------------------------ the stream

    void OpenSource()
    {
        if (FakeFile is { Length: > 0 } fake)
        {
            _inRate = 0;
            _feeding = true;
            _fake = new Thread(() => Feed(fake)) { IsBackground = true, Name = "recorder fake" };
            _fake.Start();
            return;
        }
        var device = Microphones.Open();
        _capture = new WasapiCapture(device, true, 50);
        _inRate = _capture.WaveFormat.SampleRate;
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            // The microphone went away (unplugged, taken by a program in the exclusive mode): what is recorded is kept.
            if (e.Exception is null || State is RecState.Idle or RecState.Saving) return;
            _error = e.Exception.Message;
            Journal.Log("[ERR] the microphone of the recording stopped: " + e.Exception.Message);
            ThreadPool.QueueUserWorkItem(_ => { _error = ""; Stop(); });
        };
        _capture.StartRecording();
    }

    void CloseSource()
    {
        _feeding = false;
        _fake?.Join();
        _fake = null;
        if (_capture is null) return;
        try { _capture.StopRecording(); } catch { }
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        _capture = null;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var format = _capture?.WaveFormat;
        if (format is null || e.BytesRecorded == 0) return;
        int channels = format.Channels;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        int bytesPerSample = format.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int at = (f * channels + c) * bytesPerSample;
                sum += isFloat ? BitConverter.ToSingle(e.Buffer, at)
                    : bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, at) / 32768f
                    : BitConverter.ToInt32(e.Buffer, at) / 2147483648f;
            }
            mono[f] = sum / channels;
        }
        Take(mono, _inRate);
    }

    void Feed(string wav)
    {
        using var reader = new WaveFileReader(wav);
        ISampleProvider read = reader.ToSampleProvider();
        ISampleProvider source = read.WaveFormat.Channels > 1 ? new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(read) : read;
        int rate = source.WaveFormat.SampleRate;
        int block = rate / 20; // 50 ms, as the shared stream gives it
        var buffer = new float[block];
        var clock = Stopwatch.StartNew();
        long fed = 0;
        while (_feeding)
        {
            int n = source.Read(buffer, 0, block);
            if (n <= 0) { Array.Clear(buffer); n = block; }
            var chunk = new float[n];
            Array.Copy(buffer, chunk, n);
            Take(chunk, rate);
            fed += n;
            int wait = (int)(fed * 1000 / rate - clock.ElapsedMilliseconds);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    /// <summary>A block of the stream: told as a level always, written only while recording, at 48 kHz.</summary>
    void Take(float[] mono, int rate)
    {
        foreach (var s in mono) _peak = Math.Max(_peak, Math.Abs(s));
        _peakFrames += mono.Length;
        if (_peakFrames >= rate / 30)
        {
            Level?.Invoke(Math.Min(1f, _peak));
            _peak = 0;
            _peakFrames = 0;
        }
        if (State != RecState.Recording) return;
        var at48 = rate == Rate ? mono : Resample(mono, rate);
        var pcm = new short[at48.Length];
        for (int i = 0; i < at48.Length; i++) pcm[i] = (short)Math.Clamp((int)Math.Round(at48[i] * 32767f), short.MinValue, short.MaxValue);
        Interlocked.Add(ref _frames, pcm.Length);
        _blocks.Add(pcm);
    }

    float[] Resample(float[] mono, int rate)
    {
        if (_resampler is null || _inRate != rate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(rate, Rate);
            _inRate = rate;
        }
        int needed = _resampler.ResamplePrepare(mono.Length, 1, out var input, out int offset);
        Array.Copy(mono, 0, input, offset, Math.Min(needed, mono.Length));
        var output = new float[(int)((long)mono.Length * Rate / rate) + 64];
        int made = _resampler.ResampleOut(output, 0, mono.Length, output.Length, 1);
        Array.Resize(ref output, made);
        return output;
    }

    /// <summary>The thread of the disk of the recording: the blocks into the file, the header and a flush every 5 s.</summary>
    void Write(FileStream stream, BlockingCollection<short[]> blocks)
    {
        long data = 0;
        var flushed = Stopwatch.StartNew();
        try
        {
            foreach (var pcm in blocks.GetConsumingEnumerable())
            {
                if (pcm.Length == 0) break;
                var bytes = new byte[pcm.Length * 2];
                Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                stream.Write(bytes);
                data += bytes.Length;
                if (flushed.ElapsedMilliseconds >= 5000)
                {
                    Wav.Rewrite(stream, data, Rate);
                    stream.Flush(true);
                    flushed.Restart();
                }
            }
            if (!_abandon)
            {
                Wav.Rewrite(stream, data, Rate);
                stream.Flush(true);
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Journal.Log("[ERR] the recording could not be written: " + ex.Message);
        }
        finally
        {
            var file = stream.Name;
            stream.Dispose();
            if (!_abandon) try { File.Delete(file + ".recording"); } catch { }
            // Blocks left by a writer that broke off are let go.
            while (blocks.TryTake(out _)) { }
        }
    }

    /// <summary>
    /// The recordings a fall of the program left with their marker: the header written by the length of the file, the
    /// marker taken off. The files recovered, for the library.
    /// </summary>
    public static System.Collections.Generic.List<string> Recover(string recordingsRoot)
    {
        var recovered = new System.Collections.Generic.List<string>();
        if (!Directory.Exists(recordingsRoot)) return recovered;
        foreach (var marker in Directory.EnumerateFiles(recordingsRoot, "*.wav.recording", SearchOption.AllDirectories))
        {
            var file = marker[..^".recording".Length];
            try
            {
                if (File.Exists(file))
                {
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        long data = Math.Max(0, stream.Length - Wav.HeaderBytes);
                        data -= data % 2;
                        Wav.Rewrite(stream, data, Rate);
                        stream.Flush(true);
                    }
                    recovered.Add(file);
                    Journal.Log("[REC] recovered after a fall: " + file);
                }
                File.Delete(marker);
            }
            catch (Exception ex) { Journal.Log($"[ERR] the recording {file} was not recovered: {ex.Message}"); }
        }
        return recovered;
    }

    /// <summary>A stand: the recording left as a fall of the program leaves it - the last header that of its last 5 s, the marker in place.</summary>
    public string AbandonForStand()
    {
        string file;
        lock (_lock)
        {
            if (_state is not (RecState.Recording or RecState.Paused)) return "";
            file = _file;
            _state = RecState.Saving;
        }
        _abandon = true;
        CloseSource();
        _blocks.Add(Array.Empty<short>());
        _writer?.Join();
        _abandon = false;
        lock (_lock) { _state = RecState.Idle; _file = ""; }
        Tell();
        return file;
    }

    public void Dispose()
    {
        if (State is RecState.Recording or RecState.Paused) Stop();
        CloseSource();
    }
}

/// <summary>
/// The WAV of the recorder: RIFF with a JUNK chunk kept for RF64, mono PCM16. 80 bytes of header: RIFF, WAVE, JUNK of 28,
/// fmt of 16, data. Past 4 GB the same 80 bytes become RF64 with ds64 - the file need not move.
/// </summary>
public static class Wav
{
    public const int HeaderBytes = 80;

    public static byte[] Header(long dataBytes, int rate)
    {
        var header = new byte[HeaderBytes];
        using var stream = new MemoryStream(header);
        using var w = new BinaryWriter(stream);
        bool big = dataBytes + HeaderBytes - 8 > uint.MaxValue;
        w.Write(big ? "RF64"u8 : "RIFF"u8);
        w.Write(big ? uint.MaxValue : (uint)(dataBytes + HeaderBytes - 8));
        w.Write("WAVE"u8);
        w.Write(big ? "ds64"u8 : "JUNK"u8);
        w.Write(28);
        if (big)
        {
            w.Write(dataBytes + HeaderBytes - 8); // the size of RF64
            w.Write(dataBytes);                   // the size of data
            w.Write(dataBytes / 2);               // the frames, mono PCM16
            w.Write(0);                           // no table
        }
        else w.Write(new byte[28]);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8);
        w.Write(big ? uint.MaxValue : (uint)dataBytes);
        return header;
    }

    /// <summary>The header written again over the start of the file; the stream goes back to where it was.</summary>
    public static void Rewrite(FileStream stream, long dataBytes, int rate)
    {
        long at = stream.Position;
        stream.Position = 0;
        stream.Write(Header(dataBytes, rate));
        stream.Position = at < HeaderBytes ? HeaderBytes : at;
    }
}
