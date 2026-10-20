using System.Text.RegularExpressions;

namespace AudionVoiceTools.Core;

public enum LogTone { Plain, Muted, Head, Good, Warn, Bad }

/// <summary>
/// What colour a line the window writes itself is written in - a line of an operation keeps the
/// colours its writer gave it (Core\Ansi.cs). The window's own lines are few: the bounds of a run
/// ("===== name =====" and its verdict), what it did or could not do ("[GUI] ..."), what broke
/// ("[ERR] ...", "[GUI ERROR] ..."), and a line of its self-test ("[OK] ..."). Red is what broke,
/// yellow a run stopped, green what went, grey what the window says of its way. A word is looked
/// for where the window puts it - at the start of a line - not anywhere in it.
/// </summary>
public static class LogTones
{
    static readonly Regex Bad = new(@"^\[ERR\] |^\[GUI ERROR\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Good = new(@"^\[OK\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // What the window says of itself and of its way: read when something went wrong, quiet otherwise.
    static readonly Regex Muted = new(@"^\[GUI\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LogTone Of(string line)
    {
        var text = line.TrimEnd();
        if (text.Trim().Length == 0) return LogTone.Plain;
        // The lines of the window around a run: its title, and how it ended.
        if (text.StartsWith("===== ", System.StringComparison.Ordinal))
        {
            if (text.StartsWith("===== OK:", System.StringComparison.Ordinal)) return LogTone.Good;
            if (text.StartsWith("===== FAILED:", System.StringComparison.Ordinal)) return LogTone.Bad;
            if (text.StartsWith("===== STOPPED:", System.StringComparison.Ordinal)) return LogTone.Warn;
            return LogTone.Head;
        }
        if (Bad.IsMatch(text)) return LogTone.Bad;
        if (Good.IsMatch(text)) return LogTone.Good;
        if (Muted.IsMatch(text)) return LogTone.Muted;
        return LogTone.Plain;
    }

    public static string Key(LogTone tone) => tone switch
    {
        LogTone.Muted => "LogMuted", LogTone.Head => "LogHead", LogTone.Good => "LogGood",
        LogTone.Warn => "LogWarn", LogTone.Bad => "LogBad", _ => "LogPlain",
    };
}
