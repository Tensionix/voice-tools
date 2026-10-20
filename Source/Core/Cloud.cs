using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Dsp;

namespace AudionVoiceTools.Core;

/// <summary>
/// What dictation recognizes with (config\dictation.json): "local" - GigaAM on transcribe.cpp; "xai", "openai_realtime",
/// "elevenlabs" - a stream of the cloud; "openai_batch" - one request after the stop to any endpoint compatible with OpenAI.
/// The language is the cloud's alone: GigaAM is Russian. Kept apart from the look of the window and from the engine of files.
/// </summary>
public sealed class DictationConfig
{
    public string Engine { get; set; } = "local";
    public string Language { get; set; } = "auto";
    public string BatchUrl { get; set; } = "https://api.openai.com/v1";
    public string BatchModel { get; set; } = "gpt-transcribe";
    public string RealtimeModel { get; set; } = "gpt-realtime-whisper";
    /// <summary>The model of this machine (Core\DictationModels.cs, config\dictation_models.json): GigaAM v3 unless another is chosen.</summary>
    public string LocalModel { get; set; } = DictationModels.Default;
    /// <summary>The microphone: an id of Windows, "" - the one by default (Core\Microphones.cs).</summary>
    public string Microphone { get; set; } = "";
    /// <summary>Where the capsule stands: "top" or "bottom" of the screen.</summary>
    public string Panel { get; set; } = "top";
    /// <summary>The strip of the capsule at rest shown; off, the capsule comes only for a dictation or a recording.</summary>
    public bool ShowPanel { get; set; } = true;
    /// <summary>What becomes of the text before the paste (Core\TextModes.cs): "raw" - nothing; the others - a language model.</summary>
    public string Mode { get; set; } = "raw";
    /// <summary>The endpoint of the language model of the modes, compatible with OpenAI; a local server too.</summary>
    public string ModeUrl { get; set; } = "https://api.openai.com/v1";
    /// <summary>The model of the modes: GPT-6 Luna (the owner, 20.10.2026) - at the effort max for the minutes, low for the rest (TextModes).</summary>
    public string ModeModel { get; set; } = "gpt-6-luna";

    public static readonly string[] Engines = { "local", "xai", "openai_realtime", "elevenlabs", "openai_batch" };

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string FilePath(string root) => Path.Combine(root, "config", "dictation.json");

    public static DictationConfig Load(string root)
    {
        DictationConfig? found = null;
        try { if (File.Exists(FilePath(root))) found = JsonSerializer.Deserialize<DictationConfig>(File.ReadAllText(FilePath(root)), Json); }
        catch { }
        found ??= new DictationConfig();
        if (!Engines.Contains(found.Engine)) found.Engine = "local";
        found.Language = string.IsNullOrWhiteSpace(found.Language) ? "auto" : found.Language.Trim();
        found.Microphone ??= "";
        if (DictationModels.All.All(one => one.Id != found.LocalModel)) found.LocalModel = DictationModels.Default;
        if (found.Panel is not ("top" or "bottom")) found.Panel = "top";
        if (!TextModes.Modes.Contains(found.Mode)) found.Mode = "raw";
        found.ModeUrl = string.IsNullOrWhiteSpace(found.ModeUrl) ? "https://api.openai.com/v1" : found.ModeUrl.Trim();
        // "" - a service was chosen and its model is not yet: the model of another service is not put in its place.
        found.ModeModel = (found.ModeModel ?? "gpt-6-luna").Trim();
        return found;
    }

    public void Save(string root)
    {
        var file = FilePath(root);
        var temp = file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
        }
        catch { try { File.Delete(temp); } catch { } }
    }

    /// <summary>The service whose key an engine needs; "" - none.</summary>
    public static string ProviderOf(string engine) => engine switch
    {
        "xai" => "xai", "openai_realtime" or "openai_batch" => "openai", "elevenlabs" => "elevenlabs", _ => "",
    };

    public static string TitleOf(string engine) => engine switch
    {
        "xai" => "xAI", "openai_realtime" => "OpenAI Realtime", "elevenlabs" => "ElevenLabs", "openai_batch" => "OpenAI", _ => "GigaAM",
    };
}

