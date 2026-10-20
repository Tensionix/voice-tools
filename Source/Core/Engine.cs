using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AudionVoiceTools.Core;

/// <summary>
/// The engine of Handy - transcribe.cpp (MIT), Tools\transcribe\transcribe.dll - called directly, without Python.
/// One thread owns the model and the session: a model is loaded once and stays (the policy of 2.3.0 - resident models;
/// a cancel does not unload it), and its first run on Vulkan builds the shaders (6.5 s on Iris Xe), so the warm-up runs
/// when dictation is armed, not on the first phrase. Long audio is cut into pieces of 12..25 s on the quietest 32 ms
/// frame (gigaam_provider._cut_points of 2.3.0, energy of the signal): GigaAM is trained on short utterances.
/// </summary>
public sealed class Engine : IDisposable
{
    const string Dll = "transcribe";
    const int Rate = 16000, Hop = 512;
    const double PieceMin = 12.0, PieceMax = 25.0, SplitMin = 15.0;

    [DllImport(Dll)] static extern IntPtr transcribe_version();
    [DllImport(Dll)] static extern int transcribe_init_backends_default();
    [DllImport(Dll)] static extern IntPtr transcribe_status_string(int status);
    [DllImport(Dll)] static extern void transcribe_model_load_params_init(ref LoadParams p);
    [DllImport(Dll)] static extern int transcribe_open(byte[] path, ref LoadParams load, IntPtr session, out IntPtr outSession);
    [DllImport(Dll)] static extern void transcribe_session_free(IntPtr session);
    [DllImport(Dll)] static extern int transcribe_run(IntPtr session, float[] pcm, int n, IntPtr runParams);
    [DllImport(Dll)] static extern IntPtr transcribe_full_text(IntPtr session);
    [DllImport(Dll)] static extern IntPtr transcribe_get_model(IntPtr session);
    [DllImport(Dll)] static extern IntPtr transcribe_model_backend(IntPtr model);
    [DllImport(Dll)] static extern UIntPtr transcribe_abi_struct_size(int which);

    [StructLayout(LayoutKind.Sequential)]
    struct LoadParams
    {
        public ulong StructSize;
        public int Backend; // 0 auto, 1 cpu, 3 vulkan
        public IntPtr Device;
    }

    static bool _resolver, _backendsReady;
    readonly BlockingCollection<Action> _jobs = new();
    readonly Thread _thread;
    IntPtr _session;

    public string Backend { get; private set; } = "";
    public string ModelName { get; private set; } = "";
    public bool Ready => _session != IntPtr.Zero;

    public Engine(string toolsDir)
    {
        if (!_resolver)
        {
            _resolver = true;
            // The library and its ggml backends lie together; ggml looks for its CPU variants beside itself.
            NativeLibrary.SetDllImportResolver(typeof(Engine).Assembly, (name, _, _) =>
                name == Dll ? NativeLibrary.Load(Path.Combine(toolsDir, "transcribe.dll")) : IntPtr.Zero);
        }
        _thread = new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) job(); })
        { IsBackground = true, Name = "engine" };
        _thread.Start();
    }

    Task<T> On<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Add(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    /// <summary>Loads the model and runs a second of silence through it: the shaders are built here.</summary>
    public Task<string> Load(string modelPath, string backend) => On(() =>
    {
        var watch = Stopwatch.StartNew();
        if (_session != IntPtr.Zero) { transcribe_session_free(_session); _session = IntPtr.Zero; }
        // A dynamic build registers its backends (ggml-vulkan, the CPU variants) from the folder of transcribe.dll
        // once per process, before the first model.
        if (!_backendsReady)
        {
            int init = transcribe_init_backends_default();
            if (init != 0) throw new InvalidOperationException($"no compute backend: {Text(transcribe_status_string(init))}");
            _backendsReady = true;
        }
        var p = new LoadParams();
        transcribe_model_load_params_init(ref p);
        p.Backend = backend == "cpu" ? 1 : backend == "vulkan" ? 3 : 0;
        var path = Encoding.UTF8.GetBytes(modelPath + "\0");
        int status = transcribe_open(path, ref p, IntPtr.Zero, out _session);
        if (status != 0) throw new InvalidOperationException($"model not loaded: {Text(transcribe_status_string(status))}");
        Backend = Text(transcribe_model_backend(transcribe_get_model(_session)));
        ModelName = Path.GetFileNameWithoutExtension(modelPath);
        long loaded = watch.ElapsedMilliseconds;
        // The warm-up runs takes of 1, 5 and 15 seconds: Vulkan builds the shaders of each shape on its first run, and a
        // second of silence alone left the first real phrase 2.7 s on Iris Xe (0.5 s after). Quiet noise, not zeros.
        var random = new Random(7);
        foreach (int seconds in new[] { 1, 5, 15 })
        {
            var noise = new float[seconds * Rate];
            for (int i = 0; i < noise.Length; i++) noise[i] = (float)(random.NextDouble() - 0.5) * 0.002f;
            transcribe_run(_session, noise, noise.Length, IntPtr.Zero);
        }
        return $"{ModelName} on {Backend}: load {loaded} ms, warm-up {watch.ElapsedMilliseconds - loaded} ms (transcribe.cpp {Text(transcribe_version())})";
    });

    /// <summary>16 kHz mono float PCM to text; the time of recognition alone comes back with it.</summary>
    public Task<(string Text, long Ms)> Recognize(float[] pcm) => On(() =>
    {
        if (_session == IntPtr.Zero) throw new InvalidOperationException("the engine has no model");
        var watch = Stopwatch.StartNew();
        var parts = new List<string>();
        foreach (var (start, end) in Pieces(pcm))
        {
            var piece = new float[end - start];
            Array.Copy(pcm, start, piece, 0, piece.Length);
            if (piece.Length == 0) continue;
            int status = transcribe_run(_session, piece, piece.Length, IntPtr.Zero);
            if (status != 0) throw new InvalidOperationException($"recognition failed: {Text(transcribe_status_string(status))}");
            var text = Text(transcribe_full_text(_session)).Trim();
            if (text.Length > 0) parts.Add(text);
        }
        return (string.Join(" ", parts), watch.ElapsedMilliseconds);
    });

    static IEnumerable<(int, int)> Pieces(float[] pcm)
    {
        if (pcm.Length < SplitMin * Rate)
        {
            yield return (0, pcm.Length);
            yield break;
        }
        int frames = pcm.Length / Hop;
        var energy = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * Hop; i < (f + 1) * Hop; i++) sum += pcm[i] * pcm[i];
            energy[f] = (float)Math.Sqrt(sum / Hop);
        }
        int minLen = (int)(PieceMin * Rate), maxLen = (int)(PieceMax * Rate), start = 0;
        while (pcm.Length - start > maxLen)
        {
            int lo = (start + minLen) / Hop, hi = Math.Min(frames, (start + maxLen) / Hop);
            int best = lo;
            for (int f = lo; f < hi; f++) if (energy[f] < energy[best]) best = f;
            int cut = hi > lo ? best * Hop : start + maxLen;
            if (cut <= start) cut = start + maxLen;
            yield return (start, cut);
            start = cut;
        }
        yield return (start, pcm.Length);
    }

    static string Text(IntPtr utf8) => utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";

    public void Dispose()
    {
        _jobs.Add(() =>
        {
            if (_session != IntPtr.Zero) transcribe_session_free(_session);
            _session = IntPtr.Zero;
        });
        _jobs.CompleteAdding();
    }
}
