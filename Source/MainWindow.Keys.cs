using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AudionVoiceTools.Core;
using AudionVoiceTools.Views;

namespace AudionVoiceTools;

/// <summary>
/// SETUP, below the components: the keys of the services and the microphone - what the program needs to work beyond its
/// engines. A key is typed or pasted into a field that shows dots, goes to `voice_cli.cmd key set SERVICE` on its standard
/// input - never into the line of a command or the journal - and is seen afterwards by its last four characters only;
/// CHECK asks the service itself, and for Hugging Face whether the terms of Community-1 are accepted; REMOVE empties its
/// file. The microphone is the one of Windows by default, listened to for five seconds.
/// </summary>
public partial class MainWindow
{
    /// <summary>What voice_cli.cmd keys said; null until it answers.</summary>
    JsonElement? _keys;
    string _keyEditing = "";
    readonly Dictionary<string, string> _keyChecks = new();
    readonly HashSet<string> _keyBusy = new();

    float _micLevel;
    string _micResult = "";
    bool _micRunning;
    ProgressBar? _micBar;

    async Task LoadKeys()
    {
#if DEBUG
        // The pictures of a check show keys of their own: the keys of the program - even their last characters - are not
        // read for a picture.
        if (ShotFolder.Length > 0)
        {
            _keys = ShotKeys();
            _keyChecks["huggingface"] = Lang.F("ключ принят (аккаунт {0}), но условия Community-1 не приняты: откройте huggingface.co/pyannote/speaker-diarization-community-1 этим аккаунтом и примите их", "demo");
            if (_sheet == "setup") FillSheet();
            return;
        }
#endif
        if (await Door.Keys() is not { } told) return;
        _keys = told;
        if (_sheet == "setup") FillSheet();
    }

