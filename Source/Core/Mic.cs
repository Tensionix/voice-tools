using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudionVoiceTools.Core;

/// <summary>
/// The microphone: WASAPI in shared mode, driven by its own capture thread. 2.3.0 restarted PortAudio and enumerated
/// every device on the window thread at each start of dictation; here the device is taken when dictation is armed and
/// opening a stream is the only work of a start - on the thread of the control, never on the window thread. The sound
/// is kept as float mono at the rate of the device; the level (the peak of the last 33 ms) is told from the capture
/// thread; at the stop the take is resampled to 16 kHz for the engine.
///
/// A stand feeds a WAV in place of the microphone (FakeFile), at the speed of real time.
/// </summary>
public sealed class Mic : IDisposable
{
    public const int EngineRate = 16000;

    /// <summary>The peak of the last 33 ms, 0..1 - told from the capture thread.</summary>
    public event Action<float>? Level;

    /// <summary>Every block of a take as it comes, mono at the rate of the device, the pre-roll first - a stream of the cloud
    /// takes them. Told on the capture thread (the pre-roll on the thread that starts the take).</summary>
    public event Action<float[], int>? Collected;

    public string? FakeFile { get; init; }
    public string DeviceName { get; private set; } = "";

    /// <summary>
    /// Keep the stream open while dictation is armed (Handy: "always-on microphone"). Opening a WASAPI stream on the
    /// digital microphones of this laptop takes 220-430 ms, and a take that starts only then loses the first syllables;
    /// an open stream starts at once and gives the take the last PreRollMs before the press too. The price: Windows shows
    /// the microphone in use for as long as dictation is on.
    /// </summary>
    public bool KeepOpen { get; init; }
    public int PreRollMs { get; init; } = 250;

    readonly object _lock = new();
    readonly List<float> _take = new();
    readonly Queue<float[]> _ring = new();
    int _ringSamples;
    int _rate = 48000;
    WasapiCapture? _capture;
    Thread? _fake;
    volatile bool _running, _collecting;
    float _peak;
    int _peakSamples;

    /// <summary>Opens the stream when it is to be kept open: called when dictation is armed.</summary>
    public void Prepare()
    {
        if (!KeepOpen || FakeFile is { Length: > 0 } || _capture != null) return;
        Open();
    }

    /// <summary>Starts collecting - opening the stream first unless it is kept open. Returns the milliseconds until the
    /// microphone was collecting.</summary>
    public long Start()
    {
        var watch = Stopwatch.StartNew();
        float[] preRoll;
        lock (_lock)
        {
            _take.Clear();
            // The kept stream gives what it heard just before the press.
            foreach (var block in _ring) _take.AddRange(block);
            preRoll = _take.ToArray();
            _ring.Clear();
            _ringSamples = 0;
            _collecting = true;
        }
        if (preRoll.Length > 0) Collected?.Invoke(preRoll, _rate);
        if (FakeFile is { Length: > 0 })
        {
            _running = true;
            _fake = new Thread(FeedFake) { IsBackground = true, Name = "fake-mic" };
            _fake.Start();
            DeviceName = "stand: " + Path.GetFileName(FakeFile);
            return watch.ElapsedMilliseconds;
        }
        if (_capture == null) Open();
        return watch.ElapsedMilliseconds;
    }

    void Open()
    {
        var device = Microphones.Open();
        DeviceName = device.FriendlyName;
        _capture = new WasapiCapture(device, true, 20);
        _rate = _capture.WaveFormat.SampleRate;
        _capture.DataAvailable += OnData;
        _running = true;
        _capture.StartRecording();
    }

    /// <summary>Another microphone chosen: a stream kept open is opened again on it. On the thread of the control, never during a take.</summary>
    public void Reopen()
    {
        if (FakeFile is { Length: > 0 } || _collecting) return;
        bool open = _capture != null;
        Close();
        if (open && KeepOpen) Open();
    }

    void Close()
    {
        _running = false;
        if (_capture == null) return;
        _capture.StopRecording();
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        _capture = null;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var format = _capture!.WaveFormat;
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
        Add(mono);
    }

    void FeedFake()
    {
        using var reader = new WaveFileReader(FakeFile!);
        ISampleProvider read = reader.ToSampleProvider();
        ISampleProvider source = read.WaveFormat.Channels > 1 ? new StereoToMonoSampleProvider(read) : read;
        _rate = source.WaveFormat.SampleRate;
        int block = _rate / 50; // 20 ms
        var buffer = new float[block];
        var clock = Stopwatch.StartNew();
        long fed = 0;
        while (_running)
        {
            int n = source.Read(buffer, 0, block);
            if (n <= 0)
            {
                // After the file: silence, as a room is quiet after a phrase.
                Array.Clear(buffer);
                n = block;
            }
            var chunk = new float[n];
            Array.Copy(buffer, chunk, n);
            Add(chunk);
            fed += n;
            long due = fed * 1000 / _rate;
            int wait = (int)(due - clock.ElapsedMilliseconds);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    void Add(float[] mono)
    {
        lock (_lock)
        {
            if (_collecting) _take.AddRange(mono);
            else
            {
                // Between takes the kept stream holds only the last PreRollMs.
                _ring.Enqueue(mono);
                _ringSamples += mono.Length;
                while (_ring.Count > 1 && _ringSamples - _ring.Peek().Length >= _rate * PreRollMs / 1000)
                    _ringSamples -= _ring.Dequeue().Length;
                return;
            }
        }
        if (_collecting) Collected?.Invoke(mono, _rate);
        foreach (var s in mono) _peak = Math.Max(_peak, Math.Abs(s));
        _peakSamples += mono.Length;
        if (_peakSamples >= _rate / 30)
        {
            Level?.Invoke(Math.Min(1f, _peak));
            _peak = 0;
            _peakSamples = 0;
        }
    }

    /// <summary>Stops collecting and gives the take at 16 kHz, with the take at the rate of the device for the journal.</summary>
    public (float[] Pcm16k, float[] Native, int NativeRate) Stop()
    {
        float[] native;
        lock (_lock)
        {
            _collecting = false;
            native = _take.ToArray();
        }
        if (_fake != null)
        {
            _running = false;
            _fake.Join(500);
            _fake = null;
        }
        else if (!KeepOpen) Close();
        return (Resample(native, _rate), native, _rate);
    }

    static float[] Resample(float[] pcm, int rate)
    {
        if (rate == EngineRate || pcm.Length == 0) return pcm;
        var resampler = new WdlResamplingSampleProvider(new ArraySource(pcm, rate), EngineRate);
        var outBuf = new float[(int)((long)pcm.Length * EngineRate / rate) + EngineRate];
        int total = 0, n;
        while ((n = resampler.Read(outBuf, total, Math.Min(4096, outBuf.Length - total))) > 0 && total < outBuf.Length) total += n;
        Array.Resize(ref outBuf, total);
        return outBuf;
    }

    sealed class ArraySource(float[] data, int rate) : ISampleProvider
    {
        int _at;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _at);
            if (n <= 0) return 0;
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }
    }

    public void Dispose()
    {
        _collecting = false;
        Close();
    }
}
