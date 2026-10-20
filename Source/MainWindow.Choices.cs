using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AudionVoiceTools.Core;

namespace AudionVoiceTools;

/// <summary>
/// The choices of the work stand on the pages that do it (the owner, 20.10.2026: «частые вещи лучше делать прямо не
/// отрываясь от окна»; SETTINGS keeps what is set once), under ADVANCED, in blocks as SETTINGS and ABOUT have them: on
/// DICTATIONS the local models, the services of the cloud with their models, the working over of the text, the devices; on
/// RECORDINGS what becomes of a recording and the microphone; on FILES the engines, the services, the recognition, the
/// result. ADVANCED folds to a line by its chevron or by its head. The same config\dictation.json the tray and the capsule
/// change: a choice made anywhere is shown everywhere at once (Dictation.Changed). The models are the services' own lists
/// (Core\Models.cs), asked by themselves once a day and by the button any time.
/// </summary>
public partial class MainWindow
{
    /// <summary>The ADVANCED of the page in sight: written again in place when a choice changes; the search and the list stay.</summary>
    Border? _choicesCard;
    string _choicesOf = "";

    /// <summary>The addresses whose list was asked in this run: a list is asked by itself once, then by the button.</summary>
    readonly HashSet<string> _listsAsked = new(StringComparer.OrdinalIgnoreCase);

    void WireChoices()
    {
        SectionHost.SizeChanged += (_, e) => Cap(e.NewSize.Height);
        if (_dictation is null) return;
        _dictation.Changed += () => Dispatcher.UIThread.Post(RefreshChoices);
    }

    /// <summary>
    /// ADVANCED stays open on a page until its first work - the first dictation, recording, run of files - shows what can be
    /// chosen, and folds itself then, once; after that it is as it is left (the owner, 20.10.2026: «нужно их скрывать или
    /// держать открытыми до первой диктовки»).
    /// </summary>
    void FirstWork(string key)
    {
        if (_settings.Worked.Contains(key)) return;
        _settings.Worked.Add(key);
        if (!_settings.Folded.Contains(key)) _settings.Folded.Add(key);
        _settings.Save();
        RefreshChoices();
    }

    void RefreshChoices()
    {
        if (_choicesCard is not { } card || _section != _choicesOf) return;
        card.Child = _choicesOf == "recordings" ? RecordingChoices() : DictationChoices();
    }