/// <summary>The key of a service as the engine keeps it (system_core\core\credentials.py): its file of config, else its variable. Never told.</summary>
public static class CloudKeys
{
    static readonly Dictionary<string, (string File, string Env)> Files = new()
    {
        ["openai"] = ("api_key_openai.txt", "OPENAI_API_KEY"),
        ["xai"] = ("api_key_xai.txt", "XAI_API_KEY"),
        ["elevenlabs"] = ("api_key_elevenlabs.txt", "ELEVENLABS_API_KEY"),
        // The services of the files alone: the window only asks whether their key is there (FILES, the cloud).
        ["gemini"] = ("api_key_gemini.txt", "GEMINI_API_KEY"),
        ["assemblyai"] = ("api_key_assemblyai.txt", "ASSEMBLYAI_API_KEY"),
        ["gigachat"] = ("api_key_gigachat.txt", "GIGACHAT_AUTH_KEY"),
    };

    public static string Read(string root, string provider)
    {
        if (!Files.TryGetValue(provider, out var where)) return "";
        try
        {
            var file = Path.Combine(root, "config", where.File);
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } key) return key;
        }
        catch { }
        return (Environment.GetEnvironmentVariable(where.Env) ?? "").Trim();
    }

    public static bool Has(string root, string provider) => Read(root, provider).Length > 0;
}

/// <summary>
/// A stream of the cloud for dictation. A connection is opened ahead and kept ready (the spare): the press takes it, the
/// sound goes on its own thread as the microphone gives it, the stop ends it and waits for the text; a new spare is
/// opened behind it at once. A spare the service closed while it waited is opened again, with growing pauses. Nothing of
/// this runs on the window thread, and the take of the microphone is kept whole beside it - a stream that fails costs
/// nothing but the cloud: the local engine recognizes the same take (Session).
/// </summary>
public abstract class CloudStream : IDisposable
{
    public abstract string Id { get; }
    public string Title => DictationConfig.TitleOf(Id);
    protected abstract int Rate { get; }
    protected abstract string Provider { get; }
    protected abstract Uri Address(DictationConfig config);
    protected abstract void Authorize(ClientWebSocketOptions options, string key);

    /// <summary>What a connection says first, once open (the session of OpenAI).</summary>
    protected virtual Task Greet(Link link, DictationConfig config) => Task.CompletedTask;
    protected abstract void Heard(Link link, JsonElement message);
    protected abstract Task SendAudio(Link link, byte[] pcm16);
    protected abstract Task SendEnd(Link link);

    /// <summary>A stand: the address of its own server in place of the service, no key asked.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Why the spare is not there: no key, refused, no network; "" - ready or opening.</summary>
    public string Problem { get; private set; } = "";

    public event Action<string>? Partial;

    readonly string _root;
    readonly DictationConfig _config;
    readonly object _lock = new();
    Link? _spare, _live;
    bool _disposed;
    int _failures;
    WdlResampler? _resampler;
    int _inRate;

    /// <summary>The folder of the program: the dictionary of SETTINGS is read from it.</summary>
    protected string Root => _root;

    protected CloudStream(string root, DictationConfig config)
    {
        _root = root;
        _config = config;
    }

    /// <summary>One connection with what it says: ready, the text committed, the text still moving, the end.</summary>
    public sealed class Link
    {
        public ClientWebSocket Socket { get; } = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<Func<Task>> Sends { get; } = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
        public List<string> Committed { get; } = new();
        public List<string> Chunks { get; } = new();
        public string Moving = "";
        public string Error = "";
        /// <summary>The samples sent, at the rate of the service: how long the take was.</summary>
        public long Samples;
        public bool Closed;
        /// <summary>The stop was sent: the next commit ends the utterance.</summary>
        public bool Ending;
        /// <summary>No key: nothing to open again until a key and a new choice.</summary>
        public bool NoKey;
        public string Joined
        {
            get { lock (Committed) return string.Join(" ", Committed.Append(Moving).Where(part => part.Trim().Length > 0).Select(part => part.Trim())).Trim(); }
        }

