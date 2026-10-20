using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AudionVoiceTools.Core;

/// <summary>
/// The lines of a stream of a command, in whatever its writer wrote them. A program of Windows writes in the code page it
/// was told or the one it took for itself: UTF-8 (chcp 65001), the ANSI page of the system (cp1251; Python writes to a
/// pipe in it), cp1252 of a program of the West, the OEM page of the console (cp866), UTF-16 (wmic, cmd /u). A stream
/// that starts as UTF-16 is read as UTF-16 to its end; any other is cut into lines by its bytes, and every line is read
/// in the page that gives the most real text.
/// The guess is the one of the window on NiceGUI (system_core\core\output_decode.py), with what it did not know: cp1252,
/// and letters that are not words - a word of Latin and Cyrillic letters at once, three same letters in a row, the
/// letters of Serbian and Ukrainian a page read wrong gives - count against a page. The signs of a table of cp866 are no
/// longer a merit of their own: capitals of cp1251 read as cp866 are such signs, and won.
/// </summary>
public static class OutputText
{
    [DllImport("kernel32.dll")] static extern int GetACP();
    [DllImport("kernel32.dll")] static extern int GetOEMCP();

    static readonly UTF8Encoding Strict = new(false, true);
    static readonly UTF8Encoding Lenient = new(false, false);

    /// <summary>
    /// The pages a line that is not UTF-8 is tried in. A draw goes to the first: the ANSI page of the system, the one
    /// most programs write to a pipe in - lower-case letters of cp1251 read as cp866 are letters too.
    /// </summary>
    static readonly Encoding[] Pages = LoadPages();

