using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using AudionVoiceTools.Core;
using AudionVoiceTools.Views;

namespace AudionVoiceTools;

/// <summary>
/// SETUP: the engines and models the program works with - the engine of dictation, FFmpeg, the recognition of files, the
/// speakers, and with an NVIDIA card its heavy stacks - their state and version, the advice for this machine, a newer
/// release where one is compared, and their installation, as Audion VS Engine has it. The catalog is the engine's
/// (voice_cli.cmd components, system_core\core\modules.py, the same the builder installs from); an install is
/// `voice_cli.cmd install MODULE`, a run of the journal as any other, and its course - the step, the share of the
/// download, the speed, the time left - stands on the row of the component and on the bar at the bottom of the window.
/// At the start of the window what is recommended and not installed is asked about (Views\SetupDialog.cs): INSTALL
/// installs them one after another; INSTALL LATER says where SETUP is.
/// </summary>
public partial class MainWindow
{
    /// <summary>What voice_cli.cmd components said; null until it answers.</summary>
    JsonElement? _components;

    /// <summary>The start asked once: the components are read again after every install, the question is not.</summary>
    bool _setupAsked;

    /// <summary>The modules agreed to, waiting their turn: the next starts when one has ended done.</summary>
    readonly Queue<string> _setupQueue = new();
    int _setupTotal, _setupAt;

    /// <summary>The module the run under way installs; "" - the run is not one of SETUP.</summary>
    string _setupModule = "";

    /// <summary>The last course the installer told; null between installs.</summary>
    DoorEvent? _course;

    /// <summary>The row of the component under way on the sheet: its course is written there without the sheet being built again.</summary>
    (ProgressBar Bar, TextBlock Text)? _courseRow;

    void WireSetup() => SetupButton.Click += (_, _) => ToggleSheet("setup");

    /// <summary>OPEN SETUP of the question of the start: the sheet as the button of the header opens it, the button pressed; left open if it is.</summary>
    void OpenSetup()
    {
        if (_sheet != "setup") ToggleSheet("setup");
    }

    async Task LoadComponents(bool fresh = false)
    {
        if (_keys is null) _ = LoadKeys();
        var told = await Door.Components(fresh);
        if (told is null) return;
        _components = told;
        ShowSetupMark();
        if (_sheet == "setup") FillSheet();
        if (!_setupAsked) await AskSetupAtStart();
    }