        public void Commit(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            lock (Committed)
            {
                if (Committed.Count == 0 || Committed[^1] != text) Committed.Add(text);
                Moving = "";
            }
        }
    }

    protected void TellPartial(Link link)
    {
        if (link == _live && link.Joined is { Length: > 0 } text) Partial?.Invoke(text);
    }

    // ------------------------------------------------------------ the spare

    /// <summary>A spare connection opened ahead, in the background; nothing when one is there or opening.</summary>
    public void Warm()
    {
        lock (_lock)
        {
            if (_disposed || _spare is { Closed: false }) return;
            _spare = Open();
        }
    }

    Link Open()
    {
        var link = new Link();
        // What the connection says first goes first: before any sound a press may queue while it opens.
        link.Sends.Writer.TryWrite(() => Greet(link, _config));
        _ = Task.Run(async () =>
        {
            try
            {
                var key = Endpoint.Length > 0 ? "stand" : CloudKeys.Read(_root, Provider);
                if (key.Length == 0)
                {
                    link.NoKey = true;
                    throw new InvalidOperationException(Lang.F("нет ключа {0} - SETUP, КЛЮЧИ СЕРВИСОВ", DictationConfig.TitleOf(Id)));
                }
                Authorize(link.Socket.Options, key);
                link.Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var address = Endpoint.Length > 0 ? new Uri(Endpoint) : Address(_config);
                await link.Socket.ConnectAsync(address, timeout.Token);
                _ = Task.Run(() => SendLoop(link));
                _ = Task.Run(() => ReadLoop(link));
                await link.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Problem = "";
                _failures = 0;
            }
            catch (Exception ex)
            {
                var why = ex is TimeoutException ? Lang.T("сервис не ответил за 10 с") : Short(ex);
                link.Error = why;
                Problem = why;
                link.Ready.TrySetException(new InvalidOperationException(why));
                link.Done.TrySetResult();
                Close(link);
                Journal.Log($"[CLOUD] {DictationConfig.TitleOf(Id)}: the connection was not opened - {why}");
                Lost(link);
            }
        });
        return link;
    }

    static string Short(Exception ex) => (ex.InnerException ?? ex).Message.Split('\n')[0].Trim();

    async Task SendLoop(Link link)
    {
        try
        {
            await link.Ready.Task;
            await foreach (var send in link.Sends.Reader.ReadAllAsync())
                if (!link.Closed) await send();
        }
        catch (Exception ex)
        {
            link.Error = Short(ex);
            link.Done.TrySetResult();
        }
    }

