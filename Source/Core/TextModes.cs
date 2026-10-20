using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AudionVoiceTools.Core;

/// <summary>
/// The modes of the text after Letterly AI (the reference of the owner, 19.10.2026), chosen by ⌄ of the capsule and in
/// SETTINGS: the text of the engine as it is, or worked over by a language model after the stop and before the paste -
/// lightly (the punctuation, the fillers: the prompt of 2.3, builtin.cleanup.default), much, as a list, structured, or as the
/// minutes of a meeting - «самый главный режим саммари» (the owner, 19.10.2026), for a recording of RECORDINGS too; «Магия» - the
/// model guesses which of them fits (the owner: «модель пытается догадаться, какой профиль применить»), the window the text
/// goes into among its hints. One
/// request to {url}/chat/completions, which OpenAI, xAI and a local server (Ollama, LM Studio) all speak. A key goes only to
/// its own service; a model that does not answer in time or answers with something else costs nothing - the text of the
/// engine is pasted and the record says why.
/// </summary>
public static partial class TextModes
{
    public static readonly string[] Modes = { "raw", "magic", "light", "strong", "list", "structured", "minutes" };

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>How long the paste waits for the model before it takes the text of the engine; a long text - a second more for every 500 letters, up to three minutes.</summary>
    public static TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(15);

    public static string TitleOf(string mode) => mode switch
    {
        "magic" => "Магия",
        "light" => "Слегка",
        "strong" => "Значительно",
        "list" => "Списком",
        "structured" => "Структурированно",
        "minutes" => "Протокол встречи",
        _ => "Исходный текст",
    };

    public static string TipOf(string mode) => mode switch
    {
        "magic" => "Модель сама выбирает, как оформить: слегка, грамотно, списком, по частям или протоколом - по тексту и по окну, куда он встанет.",
        "light" => "Пунктуация, заглавные буквы, абзацы; убраны только слова-паразиты и случайные повторы. Слова те же.",
        "strong" => "Грамотный письменный текст: исправлены грамматика и оговорки, фразы ясные. Смысл и подробности те же.",
        "list" => "Каждая мысль или задача - отдельный пункт «- ».",
        "structured" => "Смысловые части с короткими заголовками, абзацы и пункты.",
        "minutes" => "Тема, суть, решения, задачи с исполнителями и сроками, открытые вопросы.",
        _ => "Текст движка без изменений - вставляется сразу.",
    };

    /// <summary>The service whose key the address takes: OpenAI, xAI; "" - none is sent (a local server, any other).</summary>
    public static string ProviderOf(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "";
        return uri.Host.ToLowerInvariant() switch
        {
            "api.openai.com" => "openai",
            "api.x.ai" => "xai",
            _ => "",
        };
    }

    /// <summary>
    /// The working over of the text is on (the owner, 20.10.2026): with a service of the cloud for dictation; with the local
    /// model only when config holds a key of OpenAI or xAI.
    /// </summary>
    public static bool Allowed(string root, DictationConfig config) =>
        config.Engine != "local" || CloudKeys.Has(root, "openai") || CloudKeys.Has(root, "xai");

    /// <summary>The model can be asked: an address, a model, and the key when the address is of a service that wants one.</summary>
    public static bool Ready(string root, DictationConfig config) =>
        Allowed(root, config) && Uri.TryCreate(config.ModeUrl.Trim(), UriKind.Absolute, out _) && config.ModeModel.Trim().Length > 0
        && (ProviderOf(config.ModeUrl) is not { Length: > 0 } provider || CloudKeys.Has(root, provider));

    /// <summary>Why the model cannot be asked, in words for the menu and the pages; "" - it can.</summary>
    public static string Problem(string root, DictationConfig config)
    {
        if (!Allowed(root, config)) return Lang.T("с локальной моделью - только при ключе OpenAI или xAI");
        if (!Uri.TryCreate(config.ModeUrl.Trim(), UriKind.Absolute, out _)) return Lang.T("нет адреса модели");
        if (config.ModeModel.Trim().Length == 0) return Lang.T("нет модели");
        var provider = ProviderOf(config.ModeUrl);
        return provider.Length > 0 && !CloudKeys.Has(root, provider) ? Lang.F("нет ключа {0}", provider == "xai" ? "xAI" : "OpenAI") : "";
    }

