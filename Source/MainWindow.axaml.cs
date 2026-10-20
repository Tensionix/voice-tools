using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AudionVoiceTools.Core;
using AudionVoiceTools.Views;

namespace AudionVoiceTools;

/// <summary>
/// The manager of Audion Voice Tools, on the shell of Audion Media Tools: the header with its look, the sections - the
/// dictations of the capsule and the files run through voice_cli.cmd - the journal on the right, the state at the
/// bottom. Closing it hides it: dictation goes on, the tray brings it back and ends the program.
/// </summary>
public partial class MainWindow : Window
{
    readonly Settings _settings;
    readonly Runner _runner = new();
    /// <summary>The hot path of dictation; null while the window is only put together for a check.</summary>
    readonly Dictation? _dictation;
    /// <summary>What voice_cli.cmd describe said: what is ready, the settings of a run.</summary>
    System.Text.Json.JsonElement? _told;
    string _section = "dictations";

    public MainWindow() : this(null) { }

    public MainWindow(Dictation? dictation)
    {
        _dictation = dictation;
        // The theme is the application's: set before the markup is read, so the window comes up in it.
        _settings = Settings.Load();
        Lang.Set(_settings.LanguageInUse);
        if (Application.Current is { } app) app.RequestedThemeVariant = App.Variant(_settings.Look);
        InitializeComponent();
        if (_settings.Width >= MinWidth && _settings.Height >= MinHeight)
        {
            Width = _settings.Width;
            Height = _settings.Height;
        }
        if (_settings.JournalWidth >= RightZone.MinWidth)
            Zones.ColumnDefinitions[2].Width = new GridLength(Math.Min(_settings.JournalWidth, Math.Max(RightZone.MinWidth, Width - LeftZone.MinWidth - 60)));
        Classes.Set("vault", _settings.Look == "vault");
        App.SetFonts(_settings.UiFont, _settings.LogFont);
        App.SetSizes(_settings.UiShift, _settings.LogSize);

        var version = Paths.Version;
        Title = version is null ? "Audion Voice Tools" : "Audion Voice Tools v" + version;
        VersionText.Text = (version is null ? "version unknown" : "v" + version) + " • " + DateTime.Now.Year;

        _runner.Output += OnOutput;
        _runner.Finished += OnFinished;
        Guard.Told = message =>
        {
            AppendLines(new[] { "[GUI ERROR] " + message });
            SetStatus("Ошибка окна записана в logs.");
        };
        // A dictation came into the library: the section of the dictations shows it at once.
        Journal.Recorded += record => Dispatcher.UIThread.Post(() => Recorded(record));

        Wire();
        RightZone.SizeChanged += (_, e) => StateRight.Width = Math.Max(0, e.NewSize.Width);
        bool offScreen = Program.Walk || Program.Run.Stand;
#if DEBUG
        offScreen |= ShotFolder.Length > 0;
#endif
        if (offScreen)
        {
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = OffScreen;
        }
        MarkTheme();
        MarkLanguage();
        BuildSections();
        // What the markup holds is written in Russian: walked once, into the language in use.
        Lang.Walk(this);
        Closing += OnClosing;
        Opened += async (_, _) =>
        {
            if (offScreen) Position = OffScreen;
            await StartUp();
#if DEBUG
            if (ShotFolder.Length > 0) await TakeShots(ShotFolder);
#endif
        };
    }

    /// <summary>Where a window of a check stands: off every screen. Not -32000 - Windows reads that as "minimised".</summary>
    static readonly PixelPoint OffScreen = new(-30000, -30000);

    // ------------------------------------------------------------ the look