    static Encoding[] LoadPages()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var pages = new List<Encoding>();
        foreach (var page in new[] { Asked(GetACP), 1251, Asked(GetOEMCP), 866, 1252 })
        {
            if (page <= 0 || page == 65001 || pages.Any(known => known.CodePage == page)) continue;
            try { pages.Add(Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)); }
            catch { }
        }
        return pages.ToArray();
    }

    static int Asked(Func<int> page)
    {
        try { return page(); }
        catch { return 0; }
    }

    /// <summary>
    /// Reads a stream to its end, line by line, cut as a reader of .NET cuts them: at CR, at LF, at CR LF. The last line
    /// is handed over also without its end; an empty stream hands nothing.
    /// </summary>
    public static void Read(Stream stream, Action<string> line)
    {
        var cutter = new Cutter(line);
        var buffer = new byte[16384];
        // The first bytes tell UTF-16 - four at least, the first read of a pipe holds what its writer wrote at once.
        var head = new List<byte>();
        bool told = false;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (told)
            {
                cutter.Bytes(buffer, 0, read);
                continue;
            }
            head.AddRange(buffer.AsSpan(0, read).ToArray());
            if (head.Count < 4) continue;
            told = true;
            var first = head.ToArray();
            cutter.Tell(first);
            cutter.Bytes(first, 0, first.Length);
        }
        if (!told && head.Count > 0)
        {
            var first = head.ToArray();
            cutter.Bytes(first, 0, first.Length);
        }
        cutter.End();
    }

    /// <summary>A line of bytes as text: UTF-8 when it is UTF-8 throughout, otherwise the page that reads it best.</summary>
    public static string Line(ReadOnlySpan<byte> bytes)
    {
        bool plain = true;
        foreach (var b in bytes)
            if (b >= 0x80) { plain = false; break; }
        if (plain) return Encoding.ASCII.GetString(bytes);
        try { return Strict.GetString(bytes); }
        catch (DecoderFallbackException) { }
        // UTF-8 with a wrong byte in it is read as UTF-8 too, and its letters speak for it - the 20 the window on NiceGUI
        // gave UTF-8 went to whole UTF-8 alone, and a line of cp1252 with one accent drew with it.
        string best = Lenient.GetString(bytes);
        int top = Score(best);
        foreach (var page in Pages)
        {
            string text;
            try { text = page.GetString(bytes); }
            catch (DecoderFallbackException) { continue; }
            int score = Score(text);
            if (score > top) { best = text; top = score; }
        }
        return best;
    }

    // ------------------------------------------------------------ how real a text reads

    /// <summary>What a page read wrong leaves: signs of UTF-8 read as cp1251 or cp866, the sign of a byte nobody could read.</summary>
    static readonly HashSet<char> Markers = new("�ÐÑЋЎЄҐ¤©«»╨╤╬├┬");

    static readonly string[] Sequences =
    {
        "Р°", "Р±", "РІ", "Рі", "Рґ", "Рµ", "Рё", "Р№", "Рє", "Р»", "Рј", "РЅ", "Рѕ", "Рї", "СЂ", "СЃ", "С‚", "Сѓ", "С„", "С…", "С†",
        "С‡", "С€", "С‰", "С‹", "СЊ", "СЌ", "СЋ", "СЏ", "С‘", "Р”", "Рџ", "РЎ", "Рњ", "Рќ", "Рћ", "Р­", "â€¦", "â€", "â„", "â€“",
        "â€”", "тА", "тВ", "тГ", "тД", "тЕ", "тЖ", "тЗ", "тИ", "тК", "тЛ", "тМ", "тН", "тО", "тП", "тР", "тС", "тТ", "тУ", "тФ",
        "тХ", "тЦ", "тЧ", "тШ", "тЩ", "тЪ", "тЫ", "тЬ", "тЭ", "тЮ", "тЯ",
    };

    static bool Russian(char c) => c is >= 'А' and <= 'я' or 'Ё' or 'ё';
    static bool Cyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';
    static bool Latin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 'À' and <= 'ɏ' && c != '×' && c != '÷';
    static bool Graphic(char c) => c is >= '─' and <= '▟' or >= '⠀' and <= '⣿';

    static int Score(string text)
    {
        int score = 0, same = 0;
        char last = '\0';
        bool wordLatin = false, wordCyrillic = false;
        foreach (char c in text)
        {
            if (Russian(c))
            {
                score += 8;
                // Three same letters in a row are no Russian word: a line of a table of cp866 read as cp1251 ("ННННН").
                same = c == last ? same + 1 : 1;
                if (same == 3) score -= 3 * 28;
                else if (same > 3) score -= 28;
            }
            else
            {
                same = 0;
                if (Cyrillic(c)) score -= 20;
                else if (c < 128 && (c >= ' ' && c < '\u007f' || c is '\r' or '\n' or '\t')) score += 2;
                else if (Graphic(c)) score += Markers.Contains(c) ? 0 : 2;
                else if (!char.IsControl(c) && char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.OtherNotAssigned
                         or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Surrogate
                         or System.Globalization.UnicodeCategory.Format)) score += 1;
            }
            last = c;
            if (Markers.Contains(c)) score -= c is '╨' or '╤' ? 60 : 20;
            if (c < ' ' && c is not ('\r' or '\n' or '\t')) score -= c == '\0' ? 70 : 30;
            // A word of Latin and Cyrillic letters at once is what a wrong page makes of the accents of the West.
            if (char.IsLetter(c))
            {
                wordLatin |= Latin(c);
                wordCyrillic |= Cyrillic(c);
            }
            else
            {
                if (wordLatin && wordCyrillic) score -= 40;
                wordLatin = wordCyrillic = false;
            }
        }
        if (wordLatin && wordCyrillic) score -= 40;
        for (int at = text.IndexOf("����", StringComparison.Ordinal); at >= 0; at = text.IndexOf("����", at + 4, StringComparison.Ordinal))
            score -= 80;
        foreach (var sequence in Sequences)
            for (int at = text.IndexOf(sequence, StringComparison.Ordinal); at >= 0; at = text.IndexOf(sequence, at + sequence.Length, StringComparison.Ordinal))
                score -= 120;
        return score;
    }

    // ------------------------------------------------------------ cutting a stream into lines

    sealed class Cutter
    {
        readonly Action<string> _line;
        byte[] _held = new byte[512];
        int _count;
        readonly StringBuilder _chars = new();
        Decoder? _wide;
        bool _afterCr, _first = true;

        public Cutter(Action<string> line) => _line = line;

        /// <summary>The first bytes of the stream: UTF-16 by its mark, or by the zero bytes every other place holds.</summary>
        public void Tell(byte[] head)
        {
            if (head[0] == 0xFF && head[1] == 0xFE) _wide = Encoding.Unicode.GetDecoder();
            else if (head[0] == 0xFE && head[1] == 0xFF) _wide = Encoding.BigEndianUnicode.GetDecoder();
            else
            {
                int length = Math.Min(head.Length, 200), even = 0, odd = 0;
                for (int i = 0; i < length; i++)
                    if (head[i] == 0) { if (i % 2 == 0) even++; else odd++; }
                if (Math.Max(even, odd) >= Math.Max(2, length / 5))
                    _wide = odd >= even ? Encoding.Unicode.GetDecoder() : Encoding.BigEndianUnicode.GetDecoder();
            }
        }

        public void Bytes(byte[] data, int from, int count)
        {
            if (_wide is not null)
            {
                var chars = new char[count / 2 + 2];
                int made = _wide.GetChars(data, from, count, chars, 0, false);
                for (int i = 0; i < made; i++) Char(chars[i]);
                return;
            }
            for (int i = from; i < from + count; i++)
            {
                byte b = data[i];
                if (b == (byte)'\n' && _afterCr) { _afterCr = false; continue; }
                _afterCr = b == (byte)'\r';
                if (b is (byte)'\r' or (byte)'\n')
                {
                    Hand(Line(_held.AsSpan(0, _count)));
                    _count = 0;
                    continue;
                }
                if (_count == _held.Length) Array.Resize(ref _held, _held.Length * 2);
                _held[_count++] = b;
            }
        }

        void Char(char c)
        {
            if (c == '\n' && _afterCr) { _afterCr = false; return; }
            _afterCr = c == '\r';
            if (c is '\r' or '\n')
            {
                Hand(_chars.ToString());
                _chars.Clear();
                return;
            }
            _chars.Append(c);
        }

        public void End()
        {
            if (_wide is not null)
            {
                var chars = new char[4];
                int made = _wide.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true);
                for (int i = 0; i < made; i++) Char(chars[i]);
                if (_chars.Length > 0) Hand(_chars.ToString());
            }
            else if (_count > 0) Hand(Line(_held.AsSpan(0, _count)));
        }

        /// <summary>A line goes out; the mark of the order of bytes, at the start of the stream, is no text.</summary>
        void Hand(string line)
        {
            if (_first && line.Length > 0 && line[0] == '﻿') line = line[1..];
            _first = false;
            _line(line);
        }
    }
}
