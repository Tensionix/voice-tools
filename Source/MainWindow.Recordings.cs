using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AudionVoiceTools.Core;

namespace AudionVoiceTools;

/// <summary>
/// RECORDINGS: the recordings of the library (Core\Library.cs) - those of the recorder of 2.3 come in by themselves at
/// the start, their transcripts with them - the newest first: the date, the length, the state of the transcript. A
/// recording is listened to, transcribed - by the same run of the console command as a file, its transcript beside it in
/// the library - or again, its transcript opened, its folder shown.
/// </summary>
public partial class MainWindow
{
    List<Library.Item>? _recordings;
    StackPanel? _recordingList;

    Control RecordingsView()
    {
        _recordingList = new StackPanel { Spacing = 4, Margin = new Thickness(12, 10, 12, 12) };
        if (_recordings is null) LoadRecordings();
        else FillRecordings();
        var open = new Button { Classes = { "small" }, Content = Lang.T("ПАПКА ЗАПИСЕЙ"), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(open, Lang.T("Открыть library\\recordings: записи по годам и месяцам, расшифровки рядом с ними."));
        open.Click += (_, _) => OpenFolder(Path.Combine(Journal.Library, "recordings"));
        _recordBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        FillRecordBar();
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 10, 12, 0) };
        top.Children.Add(_recordBar);
        Grid.SetColumn(open, 1);
        top.Children.Add(open);
        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        var choices = ChoicesCard("recordings");
        DockPanel.SetDock(choices, Dock.Top);
        body.Children.Add(choices);
        body.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _recordingList });
        return body;
    }

    // ------------------------------------------------------------ the recorder in the manager

    StackPanel? _recordBar;
    TextBlock? _recordTime;
    readonly Avalonia.Threading.DispatcherTimer _recordClock = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>The recordings saved and waiting for the transcription: one run at a time, the next when the window is free.</summary>
    readonly Queue<string> _transcribeQueue = new();

    void WireRecordings()
    {
        if (_dictation is null) return;
        _dictation.RecordingChanged += _ => Avalonia.Threading.Dispatcher.UIThread.Post(FillRecordBar);
        _dictation.Saved += file => Avalonia.Threading.Dispatcher.UIThread.Post(() => RecordingSaved(file));
        _recordClock.Tick += (_, _) =>
        {
            if (_recordTime is not null && _dictation is { } dictation) _recordTime.Text = Length(dictation.Recorder.Elapsed.TotalSeconds);
        };
    }

    /// <summary>
    /// The recorder in the head of RECORDINGS: RECORD when it rests; the time, PAUSE or GO ON, SAVE and CANCEL while it
    /// records - the same recording the capsule and the tray show.
    /// </summary>
    void FillRecordBar()
    {
        if (_recordBar is not { } bar) return;
        bar.Children.Clear();
        _recordTime = null;
        if (_dictation is not { } dictation) return;
        var state = dictation.Recorder.State;
        if (state == RecState.Idle)
        {
            _recordClock.Stop();
            var start = Small("ЗАПИСАТЬ", "sm7b", "Начать запись с микрофона: она ляжет в library\\recordings и после остановки сама пойдёт в расшифровку. Хоткей - Right Alt + F11; запись видна и в капсуле, диктовка во время неё работает.", () =>
            {
                if (dictation.StartRecording() is { Length: > 0 } trouble) SetStatus(Lang.F("Запись не началась: {0}", trouble));
            }, "record_start");
            start.Classes.Add("run");
            bar.Children.Add(start);
            return;
        }
        var dot = new Avalonia.Controls.Shapes.Ellipse { Classes = { "dot", "bad" }, VerticalAlignment = VerticalAlignment.Center };
        _recordTime = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Text = Length(dictation.Recorder.Elapsed.TotalSeconds) };
        var word = new TextBlock
        {
            Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center,
            Text = Lang.T(state == RecState.Paused ? "пауза" : state == RecState.Saving ? "сохраняется…" : "идёт запись"),
        };
        bar.Children.Add(dot);
        bar.Children.Add(_recordTime);
        bar.Children.Add(word);
        if (state != RecState.Saving)
        {
            bar.Children.Add(Small(state == RecState.Paused ? "ДАЛЬШЕ" : "ПАУЗА", state == RecState.Paused ? "play" : "square",
                state == RecState.Paused ? "Продолжить запись в тот же файл." : "Приостановить: микрофон остаётся открытым, в файл ничего не пишется.",
                dictation.PauseRecording, "record_pause"));
            var save = Small("СОХРАНИТЬ", "square", "Закончить запись: файл - в библиотеку, затем в расшифровку.", () => dictation.StopRecording(keep: true), "record_save");
            save.Classes.Add("run");
            bar.Children.Add(save);
            var cancel = Small("ОТМЕНИТЬ", "x", "Отменить запись: файл уйдёт в Корзину, оттуда его можно вернуть.", () => dictation.StopRecording(keep: false), "record_cancel");
            cancel.Classes.Add("burn");
            bar.Children.Add(cancel);
        }
        _recordClock.Start();
    }

    /// <summary>A recording is saved: the list knows it, and it goes to the transcription when the library says so.</summary>
    void RecordingSaved(string file)
    {
        FirstWork("recordings");
        LoadRecordings();
        SetStatus(Lang.F("Запись сохранена: {0}", Path.GetFileName(file)));
        if (Library.Current?.TranscribeRecordings != true) return;
        _transcribeQueue.Enqueue(file);
        NextTranscription();
    }

    /// <summary>The next saved recording into the run of files, once nothing else runs.</summary>
    void NextTranscription()
    {
        if (_runner.Running || _asking) return;
        while (_transcribeQueue.TryDequeue(out var file))
        {
            if (!File.Exists(file)) continue;
            Begin(Lang.T("Расшифровка записи"), new List<string> { file }, new Dictionary<string, string>(_sets));
            return;
        }
    }

    async void LoadRecordings()
    {
        if (Library.Current is not { } library) { FillRecordings(); return; }
        try { _recordings = await library.ItemsAsync("recording"); }
        catch (Exception ex)
        {
            SetStatus(Lang.F("Библиотека не ответила: {0}", ex.Message));
            return;
        }
        if (_section == "recordings") FillRecordings();
    }

    void FillRecordings()
    {
        if (_recordingList is not { } list) return;
        list.Children.Clear();
        var shown = _recordings ?? new List<Library.Item>();
        if (shown.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Classes = { "note" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12),
                Text = Library.Current is null && _dictation is not null ? Lang.T("Библиотека открывается…")
                    : Lang.T("Записей пока нет. Записи прежней версии из input\\ и output\\ встают сюда сами при старте программы, вместе с расшифровками."),
            });
            return;
        }
        string month = "";
        foreach (var item in shown)
        {
            var heading = Upper(item.Created.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
            if (heading != month)
            {
                month = heading;
                list.Children.Add(new TextBlock { Classes = { "caption" }, Text = heading, Margin = new Thickness(0, list.Children.Count > 0 ? 10 : 0, 0, 2) });
            }
            list.Children.Add(RecordingCard(item));
        }
    }

    static string Length(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    Control RecordingCard(Library.Item item)
    {
        bool there = File.Exists(item.Source);
        bool done = item.Outcome == "completed";
        var name = new TextBlock
        {
            Text = Lang.F("Запись {0}", item.Created.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture)),
            FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var facts = new List<string>();
        if (item.Seconds > 0) facts.Add(Length(item.Seconds));
        facts.Add(!there ? Lang.T("файла нет на месте") : item.Outcome switch
        {
            "completed" => Lang.T("расшифрована"),
            "failed" => Lang.T("ошибка расшифровки - причина в журнале"),
            "processing" => Lang.T("расшифровка прервана - продолжится со следующего запуска"),
            "skipped" => Lang.T("расшифровка уже была"),
            _ => Lang.T("не расшифрована"),
        });
        if (item.Origin == "recorder-2.3") facts.Add(Lang.T("из 2.3"));
        facts.Add(Path.GetFileName(item.Source));
        var line = new TextBlock { Classes = { "caption" }, Text = string.Join("  ·  ", facts), TextTrimming = TextTrimming.CharacterEllipsis };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        if (there)
        {
            actions.Children.Add(Tool("play", "Прослушать запись.", () => OpenFile(item.Source)));
            var transcript = Library.OutputsOf(item.Source).FirstOrDefault(path => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                ?? Library.OutputsOf(item.Source).FirstOrDefault(path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            if (transcript is not null) actions.Children.Add(Tool("file-text", "Открыть расшифровку.", () => OpenFile(transcript)));
            // The minutes read the plain text of the transcript when there is one.
            var plain = Library.OutputsOf(item.Source).FirstOrDefault(path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) ?? transcript;
            if (plain is not null) actions.Children.Add(MinutesTool(item, plain));
            actions.Children.Add(Tool("folder-open", "Показать запись в папке.", () => ShowInFolder(item.Source)));
            actions.Children.Add(Tool("download", "Скачать запись в «Загрузки».", () => ToDownloads(item.Source, null)));
            var run = Tool("captions", done ? "Расшифровать заново - с настройками раздела «Файлы»; прежняя расшифровка будет заменена." : "Расшифровать - с настройками раздела «Файлы»: язык, говорящие, какие файлы писать.",
                () => TranscribeRecording(item, again: done));
            run.IsEnabled = !_runner.Running;
            actions.Children.Add(run);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new StackPanel { Spacing = 2, Children = { name, line } });
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return new Border { Classes = { "subcard" }, Child = grid, Opacity = there ? 1 : 0.55 };
    }

    // ------------------------------------------------------------ the minutes of a meeting

    // The recordings whose minutes are being made: their button waits.
    readonly HashSet<string> _minutesMaking = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The minutes of a recording beside it and its transcripts: NAME.protocol.md.</summary>
    static string MinutesOf(string source) => Path.Combine(Path.GetDirectoryName(source) ?? "", Path.GetFileNameWithoutExtension(source) + ".protocol.md");

    /// <summary>
    /// «Протокол встречи» - «самый главный режим саммари» (the owner, 19.10.2026): made from the transcript by the model of SETTINGS,
    /// ОБРАБОТКА ТЕКСТА (Core\TextModes.cs, the mode "minutes"), opened once made; made, the button opens it, its menu makes it again.
    /// </summary>
    Button MinutesTool(Library.Item item, string transcript)
    {
        var file = MinutesOf(item.Source);
        bool made = File.Exists(file), making = _minutesMaking.Contains(item.Source);
        var config = _dictation?.Config ?? new DictationConfig();
        var problem = TextModes.Problem(Paths.Root, config);
        const string make = "Составить протокол встречи по расшифровке: тема, суть, решения, задачи, открытые вопросы. Модель - SETTINGS, ОБРАБОТКА ТЕКСТА.";
        var tip = making ? Lang.T("Протокол встречи составляется…")
            : made ? Lang.T("Открыть протокол встречи. Составить заново - правой кнопкой.")
            : problem.Length == 0 ? Lang.T(make) : Lang.T(make) + " " + Lang.F("Сейчас нельзя: {0}.", problem);
        var button = Tool("clipboard-list", tip, () =>
        {
            if (File.Exists(file)) OpenFile(file);
            else MakeMinutes(item, transcript, config);
        });
        button.IsEnabled = !making && (made || problem.Length == 0);
        if (made && !making && problem.Length == 0)
        {
            var again = new MenuItem { Header = Lang.T("Составить протокол заново - прежний уйдёт в Корзину") };
            again.Click += (_, _) => MakeMinutes(item, transcript, config);
            button.ContextMenu = new ContextMenu { Items = { again } };
        }
        return button;
    }

    async void MakeMinutes(Library.Item item, string transcript, DictationConfig config)
    {
        if (!_minutesMaking.Add(item.Source)) return;
        if (_section == "recordings") FillRecordings();
        SetStatus(Lang.T("Протокол встречи составляется…"));
        try
        {
            var text = await Task.Run(() => File.ReadAllText(transcript));
            var minutes = await Task.Run(() => TextModes.Apply(Paths.Root, config, "minutes", text));
            var file = MinutesOf(item.Source);
            var head = "# " + Lang.F("Протокол встречи · {0}", item.Created.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture));
            await Task.Run(() =>
            {
                // The minutes made before go to the Recycle Bin, not over: the person may have written in them.
                if (File.Exists(file) && Bin.ToBin(new[] { file }) is { Length: > 0 } code)
                    throw new IOException(Lang.F("прежний протокол не ушёл в Корзину (код {0})", code));
                File.WriteAllText(file, head + "\n\n" + minutes.Trim() + "\n", new System.Text.UTF8Encoding(false));
            });
            SetStatus(Lang.F("Протокол встречи: {0}", Path.GetFileName(file)));
            OpenFile(file);
        }
        catch (Exception ex) { SetStatus(Lang.F("Протокол не составлен: {0}", ex.Message)); }
        finally
        {
            _minutesMaking.Remove(item.Source);
            if (_section == "recordings") FillRecordings();
        }
    }

    /// <summary>A recording through the run of files: the settings of the section FILES, the transcript beside it.</summary>
    void TranscribeRecording(Library.Item item, bool again)
    {
        var sets = new Dictionary<string, string>(_sets);
        if (again) sets["pipeline.force"] = "true";
        Begin(Lang.T("Расшифровка записи"), new List<string> { item.Source }, sets);
    }

    void ShowInFolder(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", file }, UseShellExecute = false }); }
        catch (Exception ex) { SetStatus(Lang.F("Папка не открылась: {0}", ex.Message)); }
    }
}