    /// <summary>
    /// The text worked over in <paramref name="mode"/>; throws with the reason in words when the model is not there, is late,
    /// refuses, or answers with what is not this text worked over. <paramref name="window"/> - the title of the window the text goes
    /// into: «Магия» is told it.
    /// </summary>
    public static async Task<string> Apply(string root, DictationConfig config, string mode, string text, string window = "")
    {
        if (mode == "raw" || !Modes.Contains(mode)) return text;
        if (Problem(root, config) is { Length: > 0 } problem) throw new InvalidOperationException(problem);
        var request0 = new Dictionary<string, object>
        {
            ["model"] = config.ModeModel.Trim(),
            ["messages"] = new object[]
            {
                new { role = "system", content = Prompt(mode) + Window(mode, window) + Dictionary(root) },
                new { role = "user", content = text },
            },
        };
        // GPT-6 (Luna) thinks as long as its work asks: the minutes of a meeting at the effort max, the text of a dictation at low -
        // the paste waits for it. Other models are asked as they always were.
        if (Effort(config.ModeModel, mode) is { Length: > 0 } effort) request0["reasoning_effort"] = effort;
        var body = JsonSerializer.Serialize(request0);
        using var request = new HttpRequestMessage(HttpMethod.Post, config.ModeUrl.Trim().TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (ProviderOf(config.ModeUrl) is { Length: > 0 } provider)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudKeys.Read(root, provider));
        var limit = Wait + TimeSpan.FromSeconds(Math.Min(180 - Wait.TotalSeconds, text.Length / 500.0));
        // The effort max thinks for longer than a paste usually waits: the minutes are waited for two minutes at least.
        if (Effort(config.ModeModel, mode) == "max" && limit < TimeSpan.FromMinutes(2)) limit = TimeSpan.FromMinutes(2);
        using var late = new CancellationTokenSource(limit);
        var watch = Stopwatch.StartNew();
        string answer;
        try
        {
            using var response = await Http.SendAsync(request, late.Token);
            answer = await response.Content.ReadAsStringAsync(late.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {First(answer)}");
        }
        catch (OperationCanceledException) when (late.IsCancellationRequested)
        {
            throw new InvalidOperationException(Lang.F("модель не ответила за {0} с", (int)limit.TotalSeconds));
        }
        catch (HttpRequestException ex) { throw new InvalidOperationException(First((ex.InnerException ?? ex).Message)); }
        string result;
        try
        {
            using var doc = JsonDocument.Parse(answer);
            result = (doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "").Trim();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidOperationException(Lang.T("ответ модели не прочитан"));
        }
        if (result.Length == 0) throw new InvalidOperationException(Lang.T("модель вернула пустой текст"));
        // «Слегка» keeps the words: an answer much longer or much shorter is not this text with its punctuation - an answer to
        // a question of the dictation, a summary - and the text of the engine is pasted instead.
        if (mode == "light" && (result.Length > text.Length * 1.6 + 40 || result.Length < text.Length * 0.5))
            throw new InvalidOperationException(Lang.T("ответ модели не похож на исправленный текст"));
        Journal.Log($"[MODE] {mode}: {text.Length} -> {result.Length} chars in {watch.ElapsedMilliseconds} ms ({config.ModeModel.Trim()})");
        return result;
    }

    /// <summary>The reasoning effort a model is asked at: GPT-6 alone - max for the minutes, low for the rest; "" - not asked.</summary>
    public static string Effort(string model, string mode) =>
        model.Trim().StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase) ? (mode == "minutes" ? "max" : "low") : "";

    static string First(string text) => text.Split('\n')[0].Trim() is { Length: > 160 } line ? line[..160] + "…" : text.Split('\n')[0].Trim();
}
