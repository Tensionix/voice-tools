using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AudionVoiceTools.Core;

namespace AudionVoiceTools;

/// <summary>
/// SETTINGS and ABOUT, the way the family has them: two buttons at the right of the header, each opens its sheet in place
/// of the sections; the same button, the cross or Esc brings the sections back. A sheet is cards with a name in two
/// columns.
/// </summary>
public partial class MainWindow
{
    string _sheet = "";

    void ToggleSheet(string key)
    {
        _sheet = _sheet == key ? "" : key;
        SheetSettings.IsChecked = _sheet == "settings";
        SheetAbout.IsChecked = _sheet == "about";
        SetupButton.IsChecked = _sheet == "setup";
        bool open = _sheet.Length > 0;
        SectionRow.IsVisible = Zones.IsVisible = !open;
        SheetHost.IsVisible = open;
        FillSheet();
        if (!open) Shell.Focus();
    }

    /// <summary>A section of the manager asked from outside (the menu of the tray: «История»): a sheet open is closed first.</summary>
    public void OpenSection(string section)
    {
        if (_sheet.Length > 0) ToggleSheet(_sheet);
        _section = section;
        ShowSection();
    }

    /// <summary>A sheet asked from outside (the menu of the tray: «Настройки»).</summary>
    public void OpenSheet(string sheet)
    {
        if (_sheet != sheet) ToggleSheet(sheet);
    }

    /// <summary>The sheet in sight is written again: a setting changed, or the language did.</summary>
    void FillSheet()
    {
        SheetBody.Children.Clear();
        _into = null;
        if (_sheet == "settings") FillSettings();
        else if (_sheet == "about") FillAbout();
        else if (_sheet == "setup") FillSetup();
        else return;
        if (Lang.Code != "ru") Lang.Walk(SheetBody);
    }

    // ------------------------------------------------------------ what a sheet is put together of

    Panel? _into;

    void Add(Control control) => (_into ?? SheetBody).Children.Add(control);

    void Head(string text) => SheetBody.Children.Add(new TextBlock
    {
        Text = text, FontWeight = FontWeight.SemiBold, FontSize = 16, Margin = new Thickness(0, 0, 0, 8),
    });

    (StackPanel Left, StackPanel Right) Columns()
    {
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        var left = new StackPanel { Spacing = 10 };
        var right = new StackPanel { Spacing = 10 };
        Grid.SetColumn(right, 2);
        columns.Children.Add(left);
        columns.Children.Add(right);
        _into = null;
        SheetBody.Children.Add(columns);
        return (left, right);
    }

    StackPanel Below()
    {
        var below = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _into = null;
        SheetBody.Children.Add(below);
        return below;
    }