    IEnumerable<JsonElement> KeyList() =>
        _keys is { } told && told.TryGetProperty("keys", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>Whether the key of a service is there: the row of Community-1 asks it of Hugging Face.</summary>
    bool HasKey(string provider) => KeyList().Any(key => Field(key, "id") == provider && Flag(key, "present"));

    void KeysCard(Panel column)
    {
        Open(column, "КЛЮЧИ СЕРВИСОВ");
        if (_keys is null) { Note("Ключи ещё читаются…"); return; }
        foreach (var key in KeyList()) Add(KeyBlock(key));
        Note("Ключ хранится в config\\api_key_<сервис>.txt этой папки и не показывается - видны только последние четыре знака. УБРАТЬ очищает файл: в Корзину ключ не попадает.");
    }

    Control KeyBlock(JsonElement key)
    {
        string id = Field(key, "id"), fingerprint = Field(key, "fingerprint"), source = Field(key, "source");
        bool present = Flag(key, "present");
        bool busy = _keyBusy.Contains(id);

        var dot = new Avalonia.Controls.Shapes.Ellipse { Classes = { "dot" }, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.Classes.Set("ok", present);
        var state = present
            ? fingerprint + " · " + (source == "env" ? Lang.F("из переменной {0}", Field(key, "env")) : Lang.T("в файле"))
            : Lang.T("нет ключа");
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                dot,
                new TextBlock { Text = Field(key, "name"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Classes = { "caption" }, Text = state, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var body = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 8), Children = { head } };
        body.Children.Add(new TextBlock { Classes = { "note" }, Text = Said(key, "why"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(13, 0, 0, 0) });

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4, Margin = new Thickness(13, 2, 0, 0) };
        var enter = new Button { Classes = { "small" }, Content = Lang.T(present ? "ЗАМЕНИТЬ" : "ВВЕСТИ"), IsEnabled = !busy };
        Avalonia.Automation.AutomationProperties.SetAutomationId(enter, "key_enter_" + id);
        ToolTip.SetTip(enter, Lang.T("Вставить ключ в поле ниже: знаки скрыты, ключ уходит программе мимо журнала и строки команды."));
        enter.Click += (_, _) => { _keyEditing = _keyEditing == id ? "" : id; FillSheet(); };
        buttons.Children.Add(enter);
        if (present && Flag(key, "checkable"))
        {
            var check = new Button { Classes = { "small" }, Content = Lang.T("ПРОВЕРИТЬ"), IsEnabled = !busy };
            Avalonia.Automation.AutomationProperties.SetAutomationId(check, "key_check_" + id);
            ToolTip.SetTip(check, Lang.T("Спросить сам сервис одним коротким запросом: принят ли ключ. Hugging Face ещё скажет, приняты ли условия Community-1."));
            check.Click += async (_, _) => await KeyAction("check", id);
            buttons.Children.Add(check);
        }
        if (present && source == "file")
        {
            var remove = new Button { Classes = { "small", "burn" }, Content = Lang.T("УБРАТЬ"), IsEnabled = !busy };
            var yes = new Button { Classes = { "small", "burn" }, Content = Lang.T("УБРАТЬ КЛЮЧ") };
            var flyout = new Flyout
            {
                Placement = PlacementMode.BottomEdgeAlignedLeft,
                Content = new StackPanel { Spacing = 8, MaxWidth = 320, Children = { new TextBlock { Text = Lang.F("Убрать ключ {0}? Файл ключа очистится; ввести его снова можно в любой момент.", Field(key, "name")), TextWrapping = TextWrapping.Wrap }, yes } },
            };
            remove.Flyout = flyout;
            yes.Click += async (_, _) => { flyout.Hide(); await KeyAction("clear", id); };
            buttons.Children.Add(remove);
        }
        var where = new Button { Classes = { "small" }, Content = Lang.T("ГДЕ ВЗЯТЬ") };
        ToolTip.SetTip(where, Field(key, "url"));
        where.Click += (_, _) => OpenLink(Field(key, "url"));
        buttons.Children.Add(where);
        body.Children.Add(buttons);

        if (_keyChecks.TryGetValue(id, out var said))
            body.Children.Add(new TextBlock { Classes = { "caption" }, Text = said, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(13, 2, 0, 0) });

        if (_keyEditing == id)
        {
            var box = new TextBox
            {
                PasswordChar = '•', Height = 26, MinHeight = 26, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center,
                PlaceholderText = Lang.F("Ключ {0}", Field(key, "name")),
            };
            Avalonia.Automation.AutomationProperties.SetAutomationId(box, "key_box_" + id);
            var save = new Button { Classes = { "small", "run" }, Content = Lang.T("СОХРАНИТЬ"), Margin = new Thickness(6, 0, 0, 0) };
            var cancel = new Button { Classes = { "small" }, Content = Lang.T("ОТМЕНА"), Margin = new Thickness(6, 0, 0, 0) };
            async Task Save()
            {
                var typed = (box.Text ?? "").Trim();
                box.Text = "";
                if (typed.Length == 0) { SetStatus("Ключ пустой - ничего не сохранено."); return; }
                _keyEditing = "";
                await KeyAction("set", id, typed);
            }
            save.Click += async (_, _) => await Save();
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; await Save(); }
                else if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; _keyEditing = ""; FillSheet(); }
            };
            cancel.Click += (_, _) => { _keyEditing = ""; FillSheet(); };
            var edit = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(13, 4, 0, 0) };
            edit.Children.Add(box);
            Grid.SetColumn(save, 1);
            edit.Children.Add(save);
            Grid.SetColumn(cancel, 2);
            edit.Children.Add(cancel);
            body.Children.Add(edit);
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Background);
        }
        return body;
    }

    /// <summary>A key set, cleared or checked through the console command; the journal says what was done, never the key.</summary>
    async Task KeyAction(string action, string id, string? key = null)
    {
        _keyBusy.Add(id);
        FillSheet();
        var line = Door.KeyLine(action, id);
        AppendLines(new[] { action == "set" ? line + "  " + Lang.T("(ключ - на стандартном вводе, сюда он не пишется)") : line });
        var (told, trouble) = await Door.Key(action, id, key);
        _keyBusy.Remove(id);
        if (told is not { } answer)
        {
            AppendLines(new[] { "[ERR] " + trouble });
            SetStatus(Lang.F("Ключ: консольная команда не ответила - {0}", trouble));
        }
        else if (action == "check")
        {
            string state = Field(answer, "state"), detail = Field(answer, "detail");
            _keyChecks[id] = state switch
            {
                "accepted" when id == "huggingface" => Lang.F("ключ принят: аккаунт {0}, условия Community-1 приняты", detail),
                "accepted" => Lang.T("ключ принят"),
                "no_access" => Lang.F("ключ принят (аккаунт {0}), но условия Community-1 не приняты: откройте huggingface.co/pyannote/speaker-diarization-community-1 этим аккаунтом и примите их", detail),
                "refused" => Lang.F("ключ не принят: {0}", detail),
                "unreachable" => Lang.F("сервис не ответил: {0}", detail),
                "no_key" => Lang.T("ключа нет"),
                _ => Lang.T("этот ключ проверяется только расшифровкой"),
            };
            AppendLines(new[] { "[GUI] " + _keyChecks[id] });
        }
        else
        {
            _keyChecks.Remove(id);
            SetStatus(action == "set" ? Lang.F("Ключ сохранён: {0}", Field(answer, "fingerprint")) : Lang.T("Ключ убран."));
            // A stream of the cloud that waited for its key opens now.
            _dictation?.KeysChanged();
        }
        await LoadKeys();
    }

    void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(Lang.F("Ссылка не открылась: {0}", ex.Message)); }
    }

    // ------------------------------------------------------------ the microphone

    void MicCard(Panel column)
    {
        Open(column, "МИКРОФОН");
        var (chosen, all) = MicCheck.Devices();
        Pair("По умолчанию", chosen.Length > 0 ? chosen : Lang.T("микрофона нет"));
        if (all.Count > 1) Pair("Все", string.Join(", ", all));
        Note("Диктовка и запись берут микрофон, выбранный в разделе «Диктовки» или в трее; ПО УМОЛЧАНИЮ - тот, что выбран в настройках звука Windows.");
        _micBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, MinHeight = 6, Margin = new Thickness(0, 6, 0, 2), Value = Math.Sqrt(_micLevel) * 100 };
        Add(_micBar);
        if (_micResult.Length > 0 || _micRunning)
            Add(new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, Text = _micRunning ? Lang.T("Слушаю пять секунд: скажите что-нибудь…") : _micResult });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        var check = new Button { Classes = { "small" }, Content = Lang.T("ПРОВЕРИТЬ"), IsEnabled = !_micRunning && chosen.Length > 0 };
        Avalonia.Automation.AutomationProperties.SetAutomationId(check, "mic_check");
        ToolTip.SetTip(check, Lang.T("Послушать микрофон пять секунд: уровень на полосе, затем - слышит ли он. Ничего не записывается."));
        check.Click += async (_, _) => await CheckMicrophone();
        buttons.Children.Add(check);
        var windows = new Button { Classes = { "small" }, Content = Lang.T("ЗВУК WINDOWS") };
        ToolTip.SetTip(windows, Lang.T("Открыть запись в настройках звука Windows: выбрать микрофон по умолчанию, громкость, разрешения."));
        windows.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,1") { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus(Lang.F("Настройки звука не открылись: {0}", ex.Message)); }
        };
        buttons.Children.Add(windows);
        Add(buttons);
    }

    async Task CheckMicrophone()
    {
        _micRunning = true;
        _micLevel = 0;
        FillSheet();
        var result = await MicCheck.Run(TimeSpan.FromSeconds(5), level => Dispatcher.UIThread.Post(() =>
        {
            _micLevel = level;
            if (_micBar is { } bar) bar.Value = Math.Sqrt(level) * 100;
        }));
        _micRunning = false;
        var db = result.Peak > 0 ? (20 * Math.Log10(result.Peak)).ToString("0", CultureInfo.InvariantCulture) : "-∞";
        _micResult = result.Error.Length > 0 ? Lang.F("микрофон не открылся: {0}", result.Error)
            : result.Peak >= MicCheck.Heard ? Lang.F("слышу: {0}, пик {1} дБ", result.Device, db)
            : Lang.F("не слышу: {0} молчит (пик {1} дБ) - выключен, заглушён или по умолчанию выбран другой", result.Device, db);
        AppendLines(new[] { "[GUI] " + Lang.T("Проверка микрофона") + ": " + _micResult });
        FillSheet();
    }
}
