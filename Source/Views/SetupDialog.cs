using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AudionVoiceTools.Core;

namespace AudionVoiceTools.Views;

/// <summary>
/// What the program recommends for this machine and is not installed yet, and the offer to install it now - the question
/// at the start of the window, as Audion VS Engine asks it. Why the engines come apart - a few points; then a block of
/// each component - a tick to take it or leave it, its sign, its name, the size of its download, what it is for and its
/// installer, the type smaller under the name; the block of the engine of dictation has the model to choose, GigaAM v3
/// first - the best for Russian; the total of what is ticked; INSTALL: the components are installed one after another,
/// as SETUP installs them. The program is built without models (the owner, 20.10.2026: «Это не должно скачиваться при
/// сборке - опять же предлагать выбор при первой загрузке»), so this question is where they come from. At the start a
/// tick INSTALL LATER stands beside it: ticked, it says where SETUP is and the button becomes UNDERSTOOD. From SETUP
/// itself CANCEL stands in place of the tick.
/// </summary>
public sealed class SetupDialog : Window
{
    /// <summary>A component to install: its id, its face (a brand mark or a sign), its name, what it is for, the size of its download, its installer, the download in megabytes.</summary>
    public sealed record Item(string Id, Control Face, string Name, string Why, string Size, string Script, int Megabytes);

    /// <summary>A model of dictation to choose in the block of the engine: its id, its module, its name, what it is for, its size.</summary>
    public sealed record ModelOption(string Id, string Module, string Title, string About, string Size, int Megabytes);

    /// <summary>
    /// What was agreed to: the modules in their order, and the model of dictation chosen ("" - none was offered); Open - OPEN
    /// SETUP was pressed: nothing is installed, the sheet SETUP is opened.
    /// </summary>
    public sealed record Choice(IReadOnlyList<string> Modules, string Model, bool Open = false);

#if DEBUG
    /// <summary>A check that pictures the question: called once it is open; it closes the question itself.</summary>
    public static Func<SetupDialog, Task>? DevShown;
    CheckBox? _tick;
    public void DevTick() { if (_tick is not null) _tick.IsChecked = true; }
    public void DevClose() => Close(null);
    Button? _open;
    /// <summary>OPEN SETUP pressed as a hand presses it: false when the question has none (asked from SETUP).</summary>
    public bool DevOpen()
    {
        if (_open is null) return false;
        _open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return true;
    }
#endif

    /// <summary>The sizes of the type: the head, the name of a block, its text, its small print.</summary>
    const double TypeHead = 15, TypeName = 13.5, TypeText = 12, TypeSmall = 11;

    readonly IReadOnlyList<Item> _items;
    readonly Dictionary<string, CheckBox> _picks = new();
    readonly Func<int, string> _totalText;
    readonly TextBlock _total = new() { FontSize = TypeText, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly Button _install;
    /// <summary>The model chosen in the block of the engine of dictation; null - the block is not asked about.</summary>
    ModelOption? _model;

    SetupDialog(Window owner, IReadOnlyList<Item> items, IReadOnlyList<ModelOption> models, Func<int, string> totalText, bool later)
    {
        _items = items;
        _totalText = totalText;
        _install = new Button { Name = "SetupInstall", Content = Lang.T("УСТАНОВИТЬ"), Classes = { "run" }, MinWidth = 118, MinHeight = 30 };
        if (models.Count > 0 && items.Any(item => item.Id == "dictation")) _model = models[0];
        var title = Lang.T("Установка движков и моделей");
        Title = title;
        Width = 660;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // A walk by UI Automation or the pictures of a check: the question stands where its window does, off the screen,
        // and takes no focus.
        bool unseen = Program.Walk;
#if DEBUG
        unseen |= MainWindow.ShotFolder.Length > 0;
#endif
        if (unseen)
        {
            ShowActivated = false;
            Opened += (_, _) => Position = new PixelPoint(owner.Position.X + 40, owner.Position.Y + 40);
        }
        Icon = owner.Icon;
        this.Ink(BackgroundProperty, "AppBg").Ink(ForegroundProperty, "TextMain").Ink(FontFamilyProperty, "FontUi");
        FontSize = 13;
        Classes.Set("vault", owner.Classes.Contains("vault"));

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = title, FontSize = TypeHead, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });

        // Why the engines come apart: a step of the installation, not a fault.
        var points = new StackPanel { Spacing = 3 };
        foreach (var point in new[]
                 {
                     "Движки распознавания и их модели не входят в программу: у каждого своя лицензия.",
                     "Они ставятся с сайтов авторов - GitHub и Hugging Face - в папку программы; в Windows ничего не пишется.",
                     "Ход установки виден в журнале окна; СТОП прерывает её.",
                 })
            points.Children.Add(Bullet(Lang.T(point)));
        body.Children.Add(points);