    IEnumerable<JsonElement> ComponentList() =>
        _components is { } told && told.TryGetProperty("components", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray() : Enumerable.Empty<JsonElement>();

    static string Field(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    static bool Flag(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A text the engine says in both languages, in the language of the window.</summary>
    static string Said(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var both) && both.ValueKind == JsonValueKind.Object
            ? (Field(both, Lang.Code == "ru" ? "ru" : "en") is { Length: > 0 } one ? one : Field(both, "en")) : "";

    static int Megabytes(JsonElement component) =>
        component.TryGetProperty("download_mb", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt32() : 0;

    string NameOf(string module) =>
        ComponentList().Where(component => Field(component, "id") == module).Select(component => Said(component, "name")).FirstOrDefault() ?? module;

    /// <summary>
    /// What is recommended for this machine and is not installed, in the order of the catalog. The engine of dictation is
    /// in place with any model of it: one chosen instead of GigaAM at the start is not asked about again.
    /// </summary>
    List<JsonElement> Needed()
    {
        bool dictates = ComponentList().Any(component => Field(component, "id").StartsWith("dictation_", StringComparison.Ordinal) && Field(component, "state") == "installed");
        return ComponentList()
            .Where(component => Field(component, "advice") == "recommended" && Field(component, "state") == "missing")
            .Where(component => !(dictates && Field(component, "id") == "dictation")).ToList();
    }

    /// <summary>What is installed and has a newer release out.</summary>
    List<JsonElement> Stale() => ComponentList()
        .Where(component => Field(component, "state") == "installed" && Field(component, "fresh") == "old").ToList();

    /// <summary>The dot of SETUP: Burn Orange - something recommended is not installed; amber - a newer release is out. Its tooltip names what.</summary>
    void ShowSetupMark()
    {
        var needed = Needed();
        var stale = Stale();
        SetupDot.IsVisible = needed.Count > 0 || stale.Count > 0;
        SetupDot.Classes.Set("bad", needed.Count > 0);
        SetupDot.Classes.Set("warn", needed.Count == 0 && stale.Count > 0);
        var said = new List<string>();
        if (needed.Count > 0) said.Add(Lang.F("Не установлено: {0}", string.Join(", ", needed.Select(one => Said(one, "name")))));
        if (stale.Count > 0) said.Add(Lang.F("Есть новее: {0}", string.Join(", ", stale.Select(one => Said(one, "name") + " " + Field(one, "latest")))));
        ToolTip.SetTip(SetupDot, said.Count > 0 ? string.Join("\n", said) : null);
    }

    /// <summary>The sign of a component by what it does; FFmpeg has its brand mark.</summary>
    static string SignOf(string id) => id switch
    {
        "dictation" => "sm7b", "gigaam" => "languages", "vulkan" or "whispercpp_models" => "audio-waveform",
        "speakers" or "gpu" => "users", "whispercpp_cuda" => "gpu", "restore_intel" or "restore_rtx" => "rotate-ccw", _ => "package",
    };

    static Control Face(string id, double size = 13) => Icons.Mark(id, size) is { } mark ? mark : Icons.Sign(SignOf(id));

    static CultureInfo Culture => Lang.Code == "ru" ? new CultureInfo("ru-RU") : CultureInfo.InvariantCulture;

    /// <summary>A download in megabytes, or in gigabytes from one.</summary>
    static string SizeText(int megabytes)
    {
        if (megabytes <= 0) return "";
        return megabytes >= 1024
            ? Lang.F("{0} ГБ", (megabytes / 1024.0).ToString("0.0", Culture))
            : Lang.F("{0} МБ", megabytes.ToString(Culture));
    }

    /// <summary>Bytes as a person reads them: МБ below a gigabyte, ГБ from it.</summary>
    static string BytesText(double bytes) => bytes >= 1073741824
        ? Lang.F("{0} ГБ", (bytes / 1073741824).ToString("0.00", Culture))
        : Lang.F("{0} МБ", (bytes / 1048576).ToString(bytes >= 104857600 ? "0" : "0.0", Culture));

    /// <summary>The time left, rounded the way it is said: seconds under a minute, minutes under an hour.</summary>
    static string LeftText(double seconds) => seconds < 60
        ? Lang.F("~{0} с", Math.Max(1, Math.Round(seconds)))
        : seconds < 3600 ? Lang.F("~{0} мин", Math.Round(seconds / 60))
        : Lang.F("~{0} ч {1} мин", Math.Floor(seconds / 3600), Math.Round(seconds % 3600 / 60));

    static string TotalText(IEnumerable<JsonElement> components) => TotalText(components.Sum(Megabytes));

    static string TotalText(int total)
    {
        return total > 0 ? Lang.F("Всего к скачиванию: около {0}.", SizeText(total)) : Lang.T("Сколько будет скачано, установка назовёт сама.");
    }

    SetupDialog.Item ItemOf(JsonElement component)
    {
        var id = Field(component, "id");
        return new SetupDialog.Item(id, Face(id, 15), Said(component, "name"), Said(component, "about"), SizeText(Megabytes(component)), Field(component, "script"), Megabytes(component));
    }

    /// <summary>
    /// The models of dictation the question offers in the block of the engine, GigaAM v3 first - the best for Russian, the
    /// default; each with what it is for, its languages and its download (its module's, or the size of its file).
    /// </summary>
    List<SetupDialog.ModelOption> ModelOptions() => DictationModels.All.Select(model =>
    {
        var module = ComponentList().FirstOrDefault(component => Field(component, "id") == model.Module);
        int megabytes = module.ValueKind == JsonValueKind.Object && Megabytes(module) > 0 ? Megabytes(module) : (int)Math.Round(model.Bytes / 1048576.0);
        return new SetupDialog.ModelOption(model.Id, model.Module, model.Title, model.About + "  ·  " + model.LanguagesText, SizeText(megabytes), megabytes);
    }).ToList();

    /// <summary>The start: the components are read; something recommended is not installed - the question, once a start.</summary>
    async Task AskSetupAtStart()
    {
        // A stand dictates from a file and ends by itself: a question would hold it. A hidden manager is asked when shown.
        if (Program.Run.Stand || !IsVisible) return;
#if DEBUG
        // The pictures of a check are of the window as it stands, not of a question over it.
        if (ShotFolder.Length > 0) return;
#endif
        _setupAsked = true;
        if (Needed().Count == 0) return;
        if (await AskSetup(later: true) is { } agreed) StartSetup(agreed);
        else if (_sheet != "setup") SetStatus("Рекомендованное не установлено: его ставит SETUP в шапке окна.");
    }

    bool _asking;

    /// <summary>The question: every recommended component not installed, what it is for and its size. The modules agreed to, or null.</summary>
    async Task<List<string>?> AskSetup(bool later)
    {
        var needed = Needed();
        if (needed.Count == 0 || _runner.Running || _asking || !IsVisible) return null;
        _asking = true;
        ApplyBusy();
        try
        {
            var items = needed.Select(ItemOf).ToList();
            var models = items.Any(item => item.Id == "dictation") ? ModelOptions() : new List<SetupDialog.ModelOption>();
            if (await SetupDialog.Ask(this, items, models, TotalText, later) is not { } choice) return null;
            if (choice.Open) { OpenSetup(); return null; }
            // The model chosen is taken into DICTATIONS once its install has ended done (Install, _installThenUse).
            if (choice.Model.Length > 0) _installThenUse = choice.Model;
            return choice.Modules.ToList();
        }
        finally
        {
            _asking = false;
            ApplyBusy();
        }
    }

    /// <summary>The modules agreed to, one after another: the downloads were agreed to in the question.</summary>
    void StartSetup(IEnumerable<string> modules)
    {
        _setupQueue.Clear();
        foreach (var module in modules) _setupQueue.Enqueue(module);
        _setupTotal = _setupQueue.Count;
        _setupAt = 0;
        NextSetup();
    }

    void NextSetup()
    {
        if (_setupQueue.Count == 0 || _runner.Running) return;
        _setupAt++;
        Install(_setupQueue.Dequeue());
    }

    /// <summary>One module put in place: a run of the journal - its line, the output of the installer, STOP. The sheet stays: the course is on it.</summary>
    void Install(string module, bool update = false)
    {
        if (_runner.Running) return;
        if (_setupQueue.Count == 0 && _setupAt == 0) _setupTotal = 0;
        _setupModule = module;
        _course = null;
        var name = "SETUP → " + NameOf(module) + (_setupTotal > 1 ? " " + Lang.F("({0} из {1})", _setupAt, _setupTotal) : "");
        Begin(name, Door.InstallLine(module, update), Door.Install(module, update), keepSheet: true);
        if (!_runner.Running) _setupModule = "";
    }

    /// <summary>
    /// The course an installer told: the bar at the bottom fills by the share of the download under way, the live line under
    /// the journal and the row of the component say the step, the file, the bytes, the speed and the time left.
    /// </summary>
    void SetupCourse(DoorEvent said)
    {
        _course = said;
        ProgressBar.IsIndeterminate = said.Share < 0;
        if (said.Share >= 0) ProgressBar.Value = Math.Max(2, Math.Min(100, said.Share * 100));
        var text = CourseText(said);
        ShowActivity(NameOf(said.Module) + "  ·  " + text);
        if (_courseRow is { } row)
        {
            row.Bar.IsIndeterminate = said.Share < 0;
            if (said.Share >= 0) row.Bar.Value = said.Share * 100;
            row.Text.Text = text;
        }
    }

    static string CourseText(DoorEvent said)
    {
        var parts = new List<string>();
        if (said.Steps > 0) parts.Add(Lang.F("шаг {0} из {1}", said.Step, said.Steps) + (said.Label.Length > 0 ? ": " + said.Label : ""));
        if (said.Item.Length > 0 && said.Item != said.Label) parts.Add(said.Item);
        if (said.Share >= 0) parts.Add(Math.Round(said.Share * 100).ToString(CultureInfo.InvariantCulture) + "%");
        if (said.BytesTotal > 0) parts.Add(Lang.F("{0} из {1}", BytesText(said.BytesDone), BytesText(said.BytesTotal)));
        if (said.Speed > 0) parts.Add(Lang.F("{0}/с", BytesText(said.Speed)));
        if (said.Eta >= 0) parts.Add(Lang.F("осталось {0}", LeftText(said.Eta)));
        return parts.Count > 0 ? string.Join("  ·  ", parts) : Lang.T("готовится…");
    }

    /// <summary>A run of SETUP has ended: the components are read again; done - the next module, anything else - the rest waits for SETUP.</summary>
    async void SetupEnded(string outcome)
    {
        var module = _setupModule;
        _setupModule = "";
        _course = null;
        // The engine of dictation came: the capsule loads it now, not at the next start.
        if (module == "dictation" && outcome == "done") _dictation?.Session.Arm();
        // A model installed from DICTATIONS → LOCAL MODELS is chosen at once; the list shows it installed either way.
        if (_installThenUse.Length > 0 && DictationModels.Of(_installThenUse).Module == module)
        {
            if (outcome == "done") _dictation?.SetLocalModel(_installThenUse);
            _installThenUse = "";
        }
        RefreshChoices();
        await LoadComponents();
        if (_setupQueue.Count == 0) { _setupTotal = _setupAt = 0; return; }
        if (outcome == "done") { NextSetup(); return; }
        var left = string.Join(", ", _setupQueue.Select(NameOf));
        _setupQueue.Clear();
        _setupTotal = _setupAt = 0;
        SetStatus(Lang.F("Установка остановлена, не поставлено: {0}. Причина - в журнале; продолжить - SETUP в шапке окна.", left));
    }

    // ------------------------------------------------------------ the sheet

    /// <summary>SETUP: the name with the machine as a badge beside it, the components in two columns, INSTALL ALL.</summary>
    void FillSetup()
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "SETUP", FontWeight = FontWeight.SemiBold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(MachineBadge());
        SheetBody.Children.Add(head);

        _courseRow = null;
        if (_components is null) { Note("Состав ещё читается…"); return; }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        int at = 0;
        foreach (var component in ComponentList())
        {
            if (at % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var block = ComponentBlock(component);
            Grid.SetRow(block, at / 2);
            Grid.SetColumn(block, at % 2 == 0 ? 0 : 2);
            grid.Children.Add(block);
            at++;
        }
        SheetBody.Children.Add(grid);

        bool free = !_runner.Running && !_asking;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        var needed = Needed();
        if (needed.Count > 0)
        {
            var all = new Button { Classes = { "small", "run" }, Content = Lang.T("УСТАНОВИТЬ ВСЁ") };
            Avalonia.Automation.AutomationProperties.SetAutomationId(all, "setup_install_missing");
            ToolTip.SetTip(all, Lang.T("Поставить по порядку всё рекомендованное, чего ещё нет: сначала - вопросом, что и сколько будет скачано."));
            all.IsEnabled = free;
            all.Click += async (_, _) => { if (await AskSetup(later: false) is { } agreed) StartSetup(agreed); };
            buttons.Children.Add(all);
        }
        var again = new Button { Classes = { "small" }, Content = Lang.T("ПРОВЕРИТЬ СНОВА") };
        Avalonia.Automation.AutomationProperties.SetAutomationId(again, "components_refresh");
        ToolTip.SetTip(again, Lang.T("Спросить программу снова, что стоит, и выпуски - есть ли новее. Сами выпуски спрашиваются не чаще раза в сутки."));
        again.IsEnabled = free;
        again.Click += async (_, _) =>
        {
            again.IsEnabled = false;
            SetStatus("Читаю состав программы и свежие выпуски…");
            await LoadComponents(fresh: true);
            SetStatus("Состав программы прочитан.");
        };
        buttons.Children.Add(again);
        if (needed.Count > 0) buttons.Children.Add(new TextBlock { Classes = { "note" }, Text = TotalText(needed), VerticalAlignment = VerticalAlignment.Center });
        if (_components is { } told)
        {
            // When GitHub was asked of the newest releases - or why it could not be: no network, its limit of an hour.
            var refused = Field(told, "release_error");
            var said = Field(told, "checked") is { Length: > 0 } stamp && DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var checkedAt)
                ? Lang.F("выпуски спрошены {0}", checkedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)) : "";
            if (refused.Length > 0) said = (said.Length > 0 ? said + "; " : "") + Lang.F("GitHub сейчас не ответил ({0}) - спрошу позже, не раньше чем через час", refused);
            if (said.Length > 0) buttons.Children.Add(new TextBlock { Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Text = said });
        }
        SheetBody.Children.Add(buttons);

        // What the program needs beyond its engines: the keys of the services, the microphone.
        var (left, right) = Columns();
        left.Margin = right.Margin = new Thickness(0, 14, 0, 0);
        KeysCard(left);
        MicCard(right);
    }

    /// <summary>One component in a block of its own: face, name, version, size, button; state and advice; what it is for; its course while it installs.</summary>
    Control ComponentBlock(JsonElement component)
    {
        string id = Field(component, "id"), state = Field(component, "state"), advice = Field(component, "advice"), fresh = Field(component, "fresh");
        string version = Field(component, "version"), latest = Field(component, "latest");
        bool installed = state == "installed", restore = Field(component, "action") == "restore", stale = installed && fresh == "old";
        bool runs = _setupModule == id && _runner.Running;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*,Auto,Auto") };
        void Put(Control control, int at) { Grid.SetColumn(control, at); control.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(control); }
        Put(Face(id), 0);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = Said(component, "name"), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (installed && version.Length > 0) title.Children.Add(new TextBlock { Classes = { "caption" }, Text = version, VerticalAlignment = VerticalAlignment.Center });
        Put(title, 1);
        Put(new TextBlock { Classes = { "caption" }, Text = SizeText(Megabytes(component)), Margin = new Thickness(8, 0) }, 2);
        var press = new Button
        {
            Classes = { "small" }, Margin = new Thickness(4, 0, 0, 0),
            Content = Lang.T(restore ? "ВОССТАНОВИТЬ" : stale && Flag(component, "updatable") ? "ОБНОВИТЬ" : installed ? "ПОСТАВИТЬ ЗАНОВО" : "УСТАНОВИТЬ"),
        };
        if ((!installed && advice == "recommended") || (stale && Flag(component, "updatable"))) press.Classes.Add("run");
        Avalonia.Automation.AutomationProperties.SetAutomationId(press, "install_" + id);
        ToolTip.SetTip(press, Lang.F("Запустить {0}: ход установки - здесь и на полосе внизу окна, СТОП прерывает её.", Field(component, "script")));
        press.IsEnabled = !_runner.Running && !_asking;
        bool update = stale && Flag(component, "updatable");
        press.Click += (_, _) => Install(id, update);
        Put(press, 3);

        var dot = new Ellipse { Classes = { "dot" }, Margin = new Thickness(0, 0, 6, 0) };
        dot.Classes.Set("ok", installed && !stale);
        dot.Classes.Set("warn", stale);
        dot.Classes.Set("bad", !installed && advice == "recommended");
        var words = new List<string> { Lang.T(runs ? "ставится" : installed ? "установлен" : "не установлен") };
        if (stale) words.Add(Lang.F("есть новее: {0}", latest) + (Flag(component, "updatable") ? "" : " - " + Lang.T("переход после проверки")));
        else if (installed && fresh == "fresh") words.Add(Lang.T("свежий"));
        words.Add(Lang.T(advice switch { "recommended" => "рекомендуется", "optional" => "по желанию", _ => "этой машине не нужен" }));
        // Community-1 is gated: without the key of Hugging Face its weights do not come.
        if (id == "gpu" && _keys is not null && !HasKey("huggingface")) words.Add(Lang.T("нужен ключ Hugging Face - ниже, в КЛЮЧАХ СЕРВИСОВ"));
        var line = new DockPanel { Margin = new Thickness(22, 2, 0, 0), Children = { dot, new TextBlock { Classes = { "caption" }, Text = string.Join(" · ", words), TextWrapping = TextWrapping.Wrap } } };
        var about = new TextBlock { Classes = { "note" }, Text = Said(component, "about"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 2, 0, 0) };
        var body = new StackPanel { Children = { head, line, about } };

        if (runs)
        {
            // The course of the install under way, written here by SetupCourse as the installer tells it.
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4, Margin = new Thickness(22, 6, 0, 0), IsIndeterminate = _course is null || _course.Share < 0 };
            if (_course is { Share: >= 0 } course) bar.Value = course.Share * 100;
            var text = new TextBlock { Classes = { "caption" }, Margin = new Thickness(22, 3, 0, 0), TextWrapping = TextWrapping.Wrap, Text = _course is { } known ? CourseText(known) : Lang.T("готовится…") };
            body.Children.Add(bar);
            body.Children.Add(text);
            _courseRow = (bar, text);
        }
        // What this machine does not need stays in sight, quieter: it can still be installed by hand.
        var block = new Border { Classes = { "subcard" }, Margin = new Thickness(0, 0, 0, 8), Opacity = advice == "not_needed" && !installed ? 0.55 : 1, Child = body };
        Avalonia.Automation.AutomationProperties.SetAutomationId(block, "component_" + id);
        return block;
    }

    /// <summary>
    /// The machine as one badge beside the name of the sheet - Windows, the processor, the video card, the memory, the
    /// free space of the drive of the program; all of it in its tooltip, and copied as text by a press, for a report of a
    /// trouble. The video card decides the advice of SETUP.
    /// </summary>
    Control MachineBadge()
    {
        var lines = MachineLines();
        string Of(string key) => lines.Where(line => line.Key == key).Select(line => line.Value).FirstOrDefault() ?? "";
        var cpu = Of(Lang.T("Процессор")).Split(',')[0].Replace("(R)", "").Replace("(TM)", "").Replace("12th Gen ", "").Replace("Intel Core", "Intel").Trim();
        var gpus = lines.Where(line => line.Key == Lang.T("Видеокарта")).Select(line => line.Value.Replace("(R)", "").Replace("(TM)", "").Trim()).ToList();
        var shown = new List<string> { Of("Windows").Split(',')[0].Replace(" Professional", " Pro") };
        if (cpu.Length > 0) shown.Add(cpu);
        shown.AddRange(gpus);
        if (Of(Lang.T("Память")) is { Length: > 0 } memory) shown.Add(memory);
        if (Of(Lang.T("Диск программы")) is { Length: > 0 } drive) shown.Add(drive);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Icons.Sign("gpu"));
        // One line that ends in «…» where the sheet ends: the whole of it is in the tooltip and in the copy.
        row.Children.Add(new TextBlock { Classes = { "caption", "version" }, Text = string.Join("  ·  ", shown.Where(part => part.Length > 0)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 900 });
        row.Children.Add(Icons.Sign("copy"));
        var badge = new Button { Classes = { "small" }, Content = row, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(badge, string.Join("\n", lines.Select(line => line.Key + ": " + line.Value)) + "\n\n" + Lang.T("Нажать - скопировать сведения о машине текстом, для сообщения об ошибке."));
        Avalonia.Automation.AutomationProperties.SetAutomationId(badge, "system_copy");
        badge.Click += async (_, _) =>
        {
            var text = "Audion Voice Tools " + (Paths.Version ?? "") + Environment.NewLine + string.Join(Environment.NewLine, lines.Select(line => line.Key + ": " + line.Value));
            if (Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            Toast.Show(this, Lang.T("Сведения о машине скопированы."));
        };
        return badge;
    }

    List<(string Key, string Value)> MachineLines()
    {
        var lines = new List<(string Key, string Value)>();
        string Read(string key, string name)
        {
            try { return Microsoft.Win32.Registry.GetValue(key, name, null)?.ToString() ?? ""; }
            catch { return ""; }
        }
        const string Nt = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var build = Read(Nt, "CurrentBuild");
        var ubr = Read(Nt, "UBR");
        var windows = int.TryParse(build, out var number) && number >= 22000 ? "Windows 11" : "Windows 10";
        lines.Add(("Windows", $"{windows} {Read(Nt, "EditionID")} {Read(Nt, "DisplayVersion")}".Trim() + (build.Length > 0 ? Lang.F(", сборка {0}", build + (ubr.Length > 0 ? "." + ubr : "")) : "")));
        var cpu = Read(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString").Trim();
        lines.Add((Lang.T("Процессор"), (cpu.Length > 0 ? cpu + ", " : "") + Lang.F("потоков: {0}", Environment.ProcessorCount)));
        if (_components is { } told && told.TryGetProperty("machine", out var machine) && machine.TryGetProperty("gpus", out var gpus) && gpus.ValueKind == JsonValueKind.Array)
            foreach (var gpu in gpus.EnumerateArray())
            {
                // «GPU 0: NAME (UUID: …)» of nvidia-smi is NAME, and a card named twice is named once.
                var name = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(gpu.GetString() ?? "", @"^GPU\s+\d+:\s*", ""), @"\s*\(UUID:[^)]*\)\s*$", "").Trim();
                if (name.Length > 0 && !lines.Any(line => line.Key == Lang.T("Видеокарта") && string.Equals(line.Value, name, StringComparison.OrdinalIgnoreCase)))
                    lines.Add((Lang.T("Видеокарта"), name));
            }
        // An NVIDIA card too old, or its driver: SETUP offers it nothing of CUDA, and says why here.
        if (_components is { } said && said.TryGetProperty("machine", out var host) && Field(host, "nvidia_note") is { Length: > 0 } note)
            lines.Add(("NVIDIA", note));
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (memory > 0) lines.Add((Lang.T("Память"), Lang.F("{0} ГБ", (memory / 1073741824.0).ToString("0.#", Culture))));
        try
        {
            var drive = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Paths.Root) ?? Paths.Root);
            lines.Add((Lang.T("Диск программы"), Lang.F("{0}: свободно {1} ГБ из {2} ГБ", drive.Name.TrimEnd('\\', ':'),
                (drive.AvailableFreeSpace / 1073741824.0).ToString("0", CultureInfo.InvariantCulture), (drive.TotalSize / 1073741824.0).ToString("0", CultureInfo.InvariantCulture))));
        }
        catch { }
        return lines;
    }
}