    async Task ReadLoop(Link link)
    {
        var buffer = new byte[1 << 16];
        var message = new MemoryStream();
        try
        {
            while (link.Socket.State == WebSocketState.Open)
            {
                var got = await link.Socket.ReceiveAsync(buffer, CancellationToken.None);
                if (got.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, got.Count);
                if (!got.EndOfMessage) continue;
                if (got.MessageType == WebSocketMessageType.Text)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(message.ToArray());
                        Heard(link, doc.RootElement);
                    }
                    catch (JsonException) { }
                }
                message.SetLength(0);
            }
        }
        catch (Exception ex) { if (link.Error.Length == 0) link.Error = Short(ex); }
        finally
        {
            link.Ready.TrySetException(new InvalidOperationException(link.Error.Length > 0 ? link.Error : Lang.T("сервис закрыл соединение")));
            link.Done.TrySetResult();
            Close(link);
            Lost(link);
        }
    }

    /// <summary>The spare went away while it waited (an idle timeout of the service): it is opened again, a pause first.</summary>
    void Lost(Link link)
    {
        lock (_lock)
        {
            if (_disposed || link != _spare) return;
            _spare = null;
        }
        // No key is no failure to wait out: it waits for a key and a new choice.
        if (link.NoKey) return;
        int pause = new[] { 1, 3, 10, 30, 60 }[Math.Min(_failures++, 4)];
        Task.Delay(TimeSpan.FromSeconds(pause)).ContinueWith(_ => Warm());
    }

    void Close(Link link)
    {
        if (link.Closed) return;
        link.Closed = true;
        link.Sends.Writer.TryComplete();
        try { if (link.Socket.State == WebSocketState.Open) link.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000); } catch { }
        try { link.Socket.Dispose(); } catch { }
    }

    // ------------------------------------------------------------ one utterance

    /// <summary>The press: the spare becomes the stream of this dictation; a new one is opened if there was none.</summary>
    public void Begin()
    {
        lock (_lock)
        {
            if (_spare is not { Closed: false }) _spare = Open();
            _live = _spare;
            _spare = null;
        }
        _resampler = null;
    }

    /// <summary>Sound of the microphone, mono at the rate of the device: resampled for the service and sent on its thread.</summary>
    public void Feed(float[] mono, int rate)
    {
        var link = _live;
        if (link is null || link.Closed || mono.Length == 0) return;
        var pcm = rate == Rate ? mono : Resample(mono, rate);
        link.Samples += pcm.Length;
        var bytes = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++)
        {
            short s = (short)Math.Clamp((int)Math.Round(pcm[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)s;
            bytes[2 * i + 1] = (byte)(s >> 8);
        }
        link.Sends.Writer.TryWrite(() => SendAudio(link, bytes));
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

    /// <summary>The stop: the end is sent, the text waited for; the stream is closed and a new spare opened behind it.</summary>
    public async Task<string> Finish(TimeSpan wait)
    {
        var link = _live;
        _live = null;
        Warm();
        if (link is null) throw new InvalidOperationException(Lang.T("облако не было подключено"));
        try
        {
            link.Sends.Writer.TryWrite(() => SendEnd(link));
            await Task.WhenAny(link.Done.Task, Task.Delay(wait));
            var text = link.Joined;
            if (text.Length == 0 && link.Error.Length > 0) throw new InvalidOperationException(link.Error);
            if (text.Length == 0 && !link.Done.Task.IsCompleted) throw new TimeoutException(Lang.F("облако не ответило за {0} с", (int)wait.TotalSeconds));
            return Settle(text, link.Samples / (double)Rate);
        }
        finally { Close(link); }
    }

    /// <summary>The text of the stream put right before it is given: as it came, unless the service says otherwise.</summary>
    protected virtual string Settle(string text, double seconds) => text;

    /// <summary>A cancel: the stream is dropped, a new spare is opened.</summary>
    public void Cancel()
    {
        var link = _live;
        _live = null;
        if (link is not null) Close(link);
        Warm();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            if (_spare is { } spare) Close(spare);
            if (_live is { } live) Close(live);
            _spare = _live = null;
        }
    }

    protected static string Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>The text of an event (cloud_realtime.py, _text): "text", else "transcript", a string or an object with "text".</summary>
    protected static string TextOf(JsonElement payload)
    {
        if (payload.TryGetProperty("text", out var text) || payload.TryGetProperty("transcript", out text))
            return (text.ValueKind == JsonValueKind.String ? text.GetString()
                : text.ValueKind == JsonValueKind.Object ? Text(text, "text") : "")?.Trim() ?? "";
        return "";
    }

    protected static string TypeOf(JsonElement payload)
    {
        foreach (var name in new[] { "type", "message_type", "event" })
            if (Text(payload, name) is { Length: > 0 } kind) return kind;
        return "";
    }

    protected static Task SendText(Link link, string json) =>
        link.Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
}

/// <summary>xAI, wss://api.x.ai/v1/stt: binary PCM16 at 16 kHz, {"type": "audio.done"} at the end (cloud_realtime.py, XAIRealtimeLiveTranscriber).</summary>
public sealed class XaiStream : CloudStream
{
    public XaiStream(string root, DictationConfig config) : base(root, config) { }
    public override string Id => "xai";
    protected override int Rate => 16000;
    protected override string Provider => "xai";

    protected override Uri Address(DictationConfig config)
    {
        var query = "sample_rate=16000&encoding=pcm&interim_results=true&smart_turn=0.65&smart_turn_timeout=3000";
        if (config.Language is { Length: > 0 } language && language != "auto") query += "&language=" + Uri.EscapeDataString(language);
        // The dictionary of SETTINGS as the key terms of xAI, up to 100 (2.3, providers\cloud_realtime.py).
        foreach (var term in Vocabulary.Terms(Root)) query += "&keyterm=" + Uri.EscapeDataString(term);
        return new Uri("wss://api.x.ai/v1/stt?" + query);
    }

