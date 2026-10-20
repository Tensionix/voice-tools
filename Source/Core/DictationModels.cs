using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AudionVoiceTools.Core;

/// <summary>
/// The models of dictation transcribe.cpp runs on this machine - config\dictation_models.json, the same list Handy offers
/// (Parakeet, Whisper) beside GigaAM, which stays the default: each pinned by its revision and SHA-256, installed by the
/// installer of the engine with -ModelId (a module of SETUP of its own), chosen on DICTATIONS → ADVANCED → LOCAL MODELS.
/// </summary>
public static class DictationModels
{
    public sealed record Model(string Id, string Title, string File, long Bytes, string Module, string Languages, string Licence, string AboutRu, string AboutEn)
    {
        public string Path => System.IO.Path.Combine(Paths.Root, "models", "transcribe", File);

        /// <summary>The file is in place whole: its size is the pinned one (the installer checked its SHA-256).</summary>
        public bool Installed
        {
            get
            {
                try { return new FileInfo(Path) is { Exists: true } file && file.Length == Bytes; }
                catch { return false; }
            }
        }

        public string About => Lang.Code == "ru" ? AboutRu : AboutEn;

        /// <summary>The languages in words: "ru", "en", or how many.</summary>
        public string LanguagesText => Languages switch
        {
            "ru" => Lang.T("русский"),
            "en" => Lang.T("английский"),
            // 2 языка, 5 языков, 31 язык.
            _ when int.TryParse(Languages, out var count) && count % 10 == 1 && count % 100 != 11 => Lang.F("{0} язык", Languages),
            _ when int.TryParse(Languages, out var count) && count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 => Lang.F("{0} языка", Languages),
            _ => Lang.F("{0} языков", Languages),
        };
    }

    public const string Default = "gigaam-v3";

    static readonly Model GigaAm = new(Default, "GigaAM v3", "gigaam-v3-e2e-rnnt-Q8_0.gguf", 273724832, "dictation", "ru", "MIT",
        "Русская речь: точнее всех на русском и быстрая; по умолчанию.", "Russian speech: the most accurate on Russian, and fast; the default.");

    static IReadOnlyList<Model>? _all;

    /// <summary>The engine itself is in place (Tools\transcribe): without it no model runs.</summary>
    public static bool EngineInPlace => File.Exists(System.IO.Path.Combine(Paths.Root, "Tools", "transcribe", "transcribe.dll"));

    /// <summary>The models of the list, GigaAM first; a list that does not read is GigaAM alone.</summary>
    public static IReadOnlyList<Model> All
    {
        get
        {
            if (_all is not null) return _all;
            var found = new List<Model>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(Paths.Root, "config", "dictation_models.json"), Encoding.UTF8));
                foreach (var one in doc.RootElement.GetProperty("models").EnumerateArray())
                {
                    string Text(string name) => one.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                    var about = one.TryGetProperty("about", out var both) ? both : default;
                    string Said(string code) => about.ValueKind == JsonValueKind.Object && about.TryGetProperty(code, out var said) ? said.GetString() ?? "" : "";
                    found.Add(new Model(Text("id"), Text("title"), Text("file"), one.TryGetProperty("bytes", out var bytes) ? bytes.GetInt64() : 0,
                        Text("module"), Text("languages"), Text("licence"), Said("ru"), Said("en")));
                }
            }
            catch { }
            if (!found.Any(one => one.Id == Default)) found.Insert(0, GigaAm);
            return _all = found.Where(one => one.Id.Length > 0 && one.File.Length > 0).ToList();
        }
    }

    /// <summary>The model of an id; an unknown one - GigaAM.</summary>
    public static Model Of(string id) => All.FirstOrDefault(one => one.Id == id) ?? All.First(one => one.Id == Default);
}
