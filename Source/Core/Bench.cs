using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudionVoiceTools.Core;

/// <summary>
/// The measure of the engines of dictation (the plan of 3.0, step 6): the same takes through every engine there is, by
/// the code of the program itself - the engine of Handy on Vulkan and on the processor, whisper-server with the Whisper of
/// the pack ("whisper", once "whisper-turbo": turbo on the processor's pack, large-v2 on the CUDA one - turbo repeats
/// phrases on CUDA), the streams of the clouds fed at the speed of speech, the request after the stop. Measured: the share
/// of errors by letters and by words against the text that was read - Russian alone and with English terms apart - the
/// stop to the text, the first words of a stream, the memory, and runs in a row without a failure. Nothing is shown,
/// nothing pasted.
///
///   AudionVoiceTools.exe --bench [--set DIR | --from-library DAYS [--library DIR]] [--phrases FILE] [--engines a,b] [--cloud] [--runs N] [--to FILE.json]
///   AudionVoiceTools.exe --bench --again FILE.json [--to FILE.json]
///
/// --set: DIR\ID.wav for each phrase of tests\bench\phrases.json (tests\bench\make_voice.ps1 makes such a set by the voices
/// of Windows). --from-library: the dictations of the last DAYS days whose sound is kept, each taken for the phrase its text
/// is nearest to - the phrases are read by the hotkey into any window, in any order. --cloud: the services that have a key,
/// one pass each. Beside the JSON - the same as a table in Russian (.md).
/// </summary>
public static class Bench
{
    public sealed record Phrase(string Id, string Group, string Text);

    sealed record Sample(Phrase Phrase, float[] Pcm, string Source);

    public sealed class Take
    {
        public string Id { get; set; } = "";
        public string Group { get; set; } = "";
        public string Reference { get; set; } = "";
        public string Text { get; set; } = "";
        public string Error { get; set; } = "";
        public double Seconds { get; set; }
        public long Ms { get; set; }
        /// <summary>A stream: the first words in the capsule after the start of the take; -1 - none came.</summary>
        public long FirstWordsMs { get; set; } = -1;
        public double Cer { get; set; }
        public double Wer { get; set; }
    }