    /// <summary>Told the key terms, xAI may echo the whole list on a short take: the echo is taken off (Core\Vocabulary.cs).</summary>
    protected override string Settle(string text, double seconds) => Vocabulary.StripEcho(text, Vocabulary.Terms(Root), seconds);

    protected override void Authorize(ClientWebSocketOptions options, string key) => options.SetRequestHeader("Authorization", "Bearer " + key);

    protected override void Heard(Link link, JsonElement payload)
    {
        var kind = TypeOf(payload);
        if (kind == "transcript.created") { link.Ready.TrySetResult(); return; }
        if (kind == "transcript.partial")
        {
            var text = TextOf(payload);
            bool final = payload.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True;
            bool speechFinal = payload.TryGetProperty("speech_final", out var s) && s.ValueKind == JsonValueKind.True;
            if (final)
            {
                if (speechFinal) { CommitCumulative(link, text, string.Join(" ", link.Chunks)); link.Chunks.Clear(); }
                else { link.Commit(text); if (text.Length > 0) link.Chunks.Add(text); }
            }
            else link.Moving = text;
            TellPartial(link);
            return;
        }
        if (kind is "chunk_final" or "transcript.chunk_final") { link.Commit(TextOf(payload)); TellPartial(link); return; }
        if (kind is "speech_final" or "transcript.speech_final") { CommitCumulative(link, TextOf(payload), string.Join(" ", link.Chunks)); link.Chunks.Clear(); return; }
        if (kind == "transcript.done")
        {
            string before;
            lock (link.Committed) before = string.Join(" ", link.Committed);
            CommitCumulative(link, TextOf(payload), before);
            link.Done.TrySetResult();
            return;
        }
        if (kind == "error")
        {
            link.Error = Text(payload, "message") is { Length: > 0 } message ? message : "xAI STT failed";
            link.Done.TrySetResult();
        }
    }

    static void CommitCumulative(Link link, string text, string previous)
    {
        var suffix = NovelSuffix(previous, text);
        if (suffix.Length > 0) link.Commit(suffix);
        else link.Moving = "";
    }

