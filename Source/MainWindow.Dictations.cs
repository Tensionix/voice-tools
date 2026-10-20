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
using Avalonia.Threading;
using AudionVoiceTools.Core;
using AudionVoiceTools.Views;

namespace AudionVoiceTools;

/// <summary>
/// DICTATIONS: what the capsule took down, from the library (Core\Library.cs), the pinned first, then the newest, by
/// days - the time, the text, the window it went into, the engine, how long it was spoken and how long the paste was
/// waited for; the texts of 2.3 among them, marked so. The search goes through the text by the beginnings of words, off
/// the window thread. A dictation is copied, pasted again into the window in front, listened to, pinned - its sound
/// then outlives its term - or deleted: the record out, its sound to the Recycle Bin.
/// </summary>
public partial class MainWindow
{
    List<DictationRecord>? _dictations;
    StackPanel? _dictationList;
    TextBox? _dictationFind;
    readonly DispatcherTimer _findWait = new() { Interval = TimeSpan.FromMilliseconds(250) };
    int _dictationAsk;

    Control DictationsView()
    {
        var find = new TextBox
        {
            Classes = { "masks" }, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = Lang.T("Поиск по тексту диктовок"), Text = _dictationFind?.Text ?? "",
        };
        Avalonia.Automation.AutomationProperties.SetAutomationId(find, "dictations_find");
        // The search waits for a pause in the typing: one query for a word, not one for every letter.
        find.TextChanged += (_, _) => { _findWait.Stop(); _findWait.Start(); };
        _dictationFind = find;
        var open = new Button { Classes = { "small" }, Margin = new Thickness(8, 0, 0, 0), Content = Lang.T("ПАПКА ЗВУКА") };
        ToolTip.SetTip(open, Lang.T("Открыть library\\audio: звук каждой диктовки, в том числе отменённой."));
        open.Click += (_, _) => OpenFolder(Path.Combine(Journal.Library, "audio"));
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 10, 12, 8) };
        bar.Children.Add(find);
        Grid.SetColumn(open, 1);
        bar.Children.Add(open);

        _dictationList = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 12, 12) };
        if (_dictations is null) LoadDictations();
        else FillDictations();
        var body = new DockPanel();
        // What dictation runs on stands over its history: the engine, the models, the mode, the microphone, the panel.
        var choices = ChoicesCard("dictations");
        DockPanel.SetDock(choices, Dock.Top);
        body.Children.Add(choices);
        DockPanel.SetDock(bar, Dock.Top);
        body.Children.Add(bar);
        body.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _dictationList });
        return body;
    }

    void WireDictations()
    {
        _findWait.Tick += (_, _) => { _findWait.Stop(); LoadDictations(); };
        // The library opened after the window - what 2.3 kept is in it now.
        Library.Opened += () => Dispatcher.UIThread.Post(() => { LoadDictations(); LoadRecordings(); });
    }

    /// <summary>The dictations asked of the library off the window thread; an answer to an older question is not shown.</summary>
    async void LoadDictations()
    {
        if (Library.Current is not { } library) { FillDictations(); return; }
        int ask = ++_dictationAsk;
        List<DictationRecord> found;
        try { found = await library.DictationsAsync(_dictationFind?.Text ?? ""); }
        catch (Exception ex)
        {
            SetStatus(Lang.F("Библиотека не ответила: {0}", ex.Message));
            return;
        }
        if (ask != _dictationAsk) return;
        _dictations = found;
        if (_section == "dictations") FillDictations();
    }

    /// <summary>A dictation came into the library: the list is asked again - it stands on top, unless pinned ones do.</summary>
    void Recorded(DictationRecord record)
    {
        LoadDictations();
        FirstWork("dictations");
    }

    /// <summary>
    /// The list as the owner drew it (19.10.2026, after the history of Handy): a day - its date, then one card with the
    /// dictations of that day, a thin line between them; each dictation its time in grey before its text, the text large.
    /// The pinned stand first, under their own head.
    /// </summary>
    void FillDictations()
    {
        if (_dictationList is not { } list) return;
        list.Children.Clear();
        var words = (_dictationFind?.Text ?? "").Trim();
        var shown = _dictations ?? new List<DictationRecord>();
        if (shown.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Classes = { "note" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12),
                Text = Library.Current is null && _dictation is not null ? Lang.T("Библиотека открывается…")
                    : words.Length > 0 ? Lang.T("Таких диктовок нет.")
                    : Lang.T("Диктовок ещё нет. Right Alt + F12 - начать и закончить, Esc - отменить; текст встанет туда, где курсор."),
            });
            return;
        }
        var culture = DayCulture();
        foreach (var day in shown.Take(300).GroupBy(record => record.Pinned ? Lang.T("Закреплено") : record.Created.ToString(DayPattern(culture), culture)))
        {
            list.Children.Add(new TextBlock { Classes = { "dayhead" }, Text = day.Key });
            var entries = new StackPanel();
            foreach (var record in day)
            {
                if (entries.Children.Count > 0) entries.Children.Add(new Border { Classes = { "entrysep" } });
                entries.Children.Add(DictationEntry(record, culture));
            }
            list.Children.Add(new Border { Classes = { "daycard" }, Child = entries });
        }
    }

    /// <summary>The long date of the language without its weekday: «15 октября 2026 г.», «October 15, 2026».</summary>
    static string DayPattern(CultureInfo culture) => culture.DateTimeFormat.LongDatePattern.Replace("dddd, ", "").Replace("dddd ", "").Trim();

    static CultureInfo DayCulture() => CultureInfo.GetCultureInfo(Lang.Code switch { "ru" => "ru-RU", "de" => "de-DE", _ => "en-US" });

    /// <summary>
    /// One dictation: the time in grey and the text after it, selectable; what else is known of it - the window it went
    /// into, the engine, how long it was spoken, how long the paste was waited for - in the tip of the text. Over it, as
    /// the pointer comes, a strip of five: copy, recognize again, listen, save the sound, delete. Paste again and pin are
    /// in its menu (the right button).
    /// </summary>
    Control DictationEntry(DictationRecord record, CultureInfo culture)
    {
        bool pasted = record.Outcome == "pasted";
        // What was pasted: the text worked over by the mode of the text, else the text of the engine.
        var text = record.Pasted.Length > 0 ? record.Pasted : record.Outcome switch
        {
            "cancelled" => Lang.T("отменена - звук сохранён"),
            "empty" => Lang.T("пусто - речь не распознана"),
            "error" => Lang.F("ошибка: {0}", record.Error),
            _ => "—",
        };
        var time = record.Created.ToString(record.Pinned ? "d MMM yyyy, HH:mm" : "HH:mm", culture);
        var words = new SelectableTextBlock { Classes = { "dictext" }, TextWrapping = TextWrapping.Wrap };
        words.Inlines = new Avalonia.Controls.Documents.InlineCollection
        {
            new Avalonia.Controls.Documents.Run(time) { Classes = { "time" } },
            new Avalonia.Controls.Documents.Run("  " + text),
        };
        if (!pasted) words.Classes.Add("muted");
        var facts = new List<string>();
        if (record.Origin == "history-2.3") facts.Add(Lang.T("из 2.3, без звука"));
        if (record.TargetTitle.Length > 0) facts.Add(record.TargetTitle);
        if (record.Engine.Length > 0) facts.Add(record.Engine);
        if (record.CleanText.Length > 0) facts.Add(Lang.F("режим текста: {0}", Lang.T(TextModes.TitleOf(record.Mode))));
        if (record.AudioSeconds > 0) facts.Add(Lang.F("{0} с речи", record.AudioSeconds.ToString("0.0", CultureInfo.CurrentCulture)));
        if (record.StopToPasteMs > 0) facts.Add(Lang.F("вставка через {0} мс", record.StopToPasteMs));
        if (record.Error.Length > 0 && record.Text.Length > 0) facts.Add(record.Error);
        if (facts.Count > 0) ToolTip.SetTip(words, string.Join("\n", facts));

        var sound = Library.Current?.Full(record.Audio) ?? "";
        bool heard = sound.Length > 0 && File.Exists(sound);
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        if (record.Pasted.Length > 0) strip.Children.Add(Tool("copy", "Копировать текст в буфер обмена.", () => CopyText(record.Pasted)));
        if (heard && _dictation is not null)
        {
            Button? again = null;
            again = Tool("repeat-2", "Распознать заново движком этой машины - GigaAM. Текст заменится новым.", () => RecognizeAgain(record, sound, again!));
            strip.Children.Add(again);
        }
        if (heard)
        {
            bool playing = _playingId == record.Id;
            Button? play = null;
            play = Tool(playing ? "square" : "play", playing ? "Остановить." : "Прослушать.", () => Listen(record, sound, play!));
            if (playing) _playButton = play;
            strip.Children.Add(play);
            strip.Children.Add(Tool("download", "Скачать звук диктовки в «Загрузки».", () => ToDownloads(sound, $"Audion_Dictation_{record.Created:yyyy-MM-dd_HH-mm-ss}.wav")));
        }
        var delete = DeleteTool(record);
        strip.Children.Add(delete);
        var pill = new Border { Classes = { "pill" }, Child = strip, IsVisible = false };

        var entry = new Grid { Background = Brushes.Transparent };
        entry.Children.Add(words);
        entry.Children.Add(pill);
        entry.PointerEntered += (_, _) => pill.IsVisible = true;
        entry.PointerExited += (_, _) => { if (delete.Flyout is not { IsOpen: true }) pill.IsVisible = false; };
        if (delete.Flyout is { } asked) asked.Closed += (_, _) => pill.IsVisible = entry.IsPointerOver;

        var menu = new ContextMenu();
        if (record.Pasted.Length > 0)
        {
            var paste = new MenuItem { Header = Lang.T("Вставить ещё раз") };
            paste.Click += (_, _) => PasteAgain(record.Pasted);
            menu.Items.Add(paste);
        }
        if (record.CleanText.Length > 0 && record.Text.Length > 0)
        {
            var spoken = new MenuItem { Header = Lang.T("Копировать текст движка - до обработки") };
            spoken.Click += (_, _) => CopyText(record.Text);
            menu.Items.Add(spoken);
        }
        var pin = new MenuItem { Header = Lang.T(record.Pinned ? "Открепить" : "Закрепить наверху: звук хранится и после срока") };
        pin.Click += (_, _) => Journal.Post(() => { Library.Current?.Pin(record.Id, !record.Pinned); Dispatcher.UIThread.Post(LoadDictations); });
        menu.Items.Add(pin);
        entry.ContextMenu = menu;
        return entry;
    }

    /// <summary>Delete, asked once more beside the button: the record goes for good, its sound to the Recycle Bin.</summary>
    Button DeleteTool(DictationRecord record)
    {
        var button = Tool("trash-2", "Удалить диктовку: запись из библиотеки, звук - в Корзину.", () => { });
        button.Classes.Add("danger");
        var yes = new Button { Classes = { "small", "burn" }, Content = Lang.T("УДАЛИТЬ") };
        var ask = new StackPanel
        {
            // Air between the question and its button (the owner, 19.10.2026).
            Spacing = 16, MaxWidth = 300, Margin = new Thickness(4, 4, 4, 2),
            Children = { new TextBlock { Text = Lang.T("Удалить диктовку? Текст уйдёт из библиотеки, звук - в Корзину."), TextWrapping = TextWrapping.Wrap, LineHeight = 20 }, yes },
        };
        var flyout = new Flyout { Content = ask, Placement = PlacementMode.BottomEdgeAlignedRight };
        button.Flyout = flyout;
        yes.Click += (_, _) =>
        {
            flyout.Hide();
            Journal.Post(() =>
            {
                var trouble = Library.Current?.Delete(record.Id) ?? "";
                Dispatcher.UIThread.Post(() =>
                {
                    if (trouble.Length > 0) SetStatus(Lang.F("Диктовка не удалена: {0}", trouble));
                    else Toast.Show(this, Lang.T("Диктовка удалена, звук - в Корзине."));
                    LoadDictations();
                });
            });
        };
        return button;
    }

    Button Tool(string sign, string tip, Action press)
    {
        var button = new Button { Classes = { "tool" }, Content = Icons.Sign(sign), Margin = new Thickness(0) };
        ToolTip.SetTip(button, Lang.T(tip));
        button.Click += (_, _) => press();
        return button;
    }

    async void CopyText(string text)
    {
        if (Clipboard is not { } clipboard) return;
        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            Toast.Show(this, Lang.T("Текст скопирован."));
        }
        catch (Exception ex) { SetStatus(Lang.F("Буфер обмена не принял текст: {0}", ex.Message)); }
    }

    /// <summary>The text goes to the window that comes in front once the manager steps aside - by the paste of dictation,
    /// which keeps the clipboard of the person.</summary>
    void PasteAgain(string text)
    {
        if (_dictation is null) return;
        WindowState = WindowState.Minimized;
        _dictation.Session.PasteText(text);
    }

    /// <summary>A file opened by the program Windows keeps for it: a recording in its player, a transcript in its editor.</summary>
    void OpenFile(string file)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(Lang.F("Файл не открылся: {0}", ex.Message)); }
    }

    // ------------------------------------------------------------ the sound of a dictation

    NAudio.Wave.WasapiOut? _player;
    NAudio.Wave.WaveFileReader? _playing;
    string _playingId = "";
    Button? _playButton;

    /// <summary>The sound played here, in the window, by WASAPI; the same button again stops it, another dictation takes its place.</summary>
    void Listen(DictationRecord record, string wav, Button button)
    {
        bool same = _playingId == record.Id;
        StopListening();
        if (same) return;
        try
        {
            var reader = new NAudio.Wave.WaveFileReader(wav);
            var output = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
            output.Init(reader);
            output.PlaybackStopped += (_, _) => Dispatcher.UIThread.Post(() => { if (_player == output) StopListening(); });
            output.Play();
            (_player, _playing, _playingId, _playButton) = (output, reader, record.Id, button);
            button.Content = Icons.Sign("square");
            ToolTip.SetTip(button, Lang.T("Остановить."));
        }
        catch (Exception ex) { SetStatus(Lang.F("Звук не открылся: {0}", ex.Message)); }
    }

    void StopListening()
    {
        var (output, reader, button) = (_player, _playing, _playButton);
        (_player, _playing, _playingId, _playButton) = (null, null, "", null);
        if (button is not null)
        {
            button.Content = Icons.Sign("play");
            ToolTip.SetTip(button, Lang.T("Прослушать."));
        }
        try { output?.Stop(); } catch { }
        output?.Dispose();
        reader?.Dispose();
    }

    /// <summary>
    /// «Скачать»: a copy into Downloads of Windows at one press (the owner, 19.10.2026) - a dictation under the name of its time, a
    /// recording under its own; a name already there gets « (2)». Off the window thread; the toast says where it went.
    /// </summary>
    async void ToDownloads(string file, string? name)
    {
        try
        {
            var copy = await Task.Run(() => Downloads.Copy(file, name));
            Toast.Show(this, Lang.F("В «Загрузках»: {0}", Path.GetFileName(copy)));
        }
        catch (Exception ex) { SetStatus(Lang.F("Не скачано: {0}", ex.Message)); }
    }

    /// <summary>The sound of the dictation recognized again by GigaAM of this machine; the new text takes the place of the old in the library.</summary>
    async void RecognizeAgain(DictationRecord record, string wav, Button button)
    {
        if (_dictation is not { } dictation) return;
        button.IsEnabled = false;
        SetStatus(Lang.T("Распознаю заново…"));
        try
        {
            var pcm = await Task.Run(() =>
            {
                using var reader = new NAudio.Wave.WaveFileReader(wav);
                var samples = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
                var all = new List<float>();
                var buffer = new float[16000];
                int read;
                while ((read = samples.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.Take(read));
                return all.ToArray();
            });
            var (text, engine) = await dictation.RecognizeAgain(pcm);
            if (text.Length == 0)
            {
                SetStatus(Lang.T("Заново - пусто: речь не распознана."));
                return;
            }
            record.Text = text;
            record.Engine = engine;
            // The text worked over was of the old text: the new one stands as the engine gave it.
            record.CleanText = record.Mode = "";
            Journal.Post(() =>
            {
                Library.Current?.Put(record, record.Origin);
                Dispatcher.UIThread.Post(LoadDictations);
            });
            SetStatus(Lang.T("Распознано заново."));
        }
        catch (Exception ex) { SetStatus(Lang.F("Не распознано: {0}", ex.Message)); }
        finally { button.IsEnabled = true; }
    }
}
