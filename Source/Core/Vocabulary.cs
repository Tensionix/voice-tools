using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AudionVoiceTools.Core;

/// <summary>
/// The dictionary of SETTINGS - terms, abbreviations, shortenings, units (the owner, 19.10.2026): config\dictionary.txt, an entry
/// a line. A word as it is written - «ФГИС ТП», «Kubernetes»: the clouds hear it (xAI and ElevenLabs as their key terms, OpenAI
/// after the stop in its prompt), the modes of the text keep it, the run of files takes it as its term_dictionary
/// (system_core\settings.py). «сказано -> написано» - a replacement made in the text of every dictation before it is pasted,
/// GigaAM's too, which cannot be told words: «километров в час -> км/ч». A line of # is a note. Until the file is written, the
/// dictionary is the one of 2.3 - term_dictionary of config\app_settings.yaml.
/// </summary>
public static class Vocabulary
{
    /// <summary>One line: <paramref name="Said"/> "" - a term, written as <paramref name="Written"/>; else a replacement.</summary>
    public sealed record Entry(string Said, string Written);

    static readonly string[] Arrows = { "->", "→", "=>" };
    static readonly object Lock = new();
    static string _readFrom = "";
    static DateTime _readAt;
    static List<Entry> _entries = new();
    static List<(Regex Said, string Written)> _replacements = new();

    public static string FilePath(string root) => Path.Combine(root, "config", "dictionary.txt");

    /// <summary>The dictionary as the person writes it: the file, else the terms of 2.3, a line each.</summary>
    public static string Text(string root)
    {
        try
        {
            var file = FilePath(root);
            if (File.Exists(file)) return File.ReadAllText(file, Encoding.UTF8);
        }
        catch { }
        return string.Join(Environment.NewLine, TermsOf23(root));
    }

    /// <summary>The dictionary written whole (UTF-8, through a temporary file); the next dictation takes it.</summary>
    public static void Save(string root, string text)
    {
        var file = FilePath(root);
        var temp = file + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(temp, text.Replace("\r\n", "\n").TrimEnd() + "\n", new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
        lock (Lock) _readFrom = "";
    }

    public static IReadOnlyList<Entry> Entries(string root)
    {
        Load(root);
        lock (Lock) return _entries;
    }

    /// <summary>
    /// What the clouds and the models are told to write exactly so: the terms, and what the replacements write when it has a
    /// letter in it - «GitHub Actions», not «км/ч»; once each whatever the case, up to 100 of 50 letters (as 2.3, core\terms.py).
    /// </summary>
    public static IReadOnlyList<string> Terms(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Entries(root).Where(one => one.Said.Length == 0 || one.Written.Any(char.IsLetter)).Select(one => one.Written)
            .Where(term => term.Length <= 50 && seen.Add(term)).Take(100).ToList();
    }

    /// <summary>The replacements of the dictionary made in a text: whole words, whatever the case; the rest of it as it was.</summary>
    public static string Replace(string root, string text)
    {
        Load(root);
        List<(Regex Said, string Written)> replacements;
        lock (Lock) replacements = _replacements;
        foreach (var (said, written) in replacements) text = said.Replace(text, _ => written);
        return text;
    }

    /// <summary>
    /// The echo of the dictionary taken off (2.3, live\controller.py, _strip_implausible_keyterm_echo): told the terms, xAI
    /// may give back the whole list after the real words on a short or almost silent take. Only an exact tail of all the
    /// words of the terms, in their order, at least six of them, is taken off - and only when the take is too short to have
    /// said it: even fast speech needs about 280 ms a word. A dictation that reads the dictionary out stays as it is.
    /// </summary>
    public static string StripEcho(string text, IReadOnlyList<string> terms, double seconds)
    {
        var words = terms.SelectMany(term => Regex.Matches(term, @"\w+").Select(one => one.Value.ToLowerInvariant())).ToList();
        var found = Regex.Matches(text, @"\w+");
        if (words.Count < 6 || found.Count < words.Count) return text;
        if (!found.Skip(found.Count - words.Count).Select(one => one.Value.ToLowerInvariant()).SequenceEqual(words)) return text;
        if (Math.Max(0, seconds) >= words.Count * 0.28) return text;
        return text[..found[found.Count - words.Count].Index].TrimEnd(' ', '\t', '\r', '\n', ',', '.', ';', ':', '—', '-');
    }

    /// <summary>The lines read into entries: a term, or «сказано -> написано»; empty lines, notes (#) and a replacement with a half missing passed by.</summary>
    public static List<Entry> Parse(string text)
    {
        var entries = new List<Entry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var arrow = Arrows.Select(one => (One: one, At: line.IndexOf(one, StringComparison.Ordinal))).Where(one => one.At >= 0).OrderBy(one => one.At).FirstOrDefault();
            if (arrow.One is null) entries.Add(new Entry("", line));
            else if (line[..arrow.At].Trim() is { Length: > 0 } said && line[(arrow.At + arrow.One.Length)..].Trim() is { Length: > 0 } written)
                entries.Add(new Entry(said, written));
        }
        return entries;
    }

    static void Load(string root)
    {
        var file = FilePath(root);
        DateTime stamp;
        try { stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue; }
        catch { stamp = DateTime.MinValue; }
        lock (Lock) if (_readFrom == file && _readAt == stamp) return;
        var entries = Parse(Text(root));
        // The longest first: «гитхаб экшнс» before «гитхаб».
        var replacements = entries.Where(one => one.Said.Length > 0).OrderByDescending(one => one.Said.Length)
            .Select(one => (new Regex(@"(?<![\w-])" + string.Join(@"\s+", one.Said.Split(' ').Select(Regex.Escape)) + @"(?![\w-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), one.Written)).ToList();
        lock (Lock) (_readFrom, _readAt, _entries, _replacements) = (file, stamp, entries, replacements);
    }

    /// <summary>The terms of 2.3: the list term_dictionary of config\app_settings.yaml - «- term» lines, or [a, b] on its line.</summary>
    static List<string> TermsOf23(string root)
    {
        var terms = new List<string>();
        try
        {
            var file = Path.Combine(root, "config", "app_settings.yaml");
            if (!File.Exists(file)) return terms;
            bool inside = false;
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                if (!inside)
                {
                    if (!line.StartsWith("term_dictionary:", StringComparison.Ordinal)) continue;
                    var rest = line["term_dictionary:".Length..].Trim();
                    if (rest.StartsWith('[')) return rest.Trim('[', ']').Split(',').Select(Unquote).Where(one => one.Length > 0).ToList();
                    inside = true;
                    continue;
                }
                var item = line.TrimStart();
                if (item.StartsWith("- ", StringComparison.Ordinal) || item == "-") { if (Unquote(item[1..]) is { Length: > 0 } term) terms.Add(term); }
                else if (item.Length > 0 && !item.StartsWith('#')) break;
            }
        }
        catch { }
        return terms;
    }

    static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
        return value.Trim();
    }
}