    void Wire()
    {
        LangRu.Click += (_, _) => SetLanguage("ru");
        LangEn.Click += (_, _) => SetLanguage("en");
        ThemeVault.Click += (_, _) => SetTheme("vault");
        ThemeDark.Click += (_, _) => SetTheme("dark");
        ThemeCode.Click += (_, _) => SetTheme("codedark");
        ThemeLight.Click += (_, _) => SetTheme("light");
        StopButton.Click += (_, _) => StopRun();
        CopyLogButton.Click += (_, _) => CopyLog();
        ClearLogButton.Click += (_, _) => ClearLog();
        WideLogButton.Click += (_, _) => ToggleWide();
        LogsButton.Click += (_, _) => OpenFolder(Paths.ProgramLogs);
        ConfigButton.Click += (_, _) => OpenFolder(Paths.Config);
        _clock.Tick += (_, _) => ShowClock();
        SheetSettings.Click += (_, _) => ToggleSheet("settings");
        SheetAbout.Click += (_, _) => ToggleSheet("about");
        WireSetup();
        WireDictations();
        WireRecordings();
        WireChoices();
        LoadFileSets();
        SheetClose.Click += (_, _) => ToggleSheet(_sheet);
        KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape || _sheet.Length == 0) return;
            ToggleSheet(_sheet);
            e.Handled = true;
        };
    }

    void MarkLanguage()
    {
        LangRu.IsChecked = Lang.Code == "ru";
        LangEn.IsChecked = Lang.Code != "ru";
    }

    void SetLanguage(string code)
    {
        if (Lang.Code != code)
        {
            Lang.Set(code);
            _settings.Lang = code;
            _settings.Save();
            BuildSections();
            Lang.Walk(this);
            FillSheet();
        }
        MarkLanguage();
    }

    void SetTheme(string look)
    {
        _settings.Look = look;
        _settings.Save();
        if (Application.Current is { } app) app.RequestedThemeVariant = App.Variant(look);
        Classes.Set("vault", look == "vault");
        MarkTheme();
        // The lines of the journal took their brushes from the old theme.
        Dispatcher.UIThread.Post(Recolour, DispatcherPriority.Background);
        FillSheet();
    }

    void MarkTheme()
    {
        ThemeVault.IsChecked = _settings.Look == "vault";
        ThemeDark.IsChecked = _settings.Look == "dark";
        ThemeCode.IsChecked = _settings.Look == "codedark";
        ThemeLight.IsChecked = _settings.Look == "light";
        ThemeButton.IsChecked = false;
    }

    static string Upper(string text) => text.ToUpper(CultureInfo.CurrentCulture);

    /// <summary>The status line: written in Russian by whoever speaks, shown in the language of the window.</summary>
    void SetStatus(string text) => StatusText.Text = Lang.T(text);

    // ------------------------------------------------------------ the start

    async System.Threading.Tasks.Task StartUp()
    {
        if (!Door.Ready)
        {
            AppendLines(new[] { "[ERR] " + Door.Trouble });
            SetStatus("Движок файлов недоступен: файлы не обработать, диктовка работает.");
            ApplyBusy();
            return;
        }
        SetStatus("Читаю состав программы…");
        var (told, trouble) = await Door.Describe();
        _told = told;
        if (told is null)
        {
            AppendLines(new[] { "[ERR] " + Lang.F("консольная команда не ответила: {0}", trouble) });
            SetStatus("Движок файлов не ответил: причина в журнале.");
        }
        else SetStatus("Готово.");
        if (_section == "files") ShowSection();
        ApplyBusy();
        // SETUP: what is installed and what this machine is advised - read off the window's thread; the question follows.
        await LoadComponents();
    }

    // ------------------------------------------------------------ the sections

    /// <summary>The sections of the manager: a sign, a name, a line of what it is for.</summary>
    static readonly (string Id, string Icon, string Name, string Line)[] Sections =
    {
        ("dictations", "audio-lines", "Диктовки", "Что надиктовано: текст, куда вставлен, время"),
        ("recordings", "file-audio", "Записи", "Записи с микрофона: прослушать, расшифровать, открыть текст"),
        ("files", "file-stack", "Файлы", "Записи и видео с диска: расшифровка рядом с исходником"),
        ("dictionary", "languages", "Словарь", "Термины и замены: как писать слова диктовки и расшифровки"),
        ("journal", "scroll-text", "Журнал", "Журналы по дням: ошибки видны и после перезапуска"),
    };

    void BuildSections()
    {
        SectionRow.Children.Clear();
        SectionRow.ColumnDefinitions.Clear();
        foreach (var (id, icon, name, line) in Sections)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center };
            if (Icons.Glyph(icon) is not null) head.Children.Add(Icons.Sign(icon));
            head.Children.Add(new TextBlock { Classes = { "sectionname" }, Text = Upper(Lang.T(name)) });
            var face = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { head } };
            face.Children.Add(new TextBlock { Classes = { "hint" }, Text = Lang.T(line), TextTrimming = TextTrimming.CharacterEllipsis });
            var tab = new ToggleButton { Classes = { "switch", "section" }, Tag = id, Content = face, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 46 };
            Avalonia.Automation.AutomationProperties.SetAutomationId(tab, "section_" + id);
            ToolTip.SetTip(tab, Lang.T(name) + "\n" + Lang.T(line));
            tab.Click += (_, _) => { _section = id; ShowSection(); };
            if (SectionRow.ColumnDefinitions.Count > 0) SectionRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(6)));
            SectionRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(tab, SectionRow.ColumnDefinitions.Count - 1);
            SectionRow.Children.Add(tab);
        }
        ShowSection();
    }

    void ShowSection()
    {
        foreach (var child in SectionRow.Children)
            if (child is ToggleButton tab) tab.IsChecked = (string?)tab.Tag == _section;
        SectionHost.Children.Clear();
        _choicesCard = null;
        _choicesOf = "";
        SectionHost.Children.Add(_section switch
        {
            "files" => FilesView(), "recordings" => RecordingsView(), "dictionary" => DictionaryView(), "journal" => LogsView(), _ => DictationsView(),
        });
    }

    void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex) { SetStatus(Lang.F("Папка не открылась: {0}", ex.Message)); }
    }

    // ------------------------------------------------------------ the journal

    /// <summary>The text of the journal since it was last cleared: the copy button gives all of it, up to a limit.</summary>
    readonly StringBuilder _logAll = new();
    const int LogAllLimit = 16_000_000;
    /// <summary>The panel keeps the last lines: pages of text, only the last one laid out when lines arrive.</summary>
    const int LogLimit = 5000;
    const int PageLines = 100;
    readonly List<int> _pageLines = new();
    readonly List<int> _pageRuns = new();
    readonly List<(Run Run, Hue Hue)> _logRuns = new();
    int _logShown;
    bool _scrollPosted;
    /// <summary>The colours of the run that goes: what a line opened is in force on the next.</summary>
    readonly Ansi _ansi = new();

    /// <summary>
    /// Lines into the journal. A line a run told is written in the colours its writer gave it (Core\Ansi.cs); a line of
    /// the window itself in the tone of what it says (Core\LogTones.cs), and into logs\gui-DATE.log with its time.
    /// </summary>
    void AppendLines(IReadOnlyList<string> lines, bool told = false)
    {
        if (lines.Count == 0) return;
        bool follow = _scrollPosted || LogScroll.Offset.Y + LogScroll.Viewport.Height >= LogScroll.Extent.Height - 40;

        var read = new List<(string Text, Hue Hue)>[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            string plain = lines[i];
            if (told) read[i] = _ansi.Read(lines[i], out plain);
            else
            {
                read[i] = new List<(string, Hue)>();
                if (plain.Length > 0) read[i].Add((plain, new Hue(LogTones.Key(LogTones.Of(plain)), null, plain.StartsWith("===== ", StringComparison.Ordinal), false)));
            }
            _logAll.Append(plain).Append("\r\n");
        }
        if (!told) KeepLines(lines);
        if (_logAll.Length > LogAllLimit)
        {
            int cut = LogAllLimit / 4;
            while (cut < _logAll.Length && _logAll[cut - 1] != '\n') cut++;
            _logAll.Remove(0, cut);
        }

        int at = Math.Max(0, lines.Count - LogLimit);
        _logShown += lines.Count - at;
        var pages = LogPages.Children;
        while (at < lines.Count)
        {
            if (_pageLines.Count == 0 || _pageLines[^1] >= PageLines)
            {
                pages.Add(new Controls.LogPage { TextWrapping = TextWrapping.Wrap });
                _pageLines.Add(0);
                _pageRuns.Add(0);
            }
            int held = _pageLines[^1], take = Math.Min(PageLines - held, lines.Count - at);
            var added = new List<Inline>();
            var piece = new StringBuilder();
            var kind = new Hue("LogPlain", null, false, false);
            void Close()
            {
                if (piece.Length == 0) return;
                var run = new Run(piece.ToString()) { Foreground = Ansi.Brush(kind) };
                if (kind.Bold) run.FontWeight = FontWeight.Bold;
                if (kind.Underline) run.TextDecorations = TextDecorations.Underline;
                added.Add(run);
                _logRuns.Add((run, kind));
                piece.Clear();
            }
            for (int i = 0; i < take; i++)
            {
                if (held + i > 0) piece.Append('\n');
                foreach (var (text, hue) in read[at + i])
                {
                    if (hue != kind) Close();
                    kind = hue;
                    piece.Append(text);
                }
            }
            Close();
            var page = (Controls.LogPage)pages[^1];
            (page.Inlines ??= new InlineCollection()).AddRange(added);
            _pageLines[^1] = held + take;
            _pageRuns[^1] += added.Count;
            at += take;
        }

        int gone = 0, lost = 0;
        while (gone < _pageLines.Count - 1 && _logShown - _pageLines[gone] >= LogLimit)
        {
            _logShown -= _pageLines[gone];
            lost += _pageRuns[gone++];
        }
        if (gone > 0)
        {
            pages.RemoveRange(0, gone);
            _pageLines.RemoveRange(0, gone);
            _pageRuns.RemoveRange(0, gone);
            _logRuns.RemoveRange(0, lost);
        }

        if (!follow || _scrollPosted) return;
        _scrollPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPosted = false;
            LogScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    /// <summary>The file of the day of the window: logs\gui-DATE.log.</summary>
    static string DayLog => Path.Combine(Paths.Logs, $"gui-{DateTime.Now:yyyyMMdd}.log");

    static void KeepLines(IReadOnlyList<string> lines)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var text = new StringBuilder();
        foreach (var line in lines.SelectMany(line => line.Split('\n')).Select(line => line.TrimEnd('\r')))
            if (line.Length > 0) text.Append(stamp).Append("  ").Append(line).Append("\r\n");
        if (text.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Paths.Logs);
            File.AppendAllText(DayLog, text.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    void ClearLog()
    {
        LogPages.Children.Clear();
        _pageLines.Clear();
        _pageRuns.Clear();
        _logRuns.Clear();
        _logAll.Clear();
        _logShown = 0;
        if (!_runner.Running) _ansi.Reset();
    }

    /// <summary>After a change of the theme the lines already written keep their meaning and take the new colours.</summary>
    void Recolour()
    {
        foreach (var (run, hue) in _logRuns)
            if (hue.Own is null) run.Foreground = App.Ink(hue.Key);
        if (ActivityRow.IsVisible) ShowActivity(_activity, true);
    }

    async void CopyLog()
    {
        if (_logAll.Length == 0 || Clipboard is not { } clipboard) return;
        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, _logAll.ToString());
            Toast.Show(this, Lang.T("Журнал скопирован."));
        }
        catch (Exception ex) { SetStatus(Lang.F("Буфер обмена не принял текст: {0}", ex.Message)); }
    }

    bool _wide;
    double _journalWidth = 560;

    /// <summary>The journal over the whole window - the section steps aside - and back to the width it had.</summary>
    void ToggleWide()
    {
        if (!_wide) _journalWidth = RightZone.Bounds.Width;
        _wide = !_wide;
        LeftZone.IsVisible = !_wide;
        Zones.ColumnDefinitions[0].Width = _wide ? new GridLength(0) : GridLength.Star;
        Zones.ColumnDefinitions[2].Width = _wide ? GridLength.Star : new GridLength(_journalWidth);
        if (WideLogButton.Content is Avalonia.Controls.Shapes.Path sign) sign.Data = Icons.Glyph(_wide ? "minimize-2" : "maximize-2");
    }

    // ------------------------------------------------------------ a run of files

    string _runName = "";
    DateTime _started;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    DoorEvent? _finish;
    bool _stopAsked, _stopAgain;
    DispatcherTimer? _killTimer, _againTimer;
    const int StopWaitSeconds = 60;
    string _activity = "";

    /// <summary>One run at a time: while it goes, the buttons that start another wait.</summary>
    void ApplyBusy()
    {
        StopButton.IsVisible = _runner.Running;
        StopButton.IsEnabled = _runner.Running && (!_stopAsked || _stopAgain);
        ApplyFilesBusy();
        // The buttons of SETUP wait for the run, and the row of the component under way shows its course.
        if (_sheet == "setup") FillSheet();
    }

    /// <summary>A run of files: its name and the line it is started with into the journal, then the door.</summary>
    void Begin(string name, IReadOnlyList<string> inputs, IReadOnlyDictionary<string, string> sets) =>
        Begin(name, Door.RunLine(inputs, sets), Door.Run(inputs, sets));

    /// <summary>A run of the door - files or SETUP: its name and the line as a person would type it into the journal, then the door.</summary>
    void Begin(string name, string line, Launch launch, bool keepSheet = false)
    {
        if (_runner.Running) return;
        if (Door.Prepare() is { Length: > 0 } trouble)
        {
            AppendLines(new[] { "[ERR] " + Lang.F("файлы прогона не подготовлены: {0}", trouble) });
            return;
        }
        // A run of SETUP keeps its sheet: the course of the install stands on the row of the component.
        if (_sheet.Length > 0 && !keepSheet) ToggleSheet(_sheet);
        _runName = name;
        _started = DateTime.Now;
        _finish = null;
        _stopAsked = _stopAgain = false;
        _ansi.Reset();
        AppendLines(new[] { "", $"===== {_runName} =====", line });
        if (_runner.Start(launch) is { } failed)
        {
            AppendLines(new[] { "[ERR] " + Lang.F("не запустилось: {0}", failed) });
            SetStatus("Не запустилось: причина в журнале.");
            return;
        }
        SetStatus(_runName);
        ProgressBar.IsVisible = true;
        ProgressBar.IsIndeterminate = true;
        ShowClock();
        _clock.Start();
        ShowActivity("");
        ApplyBusy();
    }

    /// <summary>The live line under the journal: the file under way and its share, behind a dot that breathes.</summary>
    void ShowActivity(string text, bool? shown = null)
    {
        _activity = text.Trim();
        bool runs = shown ?? _runner.Running;
        if (runs)
        {
            if (_activity.Length > 0) LiveLine.Paint(ActivityText, _activity);
            else LiveLine.Quiet(ActivityText, _runName + "…");
        }
        ActivityDot.Classes.Set("pulse", runs);
        ActivityRow.IsVisible = runs;
    }

    void OnOutput(IReadOnlyList<string> lines)
    {
        var plain = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (Door.Read(line) is not { } said) { plain.Add(line); continue; }
            switch (said.Kind)
            {
                case "progress":
                    ProgressBar.IsIndeterminate = said.Progress <= 0;
                    if (said.Progress > 0) ProgressBar.Value = Math.Max(2, Math.Min(100, said.Progress * 100));
                    ShowActivity($"{said.Index + 1}/{said.Count}  {Path.GetFileName(said.Path)}  {Math.Round(Math.Max(0, said.Share) * 100)}%");
                    FileProgress(said);
                    break;
                case "file":
                    FileState(said);
                    break;
                case "install":
                    SetupCourse(said);
                    break;
                case "finished":
                    _finish = said;
                    break;
            }
        }
        AppendLines(plain, told: true);
    }

    void ShowClock()
    {
        var span = DateTime.Now - _started;
        ClockText.Text = span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
        ClockText.Classes.Set("idle", false);
    }

    /// <summary>
    /// STOP: the run is asked by the signal file and stops before its next chunk - the file under way is not failed, the
    /// next run goes on from its cached chunks. A run that has not ended in a minute is ended from here, or at once by a
    /// second press: the button comes back a few seconds after the first.
    /// </summary>
    void StopRun()
    {
        if (!_runner.Running) return;
        if (_stopAsked)
        {
            if (!_stopAgain) return;
            _stopAgain = false;
            _killTimer?.Stop();
            ApplyBusy();
            AppendLines(new[] { "[GUI] " + Lang.T("СТОП нажат повторно: прогон завершён принудительно") });
            _runner.Stop();
            return;
        }
        _stopAsked = true;
        ApplyBusy();
        AppendLines(new[] { "[GUI] " + Lang.T("остановлено кнопкой СТОП: прогон останавливается сам") });
        SetStatus(Lang.F("{0}: останавливается…", _runName));
        if (Door.AskToStop() is { Length: > 0 } trouble)
        {
            AppendLines(new[] { "[GUI] " + Lang.F("сигнал остановки не записан ({0}): прогон завершён принудительно", trouble) });
            _runner.Stop();
            return;
        }
        _killTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(StopWaitSeconds) };
        _killTimer.Tick += (_, _) =>
        {
            _killTimer?.Stop();
            if (!_runner.Running) return;
            AppendLines(new[] { "[GUI] " + Lang.F("прогон не остановился за {0} с: завершён принудительно", StopWaitSeconds) });
            _runner.Stop();
        };
        _killTimer.Start();
        _againTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _againTimer.Tick += (_, _) =>
        {
            _againTimer?.Stop();
            if (!_runner.Running || !_stopAsked) return;
            _stopAgain = true;
            ApplyBusy();
        };
        _againTimer.Start();
    }

    void OnFinished(int code, bool stopped)
    {
        _clock.Stop();
        _killTimer?.Stop();
        _againTimer?.Stop();
        ShowClock();
        // What the door said comes first; a door that did not get to say it is read by its exit code.
        string outcome = stopped ? "stopped"
            : _finish is { Outcome.Length: > 0 } said ? said.Outcome
            : code == Door.ExitStopped ? "stopped"
            : code == Door.ExitUsage ? "refused"
            : code == Door.ExitDone ? "done" : "failed";
        _ansi.Reset();
        AppendLines(new[] { $"===== {(outcome == "done" ? "OK" : outcome == "stopped" ? "STOPPED" : "FAILED")}: {_runName} · {ClockText.Text} =====" });
        ProgressBar.IsVisible = false;
        ShowActivity("", false);
        SetStatus(outcome switch
        {
            "done" => Lang.F("Готово: {0}", _runName),
            "stopped" => Lang.F("Остановлено: {0}", _runName),
            "refused" => Lang.F("{0}: нечего обрабатывать - причина в журнале.", _runName),
            _ => Lang.F("{0}: ошибка - подробности в журнале.", _runName),
        });
        if (_setupModule.Length > 0) SetupEnded(outcome);
        else
        {
            FilesFinished(outcome);
            // A recording may have been transcribed: the library knows of it after its thread of the disk has written.
            Journal.Post(() => Dispatcher.UIThread.Post(LoadRecordings));
        }
        ApplyBusy();
        if (App.Quitting) Close();
        // A recording saved during the run waited for it.
        else Dispatcher.UIThread.Post(NextTranscription, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------ the end of the window

    /// <summary>
    /// Closing the manager hides it: the capsule and the hotkey go on, the tray brings it back. Only the end of the
    /// program closes it - a run of files is then asked to stop the way STOP asks.
    /// </summary>
    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveWindow();
        if (!App.Quitting && e.CloseReason != WindowCloseReason.OSShutdown && _dictation is not null && !Program.Run.Stand)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (_runner.Running)
        {
            Door.AskToStop();
            _runner.StopForClose();
        }
        if (!_runner.Running) Paths.DropSession();
    }

    void SaveWindow()
    {
        // A stand and a walk are checks: the look of the window the owner works in is not theirs to change.
        if (Program.Run.Stand || Program.Walk) return;
        if (WindowState == WindowState.Normal && IsVisible)
        {
            _settings.Width = Width;
            _settings.Height = Height;
        }
        _settings.JournalWidth = Math.Round(_wide ? _journalWidth : RightZone.Bounds.Width);
        _settings.Save();
    }
}
