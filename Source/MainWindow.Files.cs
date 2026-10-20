using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AudionVoiceTools.Core;

namespace AudionVoiceTools;

/// <summary>
/// FILES: recordings and videos from the disk, run through voice_cli.cmd run - the sidecars (.md, .txt, .srt, .vtt,
/// .transcript.json) are written next to each source. The choices of a run are the settings of the program
/// (config\app_settings.yaml, as `describe` tells them); what the window changes goes as --set, so the journal shows the
/// very call a console would make. The state of every file comes from the service lines of the run.
/// </summary>
public partial class MainWindow
{
    readonly List<string> _inputs = new();
    /// <summary>What the window set for the next run, by the dotted key of the settings.</summary>
    readonly Dictionary<string, string> _sets = new();
    /// <summary>The files of the run that goes or went last: their state and share.</summary>
    readonly Dictionary<string, (string State, double Share)> _fileStates = new(StringComparer.OrdinalIgnoreCase);
    StackPanel? _fileList, _fileStatesList;
    Button? _runFiles;

    static readonly string[] MediaKinds =
        { "*.m4a", "*.mp3", "*.wav", "*.flac", "*.ogg", "*.opus", "*.aac", "*.wma", "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi", "*.mxf" };

    /// <summary>A setting of a run: what the window set, else what the program holds ("" when neither says).</summary>
    string Setting(string key)
    {
        if (_sets.TryGetValue(key, out var set)) return set;
        if (_told is not { } told || !told.TryGetProperty("settings", out var node)) return "";
        foreach (var part in key.Split('.'))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out var next)) return "";
            node = next;
        }
        return node.ValueKind switch
        {
            JsonValueKind.True => "true", JsonValueKind.False => "false",
            JsonValueKind.String => node.GetString() ?? "", JsonValueKind.Number => node.GetRawText(), _ => "",
        };
    }

    void Set(string key, string value) => SetMany((key, value));

    /// <summary>Choices of a run set together; null - back to what the program holds. Kept for the next start of the window.</summary>
    void SetMany(params (string Key, string? Value)[] pairs)
    {
        foreach (var (key, value) in pairs)
            if (value is null) _sets.Remove(key);
            else _sets[key] = value;
        _settings.FileSets = new Dictionary<string, string>(_sets);
        _settings.Save();
        ShowSection();
    }

    /// <summary>The choices FILES keeps from the last start of the window: what it set then is set again.</summary>
    void LoadFileSets()
    {
        foreach (var (key, value) in _settings.FileSets) _sets[key] = value;
    }

    /// <summary>What recognizes the files, in a line: the engine, the service, the speakers.</summary>
    string FilesSummary()
    {
        var engine = FilesEngine();
        var name = engine switch
        {
            "cloud" => Lang.F("облако · {0}", CloudName(Setting("stt.provider"))),
            "turbo" => "Whisper turbo · whisper.cpp",
            "large-v2" => "Whisper large-v2 · whisper.cpp",
            _ => "GigaAM · ONNX Runtime",
        };
        return Setting("diarization.enabled") == "true" ? name + " · " + Lang.T("по говорящим") : name;
    }

    /// <summary>The engine of the files in a word: "gigaam", "turbo", "large-v2", or "cloud" when the run goes to a service.</summary>
    string FilesEngine()
    {
        if (Setting("compute_mode") == "api") return "cloud";
        string engine = Setting("vulkan.engine"), model = Setting("vulkan.model");
        return engine is "whispercpp" or "whisper" ? (model == "large-v2" || Paths.WhisperOnCuda ? "large-v2" : "turbo") : "gigaam";
    }

    static string CloudName(string provider) => provider switch
    {
        "xai" => "xAI", "gemini" => "Gemini", "assemblyai" => "AssemblyAI", "gigachat" => "GigaChat", _ => "OpenAI",
    };

    /// <summary>A module of SETUP is in place; while the components are not read yet, nothing is held back.</summary>
    bool Installed(string module) => _components is null
        || ComponentList().Any(component => Field(component, "id") == module && Field(component, "state") == "installed");

    Control FilesView()
    {
        var page = new StackPanel { Spacing = 10, Margin = new Thickness(12, 10, 12, 12) };

        var adds = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        adds.Children.Add(Small("ДОБАВИТЬ ФАЙЛЫ", "file", "Выбрать записи и видео: звук, MP4, MOV, MKV и другие.", PickFiles, "files_add"));
        adds.Children.Add(Small("ДОБАВИТЬ ПАПКУ", "folder", "Выбрать папку: в работу пойдут все записи и видео в ней.", PickFolder, "files_add_folder"));
        adds.Children.Add(Small("ОЧИСТИТЬ СПИСОК", "eraser", "Убрать всё из списка. Сами файлы не трогаются.", () => { _inputs.Clear(); _fileStates.Clear(); ShowSection(); }, "files_clear"));
        page.Children.Add(adds);

        _fileList = new StackPanel { Spacing = 3 };
        if (_inputs.Count == 0)
            _fileList.Children.Add(new TextBlock
            {
                Classes = { "note" }, TextWrapping = TextWrapping.Wrap,
                Text = Lang.T("Список пуст. Добавьте файлы или папку: расшифровка ляжет рядом с каждым исходником - .md, .txt, субтитры и .transcript.json."),
            });
        foreach (var input in _inputs.ToList())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Tag = Directory.Exists(input) ? "folder" : "file", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock { Text = Paths.Short(input), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var drop = Tool("x", "Убрать из списка. Файл не трогается.", () => { _inputs.Remove(input); ShowSection(); });
            Grid.SetColumn(drop, 2);
            row.Children.Add(drop);
            _fileList.Children.Add(row);
        }
        page.Children.Add(new Border { Classes = { "subcard" }, Child = _fileList });

        // The choices of the run under ADVANCED, in blocks as SETTINGS has them; each switch is a key of the settings.
        var formats = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        foreach (var (key, label) in new[] { ("markdown", "MD"), ("txt", "TXT"), ("srt", "SRT"), ("vtt", "VTT"), ("json", "JSON") })
        {
            bool on = Setting("exports." + key) == "true";
            var chip = new ToggleButton { Classes = { "switch" }, Content = label, IsChecked = on };
            ToolTip.SetTip(chip, Lang.T(key == "json" ? "Полная расшифровка с метками времени: из неё пересобираются остальные файлы." : "Писать этот файл рядом с исходником."));
            chip.Click += (_, _) => Set("exports." + key, on ? "false" : "true");
            formats.Children.Add(chip);
        }
        page.Children.Add(Advanced("files", FilesSummary(), new (string, Control, bool)[]
        {
            ("ЛОКАЛЬНЫЕ МОДЕЛИ", LocalFileModels(), true),
            ("API: СЕРВИСЫ И МОДЕЛИ", CloudFileModels(), true),
            ("РАСПОЗНАВАНИЕ", Rows(ChipRow("ЯЗЫК", "language", new[] { ("auto", "АВТО"), ("ru", "RU"), ("en", "EN") }, "Язык речи. АВТО - движок определит сам."),
                SpeakerRows()), false),
            ("РЕЗУЛЬТАТ", Rows(Labelled("ФАЙЛЫ", formats), ChipRow("ГОТОВЫЕ", "pipeline.force", new[] { ("false", "ПРОПУСКАТЬ"), ("true", "ПЕРЕДЕЛАТЬ") },
                "Файл, у которого расшифровка уже лежит рядом, пропускается; ПЕРЕДЕЛАТЬ - распознать заново.")), false),
        }));

        _runFiles = new Button { Classes = { "run" }, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 6) };
        _runFiles.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { Icons.Sign("play"), new TextBlock { Text = Lang.T("ЗАПУСТИТЬ") } } };
        Avalonia.Automation.AutomationProperties.SetAutomationId(_runFiles, "files_run");
        ToolTip.SetTip(_runFiles, Lang.T("Распознать файлы списка. Ход каждого файла - здесь и в журнале; СТОП внизу останавливает, готовые куски не пропадают."));
        _runFiles.Click += (_, _) => { _fileStates.Clear(); FillFileStates(); Begin(Lang.T("Расшифровка файлов"), _inputs.ToList(), new Dictionary<string, string>(_sets)); };
        page.Children.Add(_runFiles);

        _fileStatesList = new StackPanel { Spacing = 2 };
        page.Children.Add(_fileStatesList);
        FillFileStates();
        ApplyFilesBusy();
        if (_told is null && Door.Ready)
            page.Children.Add(new TextBlock { Classes = { "note" }, Text = Lang.T("Читаю настройки программы…") });
        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = page };
    }

    Control ChipRow(string title, string key, (string Value, string Label)[] choices, string tip)
    {
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        var now = Setting(key);
        foreach (var (value, label) in choices)
        {
            var chip = new ToggleButton { Classes = { "switch" }, Content = Lang.T(label), IsChecked = string.Equals(now, value, StringComparison.OrdinalIgnoreCase) };
            ToolTip.SetTip(chip, Lang.T(tip));
            chip.Click += (_, _) => Set(key, value);
            chips.Children.Add(chip);
        }
        return Labelled(title, chips);
    }

    /// <summary>
    /// What recognizes the files - vulkan.engine with its vulkan.model, set together as the 2.3 window set them: GigaAM,
    /// or whisper.cpp with turbo or large-v2 (on the card the pack of SETUP has, CUDA or the processor). A Whisper SETUP
    /// has not put in place is shown and not offered. The CUDA pack goes with large-v2 alone: turbo repeats phrases on
    /// CUDA, so it is not offered there, and the engine takes large-v2 for a turbo the settings still hold.
    /// </summary>
    Control LocalFileModels()
    {
        bool cuda = Paths.WhisperOnCuda;
        string now = FilesEngine();
        bool whisper = File.Exists(Path.Combine(Paths.Root, "Tools", "whispercpp", "whisper-server.exe"))
            || File.Exists(Path.Combine(Paths.Root, "Tools", "whispercpp", "whisper-cli.exe"));
        bool Model(string file) => whisper && File.Exists(Path.Combine(Paths.Root, "models", file));
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        var choices = new List<(string Value, string Label, bool Ready)> { ("gigaam", "GIGAAM · ONNX", true) };
        if (!cuda) choices.Add(("turbo", "WHISPER TURBO · WHISPER.CPP", Model("ggml-large-v3-turbo.bin")));
        choices.Add(("large-v2", "WHISPER LARGE-V2 · WHISPER.CPP", Model("ggml-large-v2.bin")));
        foreach (var (value, label, ready) in choices)
        {
            var tip = !ready ? "Не установлен: SETUP." : value switch
            {
                "gigaam" => "GigaAM v3 RNN-T на этой машине (ONNX Runtime: видеокарта или процессор) - русская речь.",
                "turbo" => "Whisper large-v3-turbo на whisper.cpp, на процессоре - английская и смешанная речь.",
                _ => cuda ? "Whisper large-v2 на whisper.cpp, на видеокарте NVIDIA (CUDA) - английская и смешанная речь."
                    : "Whisper large-v2 на whisper.cpp - точнее на трудных записях, но в разы тяжелее.",
            };
            var chosen = value;
            // The engine of the machine runs in compute_mode vulkan: set with it, so a program held in another mode follows.
            chips.Children.Add(PageChip(Lang.T(label), now == value, Lang.T(tip), () =>
            {
                if (chosen == "gigaam") SetMany(("compute_mode", "vulkan"), ("vulkan.engine", "gigaam"), ("vulkan.model", "gigaam-v3-e2e-rnnt"), ("vulkan.backend", null));
                else SetMany(("compute_mode", "vulkan"), ("vulkan.engine", "whispercpp"), ("vulkan.model", chosen), ("vulkan.backend", "auto"));
            }, "files_engine_" + value, ready));
        }
        return Rows(chips, now == "gigaam"
            ? ChipRow("ПАУЗЫ (VAD)", "transcription.vad", new[] { ("true", "SILERO VAD"), ("false", "РОВНО ПО ВРЕМЕНИ") },
                "Где GigaAM режет длинную запись на куски по 12-25 секунд: SILERO VAD - в паузах речи, РОВНО ПО ВРЕМЕНИ - в самом тихом месте окна. Звук не выбрасывается ни в одном случае: тихая речь тоже доходит до модели.")
            : now == "cloud" ? Quiet(Lang.T("Сейчас файлы идут в облако; модель этой машины - здесь.")) : null);
    }

    /// <summary>
    /// FILES in the cloud: the service by its key - the engine knows OpenAI, xAI, Gemini, AssemblyAI and GigaChat - and its
    /// model: the list of OpenAI is its own, asked as the dictation's is; xAI and AssemblyAI choose theirs themselves.
    /// </summary>
    Control CloudFileModels()
    {
        bool cloud = FilesEngine() == "cloud";
        var provider = Setting("stt.provider") is { Length: > 0 } set ? set : "openai";
        var services = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        foreach (var id in new[] { "openai", "xai", "gemini", "assemblyai", "gigachat" })
        {
            bool keyed = CloudKeys.Has(Paths.Root, id);
            var chosen = id;
            services.Children.Add(PageChip(Upper(CloudName(id)), cloud && provider == id,
                keyed ? Lang.F("Расшифровка сервисом {0} по его ключу; запись уходит в сеть.", CloudName(id)) : Lang.F("Нет ключа {0} - SETUP, КЛЮЧИ СЕРВИСОВ.", CloudName(id)),
                () => SetMany(("compute_mode", "api"), ("stt.provider", chosen)), "files_cloud_" + id, keyed));
        }
        if (!cloud) return Rows(services, Quiet(Lang.T("Выберите сервис - файлы пойдут в облако по его ключу; модель - из списка самого сервиса.")));
        Control model = provider switch
        {
            "openai" => ModelPicker("https://api.openai.com/v1", ModelCatalog.Kind.Transcribe, Setting("stt.model"), value => Set("stt.model", value), "files",
                "gpt-transcribe", "gpt-4o-transcribe-diarize"),
            "gemini" => Address(Setting("stt.gemini_model"), value => Set("stt.gemini_model", value), "Модель Gemini, например gemini-3.5-flash.", url: false),
            "gigachat" => Fact(Setting("stt.gigachat_model") is { Length: > 0 } giga ? giga : "GigaChat"),
            _ => Fact(Lang.T("модель выбирает сам сервис")),
        };
        return Rows(services, Labelled("МОДЕЛЬ", model));
    }

    /// <summary>
    /// SPEAKERS: not separated, or by the engine of this machine - Community-1 (pyannote, the key of Hugging Face; CUDA or the
    /// processor), Sortformer (Vulkan, up to four), or АВТО: Community-1 where it is installed. How many speak, when known,
    /// helps Community-1. A service of the cloud separates the speakers itself.
    /// </summary>
    Control SpeakerRows()
    {
        bool on = Setting("diarization.enabled") == "true";
        if (FilesEngine() == "cloud")
        {
            return Rows(ChipRow("ГОВОРЯЩИЕ", "diarization.enabled", new[] { ("false", "НЕ РАЗДЕЛЯТЬ"), ("true", "РАЗДЕЛИТЬ") },
                "Разделить расшифровку по говорящим: в облаке это делает сам сервис (OpenAI - моделью gpt-4o-transcribe-diarize)."));
        }
        var engine = Setting("diarization.engine") is { Length: > 0 } set ? set.Replace("sherpa", "sortformer") : "auto";
        string now = on ? engine : "off";
        bool community = Installed("gpu"), sortformer = Installed("speakers");
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        foreach (var (value, label, ready, tip) in new[]
                 {
                     ("off", "НЕ РАЗДЕЛЯТЬ", true, "Расшифровка одним текстом, без говорящих."),
                     ("auto", "АВТО", community || sortformer, "Community-1, где он установлен; иначе Sortformer."),
                     ("pyannote", "COMMUNITY-1", community, "pyannote Community-1: точнее, нужен ключ Hugging Face; на видеокарте NVIDIA или на процессоре."),
                     ("sortformer", "SORTFORMER", sortformer, "Sortformer на Vulkan или процессоре: до четырёх говорящих, без ключа."),
                 })
        {
            var chosen = value;
            chips.Children.Add(PageChip(Lang.T(label), now == value, Lang.T(ready ? tip : "Не установлен: SETUP."),
                () => SetMany(chosen == "off" ? new (string, string?)[] { ("diarization.enabled", "false") }
                    : new (string, string?)[] { ("diarization.enabled", "true"), ("diarization.engine", chosen) }), "files_speakers_" + value, ready));
        }
        var speakers = Labelled("ГОВОРЯЩИЕ", chips);
        if (!on) return Rows(speakers);
        var count = int.TryParse(Setting("diarization.speakers"), out var known) ? known : 0;
        var counts = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        foreach (var n in new[] { 0, 2, 3, 4, 5, 6, 7, 8 })
        {
            var chosen = n;
            counts.Children.Add(PageChip(n == 0 ? Lang.T("САМ") : n.ToString(System.Globalization.CultureInfo.InvariantCulture), count == n,
                Lang.T(n == 0 ? "Сколько говорит людей, движок находит сам." : "Сколько людей говорит в записи: Community-1 берёт это число; Sortformer находит до четырёх сам и числа не берёт."),
                () => Set("diarization.speakers", chosen.ToString(System.Globalization.CultureInfo.InvariantCulture)), "files_count_" + n));
        }
        return Rows(speakers, Labelled("СКОЛЬКО", counts));
    }

    Control Labelled(string title, Control body)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        row.Children.Add(new TextBlock { Classes = { "caption" }, Text = Lang.T(title), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 8, 0), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    Button Small(string text, string sign, string tip, Action press, string id)
    {
        var button = new Button { Classes = { "small" } };
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icons.Sign(sign), new TextBlock { Text = Lang.T(text) } } };
        ToolTip.SetTip(button, Lang.T(tip));
        Avalonia.Automation.AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => press();
        return button;
    }

    async void PickFiles()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = Lang.T("Записи и видео"),
            FileTypeFilter = new[] { new FilePickerFileType(Lang.T("Звук и видео")) { Patterns = MediaKinds }, FilePickerFileTypes.All },
        });
        foreach (var file in picked)
            if (file.TryGetLocalPath() is { } path && !_inputs.Contains(path, StringComparer.OrdinalIgnoreCase)) _inputs.Add(path);
        ShowSection();
    }

    async void PickFolder()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Lang.T("Папка с записями") });
        foreach (var folder in picked)
            if (folder.TryGetLocalPath() is { } path && !_inputs.Contains(path, StringComparer.OrdinalIgnoreCase)) _inputs.Add(path);
        ShowSection();
    }

    void ApplyFilesBusy()
    {
        if (_runFiles is { } run) run.IsEnabled = !_runner.Running && Door.Ready && _inputs.Count > 0;
    }

    void FileProgress(DoorEvent said)
    {
        if (said.Path.Length == 0) return;
        var state = _fileStates.TryGetValue(said.Path, out var was) ? was.State : "processing";
        _fileStates[said.Path] = (state, Math.Max(0, said.Share));
        FillFileStates();
    }

    void FileState(DoorEvent said)
    {
        if (said.Path.Length == 0) return;
        var share = said.State == "completed" ? 1.0 : _fileStates.TryGetValue(said.Path, out var was) ? was.Share : 0;
        _fileStates[said.Path] = (said.State, share);
        FillFileStates();
        // The end of a file into the library: a recording takes it on itself, any other file is kept as a file.
        if (said.State is "completed" or "failed" or "skipped")
        {
            var (path, state) = (said.Path, said.State);
            Journal.Post(() => Library.Current?.PutFile(path, state));
        }
    }

    void FilesFinished(string outcome)
    {
        FillFileStates();
        if (outcome == "done") FirstWork("files");
    }

    void FillFileStates()
    {
        if (_fileStatesList is not { } list) return;
        list.Children.Clear();
        foreach (var (path, (state, share)) in _fileStates)
        {
            var (word, tone) = state switch
            {
                "completed" => (Lang.T("готово"), "good"),
                "failed" => (Lang.T("ошибка - причина в журнале"), "bad"),
                "skipped" => (Lang.T("пропущен: расшифровка уже есть"), "quiet"),
                _ => (Lang.F("идёт {0}%", Math.Round(share * 100).ToString(CultureInfo.CurrentCulture)), "warn"),
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis });
            var mark = new TextBlock { Classes = { "num", "fact", tone }, Text = word, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(mark, 1);
            row.Children.Add(mark);
            list.Children.Add(row);
        }
    }
}
