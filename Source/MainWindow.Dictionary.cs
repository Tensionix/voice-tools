using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AudionVoiceTools.Core;

namespace AudionVoiceTools;

/// <summary>
/// THE DICTIONARY (Core\Vocabulary.cs, config\dictionary.txt) as a page of its own, a line a row - the owner, 20.10.2026: «это
/// нужно строками, словарь будет большим, растягиваться окно должно». A row is a term - how a word is written, «ФГИС ТП» - or a
/// replacement, «гитхаб экшнс → GitHub Actions», or a note. The page takes the height of the window; the search narrows the rows.
/// Kept a moment after the typing stops, and when the page is left; the streams of the cloud are opened again with it. The
/// file stays as it was: a line each, «сказано -> написано», # a note.
/// </summary>
public partial class MainWindow
{
    sealed class WordRow
    {
        public bool Note;
        public string Said = "";
        public string Written = "";
        public Control? View;
    }

    List<WordRow> _words = new();
    StackPanel? _wordList;
    TextBlock? _wordCount;
    TextBox? _wordFind;
    ScrollViewer? _wordScroll;
    readonly DispatcherTimer _wordsWait = new() { Interval = TimeSpan.FromMilliseconds(900) };
    bool _wordsWired;

    Control DictionaryView()
    {
        if (!_wordsWired)
        {
            _wordsWired = true;
            _wordsWait.Tick += (_, _) => SaveWords();
        }
        // An edit still waiting is kept before the file is read again: the page shows what is in the file.
        if (_wordsWait.IsEnabled) SaveWords();
        var text = Vocabulary.Text(Paths.Root);
#if DEBUG
        // A picture shows a dictionary made up for it, never the one of the person.
        if (ShotFolder.Length > 0) text = ShotDictionary;
#endif
        _words = ReadWords(text);

        var find = new TextBox
        {
            Classes = { "masks" }, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = Lang.T("Поиск по словарю"), Text = _wordFind?.Text ?? "",
        };
        Avalonia.Automation.AutomationProperties.SetAutomationId(find, "dictionary_find");
        find.TextChanged += (_, _) => FilterWords();
        _wordFind = find;
        var adds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 0, 0, 0) };
        adds.Children.Add(Small("СТРОКА", "file-text", "Добавить строку: термин или замену «сказано → написано».", () => AddWord(note: false), "dictionary_add"));
        adds.Children.Add(Small("ЗАМЕТКА", "pin", "Добавить заметку: строку для себя, её никто не читает.", () => AddWord(note: true), "dictionary_note"));
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 10, 12, 4) };
        bar.Children.Add(find);
        Grid.SetColumn(adds, 1);
        bar.Children.Add(adds);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28,*,30"), Margin = new Thickness(12, 4, 12, 2) };
        head.Children.Add(new TextBlock { Classes = { "caption" }, Text = Lang.T("КАК СКАЗАНО - для замены; пусто у термина") });
        var written = new TextBlock { Classes = { "caption" }, Text = Lang.T("КАК ПИСАТЬ") };
        Grid.SetColumn(written, 2);
        head.Children.Add(written);

        _wordList = new StackPanel { Spacing = 4, Margin = new Thickness(12, 2, 12, 12) };
        foreach (var row in _words) _wordList.Children.Add(WordView(row));
        _wordScroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _wordList };

        _wordCount = new TextBlock { Classes = { "note" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 10) };
        CountWords();

        var body = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        body.Children.Add(bar);
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        DockPanel.SetDock(_wordCount, Dock.Bottom);
        body.Children.Add(_wordCount);
        body.Children.Add(_wordScroll);
        FilterWords();
        return body;
    }

    /// <summary>The lines of the file as rows: empty lines pass by; a replacement half typed stays a row until it is finished.</summary>
    static List<WordRow> ReadWords(string text)
    {
        var rows = new List<WordRow>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) { rows.Add(new WordRow { Note = true, Written = line.TrimStart('#').Trim() }); continue; }
            var arrow = new[] { "->", "→", "=>" }.Select(one => (One: one, At: line.IndexOf(one, StringComparison.Ordinal)))
                .Where(one => one.At >= 0).OrderBy(one => one.At).FirstOrDefault();
            rows.Add(arrow.One is null ? new WordRow { Written = line }
                : new WordRow { Said = line[..arrow.At].Trim(), Written = line[(arrow.At + arrow.One.Length)..].Trim() });
        }
        return rows;
    }

    static string WriteWords(IEnumerable<WordRow> rows) => string.Join("\n", rows.Select(row =>
        row.Note ? (row.Written.Trim().Length > 0 ? "# " + row.Written.Trim() : "")
        : row.Said.Trim().Length > 0 ? row.Said.Trim() + " -> " + row.Written.Trim()
        : row.Written.Trim()).Where(line => line.Length > 0));

    Control WordView(WordRow row)
    {
        TextBox Box(string value, string hint, Action<string> keep)
        {
            var box = new TextBox { Classes = { "masks" }, Text = value, Height = 26, MinHeight = 26, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center, PlaceholderText = Lang.T(hint) };
            box.TextChanged += (_, _) =>
            {
                keep(box.Text ?? "");
                _wordsWait.Stop();
                _wordsWait.Start();
            };
            return box;
        }
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28,*,30") };
        if (row.Note)
        {
            // A note: italic, across both fields, a # before it as in the file.
            var sign = new TextBlock { Text = "#", Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
            var note = Box(row.Written, "заметка для себя", typed => row.Written = typed);
            note.FontStyle = FontStyle.Italic;
            var both = new DockPanel();
            DockPanel.SetDock(sign, Dock.Left);
            both.Children.Add(sign);
            both.Children.Add(note);
            Grid.SetColumnSpan(both, 3);
            line.Children.Add(both);
        }
        else
        {
            line.Children.Add(Box(row.Said, "как сказано", typed => row.Said = typed));
            var arrow = new TextBlock { Text = "→", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "caption" } };
            Grid.SetColumn(arrow, 1);
            line.Children.Add(arrow);
            var written = Box(row.Written, "как писать", typed => row.Written = typed);
            Grid.SetColumn(written, 2);
            line.Children.Add(written);
        }
        var drop = Tool("x", "Убрать строку из словаря.", () =>
        {
            _words.Remove(row);
            _wordList?.Children.Remove(line);
            SaveWords();
        });
        drop.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(drop, 3);
        line.Children.Add(drop);
        row.View = line;
        return line;
    }

    void AddWord(bool note)
    {
        if (_wordList is null) return;
        if (_wordFind is { Text.Length: > 0 } find) find.Text = "";
        var row = new WordRow { Note = note };
        _words.Add(row);
        var view = WordView(row);
        _wordList.Children.Add(view);
        Dispatcher.UIThread.Post(() =>
        {
            view.BringIntoView();
            // The first field of the new row takes the typing: «как сказано», or the note.
            if (view is Grid grid && grid.Children.OfType<TextBox>().FirstOrDefault() is { } first) first.Focus();
        }, DispatcherPriority.Background);
    }

    void FilterWords()
    {
        var words = (_wordFind?.Text ?? "").Trim();
        foreach (var row in _words)
            if (row.View is { } view)
                view.IsVisible = words.Length == 0 || row.Said.Contains(words, StringComparison.OrdinalIgnoreCase) || row.Written.Contains(words, StringComparison.OrdinalIgnoreCase);
    }

    void CountWords()
    {
        if (_wordCount is null) return;
        int terms = _words.Count(row => !row.Note && row.Said.Trim().Length == 0 && row.Written.Trim().Length > 0);
        int replacements = _words.Count(row => !row.Note && row.Said.Trim().Length > 0 && row.Written.Trim().Length > 0);
        _wordCount.Text = Lang.F("Терминов {0}, замен {1}. Термин - как писать слово: так его слышат облака (xAI, ElevenLabs, OpenAI после остановки), так пишут режимы текста, протокол встречи и расшифровка файлов. Замена делается в тексте каждой диктовки, и GigaAM тоже: ему слова не подсказать. Файл config\\dictionary.txt.", terms, replacements);
    }

    /// <summary>The rows written to the file now; the next dictation takes them, the streams of the cloud are opened again.</summary>
    void SaveWords()
    {
        _wordsWait.Stop();
        CountWords();
#if DEBUG
        if (ShotFolder.Length > 0) return;
#endif
        var text = WriteWords(_words);
        if (text.TrimEnd() == Vocabulary.Text(Paths.Root).Replace("\r\n", "\n").TrimEnd()) return;
        try
        {
            Vocabulary.Save(Paths.Root, text);
            _dictation?.ConfigChanged();
            var entries = Vocabulary.Parse(text);
            SetStatus(Lang.F("Словарь сохранён: терминов {0}, замен {1}.", entries.Count(one => one.Said.Length == 0), entries.Count(one => one.Said.Length > 0)));
        }
        catch (Exception ex) { SetStatus(Lang.F("Словарь не сохранён: {0}", ex.Message)); }
    }
}