    public sealed class EngineReport
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Backend { get; set; } = "";
        public string Note { get; set; } = "";
        public long LoadMs { get; set; }
        public double MemoryMb { get; set; }
        public int Runs { get; set; }
        public int Failures { get; set; }
        /// <summary>Runs whose text differed from the first run of the same take.</summary>
        public int Unstable { get; set; }
        public long MedianMs { get; set; }
        public long P90Ms { get; set; }
        public long MaxMs { get; set; }
        public long FirstWordsMs { get; set; } = -1;
        public double Cer { get; set; }
        public double Wer { get; set; }
        public double CerRu { get; set; }
        public double WerRu { get; set; }
        public double CerMixed { get; set; }
        public double WerMixed { get; set; }
        public List<Take> Takes { get; set; } = new();
    }

    static readonly string[] Local = { "gigaam-vulkan", "gigaam-cpu", "whisper" };
    static readonly string[] Clouds = { "xai", "openai_realtime", "elevenlabs", "openai_batch" };
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    static TextWriter _log = TextWriter.Null;
    static string _folder = "";

    public static int Run(string[] args)
    {
        string root = Paths.Root, set = "", phrasesFile = Path.Combine(root, "tests", "bench", "phrases.json"), to = "", engines = "", again = "", libraryRoot = "";
        int days = 0, runs = 100;
        bool cloud = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i].Trim().Trim('"') : "";
            switch (args[i].ToLowerInvariant())
            {
                case "--set": set = Next(); break;
                case "--from-library": days = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                // --library DIR: the folder whose library\ the dictations come from (a stand's own); the program's by default.
                case "--library": libraryRoot = Next(); break;
                case "--phrases": phrasesFile = Next(); break;
                case "--engines": engines = Next(); break;
                case "--cloud": cloud = true; break;
                case "--runs": runs = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--to": to = Next(); break;
                // --again FILE.json: the takes of a measure counted and drawn again, nothing run - what changed is the counting.
                case "--again": again = Next(); break;
            }
        }
        if (again.Length > 0) return Again(again, to.Length > 0 ? to : again);
        if (to.Length == 0) to = Path.Combine(root, "._runtime", "bench", $"bench-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        _folder = Path.GetDirectoryName(Path.GetFullPath(to))!;
        Directory.CreateDirectory(_folder);
        using var log = new StreamWriter(Path.ChangeExtension(to, ".log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        _log = log;
        try
        {
            var phrases = ReadPhrases(phrasesFile);
            var (samples, source) = days > 0 ? FromLibrary(libraryRoot.Length > 0 ? libraryRoot : root, phrases, days) : (FromSet(set, phrases), "set " + set);
            Say($"[BENCH] {samples.Count} of {phrases.Count} phrases: {source}");
            if (samples.Count == 0) return 1;

            var config = DictationConfig.Load(root);
            var wanted = engines.Length > 0
                ? engines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : Local.Concat(cloud ? Clouds.Where(one => CloudKeys.Has(root, DictationConfig.ProviderOf(one))) : Array.Empty<string>()).ToList();
            if (cloud && engines.Length == 0)
                foreach (var missing in Clouds.Where(one => !CloudKeys.Has(root, DictationConfig.ProviderOf(one))))
                    Say($"[BENCH] {missing}: no key, not measured");

            var reports = new List<EngineReport>();
            foreach (var id in wanted)
            {
                Say($"[BENCH] {id}: begins");
                var report = id switch
                {
                    "gigaam-vulkan" => RunGigaAm(root, samples, runs, "vulkan").GetAwaiter().GetResult(),
                    "gigaam-cpu" => RunGigaAm(root, samples, runs, "cpu").GetAwaiter().GetResult(),
                    // model:ID[:cpu] - a model of config\dictation_models.json on transcribe.cpp, Vulkan unless :cpu is said.
                    _ when id.StartsWith("model:", StringComparison.Ordinal) => RunGigaAm(root, samples, runs, id.EndsWith(":cpu", StringComparison.Ordinal) ? "cpu" : "vulkan",
                        id["model:".Length..].Replace(":cpu", "").Replace(":vulkan", "")).GetAwaiter().GetResult(),
                    "whisper" or "whisper-turbo" => RunWhisper(root, samples, runs).GetAwaiter().GetResult(),
                    "openai_batch" => RunBatch(root, config, samples).GetAwaiter().GetResult(),
                    _ when Clouds.Contains(id) => RunStream(root, config, id, samples).GetAwaiter().GetResult(),
                    _ => null,
                };
                if (report is null) { Say($"[BENCH] {id}: no such engine"); continue; }
                Score(report);
                reports.Add(report);
                Say($"[BENCH] {id}: CER {report.Cer:P1}, WER {report.Wer:P1}, stop to text {report.MedianMs} ms median, {report.Failures} failures of {report.Runs}");
            }

            Write(to, DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Environment.MachineName, source,
                samples.Select(one => one.Phrase).ToList(), config.Language, reports);
            Say($"[BENCH] done: {to}");
            return 0;
        }
        catch (Exception error)
        {
            Say("[BENCH] failed: " + error);
            return 2;
        }
        finally { _log = TextWriter.Null; }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The measure as JSON, and beside it the same as a table in Russian (.md).</summary>
    static void Write(string to, string when, string machine, string source, List<Phrase> phrases, string language, List<EngineReport> reports)
    {
        var summary = new { when, machine, source, samples = phrases.Count, cloudLanguage = language, engines = reports };
        File.WriteAllText(to, JsonSerializer.Serialize(summary, Json), new UTF8Encoding(false));
        File.WriteAllText(Path.ChangeExtension(to, ".md"), Table(reports, phrases, when, machine, source, source.StartsWith("dictations", StringComparison.Ordinal), language), new UTF8Encoding(false));
    }

    /// <summary>The takes of a measure counted again with the counting of now; the times and the runs are as they were.</summary>
    static int Again(string from, string to)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(from));
        var top = doc.RootElement;
        var reports = top.GetProperty("engines").Deserialize<List<EngineReport>>()!;
        foreach (var report in reports)
        {
            if (report.Id is "whisper" or "whisper-turbo") foreach (var take in report.Takes) take.Text = WhisperText(take.Text);
            Score(report);
        }
        var phrases = reports.SelectMany(one => one.Takes).GroupBy(take => take.Id).Select(group => group.First())
            .Select(take => new Phrase(take.Id, take.Group, take.Reference)).ToList();
        Write(to, top.GetProperty("when").GetString() ?? "", top.GetProperty("machine").GetString() ?? "", top.GetProperty("source").GetString() ?? "",
            phrases, top.GetProperty("cloudLanguage").GetString() ?? "", reports);
        try { Console.Out.WriteLine($"[BENCH] counted again: {to}"); } catch { }
        return 0;
    }

    static void Say(string line)
    {
        line = $"{DateTime.Now:HH:mm:ss} {line}";
        try { Console.Out.WriteLine(line); Console.Out.Flush(); } catch { }
        _log.WriteLine(line);
    }

    // ------------------------------------------------------------ the material

    static List<Phrase> ReadPhrases(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.GetProperty("phrases").EnumerateArray()
            .Select(one => new Phrase(one.GetProperty("id").GetString()!, one.GetProperty("group").GetString()!, one.GetProperty("text").GetString()!))
            .ToList();
    }

    static List<Sample> FromSet(string folder, List<Phrase> phrases) =>
        phrases.Where(phrase => File.Exists(Path.Combine(folder, phrase.Id + ".wav")))
            .Select(phrase => new Sample(phrase, ReadWav(Path.Combine(folder, phrase.Id + ".wav")), Path.Combine(folder, phrase.Id + ".wav")))
            .ToList();

    /// <summary>
    /// The dictations of the last days with their sound, each for the phrase its text is nearest to; a long one that holds
    /// several phrases read in a row is cut into them (SplitTake). The latest take of a phrase wins.
    /// </summary>
    static (List<Sample>, string) FromLibrary(string root, List<Phrase> phrases, int days)
    {
        var best = new Dictionary<string, (DateTime Created, float[] Pcm, string Source)>();
        int cut = 0, long_ = 0;
        void Take(Phrase phrase, DateTime created, float[] pcm, string source)
        {
            if (!best.TryGetValue(phrase.Id, out var had) || had.Created <= created) best[phrase.Id] = (created, pcm, source);
        }
        try
        {
            using var library = new Library(root);
            var since = DateTime.Now.AddDays(-days);
            foreach (var record in library.Dictations(most: 5000).Where(one => one.Created >= since && one.Audio.Length > 0 && one.Text.Length > 0))
            {
                var file = Path.Combine(root, record.Audio);
                if (!File.Exists(file)) continue;
                var pcm = ReadWav(file);
                if (pcm.Length > LongTake * 16000)
                {
                    long_++;
                    var found = SplitTake(root, pcm, phrases);
                    Say($"[BENCH] {record.Id}: {pcm.Length / 16000.0:0.0} s, {found.Count} phrases in it");
                    for (int i = 0; i < found.Count; i++)
                        Take(found[i].Phrase, record.Created.AddTicks(i), found[i].Pcm, $"{file} #{i + 1}");
                    cut += found.Count;
                    continue;
                }
                var (phrase, cer) = phrases.Select(one => (one, Cer(one.Text, record.Text))).MinBy(pair => pair.Item2);
                if (cer <= 0.5) Take(phrase, record.Created, pcm, file);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        var samples = phrases.Where(phrase => best.ContainsKey(phrase.Id))
            .Select(phrase => new Sample(phrase, best[phrase.Id].Pcm, best[phrase.Id].Source)).ToList();
        return (samples, $"dictations of the library, the last {days} days" + (long_ > 0 ? $"; {cut} phrases cut from {long_} long ones" : ""));
    }

    /// <summary>A dictation longer than this, in seconds, is taken for several phrases read in a row.</summary>
    const double LongTake = 30;

    /// <summary>
    /// A take of several phrases read one after another: cut at every pause of a quarter of a second, the pieces recognized
    /// by GigaAM, and the neighbouring pieces grouped so that the groups read as the phrases best - a group like no phrase
    /// is left out. The cuts lie in the pauses, so every engine gets the same sound and the grouping favours none.
    /// </summary>
    static List<(Phrase Phrase, float[] Pcm)> SplitTake(string root, float[] pcm, List<Phrase> phrases)
    {
        const int Hop = 320; // 20 ms
        int frames = pcm.Length / Hop;
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * Hop; i < (f + 1) * Hop; i++) sum += pcm[i] * pcm[i];
            db[f] = 10 * Math.Log10(sum / Hop + 1e-12);
        }
        var sorted = db.OrderBy(one => one).ToArray();
        double quiet = Math.Min(sorted[frames / 10] + 8, sorted[frames / 2] - 20);
        var cuts = new List<int> { 0 };
        int run = -1;
        for (int f = 0; f <= frames; f++)
        {
            bool silent = f < frames && db[f] < quiet;
            if (silent && run < 0) run = f;
            else if (!silent && run >= 0)
            {
                if (f - run >= 13) cuts.Add((run + f) / 2 * Hop);
                run = -1;
            }
        }
        cuts.Add(pcm.Length);
        var pieces = new List<(int Start, int End)>();
        for (int i = 0; i + 1 < cuts.Count; i++)
            if (cuts[i + 1] - cuts[i] > 16000 / 6) pieces.Add((cuts[i], cuts[i + 1]));

        var engine = new Engine(Path.Combine(root, "Tools", "transcribe"));
        var texts = new List<string>();
        try
        {
            engine.Load(Path.Combine(root, "models", "transcribe", "gigaam-v3-e2e-rnnt-Q8_0.gguf"), "vulkan").GetAwaiter().GetResult();
            foreach (var (start, end) in pieces) texts.Add(engine.Recognize(pcm[start..end]).GetAwaiter().GetResult().Text);
        }
        finally { engine.Dispose(); }

        // best[j]: the most letters read right by the pieces before j; a group of pieces counts for the phrase it reads as,
        // at most half of its letters wrong; a piece like no phrase counts nothing.
        int n = pieces.Count;
        const int MaxGroup = 10;
        var best = new double[n + 1];
        var from = new int[n + 1];
        var what = new Phrase?[n + 1];
        for (int j = 1; j <= n; j++)
        {
            best[j] = best[j - 1];
            from[j] = j - 1;
            for (int i = Math.Max(0, j - MaxGroup); i < j; i++)
            {
                var text = string.Join(" ", texts.Skip(i).Take(j - i));
                foreach (var phrase in phrases)
                {
                    var (errors, length) = Chars(phrase.Text, text);
                    if (errors > length * 0.5) continue;
                    if (best[i] + length - errors > best[j])
                    {
                        best[j] = best[i] + length - errors;
                        from[j] = i;
                        what[j] = phrase;
                    }
                }
            }
        }
        var groups = new List<(Phrase Phrase, float[] Pcm)>();
        for (int j = n; j > 0; j = from[j])
            if (what[j] is { } phrase) groups.Add((phrase, pcm[pieces[from[j]].Start..pieces[j - 1].End]));
        groups.Reverse();
        return groups;
    }

    /// <summary>Any WAV as 16 kHz mono float: two channels mixed, another rate resampled.</summary>
    static float[] ReadWav(string file)
    {
        using var reader = new WaveFileReader(file);
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels == 2) provider = new StereoToMonoSampleProvider(provider);
        else if (provider.WaveFormat.Channels != 1) throw new InvalidDataException($"{file}: {provider.WaveFormat.Channels} channels");
        if (provider.WaveFormat.SampleRate != 16000) provider = new WdlResamplingSampleProvider(provider, 16000);
        var all = new List<float>();
        var buffer = new float[16000];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.Take(read));
        return all.ToArray();
    }

    // ------------------------------------------------------------ the engines

    static double PrivateMb(Process process)
    {
        process.Refresh();
        return process.PrivateMemorySize64 / 1048576.0;
    }

    /// <summary>The engine of the hot path: its model loaded and warmed as dictation arms it, then the takes again and again.</summary>
    static async Task<EngineReport> RunGigaAm(string root, List<Sample> samples, int runs, string backend, string modelId = DictationModels.Default)
    {
        var model = DictationModels.Of(modelId);
        var report = new EngineReport
        {
            Id = (model.Id == DictationModels.Default ? "gigaam" : model.Id) + "-" + backend,
            Title = $"{model.Title} Q8_0, transcribe.cpp, {(backend == "cpu" ? "процессор" : "Vulkan")}",
        };
        var self = Process.GetCurrentProcess();
        GC.Collect();
        double before = PrivateMb(self), most = 0;
        var engine = new Engine(Path.Combine(root, "Tools", "transcribe"));
        try
        {
            var watch = Stopwatch.StartNew();
            var said = await engine.Load(Path.Combine(root, "models", "transcribe", model.File), backend);
            report.LoadMs = watch.ElapsedMilliseconds;
            report.Backend = engine.Backend;
            report.Note = said;
            Say("[BENCH] " + said);
            var first = new Dictionary<string, string>();
            await Repeat(report, samples, runs, async sample =>
            {
                var (text, ms) = await engine.Recognize(sample.Pcm);
                most = Math.Max(most, PrivateMb(self));
                return (text, ms);
            }, first);
        }
        catch (Exception error) { report.Note = "не загрузился: " + error.Message; }
        finally { engine.Dispose(); }
        report.MemoryMb = Math.Max(0, Math.Round(most - before));
        return report;
    }

    /// <summary>whisper-server of the pack with its Whisper - large-v2 on the CUDA pack, turbo on the processor's - kept
    /// running as 2.3 keeps it, the take posted to /inference.</summary>
    static async Task<EngineReport> RunWhisper(string root, List<Sample> samples, int runs)
    {
        var cuda = Paths.WhisperOnCuda;
        var (name, file) = cuda ? ("large-v2", "ggml-large-v2.bin") : ("large-v3-turbo", "ggml-large-v3-turbo.bin");
        var report = new EngineReport { Id = "whisper", Title = $"Whisper {name}, whisper-server" };
        var exe = Path.Combine(root, "Tools", "whispercpp", "whisper-server.exe");
        var model = Path.Combine(root, "models", file);
        if (!File.Exists(exe) || !File.Exists(model)) { report.Note = $"нет whisper-server или модели {name}"; return report; }
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root,
        };
        foreach (var arg in new[] { "-m", model, "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        var said = new List<string>();
        using var server = Process.Start(start)!;
        server.OutputDataReceived += (_, line) => { if (line.Data is { } text) lock (said) said.Add(text); };
        server.ErrorDataReceived += (_, line) => { if (line.Data is { } text) lock (said) said.Add(text); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            var watch = Stopwatch.StartNew();
            var url = $"http://127.0.0.1:{port}";
            while (true)
            {
                if (server.HasExited) throw new InvalidOperationException($"whisper-server ended with {server.ExitCode}");
                if (watch.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("whisper-server did not come up in 3 min");
                try
                {
                    using var answer = await Http.GetAsync(url + "/");
                    if ((int)answer.StatusCode < 500) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(250);
            }
            await Post(url, new float[16000]);
            report.LoadMs = watch.ElapsedMilliseconds;
            lock (said)
                report.Backend = said.Any(line => line.Contains("CUDA", StringComparison.OrdinalIgnoreCase) && line.Contains("found", StringComparison.OrdinalIgnoreCase)) ? "CUDA"
                    : said.Any(line => line.Contains("ggml_vulkan: Found", StringComparison.OrdinalIgnoreCase)) ? "Vulkan" : "CPU";
            report.Note = $"язык ru, {report.Backend}";
            Say($"[BENCH] whisper-server up on {report.Backend} in {report.LoadMs} ms");
            double most = 0;
            await Repeat(report, samples, runs, async sample =>
            {
                var clock = Stopwatch.StartNew();
                var text = await Post(url, sample.Pcm);
                most = Math.Max(most, PrivateMb(server));
                return (text, clock.ElapsedMilliseconds);
            }, new Dictionary<string, string>());
            report.MemoryMb = Math.Round(most);
        }
        catch (Exception error) { report.Note = "не поднялся: " + error.Message; }
        finally
        {
            try { if (!server.HasExited) server.Kill(entireProcessTree: true); } catch { }
            lock (said) File.WriteAllLines(Path.Combine(_folder, "whisper-server.log"), said);
        }
        return report;
    }

    static async Task<string> Post(string url, float[] pcm)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Wav(pcm));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "take.wav");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        form.Add(new StringContent("ru"), "language");
        using var answer = await Http.PostAsync(url + "/inference", form);
        var body = await answer.Content.ReadAsStringAsync();
        if (!answer.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)answer.StatusCode}: {body.Split('\n')[0]}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var text) ? WhisperText(text.GetString() ?? "") : "";
    }

    /// <summary>
    /// The text of whisper-server is its segments, each on a line of its own, and a segment may end inside a word
    /// ("об\nновления"); a segment that begins a word begins with its space. Joined without the breaks, the text is whole.
    /// </summary>
    public static string WhisperText(string text) => text.Replace("\r", "").Replace("\n", "").Trim();

    static byte[] Wav(float[] pcm)
    {
        var wav = new MemoryStream();
        using var w = new BinaryWriter(wav);
        int bytes = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in pcm) w.Write((short)Math.Clamp((int)Math.Round(s * 32767f), short.MinValue, short.MaxValue));
        w.Flush();
        return wav.ToArray();
    }

    /// <summary>
    /// The takes run in a row until RUNS: the first pass gives the text of each, later passes say whether the engine
    /// holds - a failure, or a text other than the first time.
    /// </summary>
    static async Task Repeat(EngineReport report, List<Sample> samples, int runs, Func<Sample, Task<(string Text, long Ms)>> recognize, Dictionary<string, string> first)
    {
        var times = new List<long>();
        for (int run = 0; run < runs; run++)
        {
            var sample = samples[run % samples.Count];
            string text = "", error = "";
            long ms = 0;
            try { (text, ms) = await recognize(sample); }
            catch (Exception ex) { error = ex.Message; report.Failures++; }
            report.Runs++;
            if (error.Length == 0) times.Add(ms);
            if (run < samples.Count)
            {
                first[sample.Phrase.Id] = text;
                report.Takes.Add(new Take
                {
                    Id = sample.Phrase.Id, Group = sample.Phrase.Group, Reference = sample.Phrase.Text, Text = text, Error = error, Ms = ms,
                    Seconds = Math.Round(sample.Pcm.Length / 16000.0, 1),
                });
            }
            else if (error.Length == 0 && first.TryGetValue(sample.Phrase.Id, out var was) && was != text) report.Unstable++;
            if ((run + 1) % 20 == 0 || run + 1 == runs) Say($"[BENCH] {report.Id}: {run + 1}/{runs}, {report.Failures} failures");
        }
        Times(report, times);
    }

    static void Times(EngineReport report, List<long> times)
    {
        if (times.Count == 0) return;
        var sorted = times.OrderBy(one => one).ToList();
        report.MedianMs = sorted[sorted.Count / 2];
        report.P90Ms = sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.9) - 1)];
        report.MaxMs = sorted[^1];
    }

    /// <summary>
    /// A stream of the cloud as dictation drives it: the spare opened ahead, the take fed at the speed of speech in blocks of
    /// 100 ms, the stop waits for the text as long as the program waits (8 s). A pause between the takes, as a person makes.
    /// </summary>
    static async Task<EngineReport> RunStream(string root, DictationConfig config, string id, List<Sample> samples)
    {
        CloudStream stream = id switch
        {
            "xai" => new XaiStream(root, config),
            "openai_realtime" => new OpenAiRealtimeStream(root, config),
            _ => new ElevenLabsStream(root, config),
        };
        var report = new EngineReport
        {
            Id = id, Title = DictationConfig.TitleOf(id) + " · поток", Backend = "облако",
            Note = $"язык {config.Language}" + (id == "openai_realtime" ? $", {config.RealtimeModel}" : ""),
        };
        var times = new List<long>();
        var firsts = new List<long>();
        try
        {
            stream.Warm();
            await Task.Delay(4000);
            foreach (var sample in samples)
            {
                var clock = Stopwatch.StartNew();
                long firstWords = -1;
                void Heard(string _) { if (firstWords < 0) firstWords = clock.ElapsedMilliseconds; }
                stream.Partial += Heard;
                stream.Begin();
                clock.Restart();
                for (int at = 0; at < sample.Pcm.Length; at += 1600)
                {
                    var block = sample.Pcm.AsSpan(at, Math.Min(1600, sample.Pcm.Length - at)).ToArray();
                    stream.Feed(block, 16000);
                    long due = (at + block.Length) * 1000L / 16000 - clock.ElapsedMilliseconds;
                    if (due > 0) await Task.Delay((int)due);
                }
                var stop = Stopwatch.StartNew();
                string text = "", error = "";
                try { text = await stream.Finish(TimeSpan.FromSeconds(8)); }
                catch (Exception ex) { error = ex.Message; report.Failures++; }
                long ms = stop.ElapsedMilliseconds;
                stream.Partial -= Heard;
                report.Runs++;
                if (error.Length == 0) times.Add(ms);
                if (firstWords >= 0) firsts.Add(firstWords);
                report.Takes.Add(new Take
                {
                    Id = sample.Phrase.Id, Group = sample.Phrase.Group, Reference = sample.Phrase.Text, Text = text, Error = error, Ms = ms,
                    FirstWordsMs = firstWords, Seconds = Math.Round(sample.Pcm.Length / 16000.0, 1),
                });
                Say($"[BENCH] {id} {sample.Phrase.Id}: {ms} ms{(error.Length > 0 ? " - " + error : "")}");
                await Task.Delay(1500);
            }
        }
        finally { stream.Dispose(); }
        Times(report, times);
        if (firsts.Count > 0) report.FirstWordsMs = firsts.OrderBy(one => one).ElementAt(firsts.Count / 2);
        return report;
    }

    /// <summary>The request after the stop to the endpoint of the config, the take sent whole.</summary>
    static async Task<EngineReport> RunBatch(string root, DictationConfig config, List<Sample> samples)
    {
        var batch = new BatchCloud(root, config);
        var report = new EngineReport
        {
            Id = "openai_batch", Title = $"{batch.Title} · после остановки", Backend = "облако", Note = $"{config.BatchModel}, язык {config.Language}",
        };
        var times = new List<long>();
        foreach (var sample in samples)
        {
            var clock = Stopwatch.StartNew();
            string text = "", error = "";
            try { text = await batch.Recognize(sample.Pcm); }
            catch (Exception ex) { error = ex.Message; report.Failures++; }
            long ms = clock.ElapsedMilliseconds;
            report.Runs++;
            if (error.Length == 0) times.Add(ms);
            report.Takes.Add(new Take
            {
                Id = sample.Phrase.Id, Group = sample.Phrase.Group, Reference = sample.Phrase.Text, Text = text, Error = error, Ms = ms,
                Seconds = Math.Round(sample.Pcm.Length / 16000.0, 1),
            });
            Say($"[BENCH] openai_batch {sample.Phrase.Id}: {ms} ms{(error.Length > 0 ? " - " + error : "")}");
        }
        Times(report, times);
        return report;
    }

    // ------------------------------------------------------------ the errors

    /// <summary>Lower case, ё as е, every sign but letters and digits a space: what is counted is the words, not the punctuation.</summary>
    public static string Normal(string text)
    {
        var chars = text.ToLowerInvariant().Replace('ё', 'е').Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    static int Distance<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : IEquatable<T>
    {
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (int j = 0; j <= b.Count; j++) previous[j] = j;
        for (int i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Count; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1].Equals(b[j - 1]) ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Count];
    }

    static (int Errors, int Length) Chars(string reference, string text)
    {
        var r = Normal(reference).ToCharArray();
        return (Distance(r, Normal(text).ToCharArray()), r.Length);
    }

    static (int Errors, int Length) Words(string reference, string text)
    {
        var r = Normal(reference).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (Distance(r, Normal(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)), r.Length);
    }

    public static double Cer(string reference, string text)
    {
        var (errors, length) = Chars(reference, text);
        return length == 0 ? 0 : (double)errors / length;
    }

    public static double Wer(string reference, string text)
    {
        var (errors, length) = Words(reference, text);
        return length == 0 ? 0 : (double)errors / length;
    }

    /// <summary>The errors of an engine summed over its takes and divided by the length read - a take that failed counts whole.</summary>
    static void Score(EngineReport report)
    {
        foreach (var take in report.Takes)
        {
            take.Cer = Math.Round(Cer(take.Reference, take.Text), 4);
            take.Wer = Math.Round(Wer(take.Reference, take.Text), 4);
        }
        (double, double) Of(IEnumerable<Take> takes)
        {
            var list = takes.ToList();
            var chars = list.Select(one => Chars(one.Reference, one.Text)).ToList();
            var words = list.Select(one => Words(one.Reference, one.Text)).ToList();
            double c = chars.Sum(one => one.Length) is > 0 and var cl ? (double)chars.Sum(one => one.Errors) / cl : 0;
            double w = words.Sum(one => one.Length) is > 0 and var wl ? (double)words.Sum(one => one.Errors) / wl : 0;
            return (Math.Round(c, 4), Math.Round(w, 4));
        }
        (report.Cer, report.Wer) = Of(report.Takes);
        (report.CerRu, report.WerRu) = Of(report.Takes.Where(one => one.Group == "ru"));
        (report.CerMixed, report.WerMixed) = Of(report.Takes.Where(one => one.Group == "mixed"));
    }

    // ------------------------------------------------------------ the table

    static string Percent(double share) => (share * 100).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " %";

    static string Table(List<EngineReport> reports, List<Phrase> phrases, string when, string machine, string source, bool owner, string language)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var md = new StringBuilder();
        md.AppendLine("# Замер движков диктовки");
        md.AppendLine();
        string date = DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToString("dd.MM.yyyy HH:mm", ru) : when;
        md.AppendLine($"{date}, {machine}. Фраз: {phrases.Count} — " +
                      $"русских {phrases.Count(one => one.Group == "ru")}, с английскими терминами {phrases.Count(one => one.Group == "mixed")}.");
        md.AppendLine();
        md.AppendLine(owner
            ? "Звук — диктовки из библиотеки программы, каждая отнесена к ближайшей фразе по тексту."
            : $"Звук — голоса Windows (Irina и Zira для английских терминов), `{source}`. Это проверка скорости и самого замера: о качестве на живой речи такой звук не говорит.");
        md.AppendLine();
        md.AppendLine("| Движок | Ошибки по буквам: рус / смеш | Ошибки по словам: рус / смеш | Стоп → текст: медиана / 90 % / худшее | Первые слова | Память | Прогонов / сбоев / иной текст |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in reports)
        {
            string time = r.Runs == r.Failures ? "—" : $"{r.MedianMs} / {r.P90Ms} / {r.MaxMs} мс";
            string first = r.FirstWordsMs >= 0 ? $"{r.FirstWordsMs} мс" : "—";
            string memory = r.MemoryMb > 0 ? $"{r.MemoryMb.ToString("0", ru)} МБ" : "—";
            md.AppendLine($"| {r.Title} | {Percent(r.CerRu)} / {Percent(r.CerMixed)} | {Percent(r.WerRu)} / {Percent(r.WerMixed)} | {time} | {first} | {memory} | {r.Runs} / {r.Failures} / {r.Unstable} |");
        }
        md.AppendLine();
        md.AppendLine("- **Ошибки** — правки, нужные, чтобы из распознанного получить прочитанное, делённые на длину прочитанного. Регистр, «ё» и знаки препинания не считаются. Сбой считается целой фразой.");
        md.AppendLine("- **Стоп → текст**: у движков машины — само распознавание; у потоков — от остановки до последнего текста сервиса; у запроса после остановки — отправка и ответ.");
        md.AppendLine("- **Первые слова** — у потоков: от начала фразы до первых слов в капсуле.");
        md.AppendLine("- **Память** — прирост памяти процесса с моделью; у whisper-server — память его процесса.");
        md.AppendLine("- **Иной текст** — повторный прогон той же фразы дал другой текст.");
        md.AppendLine($"- Язык облаков — `{language}` из настроек диктовки, Whisper — `ru`.");
        foreach (var r in reports)
        {
            md.AppendLine();
            md.AppendLine($"## {r.Title}");
            md.AppendLine();
            if (r.Note.Length > 0) md.AppendLine(r.Note + (r.LoadMs > 0 ? $"; загрузка {r.LoadMs} мс." : "."));
            md.AppendLine();
            md.AppendLine("| Фраза | Звук | Распознано | Ошибки по словам | мс |");
            md.AppendLine("|---|---|---|---|---|");
            foreach (var take in r.Takes)
            {
                var said = take.Error.Length > 0 ? $"*сбой: {take.Error}*" : take.Text.Length == 0 ? "*пусто*" : take.Text;
                md.AppendLine($"| {take.Id} | {take.Seconds.ToString("0.0", ru)} с | {said.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|")} | {Percent(take.Wer)} | {take.Ms} |");
            }
        }
        md.AppendLine();
        md.AppendLine("## Что читалось");
        md.AppendLine();
        foreach (var phrase in phrases) md.AppendLine($"- **{phrase.Id}.** {phrase.Text}");
        return md.ToString();
    }
}