    /// <summary>
    /// ADVANCED of DICTATIONS or RECORDINGS covers the list as far as it needs (the owner, 20.10.2026: «может закрывать список
    /// диктовок насколько это возможно»): the whole page but the line of the search or of the recorder, with a scroll of its
    /// own when even that is short; folded, the list has the page back.
    /// </summary>
    Control ChoicesCard(string section)
    {
        _choicesOf = section;
        _choicesCard = new Border { Margin = new Thickness(12, 8, 12, 0) };
        _choicesCard.Child = section == "recordings" ? RecordingChoices() : DictationChoices();
        _capped = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _choicesCard };
        Cap(SectionHost.Bounds.Height);
        return _capped;
    }

    ScrollViewer? _capped;

    void Cap(double height)
    {
        if (_capped is { } capped) capped.MaxHeight = height > 0 ? Math.Max(180, height - 60) : 700;
    }

    // ------------------------------------------------------------ ADVANCED: a head that folds, blocks two in a row

    /// <summary>
    /// ADVANCED of a page: its head - the word, a line of what is chosen while folded, the chevron - folds and unfolds it by a
    /// press anywhere on it; the blocks stand two in a row, each with its name, as on SETTINGS - a wide one takes a row of its
    /// own across the window.
    /// </summary>
    Control Advanced(string key, string summary, IReadOnlyList<(string Title, Control Body, bool Wide)> blocks)
    {
        bool folded = _settings.Folded.Contains(key);
        void Toggle()
        {
            if (!_settings.Folded.Remove(key)) _settings.Folded.Add(key);
            _settings.Save();
            if (key == "files") ShowSection();
            else RefreshChoices();
        }
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), MinHeight = 26 };
        ToolTip.SetTip(head, Lang.T(folded ? "Развернуть настройки." : "Свернуть настройки в строку: больше места списку."));
        head.Children.Add(new TextBlock { Classes = { "caption" }, Text = Lang.T("ДОПОЛНИТЕЛЬНО"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 14, 0) });
        if (folded)
        {
            var line = new TextBlock { Text = summary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(line, 1);
            head.Children.Add(line);
        }
        var fold = Tool(folded ? "chevron-right" : "chevron-down", folded ? "Развернуть настройки." : "Свернуть настройки в строку: больше места списку.", Toggle);
        fold.Margin = new Thickness(0);
        fold.VerticalAlignment = VerticalAlignment.Center;
        Avalonia.Automation.AutomationProperties.SetAutomationId(fold, "advanced_" + key);
        Grid.SetColumn(fold, 2);
        head.Children.Add(fold);
        head.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Toggle(); };
        var region = new StackPanel { Spacing = 8 };
        region.Children.Add(head);
        if (folded) return region;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        // The column the next half block takes; 2 - the row is full, the next block begins a row.
        int column = 2;
        for (int i = 0; i < blocks.Count; i++)
        {
            var (title, content, wide) = blocks[i];
            if (wide || column == 2)
            {
                if (grid.RowDefinitions.Count > 0) grid.RowDefinitions.Add(new RowDefinition(new GridLength(10)));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                column = 0;
            }
            else column = 2;
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new TextBlock { Classes = { "caption" }, Text = Lang.T(title), Margin = new Thickness(0, 0, 0, 4) });
            body.Children.Add(content);
            var card = new Border { Classes = { "subcard" }, Child = body, VerticalAlignment = VerticalAlignment.Stretch };
            Grid.SetRow(card, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(card, column);
            if (wide) { Grid.SetColumnSpan(card, 3); column = 2; }
            grid.Children.Add(card);
        }
        region.Children.Add(grid);
        return region;
    }

    static StackPanel Rows(params Control?[] rows)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in rows) if (row is not null) panel.Children.Add(row);
        return panel;
    }

    static WrapPanel ChipLine() => new() { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };

    static ToggleButton PageChip(string label, bool chosen, string tip, Action press, string id = "", bool enabled = true)
    {
        var chip = new ToggleButton { Classes = { "switch" }, Content = label, IsChecked = chosen, IsEnabled = enabled || chosen };
        if (tip.Length > 0) ToolTip.SetTip(chip, tip);
        ToolTip.SetShowOnDisabled(chip, true);
        if (id.Length > 0) Avalonia.Automation.AutomationProperties.SetAutomationId(chip, id);
        // A switch of a choice is not turned off by a second press: the choice stays what it is.
        chip.Click += (_, _) => { chip.IsChecked = chosen || chip.IsChecked == true; if (!chosen) press(); };
        return chip;
    }

    static TextBlock Fact(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    static TextBlock Quiet(string text) => new() { Classes = { "note" }, Text = text, TextWrapping = TextWrapping.Wrap };

    // ------------------------------------------------------------ DICTATIONS

    Control DictationChoices()
    {
        var config = _dictation?.Config ?? new DictationConfig();
        return Advanced("dictations", DictationSummary(config), new (string, Control, bool)[]
        {
            ("ЛОКАЛЬНЫЕ МОДЕЛИ · TRANSCRIBE.CPP", LocalModels(config), true),
            ("API: СЕРВИСЫ И МОДЕЛИ", CloudModels(config), true),
            ("ОБРАБОТКА И САММАРИЗАЦИЯ", TextService(config), false),
            ("УСТРОЙСТВА", Rows(Labelled("МИКРОФОН", MicChips(config)), Labelled("ПАНЕЛЬ", PanelChips(config))), false),
        });
    }

    string DictationSummary(DictationConfig config) => string.Join("  ·  ", new[]
    {
        EngineName(config.Engine), Lang.T(TextModes.TitleOf(config.Mode)),
        Lang.F("микрофон: {0}", config.Microphone.Length == 0 ? Lang.T("по умолчанию") : Microphones.NameInUse()),
    });

    /// <summary>The engine by its name: the model chosen here on transcribe.cpp, the name of a service in the cloud.</summary>
    string EngineName(string engine) => engine switch
    {
        "local" => DictationModels.Of((_dictation?.Config ?? new DictationConfig()).LocalModel).Title + " · transcribe.cpp",
        "openai_batch" => "OpenAI · " + Lang.T("после остановки"),
        _ => DictationConfig.TitleOf(engine),
    };

    /// <summary>The model an install from the list was started for: chosen once its install has ended done.</summary>
    string _installThenUse = "";

    /// <summary>
    /// The models of this machine on transcribe.cpp (config\dictation_models.json) as a list across the window, a row each:
    /// the switch with its name - chosen, dictation goes to this machine on it; what it is for and its languages; its size;
    /// installed, or the button that installs it (the module of SETUP of its own) and then chooses it.
    /// </summary>
    Control LocalModels(DictationConfig config)
    {
        var list = new StackPanel();
        bool engine = DictationModels.EngineInPlace;
        foreach (var model in DictationModels.All)
        {
            bool installed = engine && model.Installed;
            if (list.Children.Count > 0) list.Children.Add(new Border { Classes = { "entrysep" }, Margin = new Thickness(0, 4), Opacity = 0.55 });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*,80,170") };
            var id = model.Id;
            var chip = PageChip(Upper(model.Title), config.Engine == "local" && config.LocalModel == id,
                installed ? Lang.F("Диктовать моделью {0} на этой машине.", model.Title) : Lang.T("Не установлена: кнопка справа ставит её."),
                () => _dictation?.SetLocalModel(id), "local_" + id, installed);
            chip.Margin = new Thickness(0);
            chip.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(chip);
            var about = new TextBlock { Classes = { "note" }, Text = model.About + "  ·  " + model.LanguagesText, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
            Grid.SetColumn(about, 1);
            row.Children.Add(about);
            var size = new TextBlock { Classes = { "caption" }, Text = BytesText(model.Bytes), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(size, 2);
            row.Children.Add(size);
            Control state = installed
                ? (Control)Quiet(Lang.T(model.Id == DictationModels.Default ? "установлена · по умолчанию" : "установлена"))
                : Small("УСТАНОВИТЬ", "download", Lang.F("Скачать {0} ({1}) с Hugging Face, проверить по SHA-256 и выбрать её. Ход - внизу окна и в журнале.", model.Title, BytesText(model.Bytes)),
                    () => { _installThenUse = id; Install(model.Module); }, "install_" + id);
            state.VerticalAlignment = VerticalAlignment.Center;
            if (state is Button button) button.IsEnabled = !_runner.Running;
            Grid.SetColumn(state, 3);
            row.Children.Add(state);
            list.Children.Add(row);
        }
        var running = _dictation?.LocalEngine ?? "";
        // The model loads beside the window at the start: the line asks again until it can name what runs.
        if (running.Length == 0 && _dictation is not null) DispatcherTimer.RunOnce(RefreshChoices, TimeSpan.FromSeconds(3));
        return Rows(list,
            Labelled("СЕЙЧАС", Fact(running.Length > 0 ? running : Lang.T("модель загружается…"))),
            Quiet(Lang.T("Без сети и без ключа. Облако не ответило за восемь секунд - тот же дубль распознаёт выбранная здесь модель.")));
    }

    /// <summary>The services of the cloud, each by its key; the one chosen shows its model from its own list and its language.</summary>
    Control CloudModels(DictationConfig config)
    {
        var chips = ChipLine();
        foreach (var engine in DictationConfig.Engines.Where(one => one != "local"))
        {
            var provider = DictationConfig.ProviderOf(engine);
            bool keyed = CloudKeys.Has(Paths.Root, provider);
            var tip = Lang.T(engine switch
            {
                "xai" => "Поток xAI: слова идут в капсулу, пока вы говорите.",
                "openai_realtime" => "Поток OpenAI Realtime: слова идут в капсулу, пока вы говорите.",
                "elevenlabs" => "Поток ElevenLabs Scribe: слова идут в капсулу, пока вы говорите.",
                _ => "Один запрос после остановки к входу, совместимому с OpenAI: OpenAI или свой сервер.",
            });
            if (!keyed) tip += " " + Lang.F("Нет ключа {0} - SETUP, КЛЮЧИ СЕРВИСОВ.", DictationConfig.TitleOf(engine));
            var chosen = engine;
            chips.Children.Add(PageChip(Upper(EngineName(engine)), config.Engine == engine, tip, () => _dictation?.SetEngine(chosen), "engine_" + engine, keyed));
        }
        if (config.Engine == "local")
            return Rows(chips, Quiet(Lang.T("Выберите сервис - диктовка пойдёт в облако по его ключу; модель - из списка самого сервиса.")));
        Control model = config.Engine switch
        {
            "xai" => Fact(Lang.T("модель выбирает xAI: поток её не называет")),
            "elevenlabs" => Fact("scribe_v2_realtime"),
            "openai_realtime" => ModelPicker("https://api.openai.com/v1", ModelCatalog.Kind.Realtime, config.RealtimeModel,
                value => { config.RealtimeModel = value; _dictation?.ConfigChanged(); }, "realtime",
                "gpt-realtime-whisper", "gpt-live-transcribe", "gpt-transcribe"),
            _ => ModelPicker(config.BatchUrl, ModelCatalog.Kind.Transcribe, config.BatchModel,
                value => { config.BatchModel = value; _dictation?.ConfigChanged(); }, "batch", "gpt-transcribe", "gpt-4o-mini-transcribe"),
        };
        return Rows(chips, Labelled("МОДЕЛЬ", model),
            config.Engine == "openai_batch" ? Labelled("АДРЕС", Address(config.BatchUrl, typed => { config.BatchUrl = typed; _dictation?.ConfigChanged(); },
                "Вход, совместимый с OpenAI: https://api.openai.com/v1 или свой сервер. Ключ OpenAI уходит только на api.openai.com.")) : null,
            Labelled("ЯЗЫК РЕЧИ", LanguageChips(config)));
    }

    Control LanguageChips(DictationConfig config)
    {
        var chips = ChipLine();
        foreach (var (code, name) in new[] { ("auto", "АВТО"), ("ru", "RU"), ("en", "EN") })
            chips.Children.Add(PageChip(Lang.T(name), config.Language == code, Lang.T("Язык речи для облака; АВТО - сервис определит сам."), () =>
            {
                config.Language = code;
                _dictation?.ConfigChanged();
            }, "language_" + code));
        return chips;
    }

    /// <summary>
    /// The working over of the text and the summary (the minutes): it is on with a service of the cloud; with the local model
    /// only when config holds a key of OpenAI or xAI (TextModes.Problem says the same to the capsule).
    /// </summary>
    Control TextService(DictationConfig config)
    {
        const string OpenAi = "https://api.openai.com/v1", Xai = "https://api.x.ai/v1", Ollama = "http://localhost:11434/v1";
        bool allowed = TextModes.Allowed(Paths.Root, config);
        var problem = TextModes.Problem(Paths.Root, config);
        var modes = ChipLine();
        foreach (var mode in TextModes.Modes)
        {
            var chosen = mode;
            bool can = mode == "raw" || problem.Length == 0;
            var tip = Lang.T(TextModes.TipOf(mode)) + (can ? "" : " " + Lang.F("Сейчас нельзя: {0}.", problem));
            modes.Children.Add(PageChip(Upper(Lang.T(TextModes.TitleOf(mode))), config.Mode == mode, tip, () => _dictation?.SetMode(chosen), "mode_" + mode, can));
        }

        var service = TextModes.ProviderOf(config.ModeUrl) is { Length: > 0 } known ? known : "own";
        var vendors = ChipLine();
        foreach (var (id, name, url, tip) in new[]
                 {
                     ("openai", "OPENAI", OpenAi, "Модель OpenAI по ключу OpenAI."),
                     ("xai", "XAI", Xai, "Модель xAI (Grok) по ключу xAI."),
                     ("own", "СВОЙ СЕРВЕР", Ollama, "Ollama, LM Studio или другой сервер, совместимый с OpenAI: без ключа, текст не покидает сеть."),
                 })
        {
            bool keyed = id == "own" || CloudKeys.Has(Paths.Root, id);
            var said = Lang.T(tip) + (keyed ? "" : " " + Lang.F("Нет ключа {0} - SETUP, КЛЮЧИ СЕРВИСОВ.", id == "xai" ? "xAI" : "OpenAI"));
            var (chosenId, chosenUrl) = (id, url);
            vendors.Children.Add(PageChip(Lang.T(name), service == id, said, () =>
            {
                config.ModeUrl = chosenUrl;
                // A model of one service is not asked of another: the new one starts from its own default or its list.
                config.ModeModel = chosenId switch { "openai" => "gpt-6-luna", "xai" => "grok-4.7", _ => "" };
                _dictation?.ConfigChanged();
            }, "text_" + id, keyed));
        }
        var body = Rows(modes, Labelled("СЕРВИС", vendors),
            service == "own" ? Labelled("АДРЕС", Address(config.ModeUrl, typed => { config.ModeUrl = typed; _dictation?.ConfigChanged(); },
                "Адрес сервера: Ollama - http://localhost:11434/v1, LM Studio - http://localhost:1234/v1.")) : null,
            Labelled("МОДЕЛЬ", ModelPicker(config.ModeUrl, ModelCatalog.Kind.Chat, config.ModeModel,
                value => { config.ModeModel = value; _dictation?.ConfigChanged(); }, "text",
                service switch { "openai" => new[] { "gpt-6-luna", "gpt-5.4-mini" }, "xai" => new[] { "grok-4.7", "grok-4.3" }, _ => Array.Empty<string>() })));
        body.IsEnabled = allowed;
        return Rows(body, Quiet(!allowed
            ? Lang.T("Обработка и саммаризация включаются с сервисом облака; с локальной моделью - когда в config есть ключ OpenAI или xAI (SETUP, КЛЮЧИ СЕРВИСОВ).")
            : problem.Length > 0 ? Lang.F("Режимы кроме исходного текста сейчас нельзя: {0}.", problem)
            : Lang.T("Протокол встречи модель составляет с наибольшим уровнем рассуждения, остальные режимы - быстро, чтобы вставка не ждала.")));
    }

    Control MicChips(DictationConfig config)
    {
        var chips = ChipLine();
        chips.Children.Add(PageChip(Lang.T("ПО УМОЛЧАНИЮ"), config.Microphone.Length == 0,
            Lang.T("Микрофон Windows по умолчанию - тот, что выбран в настройках звука Windows."), () => _dictation?.SetMicrophone(""), "mic_default"));
        foreach (var device in Microphones.List())
        {
            var id = device.Id;
            chips.Children.Add(PageChip(device.Name, config.Microphone == id, Lang.T("Диктовка и запись берут этот микрофон."), () => _dictation?.SetMicrophone(id)));
        }
        return chips;
    }

    Control PanelChips(DictationConfig config)
    {
        var chips = ChipLine();
        chips.Children.Add(PageChip(Lang.T("ВВЕРХУ"), config.Panel == "top", Lang.T("Плавающая панель - вверху экрана."), () => _dictation?.SetPanel("top"), "panel_top"));
        chips.Children.Add(PageChip(Lang.T("ВНИЗУ"), config.Panel == "bottom", Lang.T("Плавающая панель - внизу экрана."), () => _dictation?.SetPanel("bottom"), "panel_bottom"));
        var shown = new ToggleButton { Classes = { "switch" }, Content = Lang.T("ВИДНА В ПОКОЕ"), IsChecked = config.ShowPanel, Margin = new Thickness(12, 0, 4, 3) };
        ToolTip.SetTip(shown, Lang.T("Полоска панели видна и без диктовки; выключено - панель приходит только на диктовку и запись."));
        Avalonia.Automation.AutomationProperties.SetAutomationId(shown, "panel_shown");
        shown.Click += (_, _) => _dictation?.SetShowPanel(shown.IsChecked == true);
        chips.Children.Add(shown);
        return chips;
    }

    // ------------------------------------------------------------ RECORDINGS

    Control RecordingChoices()
    {
        var config = _dictation?.Config ?? new DictationConfig();
        bool now = Library.Current?.TranscribeRecordings ?? true;
        var after = ChipLine();
        foreach (var (on, name, tip) in new[]
                 {
                     (true, "РАСШИФРОВЫВАТЬ СРАЗУ", "Сохранённая запись сама идёт в расшифровку - с выбором раздела «Файлы»."),
                     (false, "ВРУЧНУЮ", "Запись только сохраняется; расшифровать - кнопкой у записи."),
                 })
        {
            var choice = on;
            after.Children.Add(PageChip(Lang.T(name), now == choice, Lang.T(tip), () => Journal.Post(() =>
            {
                Library.Current?.SetTranscribeRecordings(choice);
                Dispatcher.UIThread.Post(RefreshChoices);
            }), "record_after_" + (on ? "now" : "later")));
        }
        var files = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        files.Children.Add(Fact(FilesSummary()));
        files.Children.Add(Small("ВЫБРАТЬ", "file-stack", "Движок, язык и говорящие расшифровки - в разделе «Файлы»: записи идут с тем же выбором.", () => { _section = "files"; ShowSection(); }, "record_files"));
        var summary = string.Join("  ·  ", Lang.T(now ? "расшифровывать сразу" : "расшифровывать вручную"), FilesSummary(),
            Lang.F("микрофон: {0}", config.Microphone.Length == 0 ? Lang.T("по умолчанию") : Microphones.NameInUse()));
        return Advanced("recordings", summary, new (string, Control, bool)[]
        {
            ("ПОСЛЕ ЗАПИСИ", Rows(after, Labelled("РАСШИФРОВКА", files)), false),
            ("УСТРОЙСТВА", Rows(Labelled("МИКРОФОН", MicChips(config))), false),
        });
    }

    // ------------------------------------------------------------ a model from the list of its service; an address

    /// <summary>
    /// The model in use on a button; its list - the service's own, newest first - drops from it. ↻ asks the service now; a
    /// list older than a day is asked by itself the first time it is shown in a run. The note says how old the list is.
    /// </summary>
    Control ModelPicker(string url, ModelCatalog.Kind kind, string inUse, Action<string> pick, string id, params string[] usual)
    {
        var listing = ModelCatalog.Kept(url, kind);
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var model in ModelCatalog.Choices(listing, inUse, usual))
        {
            var item = new MenuItem { Header = model, ToggleType = MenuItemToggleType.Radio, IsChecked = model == inUse };
            var chosen = model;
            item.Click += (_, _) =>
            {
                if (chosen == inUse) return;
                pick(chosen);
                SetStatus(Lang.F("Модель: {0}", chosen));
            };
            menu.Items.Add(item);
        }
        var button = new Button { Classes = { "small" }, Flyout = menu, VerticalAlignment = VerticalAlignment.Center };
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { new TextBlock { Text = inUse.Length > 0 ? inUse : Lang.T("выберите модель") }, Icons.Sign("chevron-down") },
        };
        ToolTip.SetTip(button, Lang.T("Модель из списка самого сервиса."));
        Avalonia.Automation.AutomationProperties.SetAutomationId(button, "model_" + id);
        var refresh = Tool("rotate-ccw", "Спросить у сервиса его список моделей сейчас: списки меняются каждый месяц.", () => _ = AskModels(url, said: true));
        refresh.Margin = new Thickness(8, 0, 8, 0);
        refresh.VerticalAlignment = VerticalAlignment.Center;
        Avalonia.Automation.AutomationProperties.SetAutomationId(refresh, "models_refresh_" + id);
        var note = new TextBlock { Classes = { "note" }, VerticalAlignment = VerticalAlignment.Center, Text = ListWords(listing), TextWrapping = TextWrapping.Wrap };
        bool canAsk = Uri.TryCreate(url.Trim(), UriKind.Absolute, out _) && (TextModes.ProviderOf(url) is not { Length: > 0 } provider || CloudKeys.Has(Paths.Root, provider));
        refresh.IsEnabled = canAsk;
#if DEBUG
        // A picture asks no service.
        if (ShotFolder.Length > 0) canAsk = false;
#endif
        if (canAsk && ModelCatalog.Stale(url) && !_listsAsked.Contains(url.Trim().TrimEnd('/'))) _ = AskModels(url, said: false);
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(refresh, 1);
        Grid.SetColumn(note, 2);
        line.Children.Add(button);
        line.Children.Add(refresh);
        line.Children.Add(note);
        return line;
    }

    static string ListWords(ModelCatalog.Listing? listing) => listing switch
    {
        null => Lang.T("список у сервиса ещё не спрашивали"),
        { Error.Length: > 0, Ids.Count: 0 } => Lang.F("список не получен: {0}", listing.Error),
        { Error.Length: > 0 } => Lang.F("список от {0}; обновить не вышло: {1}", listing.At.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), listing.Error),
        _ => Lang.F("список сервиса от {0}: {1}", listing.At.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), listing.Ids.Count),
    };

    async Task AskModels(string url, bool said)
    {
        _listsAsked.Add(url.Trim().TrimEnd('/'));
        if (said) SetStatus("Спрашиваю у сервиса список моделей…");
        var listing = await ModelCatalog.Refresh(Paths.Root, url);
        if (said) SetStatus(listing.Error.Length > 0 ? Lang.F("Список моделей не получен: {0}", listing.Error) : Lang.F("Список моделей обновлён: {0}.", listing.Ids.Count));
        if (_section == "files") ShowSection();
        else RefreshChoices();
    }

    /// <summary>An address (or, <paramref name="url"/> false, a name) typed in: kept as Enter is pressed or the field is left.</summary>
    Control Address(string value, Action<string> keep, string tip, bool url = true)
    {
        var box = new TextBox { Text = value, Height = 26, MinHeight = 26, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(box, Lang.T(tip));
        void Commit()
        {
            var typed = (box.Text ?? "").Trim();
            if (url) typed = typed.TrimEnd('/');
            if (typed.Length == 0 || typed == (url ? value.Trim().TrimEnd('/') : value.Trim())) return;
            if (url && !Uri.TryCreate(typed, UriKind.Absolute, out _)) { SetStatus(Lang.F("Не адрес: {0}", typed)); return; }
            value = typed;
            keep(typed);
            SetStatus(Lang.F(url ? "Адрес: {0}" : "Модель: {0}", typed));
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return box;
    }
}
