using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AudionVoiceTools.Core;

/// <summary>What a short call of the door answered.</summary>
public sealed record Reply(int Code, string Out, string Err)
{
    public bool Ok => Code == 0;
}

/// <summary>A service line of a run ("::voice::{...}"): the start, a file and its state, how far the work is, the end.</summary>
public sealed class DoorEvent
{
    /// <summary>started, file, progress, install (the course of an installer of SETUP) or finished; "" for a line of a kind this window does not know.</summary>
    public string Kind { get; init; } = "";
    /// <summary>The share of the whole run done, 0..1.</summary>
    public double Progress { get; init; } = -1;
    /// <summary>The share of the file under way, 0..1.</summary>
    public double Share { get; init; } = -1;
    /// <summary>The file a line is about.</summary>
    public string Path { get; init; } = "";
    /// <summary>Of a file: processing, completed, failed, skipped.</summary>
    public string State { get; init; } = "";
    public int Index { get; init; } = -1;
    public int Count { get; init; }
    /// <summary>done, failed, refused or stopped.</summary>
    public string Outcome { get; init; } = "";

    // The course of an installer of SETUP: its step, the file it downloads, how much of it, how fast, how long still.
    public string Module { get; init; } = "";
    public int Step { get; init; }
    public int Steps { get; init; }
    public string Label { get; init; } = "";
    public string Item { get; init; } = "";
    public double BytesDone { get; init; }
    public double BytesTotal { get; init; }
    public double Speed { get; init; }
    /// <summary>Seconds left of the download under way; -1 - not known.</summary>
    public double Eta { get; init; } = -1;
}

/// <summary>
/// The door of the engine for files (system_core\cli.py, voice_cli.cmd). A button of the window does nothing a console
/// cannot: it runs voice_cli.cmd with the words a person would type, and that line is written into the journal. The
/// window starts the Python of the project itself, the way the wrapper does - a path is quoted once, by the system.
/// </summary>
public static class Door
{
    public const string Control = "::voice::";
    public const int ExitDone = 0, ExitFailed = 1, ExitUsage = 2, ExitStopped = 130;

    /// <summary>Why the door cannot be called, in the words of the window; "" when it can.</summary>
    public static string Trouble =>
        !File.Exists(Paths.DoorScript) ? Lang.F("нет файла: {0}", Paths.DoorScript)
        : Paths.Python.Length == 0 ? Lang.F("нет Python программы ({0}): сначала соберите окружение - builder_main.cmd", System.IO.Path.Combine(Paths.Root, @"runtime\python.exe"))
        : "";

    public static bool Ready => Trouble.Length == 0;

    // What voice_cli.cmd does before it calls Python: UTF-8 everywhere, lines at once (-u), the door started as a file.
    static IEnumerable<string> Head => new[] { "-X", "utf8", "-u", Paths.DoorScript };

    public static string ResultFile => System.IO.Path.Combine(Paths.Session, "result.json");
    /// <summary>The stop signal: the file appears - the run stops before its next chunk.</summary>
    public static string StopFile => System.IO.Path.Combine(Paths.Session, "stop.flag");

    /// <summary>A word of a line as a console reads it back: quoted when it holds a space or a sign a console treats as its own.</summary>
    static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"', '&', '(', ')', '^', '=', ';', ',', '%', '!', '<', '>', '|' }) < 0) return value;
        var text = new StringBuilder("\"");
        int slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') text.Append('\\', slashes * 2 + 1).Append('"');
            else text.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>The same run as a person would type it: written into the journal at every start.</summary>
    public static string RunLine(IReadOnlyList<string> inputs, IReadOnlyDictionary<string, string> sets)
    {
        var words = new List<string> { Paths.DoorName, "run" };
        words.AddRange(inputs.Select(Paths.Short));
        foreach (var (name, value) in sets) { words.Add("--set"); words.Add(name + "=" + value); }
        return string.Join(" ", words.Select(Quote));
    }

    /// <summary>A run of files: their sidecars next to each source; the outcome into the session, the stop by its file.</summary>
    public static Launch Run(IReadOnlyList<string> inputs, IReadOnlyDictionary<string, string> sets)
    {
        var args = Head.Concat(new[] { "run" }).ToList();
        args.AddRange(inputs);
        foreach (var (name, value) in sets) { args.Add("--set"); args.Add(name + "=" + value); }
        args.AddRange(new[] { "--result", ResultFile, "--cancel-file", StopFile });
        return new Launch { Exe = Paths.Python, Args = args };
    }

    /// <summary>The install of a module of SETUP as a person would type it.</summary>
    public static string InstallLine(string module, bool update = false) =>
        string.Join(" ", new[] { Paths.DoorName, "install", module }.Concat(update ? new[] { "--update" } : Array.Empty<string>()).Select(Quote));

    /// <summary>A module of SETUP put in place by its installer; the outcome into the session, the stop by its file.</summary>
    public static Launch Install(string module, bool update = false) => new()
    {
        Exe = Paths.Python,
        Args = Head.Concat(new[] { "install", module }).Concat(update ? new[] { "--update" } : Array.Empty<string>())
            .Concat(new[] { "--result", ResultFile, "--cancel-file", StopFile }).ToList(),
    };