        var list = new StackPanel { Spacing = 6 };
        list.Children.Add(new TextBlock { Classes = { "caption" }, Text = Lang.T("ЧТО УСТАНОВИТЬ") });
        foreach (var item in items) list.Children.Add(Block(item, item.Id == "dictation" ? models : Array.Empty<ModelOption>()));
        body.Children.Add(list);
        body.Children.Add(_total);
        Recount();

        var hint = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), IsVisible = false,
            Child = new TextBlock
            {
                FontSize = TypeText, TextWrapping = TextWrapping.Wrap,
                Text = Lang.T("Установить можно в любой момент: кнопка SETUP в шапке окна, справа - рядом с RU и EN. В SETUP у каждого компонента своя кнопка УСТАНОВИТЬ, а УСТАНОВИТЬ ВСЁ ставит рекомендованные по порядку."),
            }.Ink(TextBlock.ForegroundProperty, "AmberInk"),
        }.Ink(Border.BorderBrushProperty, "AmberInk");
        Avalonia.Automation.AutomationProperties.SetAutomationId(hint.Child!, "setup_hint");

        var install = _install;
        Avalonia.Automation.AutomationProperties.SetAutomationId(install, "setup_install");
        install.Click += (_, _) => Close(Agreed());
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 9 };
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2, 0, 0) };
        Grid.SetColumn(row, 1);
        foot.Children.Add(row);
        if (later)
        {
            var close = new Button { Name = "SetupClose", Content = Lang.T("ПОНЯТНО"), MinWidth = 118, MinHeight = 30, IsVisible = false };
            Avalonia.Automation.AutomationProperties.SetAutomationId(close, "setup_close");
            close.Click += (_, _) => Close(null);
            // OPEN SETUP: the sheet itself, one press away; in sight whatever the tick says.
            var open = new Button { Name = "SetupOpen", Content = Lang.T("ОТКРЫТЬ SETUP"), MinWidth = 118, MinHeight = 30 };
            Avalonia.Automation.AutomationProperties.SetAutomationId(open, "setup_open");
            open.Click += (_, _) => Close(new Choice(Array.Empty<string>(), "", Open: true));
            row.Children.Add(open);
#if DEBUG
            _open = open;
#endif
            var tick = new CheckBox { Content = Lang.T("Установить позже"), VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetAutomationId(tick, "setup_later");
            tick.IsCheckedChanged += (_, _) =>
            {
                bool wait = tick.IsChecked == true;
                hint.IsVisible = close.IsVisible = wait;
                install.IsVisible = !wait;
                (wait ? (Control)close : install).Focus();
            };
            foot.Children.Add(tick);
            row.Children.Add(close);
#if DEBUG
            _tick = tick;
#endif
        }
        else
        {
            var cancel = new Button { Name = "SetupCancel", Content = Lang.T("ОТМЕНА"), MinWidth = 118, MinHeight = 30, IsCancel = true };
            Avalonia.Automation.AutomationProperties.SetAutomationId(cancel, "setup_cancel");
            cancel.Click += (_, _) => Close(null);
            row.Children.Add(cancel);
        }
        row.Children.Add(install);
        body.Children.Add(hint);
        body.Children.Add(foot);
        Opened += (_, _) => install.Focus();
#if DEBUG
        Opened += async (_, _) => { if (DevShown is { } shown) { DevShown = null; await shown(this); } };
#endif

        Content = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(10), Padding = new Thickness(18, 15),
            Child = body,
        }.Ink(Border.BackgroundProperty, "PanelBg").Ink(Border.BorderBrushProperty, "BorderSoft");
    }

    /// <summary>A point: the dot at the left, the text wrapping under itself, not under the dot.</summary>
    static Control Bullet(string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*") };
        grid.Children.Add(new TextBlock { Text = "•", FontSize = TypeText }.Ink(TextBlock.ForegroundProperty, "TextMuted"));
        var said = new TextBlock { Text = text, FontSize = TypeText, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(said, 1);
        grid.Children.Add(said);
        return grid;
    }

    /// <summary>
    /// A component in a block of its own, a step off the ground of the question: its tick, its face and name, the size at
    /// the right; what it is for under them, smaller; the model to choose, for the engine of dictation; its installer under
    /// that, smaller and quieter. A block left unticked goes quiet.
    /// </summary>
    Control Block(Item item, IReadOnlyList<ModelOption> models)
    {
        var pick = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        Avalonia.Automation.AutomationProperties.SetAutomationId(pick, "setup_pick_" + item.Id);
        _picks[item.Id] = pick;
        item.Face.VerticalAlignment = VerticalAlignment.Center;
        item.Face.Margin = new Thickness(0, 0, 8, 0);
        var name = new TextBlock { Text = item.Name, FontSize = TypeName, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetAutomationId(name, "setup_item_" + item.Id);
        var size = new TextBlock { Text = models.Count > 0 ? models[0].Size : item.Size, FontSize = TypeText, VerticalAlignment = VerticalAlignment.Center }.Ink(TextBlock.ForegroundProperty, "TextMuted");
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        head.Children.Add(pick);
        Grid.SetColumn(item.Face, 1);
        head.Children.Add(item.Face);
        Grid.SetColumn(name, 2);
        head.Children.Add(name);
        Grid.SetColumn(size, 3);
        head.Children.Add(size);
        var why = new TextBlock { Text = item.Why, FontSize = TypeText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var script = new TextBlock { Text = item.Script, FontSize = TypeSmall, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) }
            .Ink(TextBlock.ForegroundProperty, "TextMuted");
        var stack = new StackPanel { Children = { head, why } };
        if (models.Count > 0)
        {
            // The model of dictation: a select across the block, a line a model - its name and size, what it is for under it.
            stack.Children.Add(new TextBlock { Classes = { "caption" }, Text = Lang.T("МОДЕЛЬ ДИКТОВКИ"), Margin = new Thickness(0, 8, 0, 3) });
            var select = new ComboBox
            {
                ItemsSource = models, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 420,
                ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ModelOption>((option, _) => option is null ? new TextBlock() : new StackPanel
                {
                    Spacing = 1, Margin = new Thickness(0, 2),
                    Children =
                    {
                        new TextBlock { Text = option.Title + "  ·  " + option.Size, FontSize = TypeText, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = option.About, FontSize = TypeSmall, TextWrapping = TextWrapping.Wrap }.Ink(TextBlock.ForegroundProperty, "TextMuted"),
                    },
                }),
            };
            Avalonia.Automation.AutomationProperties.SetAutomationId(select, "setup_model");
            select.SelectionChanged += (_, _) =>
            {
                if (select.SelectedItem is not ModelOption option) return;
                _model = option;
                size.Text = option.Size;
                Recount();
            };
            stack.Children.Add(select);
        }
        stack.Children.Add(script);
        var block = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8),
            Child = stack,
        }.Ink(Border.BackgroundProperty, "SwitchBg").Ink(Border.BorderBrushProperty, "BorderSoft");
        pick.IsCheckedChanged += (_, _) =>
        {
            bool taken = pick.IsChecked == true;
            why.Opacity = script.Opacity = name.Opacity = size.Opacity = taken ? 1 : 0.5;
            foreach (var child in stack.Children.Skip(2)) child.IsEnabled = taken;
            Recount();
        };
        return block;
    }

    /// <summary>The download of what is ticked, the engine of dictation by the model chosen; INSTALL only with something ticked.</summary>
    void Recount()
    {
        int total = 0;
        foreach (var item in _items)
            if (_picks.TryGetValue(item.Id, out var pick) && pick.IsChecked == true)
                total += item.Id == "dictation" && _model is { } model ? model.Megabytes : item.Megabytes;
        _total.Text = _totalText(total);
        _install.IsEnabled = _picks.Values.Any(pick => pick.IsChecked == true);
    }

    /// <summary>The modules ticked, in their order - the engine of dictation as the module of the model chosen - and that model.</summary>
    Choice Agreed()
    {
        var modules = new List<string>();
        foreach (var item in _items)
        {
            if (!_picks.TryGetValue(item.Id, out var pick) || pick.IsChecked != true) continue;
            modules.Add(item.Id == "dictation" && _model is { } model ? model.Module : item.Id);
        }
        bool dictation = _picks.TryGetValue("dictation", out var engine) && engine.IsChecked == true;
        return new Choice(modules, dictation && _model is { } chosen ? chosen.Id : "");
    }

    /// <summary>
    /// What to install now, or null - later: INSTALL LATER and UNDERSTOOD, CANCEL, or the cross. <paramref name="models"/>:
    /// the models of dictation to choose in the block of its engine, the default first. <paramref name="later"/>: the tick
    /// INSTALL LATER stands beside INSTALL (the start of the window); without it - CANCEL (SETUP).
    /// </summary>
    public static Task<Choice?> Ask(Window owner, IReadOnlyList<Item> items, IReadOnlyList<ModelOption> models, Func<int, string> totalText, bool later) =>
        new SetupDialog(owner, items, models, totalText, later).ShowDialog<Choice?>(owner);
}