    void Open(Panel column, string title)
    {
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock { Text = title, Classes = { "caption" }, Margin = new Thickness(0, 0, 0, 4) });
        column.Children.Add(new Border { Classes = { "subcard" }, Child = body });
        _into = body;
    }

    void Note(string text) => Add(new TextBlock { Text = text, Classes = { "note" }, TextWrapping = TextWrapping.Wrap });

    void Pair(string key, string value, int keyWidth = 130)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions($"{keyWidth},*") };
        row.Children.Add(new TextBlock { Text = key, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) });
        var shown = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(shown, 1);
        row.Children.Add(shown);
        Add(row);
    }

    static WrapPanel Chips() => new() { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6, Margin = new Thickness(0, 2, 0, 4) };

    ToggleButton Chip(string label, bool chosen, string tip, Action press)
    {
        var chip = new ToggleButton { Content = label, Classes = { "switch" }, IsChecked = chosen };
        if (tip.Length > 0) ToolTip.SetTip(chip, Lang.T(tip));
        chip.Click += (_, _) => press();
        return chip;
    }

    // ------------------------------------------------------------ SETTINGS

    static readonly string[] WindowFonts = { "JetBrains Mono", "Inter", "Segoe UI", "Segoe UI Variable Text", "Arial", "Tahoma", "Verdana", "Calibri", "Cascadia Mono", "Consolas" };
    static readonly string[] TerminalFonts = { "JetBrains Mono", "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New" };

    /// <summary>
    /// SETTINGS - what is set once or twice (the owner, 20.10.2026: «убирай из него то, что будет меняться не один-два раза»):
    /// the hotkeys, how long the sound of dictations is kept, the colours and the fonts. The engine, the models, the mode of the
    /// text, the microphone and the panel stand on DICTATIONS, the transcription of recordings on RECORDINGS and FILES, the
    /// dictionary on its own page.
    /// </summary>
    void FillSettings()
    {
        Head("SETTINGS");
        var (left, right) = Columns();

        Open(left, "ХОТКЕИ");
        HotkeyTable(("Диктовка - начать и закончить", "Right Alt + F12"), ("Диктовка - отменить", "Esc"),
            ("Запись - начать и сохранить", "Right Alt + F11"));
        Note("Движок, модели, обработка текста, микрофон и плавающая панель - в разделе «Диктовки», расшифровка записей - в «Записях» и «Файлах», словарь - в «Словаре». Здесь - то, что задают один раз.");

        // The term of the sound of dictations - kept in the library itself, not in the look of the window.
        Open(left, "ЗВУК ДИКТОВОК");
        if (Library.Current is { } library)
        {
            var terms = Chips();
            foreach (var (days, name, tip) in new[]
                     {
                         (7, "7 ДНЕЙ", "Звук хранится неделю, потом уходит в Корзину; текст остаётся."),
                         (30, "30 ДНЕЙ", "Звук хранится месяц, потом уходит в Корзину; текст остаётся."),
                         (-1, "ВСЕГДА", "Звук каждой диктовки хранится, пока его не удалят."),
                         (0, "НЕ ХРАНИТЬ", "Звук вставленной диктовки не пишется; отменённая и неудачная хранят свой до ближайшей уборки."),
                     })
            {
                var term = days;
                terms.Children.Add(Chip(name, library.AudioDays == term, tip, () =>
                {
                    Journal.Post(() =>
                    {
                        library.SetAudioDays(term);
                        library.Sweep();
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_sheet == "settings") FillSheet(); LoadDictations(); });
                    });
                }));
            }
            Add(terms);
            Note("Закреплённая диктовка хранит свой звук и после срока. Уборка - при старте программы и раз в сутки. Запись - 48 кГц моно в library\\recordings; после сбоя программы она восстанавливается при следующем старте.");
        }
        else Note("Библиотека открывается…");

        Open(right, "ЦВЕТА");
        var themes = Chips();
        foreach (var (look, name) in new[] { ("codedark", "AUDION CODE DARK"), ("vault", "AUDION VAULT DARK"), ("dark", "AUDION DARK"), ("light", "AUDION LIGHT") })
            themes.Children.Add(Chip(name, _settings.Look == look, "", () => SetTheme(look)));
        Add(themes);

        var installed = new HashSet<string>(FontManager.Current.SystemFonts.Select(font => font.Name), StringComparer.OrdinalIgnoreCase) { "Inter", "JetBrains Mono" };
        Open(right, "ШРИФТ ОКНА");
        FontRow(WindowFonts.Where(installed.Contains), _settings.UiFont, family => _settings.UiFont = family,
            13 + _settings.UiShift, 12, 15, size => _settings.UiShift = size - 13);
        Note("Размер — основного текста; названия, подписи и бейджи идут за ним.");

        Open(right, "ШРИФТ ЖУРНАЛА");
        FontRow(TerminalFonts.Where(installed.Contains), _settings.LogFont, family => _settings.LogFont = family,
            _settings.LogSize, 9, 14, size => _settings.LogSize = size);

        var reset = new Button { Content = Lang.T("К УМОЛЧАНИЯМ"), Classes = { "small" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        ToolTip.SetTip(reset, Lang.T("Вернуть тему, шрифты и размеры к значениям программы."));
        reset.Click += (_, _) =>
        {
            _settings.UiFont = _settings.LogFont = "";
            _settings.UiShift = 0;
            _settings.LogSize = Settings.UsualLogSize;
            ApplyFonts();
            SetTheme("codedark");
        };
        _into = null;
        SheetBody.Children.Add(reset);
    }

    /// <summary>The hotkeys as a table: what a key does, the key; a soft rule between the rows.</summary>
    void HotkeyTable(params (string Action, string Keys)[] rows)
    {
        var table = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var (action, keys) in rows)
        {
            if (table.Children.Count > 0) table.Children.Add(new Border { Classes = { "entrysep" }, Margin = new Thickness(0, 5), Opacity = 0.55 });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = Lang.T(action), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var key = new TextBlock { Text = keys, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            Grid.SetColumn(key, 1);
            row.Children.Add(key);
            table.Children.Add(row);
        }
        Add(table);
    }

    /// <summary>The families as chips, each written in itself, and the size in a spinner: half a point a step.</summary>
    void FontRow(IEnumerable<string> families, string chosen, Action<string> setFamily, double size, double min, double max, Action<double> setSize)
    {
        if (chosen.Length == 0) chosen = "JetBrains Mono";
        var row = Chips();
        foreach (var family in families)
        {
            var chip = new ToggleButton
            {
                Content = family, Classes = { "switch" }, FontFamily = App.Family(family),
                IsChecked = string.Equals(family, chosen, StringComparison.OrdinalIgnoreCase),
            };
            chip.Click += (_, _) => { setFamily(family); FontsChanged(); };
            row.Children.Add(chip);
        }
        // The size in a spinner: the number in the middle of its frame, a chevron up - larger, down - smaller.
        var value = new TextBlock { Text = size.ToString("0.#", CultureInfo.InvariantCulture), Width = 40, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        void Step(double delta)
        {
            var next = Math.Clamp(Math.Round((size + delta) * 2) / 2, min, max);
            if (next == size) return;
            setSize(next);
            FontsChanged();
        }
        RepeatButton Arrow(double angle, double delta, string tip)
        {
            // The chevron is turned in a holder of its own: the icon itself is set in its box by a transform of its own.
            var sign = new Panel { Width = 9, Height = 9, RenderTransform = new RotateTransform(angle), RenderTransformOrigin = RelativePoint.Center, Children = { Icons.Sign("chevron-right") } };
            var arrow = new RepeatButton { Classes = { "spin" }, Content = sign };
            ToolTip.SetTip(arrow, Lang.T(tip));
            arrow.Click += (_, _) => Step(delta);
            return arrow;
        }
        var arrows = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Arrow(-90, 0.5, "Крупнее"), Arrow(90, -0.5, "Мельче") } };
        // One piece of the row: a line that wraps takes the number and its arrows along.
        row.Children.Add(new Border
        {
            Classes = { "spin" }, Margin = new Thickness(12, 0, 0, 3), VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { value, arrows } },
        });
        Add(row);
    }

    void FontsChanged()
    {
        _settings.Save();
        ApplyFonts();
        FillSheet();
    }

    void ApplyFonts()
    {
        App.SetFonts(_settings.UiFont, _settings.LogFont);
        App.SetSizes(_settings.UiShift, _settings.LogSize);
    }

    // ------------------------------------------------------------ ABOUT

    static Avalonia.Media.Imaging.Bitmap Brand(string file) =>
        new(Avalonia.Platform.AssetLoader.Open(new Uri($"avares://AudionVoiceTools/Assets/brand/{file}")));

    static Image Sharp(Image image)
    {
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        return image;
    }

    static Button Link(string domain)
    {
        var link = new Button { Content = domain, Classes = { "crumb", "link" }, Padding = new Thickness(0, 1), Margin = new Thickness(0, 0, 16, 0) };
        link.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://" + domain) { UseShellExecute = true }); }
            catch { }
        };
        return link;
    }

    void FillAbout()
    {
        SheetBody.Children.Add(Sharp(new Image
        {
            Source = Brand(_settings.Look == "light" ? "Audion_Lockup_Light_1800.png" : "Audion_Lockup_Dark_1800.png"),
            Height = 56, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10),
        }));
        Head("Voice Tools");
        Note("Диктовка в любое окно и расшифровка записей. Хоткей - капсула вверху экрана слушает, текст встаёт туда, где курсор, буфер обмена возвращается. Записи и видео с диска расшифровываются рядом с исходником: .md, .txt, субтитры. Распознавание локальное - GigaAM для русского, - облако по своему ключу. Портативная: всё своё хранит рядом с собой.");
        _into = null;

        var (left, right) = Columns();
        Open(left, "ВЕРСИЯ И ПЛАТФОРМА");
        Pair("Версия", Paths.Version ?? "—");
        Pair("Система", RuntimeInformation.OSDescription);
        Pair(".NET", Environment.Version.ToString());
        Pair("Avalonia", typeof(AvaloniaObject).Assembly.GetName().Version?.ToString() ?? "");

        Open(right, "ПАПКИ");
        Pair("Программа", Paths.Root);
        Pair("Библиотека", Journal.Library);
        Pair("Настройки окна", Settings.FilePath);
        Pair("Журналы", Paths.ProgramLogs);

        Open(Below(), "АВТОР");
        var author = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 4) };
        author.Children.Add(Sharp(new Image { Source = Brand("Tensionix_Mark_Amber_256.png"), Height = 32, Width = 32 }));
        author.Children.Add(new TextBlock
        {
            Text = "Tensionix", FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        Add(author);
        var links = new StackPanel { Orientation = Orientation.Horizontal };
        links.Children.Add(Link("audion.dev"));
        links.Children.Add(Link("tensionix.com"));
        Add(links);
        _into = null;
    }
}
