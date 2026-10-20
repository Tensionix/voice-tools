using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AudionVoiceTools.Core;

namespace AudionVoiceTools;

/// <summary>
/// JOURNAL: the journals of the program by days, so an error is seen after a restart too - the window
/// (logs\gui-DATE.log, what the journal on the right showed, with its times) and the dictation, the recorder and the
/// library (logs\voicetools-DATE.log of the data). A day and its file on the left, the text on the right - all of it or the
/// errors only, a search through it; the newest lines at the bottom, the last 5000 of a file. Read off the window thread.
/// </summary>
public partial class MainWindow
{
    /// <summary>A journal file of a day: its date, what it holds, the file.</summary>
    sealed record LogFile(DateTime Day, string Kind, string Path);

    string _logChosen = "";
    bool _logErrorsOnly;
    TextBox? _logFind;
    SelectableTextBlock? _logText;
    readonly DispatcherTimer _logFindWait = new() { Interval = TimeSpan.FromMilliseconds(250) };
    List<string> _logLines = new();

    static readonly Regex ErrorLine = new(@"\[(ERR|ERROR|GUI ERROR|FAIL|FAILED)\]|Traceback|Exception|^\s*FAIL\b", RegexOptions.IgnoreCase);

    /// <summary>The journals there are, the newest day first.</summary>
    List<LogFile> LogFiles()
    {
        var found = new List<LogFile>();
        void From(string folder, string pattern, string format, string kind)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, pattern))
            {
                var stamp = Path.GetFileNameWithoutExtension(file)[(pattern.IndexOf('*'))..];
                if (DateTime.TryParseExact(stamp, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) found.Add(new LogFile(day, kind, file));
            }
        }
        From(Paths.Logs, "gui-*.log", "yyyyMMdd", "window");
        // The journal of dictation lies in the logs of the data: the program's own, or a stand's.
        var data = Path.GetDirectoryName(Journal.Library);
        From(Path.Combine(string.IsNullOrEmpty(data) ? Paths.Root : data, "logs"), "voicetools-*.log", "yyyy-MM-dd", "dictation");
        return found.DistinctBy(one => one.Path.ToLowerInvariant()).OrderByDescending(one => one.Day).ThenBy(one => one.Kind).ToList();
    }

    Control LogsView()
    {
        _logFindWait.Tick -= OnLogFind;
        _logFindWait.Tick += OnLogFind;
        var files = LogFiles();
        if (_logChosen.Length == 0 && files.Count > 0) _logChosen = files[0].Path;

        var days = new StackPanel { Spacing = 2, Margin = new Thickness(12, 10, 8, 12) };
        string shownDay = "";
        foreach (var file in files)
        {
            var day = file.Day.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
            if (day != shownDay)
            {
                shownDay = day;
                days.Children.Add(new TextBlock { Classes = { "caption" }, Text = Upper(day), Margin = new Thickness(0, days.Children.Count > 0 ? 8 : 0, 0, 2) });
            }
            var chip = new ToggleButton
            {
                Classes = { "switch" }, HorizontalAlignment = HorizontalAlignment.Stretch, IsChecked = file.Path == _logChosen,
                Content = Lang.T(file.Kind == "window" ? "окно" : "диктовка и запись") + "  ·  " + SizeOf(file.Path),
            };
            ToolTip.SetTip(chip, file.Path);
            chip.Click += (_, _) => { _logChosen = file.Path; ShowSection(); };
            days.Children.Add(chip);
        }
        if (files.Count == 0) days.Children.Add(new TextBlock { Classes = { "note" }, Text = Lang.T("Журналов ещё нет."), TextWrapping = TextWrapping.Wrap });

        var find = new TextBox
        {
            Classes = { "masks" }, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = Lang.T("Поиск по журналу"), Text = _logFind?.Text ?? "",
        };
        find.TextChanged += (_, _) => { _logFindWait.Stop(); _logFindWait.Start(); };
        _logFind = find;
        var errors = new ToggleButton { Classes = { "switch" }, Content = Lang.T("ТОЛЬКО ОШИБКИ"), IsChecked = _logErrorsOnly, Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(errors, Lang.T("Показать только строки ошибок: [ERR], ERROR, FAIL, Traceback."));
        errors.Click += (_, _) => { _logErrorsOnly = errors.IsChecked == true; FillLog(); };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 12, 8) };
        bar.Children.Add(find);
        Grid.SetColumn(errors, 1);
        bar.Children.Add(errors);

        _logText = new SelectableTextBlock { FontFamily = App.Family(_settings.LogFont), FontSize = App.SizeOf("FontLog", 10.5), TextWrapping = TextWrapping.Wrap };
        var scroll = new ScrollViewer { Content = _logText, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 12, 12) };
        var right = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        right.Children.Add(bar);
        right.Children.Add(scroll);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*") };
        grid.Children.Add(new ScrollViewer { Content = days, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        LoadLog(scroll);
        return grid;
    }

    void OnLogFind(object? sender, EventArgs e)
    {
        _logFindWait.Stop();
        FillLog();
    }

    static string SizeOf(string file)
    {
        try
        {
            long bytes = new FileInfo(file).Length;
            return bytes >= 1048576 ? Lang.F("{0} МБ", (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture)) : Lang.F("{0} КБ", Math.Max(1, bytes / 1024));
        }
        catch { return ""; }
    }

    /// <summary>The last 5000 lines of the file chosen, read off the window thread; the view goes to the newest.</summary>
    async void LoadLog(ScrollViewer scroll)
    {
        var file = _logChosen;
        if (file.Length == 0 || _logText is null) return;
        List<string> lines;
        try
        {
            lines = await Task.Run(() =>
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                var all = new List<string>();
                while (reader.ReadLine() is { } line) all.Add(line);
                return all.Count > 5000 ? all.GetRange(all.Count - 5000, 5000) : all;
            });
        }
        catch (Exception ex)
        {
            _logText.Text = Lang.F("Журнал не прочитан: {0}", ex.Message);
            return;
        }
        if (file != _logChosen) return;
        _logLines = lines;
        FillLog();
        Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
    }

    void FillLog()
    {
        if (_logText is null) return;
        var words = (_logFind?.Text ?? "").Trim();
        var shown = _logLines.Where(line => (!_logErrorsOnly || ErrorLine.IsMatch(line))
            && (words.Length == 0 || line.Contains(words, StringComparison.CurrentCultureIgnoreCase))).ToList();
        _logText.Text = shown.Count > 0 ? string.Join("\n", shown)
            : Lang.T(_logLines.Count == 0 ? "Журнал пуст." : _logErrorsOnly ? "Ошибок в этом журнале нет." : "Таких строк нет.");
    }
}