    /// <summary>
    /// Only the text a cumulative final adds (cloud_realtime.py, _novel_suffix): chunk finals of xAI are locked pieces,
    /// a speech_final or transcript.done may hold the whole utterance again. Compared by words, whatever the case.
    /// </summary>
    public static string NovelSuffix(string previous, string cumulative)
    {
        previous = string.Join(" ", (previous ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        cumulative = string.Join(" ", (cumulative ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cumulative.Length == 0) return "";
        if (previous.Length == 0) return cumulative;
        var prev = Regex.Matches(previous, @"\w+").Select(m => m.Value.ToLowerInvariant()).ToList();
        var matches = Regex.Matches(cumulative, @"\w+").ToList();
        var curr = matches.Select(m => m.Value.ToLowerInvariant()).ToList();
        if (prev.Count == 0 || curr.Count == 0) return cumulative == previous ? "" : cumulative;
        if (curr.SequenceEqual(prev) || (curr.Count <= prev.Count && prev.Skip(prev.Count - curr.Count).SequenceEqual(curr))) return "";
        if (curr.Count >= prev.Count && curr.Take(prev.Count).SequenceEqual(prev))
        {
            int end = matches[prev.Count - 1].Index + matches[prev.Count - 1].Length;
            return cumulative[end..].TrimStart(' ', '\t', ',', '.', ';', ':', '—', '-');
        }
        return cumulative;
    }

    protected override Task SendAudio(Link link, byte[] pcm16) => link.Socket.SendAsync(pcm16, WebSocketMessageType.Binary, true, CancellationToken.None);
    protected override Task SendEnd(Link link) => SendText(link, "{\"type\":\"audio.done\"}");
}

/// <summary>
/// OpenAI Realtime, a session of transcription (realtime_session.py, realtime_provider.py): PCM16 at 24 kHz in base64;
/// gpt-realtime-whisper is committed by hand at the stop, the models with the VAD of the server get half a second of
/// silence instead. Not checked with a key of the owner yet.
/// </summary>
public sealed class OpenAiRealtimeStream : CloudStream
{
    public OpenAiRealtimeStream(string root, DictationConfig config) : base(root, config) => _config = config;
    readonly DictationConfig _config;
    public override string Id => "openai_realtime";
    protected override int Rate => 24000;
    protected override string Provider => "openai";
    bool ManualCommit => _config.RealtimeModel == "gpt-realtime-whisper";

    protected override Uri Address(DictationConfig config) => new("wss://api.openai.com/v1/realtime?intent=transcription");
    protected override void Authorize(ClientWebSocketOptions options, string key) => options.SetRequestHeader("Authorization", "Bearer " + key);

    protected override Task Greet(Link link, DictationConfig config)
    {
        var transcription = new Dictionary<string, object> { ["model"] = config.RealtimeModel };
        if (config.Language is { Length: > 0 } language && language != "auto") transcription["language"] = language;
        object? turns = ManualCommit ? null : new Dictionary<string, object> { ["type"] = "server_vad", ["prefix_padding_ms"] = 500, ["silence_duration_ms"] = 350, ["threshold"] = 0.45 };
        var session = new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["session"] = new Dictionary<string, object?>
            {
                ["type"] = "transcription",
                ["audio"] = new Dictionary<string, object?>
                {
                    ["input"] = new Dictionary<string, object?>
                    {
                        ["format"] = new Dictionary<string, object> { ["type"] = "audio/pcm", ["rate"] = 24000 },
                        ["transcription"] = transcription,
                        ["turn_detection"] = turns,
                    },
                },
            },
        };
        // The first send of the connection, once the server has said it is there (session.created).
        return link.Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(session), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    protected override void Heard(Link link, JsonElement payload)
    {
        var kind = TypeOf(payload);
        if (kind is "session.created" or "session.updated" or "transcription_session.created" or "transcription_session.updated") { link.Ready.TrySetResult(); return; }
        if (kind == "conversation.item.input_audio_transcription.delta") { link.Moving += Text(payload, "delta"); TellPartial(link); return; }
        if (kind == "conversation.item.input_audio_transcription.completed")
        {
            link.Commit(Text(payload, "transcript"));
            TellPartial(link);
            if (link.Ending) link.Done.TrySetResult();
            return;
        }
        if (kind == "error")
        {
            link.Error = payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? Text(error, "message") : "OpenAI Realtime failed";
            link.Ready.TrySetException(new InvalidOperationException(link.Error));
            link.Done.TrySetResult();
        }
    }

    protected override Task SendAudio(Link link, byte[] pcm16) =>
        SendText(link, "{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + Convert.ToBase64String(pcm16) + "\"}");

    protected override async Task SendEnd(Link link)
    {
        link.Ending = true;
        if (ManualCommit) await SendText(link, "{\"type\":\"input_audio_buffer.commit\"}");
        else await SendAudio(link, new byte[24000]); // half a second of silence: the VAD of the server ends the turn
    }
}

/// <summary>ElevenLabs, scribe_v2_realtime (cloud_realtime.py, ElevenLabsRealtimeLiveTranscriber): PCM16 at 16 kHz in base64, a commit with silence at the end.</summary>
public sealed class ElevenLabsStream : CloudStream
{
    public ElevenLabsStream(string root, DictationConfig config) : base(root, config) { }
    public override string Id => "elevenlabs";
    protected override int Rate => 16000;
    protected override string Provider => "elevenlabs";

    protected override Uri Address(DictationConfig config)
    {
        var query = "model_id=scribe_v2_realtime&audio_format=pcm_16000&commit_strategy=vad&include_timestamps=false";
        if (config.Language is { Length: > 0 } language && language != "auto") query += "&language_code=" + Uri.EscapeDataString(language);
        // The dictionary of SETTINGS as the key terms of ElevenLabs: up to 50, each up to 20 letters (2.3, providers\cloud_realtime.py).
        foreach (var term in Vocabulary.Terms(Root).Where(one => one.Length <= 20).Take(50)) query += "&keyterms=" + Uri.EscapeDataString(term);
        return new Uri("wss://api.elevenlabs.io/v1/speech-to-text/realtime?" + query);
    }

    protected override void Authorize(ClientWebSocketOptions options, string key) => options.SetRequestHeader("xi-api-key", key);

    protected override void Heard(Link link, JsonElement payload)
    {
        var kind = TypeOf(payload);
        if (kind == "session_started") { link.Ready.TrySetResult(); return; }
        if (kind == "partial_transcript") { link.Moving = TextOf(payload); TellPartial(link); return; }
        if (kind is "committed_transcript" or "committed_transcript_with_timestamps")
        {
            link.Commit(TextOf(payload));
            TellPartial(link);
            if (link.Ending) link.Done.TrySetResult();
            return;
        }
        if (kind.Contains("error") || kind.StartsWith("scribe_", StringComparison.Ordinal))
        {
            link.Error = Text(payload, "message") is { Length: > 0 } message ? message : Text(payload, "error") is { Length: > 0 } error ? error : "ElevenLabs STT failed";
            link.Ready.TrySetException(new InvalidOperationException(link.Error));
            link.Done.TrySetResult();
        }
    }

    Task Chunk(Link link, byte[] pcm16, bool commit) => SendText(link, JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["message_type"] = "input_audio_chunk", ["audio_base_64"] = Convert.ToBase64String(pcm16), ["commit"] = commit, ["sample_rate"] = 16000,
    }));

    protected override Task SendAudio(Link link, byte[] pcm16) => Chunk(link, pcm16, false);

    protected override Task SendEnd(Link link)
    {
        link.Ending = true;
        return Chunk(link, new byte[(int)(16000 * 0.45) * 2], true);
    }
}

/// <summary>
/// One request after the stop to an endpoint compatible with OpenAI (POST {url}/audio/transcriptions): the take as a WAV at
/// 16 kHz, the model of the config. The key of OpenAI goes to api.openai.com alone, as the modes of the text send theirs:
/// another address - a local server - is asked without one.
/// </summary>
public sealed class BatchCloud
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    readonly string _root;
    readonly DictationConfig _config;

