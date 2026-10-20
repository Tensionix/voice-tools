using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace AudionVoiceTools;

/// <summary>
/// The shell of Audion Build Publisher (and through it of Audion Sync Tools and Audion Disk
/// Tools): the themes, the sizes and the styles of App.axaml, the tooltips and the signs. What
/// those programs paint for their file lists alone - grain, glass, zebra - is switched off here
/// by plain values of the same resources.
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Tooltips come after 1500 ms, as in every Audion program, and the neighbour gets no quick
        // show: moving along a row of buttons must not pop them all.
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            control.SetValue(ToolTip.ShowDelayProperty, 1500);
            control.SetValue(ToolTip.BetweenShowDelayProperty, 0);
        });
        // No sign is drawn by hand: the markup names a sign of the owner's set (Tag="hammer") and
        // gets its geometry here, from Icons.cs. Then every sign, now and later, is centred and
        // scaled the same way.
        Control.LoadedEvent.AddClassHandler<Avalonia.Controls.Shapes.Path>((path, _) =>
        {
            if (path.Data is null && path.Tag is string name && path.Classes.Contains("icon") && Icons.Glyph(name) is { } sign) path.Data = sign;
            CenterIcon(path);
            if (path.Classes.Contains("icon") && !Settled.TryGetValue(path, out var known))
            {
                Settled.Add(path, new Place());
                path.LayoutUpdated += (_, _) => SettleIcon(path);
            }
        });
        Avalonia.Controls.Shapes.Path.DataProperty.Changed.AddClassHandler<Avalonia.Controls.Shapes.Path>((path, _) => CenterIcon(path));
        // The mark of the chosen line of a menu is tangerine, as in Disk Tools and Sync Tools.
        Control.LoadedEvent.AddClassHandler<Avalonia.Controls.Shapes.Ellipse>((dot, _) =>
        {
            if (dot.Parent is ContentControl { Name: "PART_ToggleIconPresenter" } && Current?.TryGetResource("Tangerine", Current.ActualThemeVariant, out var ink) == true && ink is IBrush brush)
                dot.Fill = brush;
        });
    }

    /// <summary>How much larger than its 10 grid a sign is shown.</summary>
    const double IconScale = 1.26;

    /// <summary>
    /// A sign drawn off the middle of its 10 box is moved by the exact bounds of its drawing to
    /// the middle, then scaled around it.
    /// </summary>
    static void CenterIcon(Avalonia.Controls.Shapes.Path path)
    {
        if (!path.Classes.Contains("icon") || path.Data is not { } data) return;
        var bounds = data.Bounds;
        var dx = 5 - (bounds.X + bounds.Width / 2);
        var dy = 5 - (bounds.Y + bounds.Height / 2);
        path.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        // The third step is the half pixel the layout could not give (SettleIcon); it outlives a new drawing.
        var settle = path.RenderTransform is TransformGroup { Children.Count: 3 } old ? old.Children[2] : new TranslateTransform();
        path.RenderTransform = new TransformGroup
        {
            Children = { new TranslateTransform(dx, dy), new ScaleTransform(IconScale, IconScale), settle },
        };
    }

    sealed class Place { public string At = ""; }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Avalonia.Controls.Shapes.Path, Place> Settled = new();

    /// <summary>
    /// A button that holds a sign alone has it in its very middle, on the pixels of the screen.
    /// The layout puts the box of a sign on a whole pixel: at 150% a button is 36 pixels and the
    /// box 15, so the box stood half a pixel to the left and up. The half pixel is given back as a
    /// render offset; nothing is a constant - the sizes and the scale are read as they are.
    /// </summary>
    static void SettleIcon(Avalonia.Controls.Shapes.Path path)
    {
        if (!Settled.TryGetValue(path, out var place) || path.RenderTransform is not TransformGroup { Children.Count: 3 } group
            || group.Children[2] is not TranslateTransform settle || !path.IsEffectivelyVisible) return;
        if (path.FindAncestorOfType<Button>() is not { } host || TopLevel.GetTopLevel(path) is not { } top) return;
        double scale = top.RenderScaling, x = 0, y = 0;
        for (Visual? v = path; v != null && v != host; v = v.GetVisualParent()) { x += v.Bounds.X; y += v.Bounds.Y; }
        var key = $"{scale}|{host.Bounds.Size}|{x}|{y}|{path.Bounds.Size}";
        if (key == place.At) return;
        place.At = key;
        // A sign beside a word keeps the place the layout gave it: the word is its neighbour, not the button.
        bool alone = !host.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsVisible && !string.IsNullOrEmpty(text.Text));
        settle.X = alone ? host.Bounds.Width / 2 - (x + path.Bounds.Width / 2) : 0;
        settle.Y = alone ? host.Bounds.Height / 2 - (y + path.Bounds.Height / 2) : 0;
    }

    /// <summary>The hot path of dictation; null while the window is only put together for a check.</summary>
    public static Core.Dictation? Dictation { get; private set; }

    /// <summary>The manager of dictations and files: opened from the tray; closing it hides it, the program stays.</summary>
    public static MainWindow? Manager { get; private set; }

    /// <summary>True once the person asked the program to end: the manager closes for good then.</summary>
    public static bool Quitting { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        Resources["ListOpacity"] = 1.0;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Before the window: an error in its own constructor is written down too.
            Core.Guard.Install();
            var o = Program.Run;
#if DEBUG
            // A picture of the manager (AUDION_SHOT): the window off the screen alone - no capsule, no hotkey, no microphone.
            if (MainWindow.ShotFolder.Length > 0)
            {
                // AUDION_SHOT_DATA=FOLDER: the library of a stand in the pictures, never the program's own.
                if (System.Environment.GetEnvironmentVariable("AUDION_SHOT_DATA") is { Length: > 0 } data) Core.Journal.Start(data);
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Manager = new MainWindow(null);
                Manager.Show();
                base.OnFrameworkInitializationCompleted();
                return;
            }
#endif
            // A stand of the cloud serves xAI itself, on this machine: no key, no network.
            if (o.Stand && o.CloudStand.Length > 0)
            {
                o.CloudEndpoint = Core.Stand.FakeCloud(o.CloudStand);
                o.Cloud = "xai";
            }
            Dictation = new Core.Dictation(o);
            Manager = new MainWindow(Dictation);
            // The program lives in the tray: the capsule and the hotkey keep working with the manager closed.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => Dictation?.Dispose();
            if (!o.Stand)
            {
                Tray(desktop);
                desktop.MainWindow = Manager;
            }

            Dictation.Start();
            if (o.Stand)
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await Dictation.RunStand(o);
                    await System.Threading.Tasks.Task.Delay(300);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Quit(desktop));
                });
        }
        base.OnFrameworkInitializationCompleted();
    }

    static Core.Tray? _tray;

    /// <summary>
    /// The icon in the tray (Core\Tray.cs): a click opens the manager; the right button opens the menu of the program at the
    /// icon (Views\CapsuleMenu.cs, the colours of the capsule): open, start or save a recording - its red dot while it goes -,
    /// the engines of dictation with the green dot of the one in use, a cloud without its key said so, exit. Built anew at
    /// every right click, from what is so at that moment.
    /// </summary>
    void Tray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        static void OnWindow(System.Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);
        _tray = new Core.Tray("Audion Voice Tools", () => OnWindow(ShowManager),
            (x, y) => OnWindow(() => Views.CapsuleMenu.OpenAt(new PixelPoint(x, y), TrayMenu(desktop))));
        // The icon leaves the tray with the program, before anything else is let go.
        desktop.Exit += (_, _) => _tray?.Dispose();
    }

    /// <summary>
    /// The menu of the tray, after Letterly AI (the reference of the owner, 19.10.2026): paste the last dictation into the
    /// program that was in front, the history, the recorder; the engine, the microphone and the place of the capsule each
    /// in a menu of its own; the strip of the capsule shown or not; the manager, its settings, exit.
    /// </summary>
    List<Views.CapsuleMenu.Item?> TrayMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var items = new List<Views.CapsuleMenu.Item?>();
        if (Dictation is { } owner)
        {
            var last = Core.Dictation.LastText();
            var into = _tray?.LastApp ?? System.IntPtr.Zero;
            items.Add(new(Core.Lang.T("Вставить последнюю диктовку"), false, last.Length > 0, () => owner.Session.PasteTextInto(into, last), "clipboard-paste"));
            items.Add(new(Core.Lang.T("История"), false, true, () => { ShowManager(); Manager?.OpenSection("dictations"); }, "scroll-text"));
            items.Add(null);
            bool idle = owner.Recorder.State == Core.RecState.Idle;
            items.Add(new(Core.Lang.T(idle ? "Начать запись (Right Alt + F11)" : "Сохранить запись (Right Alt + F11)"), false, true, owner.ToggleRecording, "file-audio", Alarm: !idle));
            var engines = new List<Views.CapsuleMenu.Item?>();
            foreach (var id in Core.DictationConfig.Engines)
            {
                var provider = Core.DictationConfig.ProviderOf(id);
                bool keyed = provider.Length == 0 || Core.CloudKeys.Has(Core.Paths.Root, provider);
                var name = id == "local" ? owner.LocalTitle : Core.DictationConfig.TitleOf(id) + (id == "openai_batch" ? " · " + Core.Lang.T("после остановки") : "");
                var chosen = id;
                engines.Add(new(keyed ? name : name + " - " + Core.Lang.T("нет ключа"), owner.Config.Engine == id, keyed, () => owner.SetEngine(chosen)));
            }
            items.Add(new(Core.Lang.T("Движок диктовки"), false, true, () => { }, "audio-lines", Sub: engines));
            var microphones = new List<Views.CapsuleMenu.Item?> { new(Core.Lang.T("Автоопределение"), owner.Config.Microphone.Length == 0, true, () => owner.SetMicrophone("")) };
            foreach (var device in Core.Microphones.List())
            {
                var id = device.Id;
                microphones.Add(new(device.Name, owner.Config.Microphone == id, true, () => owner.SetMicrophone(id)));
            }
            items.Add(new(Core.Lang.T("Микрофон"), false, true, () => { }, "sm7b", Sub: microphones));
            items.Add(new(Core.Lang.T("Положение панели"), false, true, () => { }, "app-window", Sub: new List<Views.CapsuleMenu.Item?>
            {
                new(Core.Lang.T("Вверху экрана"), owner.Config.Panel == "top", true, () => owner.SetPanel("top")),
                new(Core.Lang.T("Внизу экрана"), owner.Config.Panel == "bottom", true, () => owner.SetPanel("bottom")),
            }));
            items.Add(null);
            items.Add(new(Core.Lang.T("Показывать плавающую панель"), owner.Config.ShowPanel, true, () => owner.SetShowPanel(!owner.Config.ShowPanel)));
            items.Add(null);
        }
        items.Add(new(Core.Lang.T("Открыть Audion Voice Tools"), false, true, ShowManager, "maximize-2"));
        items.Add(new(Core.Lang.T("Настройки"), false, true, () => { ShowManager(); Manager?.OpenSheet("settings"); }, "settings"));
        items.Add(null);
        items.Add(new(Core.Lang.T("Выход"), false, true, () => Quit(desktop), "power"));
        return items;
    }
    public static void ShowManager()
    {
        if (Manager is not { } window) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public static void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Quitting = true;
        _tray?.Dispose();
        _tray = null;
        Manager?.Close();
        desktop.Shutdown();
    }

    /// <summary>Audion Code Dark: the "CodeDark" dictionary of App.axaml, the rest from Dark.</summary>
    public static ThemeVariant CodeDark { get; } = new("CodeDark", ThemeVariant.Dark);

    /// <summary>Audion Vault Dark: the "VaultDark" dictionary of App.axaml, the rest from Dark.</summary>
    public static ThemeVariant VaultDark { get; } = new("VaultDark", ThemeVariant.Dark);

    /// <summary>The variant of a theme of the settings: "dark", "codedark", "vault", "light".</summary>
    public static ThemeVariant Variant(string theme) => theme switch
    {
        "light" => ThemeVariant.Light, "codedark" => CodeDark, "vault" => VaultDark, _ => ThemeVariant.Dark,
    };

    // ------------------------------------------------------------ fonts and sizes (SETTINGS)

    /// <summary>The sizes of the window's texts: SETTINGS moves them all by the same step. The terminal has a size of its own.</summary>
    static readonly string[] SizeKeys =
    {
        "FontBase", "FontPanel", "FontHeader", "FontButton", "FontHint", "FontSwitch", "FontCaption", "FontCrumb", "FontCount",
        "FontTitle", "FontBlock", "FontTip", "FontCard", "FontField", "FontFine", "FontBadge",
    };

    /// <summary>The sizes as App.axaml gives them, read once: a step is counted from them, not from the step before.</summary>
    static Dictionary<string, double>? _usual;

    public static void SetSizes(double shift, double terminal)
    {
        if (Current is not { } app) return;
        _usual ??= SizeKeys.Where(key => app.Resources.TryGetResource(key, null, out var value) && value is double)
            .ToDictionary(key => key, key => (double)app.Resources[key]!);
        foreach (var (key, usual) in _usual) app.Resources[key] = usual + shift;
        app.Resources["FontLog"] = terminal;
    }

    /// <summary>A size of the scale as it is now.</summary>
    public static double SizeOf(string key, double usual) =>
        Current is { } app && app.Resources.TryGetResource(key, null, out var value) && value is double size ? size : usual;

    /// <summary>JetBrains Mono and Inter are the fonts inside the program; any other name is a font of Windows.</summary>
    public static FontFamily Family(string name) => name switch
    {
        "" or "JetBrains Mono" => new FontFamily("fonts:JetBrainsMono#JetBrains Mono"),
        "Inter" => new FontFamily("fonts:Inter#Inter"),
        _ => new FontFamily(name),
    };

    public static void SetFonts(string window, string terminal)
    {
        if (Current is not { } app) return;
        app.Resources["FontUi"] = Family(window);
        app.Resources["FontGrid"] = Family(terminal);
    }

    /// <summary>A brush of the current theme by its key; a neutral grey when the key is unknown.</summary>
    public static IBrush Ink(string key) =>
        Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
}