    /// <summary>The files of the last run are cleared away; the reason when that did not work.</summary>
    public static string Prepare()
    {
        try
        {
            foreach (var file in new[] { ResultFile, StopFile })
                if (File.Exists(file)) File.Delete(file);
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Asks the running run to stop. "" when the signal is written; the reason when it could not be.</summary>
    public static string AskToStop()
    {
        try { File.WriteAllText(StopFile, DateTime.Now.ToString("O")); return ""; }
        catch (Exception ex) { return ex.Message.Length > 0 ? ex.Message : ex.GetType().Name; }
    }

    /// <summary>The service line of a run read; null when the line is an ordinary one.</summary>
    public static DoorEvent? Read(string line)
    {
        if (!line.StartsWith(Control, StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line[Control.Length..]);
            var root = doc.RootElement;
            static string Text(JsonElement owner, string name) => owner.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            static double Number(JsonElement owner, string name) => owner.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : -1;
            if (root.TryGetProperty("started", out _)) return new DoorEvent { Kind = "started" };
            if (root.TryGetProperty("finished", out var finished))
                return new DoorEvent { Kind = "finished", Outcome = Text(finished, "outcome") };
            if (root.TryGetProperty("file", out var file))
                return new DoorEvent { Kind = "file", Path = Text(file, "path"), State = Text(file, "state") };
            if (root.TryGetProperty("progress", out var course) && course.ValueKind == JsonValueKind.Object && course.TryGetProperty("module", out _))
                return new DoorEvent
                {
                    Kind = "install", Module = Text(course, "module"), Step = (int)Math.Max(0, Number(course, "step")), Steps = (int)Math.Max(0, Number(course, "steps")),
                    Label = Text(course, "label"), Item = Text(course, "item"), Share = Number(course, "share"),
                    BytesDone = Math.Max(0, Number(course, "bytes_done")), BytesTotal = Math.Max(0, Number(course, "bytes_total")),
                    Speed = Math.Max(0, Number(course, "speed")), Eta = Number(course, "eta"),
                };
            if (root.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Object)
                return new DoorEvent
                {
                    Kind = "progress", Path = Text(progress, "path"), Progress = Number(progress, "total"), Share = Number(progress, "share"),
                    Index = (int)Number(progress, "index"), Count = (int)Math.Max(0, Number(progress, "count")),
                };
            return new DoorEvent { Kind = "" };
        }
        catch { return new DoorEvent { Kind = "" }; }
    }

    // ------------------------------------------------------------ short calls

    /// <summary>A call that answers and ends - the description: the door is run and its output read whole. Never throws.</summary>
    public static async Task<Reply> Call(IEnumerable<string> words, int timeoutMs = 180_000, string? input = null)
    {
        if (!Ready) return new Reply(-1, "", Trouble);
        var info = new ProcessStartInfo(Paths.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            // UTF-8 without its mark: a BOM would stand at the start of a key.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Paths.Root,
        };
        foreach (var word in Head.Concat(words)) info.ArgumentList.Add(word);
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("the process was not created");
            // A key goes on the standard input, never into the line of the command: no list of processes shows it.
            try
            {
                if (input is not null) { await process.StandardInput.WriteLineAsync(input); await process.StandardInput.FlushAsync(); }
                process.StandardInput.Close();
            }
            catch { }
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(timeoutMs)) != exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new Reply(-1, "", Lang.F("консольная команда не ответила за {0} с", timeoutMs / 1000));
            }
            var said = Task.WhenAll(output, errors);
            if (await Task.WhenAny(said, Task.Delay(5_000)) != said)
                return new Reply(process.ExitCode, output.IsCompletedSuccessfully ? output.Result : "", errors.IsCompletedSuccessfully ? errors.Result : "");
            return new Reply(process.ExitCode, await output, await errors);
        }
        catch (Exception ex) { return new Reply(-1, "", ex.Message); }
    }

    static string Lines(string text) => string.Join(" ", text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    /// <summary>The last line of an answer that is JSON: the door prints one, and a service may have printed before it.</summary>
    static string LastJson(string output) =>
        output.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.StartsWith('{')) ?? "";

    /// <summary>The modules of SETUP, their state and advice, the machine (voice_cli.cmd components); null when it did not answer.</summary>
    public static async Task<JsonElement?> Components(bool fresh = false)
    {
        var reply = await Call(fresh ? new[] { "components", "--refresh" } : new[] { "components" }, 90_000);
        if (!reply.Ok) return null;
        try { return JsonDocument.Parse(LastJson(reply.Out)).RootElement.Clone(); }
        catch { return null; }
    }

    /// <summary>The call of a key as a person would type it - the key itself never in it.</summary>
    public static string KeyLine(string action, string provider) => string.Join(" ", new[] { Paths.DoorName, "key", action, provider }.Select(Quote));

    /// <summary>The keys of the services (voice_cli.cmd keys): there or not, the last four characters; null when it did not answer.</summary>
    public static async Task<JsonElement?> Keys()
    {
        var reply = await Call(new[] { "keys" }, 30_000);
        if (!reply.Ok) return null;
        try { return JsonDocument.Parse(LastJson(reply.Out)).RootElement.Clone(); }
        catch { return null; }
    }

    /// <summary>A key set (on the standard input), cleared or checked (voice_cli.cmd key ACTION SERVICE); its answer, or null with the reason.</summary>
    public static async Task<(JsonElement? Told, string Trouble)> Key(string action, string provider, string? key = null)
    {
        var reply = await Call(new[] { "key", action, provider }, 45_000, action == "set" ? key ?? "" : null);
        if (!reply.Ok) return (null, Lines(reply.Err.Length > 0 ? reply.Err : reply.Out));
        try { return (JsonDocument.Parse(LastJson(reply.Out)).RootElement.Clone(), ""); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>What the program is (voice_cli.cmd describe): what is ready, the settings of a run; or the reason.</summary>
    public static async Task<(JsonElement? Told, string Trouble)> Describe()
    {
        var reply = await Call(new[] { "describe" });
        if (!reply.Ok) return (null, Lines(reply.Err.Length > 0 ? reply.Err : reply.Out));
        try { return (JsonDocument.Parse(LastJson(reply.Out)).RootElement.Clone(), ""); }
        catch (Exception ex) { return (null, ex.Message); }
    }
}