    public BatchCloud(string root, DictationConfig config)
    {
        _root = root;
        _config = config;
    }

    public string Title => DictationConfig.TitleOf("openai_batch");

    public async Task<string> Recognize(float[] pcm16k)
    {
        bool own = TextModes.ProviderOf(_config.BatchUrl) == "openai";
        var key = own ? CloudKeys.Read(_root, "openai") : "";
        if (own && key.Length == 0) throw new InvalidOperationException(Lang.T("нет ключа OpenAI - SETUP, КЛЮЧИ СЕРВИСОВ"));
        var wav = new MemoryStream();
        using (var w = new BinaryWriter(wav, Encoding.ASCII, leaveOpen: true))
        {
            int bytes = pcm16k.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            foreach (var s in pcm16k) w.Write((short)Math.Clamp((int)Math.Round(s * 32767f), short.MinValue, short.MaxValue));
        }
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "dictation.wav");
        form.Add(new StringContent(_config.BatchModel), "model");
        if (_config.Language is { Length: > 0 } language && language != "auto") form.Add(new StringContent(language), "language");
        // The dictionary of SETTINGS as the spellings the model is told (2.3, core\terms.py, build_stt_prompt).
        if (Vocabulary.Terms(_root) is { Count: > 0 } terms)
        {
            var prompt = "Exact spellings: " + string.Join(", ", terms);
            form.Add(new StringContent(prompt.Length > 1000 ? prompt[..1000] : prompt), "prompt");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, _config.BatchUrl.TrimEnd('/') + "/audio/transcriptions") { Content = form };
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var answer = await Http.SendAsync(request);
        var body = await answer.Content.ReadAsStringAsync();
        if (!answer.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)answer.StatusCode}: {body.Split('\n')[0]}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var text) ? (text.GetString() ?? "").Trim() : "";
    }
}
