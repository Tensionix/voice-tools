using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AudionVoiceTools.Core;

/// <summary>
/// The models a service offers, asked of the service itself - the lists change every month (the owner, 20.10.2026): GET
/// {address}/models, which OpenAI, xAI and a local server (Ollama, LM Studio) all answer, with the key of the service only
/// when the address is its own. The answer is kept in workspace\model_lists.json and asked again when it is older than a day
/// or the person asks; without the network the kept list stands, and the model in use is always among the choices. What a
/// list is for picks its models out of all of them: the text of the modes, the transcription after the stop, the stream of
/// OpenAI Realtime.
/// </summary>
public static class ModelCatalog
{
    public enum Kind { Chat, Transcribe, Realtime }

    public sealed record Listing(DateTime At, List<string> Ids, string Error);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly object Lock = new();
    static Dictionary<string, Listing>? _kept;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Asked longer ago than this, a list is asked again when it is shown.</summary>
    public static readonly TimeSpan Age = TimeSpan.FromDays(1);

    static string FilePath => Path.Combine(Paths.Root, "workspace", "model_lists.json");

    /// <summary>The address a list is asked at, without its last slash: one list per address.</summary>
    static string KeyOf(string url) => url.Trim().TrimEnd('/');

    /// <summary>The kept list of an address, picked for <paramref name="kind"/>; null - never asked.</summary>
    public static Listing? Kept(string url, Kind kind)
    {
        lock (Lock)
        {
            Read();
            if (!_kept!.TryGetValue(KeyOf(url), out var all)) return null;
            return all with { Ids = Pick(all.Ids, kind, url) };
        }
    }

    /// <summary>The list is old or was never asked: it is worth asking now.</summary>
    public static bool Stale(string url)
    {
        lock (Lock)
        {
            Read();
            return !_kept!.TryGetValue(KeyOf(url), out var all) || DateTime.UtcNow - all.At > Age;
        }
    }

    /// <summary>
    /// The list asked of the service now and kept; the error in words when it did not answer - the kept list then stays as
    /// it was, with the error beside it.
    /// </summary>
    public static async Task<Listing> Refresh(string root, string url)
    {
        var key = KeyOf(url);
        var (ids, error) = await Ask(root, key);
        Listing listing;
        lock (Lock)
        {
            Read();
            listing = error.Length == 0
                ? new Listing(DateTime.UtcNow, ids, "")
                : _kept!.TryGetValue(key, out var was) ? was with { Error = error } : new Listing(DateTime.MinValue, new List<string>(), error);
            _kept![key] = listing;
            Write();
        }
        Journal.Log(error.Length == 0 ? $"[MODELS] {key}: {ids.Count} models" : $"[MODELS] {key}: {error}");
        return listing;
    }

    /// <summary>The list asked of the service, nothing kept: the self-test asks a server of its own through it.</summary>
    internal static async Task<(List<string> Ids, string Error)> Ask(string root, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (new List<string>(), Lang.T("нет адреса"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url + "/models");
        if (TextModes.ProviderOf(url) is { Length: > 0 } provider)
        {
            var key = CloudKeys.Read(root, provider);
            if (key.Length == 0) return (new List<string>(), Lang.F("нет ключа {0}", provider == "xai" ? "xAI" : "OpenAI"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        try
        {
            using var late = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var answer = await Http.SendAsync(request, late.Token);
            var body = await answer.Content.ReadAsStringAsync(late.Token);
            if (!answer.IsSuccessStatusCode) return (new List<string>(), $"HTTP {(int)answer.StatusCode}");
            using var doc = JsonDocument.Parse(body);
            // OpenAI, xAI, LM Studio, Ollama: {"data": [{"id", "created"}]}; the newest first where the service says when.
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return (new List<string>(), Lang.T("сервис ответил не списком моделей"));
            var found = data.EnumerateArray()
                .Where(one => one.ValueKind == JsonValueKind.Object && one.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(one => (Id: one.GetProperty("id").GetString()!, Created: one.TryGetProperty("created", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt64() : 0))
                .OrderByDescending(one => one.Created).ThenBy(one => one.Id, StringComparer.Ordinal).Select(one => one.Id).Distinct().ToList();
            return (found, "");
        }
        catch (OperationCanceledException) { return (new List<string>(), Lang.T("сервис не ответил за 20 с")); }
        catch (HttpRequestException ex) { return (new List<string>(), (ex.InnerException ?? ex).Message.Split('\n')[0]); }
        catch (JsonException) { return (new List<string>(), Lang.T("ответ сервиса не прочитан")); }
    }

    /// <summary>
    /// The models of a list that do <paramref name="kind"/>: what a service lists is everything it has - pictures, voices,
    /// embeddings. A local server lists only what it was given, so all of it goes to the text.
    /// </summary>
    public static List<string> Pick(IEnumerable<string> ids, Kind kind, string url)
    {
        bool known = TextModes.ProviderOf(url).Length > 0;
        bool Has(string id, params string[] parts) => parts.Any(part => id.Contains(part, StringComparison.OrdinalIgnoreCase));
        return ids.Where(id => kind switch
        {
            Kind.Transcribe => Has(id, "transcribe", "whisper") && !Has(id, "realtime", "live"),
            Kind.Realtime => Has(id, "transcribe", "whisper") && !Has(id, "diarize"),
            _ => !known || !Has(id, "embed", "tts", "transcribe", "whisper", "realtime", "audio", "image", "imagine", "dall-e", "sora",
                "moderation", "search", "computer-use", "davinci", "babbage", "vision", "instruct"),
        }).ToList();
    }

    /// <summary>The choices shown: the list, the model in use first when the list does not have it, the usual ones when there is no list.</summary>
    public static List<string> Choices(Listing? listing, string inUse, params string[] usual)
    {
        var choices = new List<string>();
        if (inUse.Trim().Length > 0) choices.Add(inUse.Trim());
        foreach (var id in listing is { Ids.Count: > 0 } ? listing.Ids : usual.ToList())
            if (!choices.Contains(id, StringComparer.Ordinal)) choices.Add(id);
        return choices;
    }

    static void Read()
    {
        if (_kept is not null) return;
        _kept = new Dictionary<string, Listing>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath))
                foreach (var (key, listing) in JsonSerializer.Deserialize<Dictionary<string, Listing>>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new())
                    _kept[key] = listing with { Ids = listing.Ids ?? new List<string>(), Error = listing.Error ?? "" };
        }
        catch { }
    }

    static void Write()
    {
        var temp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(_kept, Json), new UTF8Encoding(false));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch { try { File.Delete(temp); } catch { } }
    }
}
