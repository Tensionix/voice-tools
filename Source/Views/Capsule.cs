using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using AudionVoiceTools.Core;

namespace AudionVoiceTools.Views;

/// <summary>
/// The floating capsule - the face of dictation, in the language of 2.3.0 (AGENTS.md): one window that is never
/// resized, 680 × 52 at 70 % by default; at rest only a strip of 120 × 12 in its middle is the window (the rest of the
/// window is cut away by a rectangular region, so clicks go through to what lies under it); it never takes the focus.
/// Listening: a pulsing dot, the level, the elapsed time, the engine, ■ and ×. What is new against 2.3.0: an error has
/// a face of its own, the engine says "local" instead of a cloud that is connecting, and the last words are the ones in
/// sight. The pulse is an animation of the compositor - it runs on the render thread and goes on even when the window
/// thread is busy. A recording of the recorder has the same capsule, as 2.3.0 had its file mode: a red dot that pulses
/// while it records, the level, the time recorded, ❚❚ / ▶ for the pause, ■ saves, × puts it aside into the Recycle Bin.
/// A dictation during a recording takes the capsule for itself and gives it back when its text is pasted.
/// </summary>
public sealed class Capsule : Window
{
    // The window stays 680 wide; the capsule in it is narrower (the owner, 19.10.2026) - the rest lets the clicks through.
    const double BaseWidth = 680, BaseHeight = 52, BodyWidth = 540, PillWidth = 120, PillHeight = 12;
    static readonly IBrush Ground = new SolidColorBrush(Color.FromArgb(0xF0, 23, 33, 43));
    static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E6EDF3"));
    static readonly IBrush Quiet = new SolidColorBrush(Color.Parse("#8B98A5"));
    static readonly IBrush Green = new SolidColorBrush(Color.Parse("#22C55E"));
    static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F59E0B"));
    static readonly IBrush Red = new SolidColorBrush(Color.Parse("#EF4444"));
    static readonly IBrush Stop = new SolidColorBrush(Color.Parse("#E8553F"));
    static readonly IBrush Well = new SolidColorBrush(Color.Parse("#24313D"));

    // The row under the pointer: the same lighter ground the menus of the program have.
    static readonly IBrush Hover = new SolidColorBrush(Color.Parse("#2F4152"));
    static readonly IBrush Pressed = new SolidColorBrush(Color.Parse("#3A4F63"));

    readonly double _k;
    readonly bool _offScreen;
    readonly Border _pill, _body, _ready;
    readonly Button _dictate;
    // The pointer left the open capsule at rest: it folds back into the strip a moment later (2.3.0: 280 ms).
    readonly DispatcherTimer _fold = new() { Interval = TimeSpan.FromMilliseconds(400) };
    bool _readyShown;
    readonly Ellipse _dot;
    readonly Rectangle[] _bars = new Rectangle[7];
    readonly TextBlock _text, _timer, _engine;
    readonly Border _textBox;
    // The words last given to Tail and what it made of them: the room known later, they are fitted again.
    string _raw = "", _fitted = "";
    readonly Button _stop, _cancel, _pause, _again, _more;
    readonly Rectangle _apart;
    readonly DispatcherTimer _clock;
    Snapshot _last = new(State.Off, "", "", 0, "");
    bool _pulsing;

    public Session? Session { get; set; }
    public Dictation? Host { get; set; }
    RecSnapshot _rec = new(RecState.Idle, "", TimeSpan.Zero, "");

    /// <summary>A dictation has the capsule: listening, recognizing, pasting, or its error.</summary>
    bool Dictating => _last.State is State.Listening or State.Finalizing or State.Pasting or State.Error;
    bool Recording => _rec.State is RecState.Recording or RecState.Paused or RecState.Saving;

    public Capsule(double scale, bool offScreen)
    {
        _k = scale;
        _offScreen = offScreen;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Width = BaseWidth * _k;
        Height = BaseHeight * _k;
        Title = "Audion Voice Tools capsule";

        _pill = new Border
        {
            Width = PillWidth * _k, Height = PillHeight * _k, CornerRadius = new CornerRadius(PillHeight * _k / 2),
            Background = Ground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        // The strip opens as the pointer comes, as in 2.3.0: a press on the strip itself starts nothing - the buttons of the
        // open capsule do (the owner, 19.10.2026). The right button opens the menu of the engine (2.3.0 had its quick menu there).
        _pill.PointerEntered += (_, _) => ShowReady();
        _pill.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(_pill).Properties;
            if (point.IsLeftButtonPressed) ShowReady();
            else if (point.IsRightButtonPressed) CapsuleMenu.Open(this, MenuItems());
        };

        // The buttons of the capsule: no frame, the lighter ground under the pointer, round as the capsule.
        Avalonia.Styling.Selector Presenter(string state) => Avalonia.Styling.Selectors.Name(Avalonia.Styling.Selectors.OfType<Avalonia.Controls.Presenters.ContentPresenter>(
            Avalonia.Styling.Selectors.Template(state.Length > 0 ? Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType<Button>(null), state) : Avalonia.Styling.Selectors.OfType<Button>(null))), "PART_ContentPresenter");
        Styles.Add(new Avalonia.Styling.Style(_ => Presenter("")) { Setters = { new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brushes.Transparent), new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.CornerRadiusProperty, new CornerRadius(20 * _k)) } });
        Styles.Add(new Avalonia.Styling.Style(_ => Presenter(":pointerover")) { Setters = { new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Hover) } });
        Styles.Add(new Avalonia.Styling.Style(_ => Presenter(":pressed")) { Setters = { new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Pressed) } });

        _dot = new Ellipse { Width = 11 * _k, Height = 11 * _k, Fill = Green, VerticalAlignment = VerticalAlignment.Center };
        // The level - more of the capsule given to it while one speaks (the owner, 19.10.2026): seven bars up to 26.
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 * _k, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle { Width = 5 * _k, Height = 4 * _k, Fill = Green, RadiusX = 2 * _k, RadiusY = 2 * _k, VerticalAlignment = VerticalAlignment.Center };
            bars.Children.Add(_bars[i]);
        }
        double font = 18 * _k;
        _text = new TextBlock { Foreground = Ink, FontSize = font, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.NoWrap };
        _timer = new TextBlock { Foreground = Quiet, FontSize = font, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
        _engine = new TextBlock { Foreground = Quiet, FontSize = 14 * _k, VerticalAlignment = VerticalAlignment.Center };
        // The same buttons for both: a dictation when it has the capsule, the recording otherwise. As Letterly AI draws its
        // panel (the reference of the owner, 19.10.2026): × on the left; in a well of its own the level, the time and ↻
        // (again) - ❚❚ for a recording; the stop a red square on the right, ⌄ after it the modes of the text and the engines -
        // past a thin line and a gap, not at the edge of the stop: in Letterly the owner often misses one for the other. Between
        // them the words as they come - what Letterly has not, and what tells best what goes on (the owner).
        _stop = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(9 * _k, 0), Height = 40 * _k, MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center, Focusable = false, Cursor = new Cursor(StandardCursorType.Hand),
            Content = new Border { Width = 22 * _k, Height = 22 * _k, CornerRadius = new CornerRadius(5 * _k), Background = Stop },
        };
        _stop.Click += (_, _) => { if (Dictating) Session?.StopClicked(); else Host?.StopRecording(keep: true); };
        _cancel = Glyph("×", Quiet, () => { if (Dictating) Session?.Cancel(); else Host?.StopRecording(keep: false); });
        _pause = Signed("pause", 17, () => Host?.PauseRecording());
        _pause.IsVisible = false;
        _again = Signed("rotate-ccw", 17, () => Session?.Again());
        _more = Signed("chevron-down", 17, () => CapsuleMenu.Open(this, MenuItems()));

        var well = new Border
        {
            Background = Well, CornerRadius = new CornerRadius(18 * _k), Height = 36 * _k, Padding = new Thickness(12 * _k, 0, 2 * _k, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 9 * _k,
                Children = { _dot, bars, _timer, _again, _pause },
            },
        };
        foreach (var inWell in new Control[] { _dot, bars, _timer }) inWell.VerticalAlignment = VerticalAlignment.Center;
        _again.Height = _pause.Height = 32 * _k;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto"), Margin = new Thickness(6 * _k, 0, 8 * _k, 0) };
        void Put(Control c, int col, double right = 10)
        {
            c.Margin = new Thickness(0, 0, right * _k, 0);
            Grid.SetColumn(c, col);
            row.Children.Add(c);
        }
        Put(_cancel, 0, 4);
        Put(well, 1, 12);
        _textBox = new Border { ClipToBounds = true, Child = _text };
        _textBox.SizeChanged += (_, _) => { if (_text.Text == _fitted && _raw.Length > 0) _text.Text = Tail(_raw); };
        Put(_textBox, 2, 10);
        Put(_engine, 3, 6);
        Put(_stop, 4, 10);
        _apart = new Rectangle { Width = 1, Height = 24 * _k, Fill = Quiet, Opacity = 0.35, VerticalAlignment = VerticalAlignment.Center };
        Put(_apart, 5, 8);
        Put(_more, 6, 0);

        _body = new Border
        {
            Width = BodyWidth * _k, HorizontalAlignment = HorizontalAlignment.Center,
            Background = Ground, CornerRadius = new CornerRadius(BaseHeight * _k / 2), Child = row, IsVisible = false,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x98, 0xA5)), BorderThickness = new Thickness(1),
        };

        // The capsule open at rest - «готов» of 2.3.0, the pointer over it: what can start here, by a button each.
        var sign = Icons.Sign("sm7b");
        sign.Stroke = Ink;
        double grow = 22 * _k / 12.6; // a sign of the window is 12.6 across; here it is the size of the words
        var mark = new LayoutTransformControl { LayoutTransform = new ScaleTransform(grow, grow), Child = sign, VerticalAlignment = VerticalAlignment.Center };
        _dictate = Start(mark, Core.Lang.T("ДИКТОВКА"), "Right Alt + F12", () => Session?.Chord(Stopwatch.GetTimestamp(), Native.GetForegroundWindow()));
        var record = Start(new Ellipse { Width = 12 * _k, Height = 12 * _k, Fill = Red, VerticalAlignment = VerticalAlignment.Center },
            Core.Lang.T("ЗАПИСЬ"), "Right Alt + F11", () => Host?.ToggleRecording());
        var readyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * _k, HorizontalAlignment = HorizontalAlignment.Center, Children = { _dictate, record } };
        _ready = new Border
        {
            Width = BodyWidth * _k, HorizontalAlignment = HorizontalAlignment.Center,
            Background = Ground, CornerRadius = new CornerRadius(BaseHeight * _k / 2), Child = readyRow, IsVisible = false,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x98, 0xA5)), BorderThickness = new Thickness(1),
        };
        _ready.PointerEntered += (_, _) => _fold.Stop();
        _ready.PointerExited += (_, _) => _fold.Start();
        // The right button over the buttons too: caught on its way down, before a button takes it.
        _ready.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(_ready).Properties.IsRightButtonPressed) return;
            e.Handled = true;
            CapsuleMenu.Open(this, MenuItems());
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _fold.Tick += (_, _) =>
        {
            _fold.Stop();
            _readyShown = false;
            Show(_last);
        };
        Content = new Grid { Children = { _body, _ready, _pill } };

        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) => Tick();

        Opened += (_, _) =>
        {
            Place();
            QuietStyle();
            // The open capsule is drawn once, unseen, when the window opens: its first real showing took 505 ms -
            // Avalonia building its visuals and shaping its text - and that was the first press after a start.
            _body.IsVisible = true;
            _body.Opacity = 0;
            _text.Text = Core.Lang.T("Слушаю…") + " " + Core.Lang.T("Распознаю…");
            _timer.Text = "00:00";
            RequestAnimationFrame(_ =>
            {
                _body.Opacity = 1;
                Show(_last);
            });
        };
    }

    Button Glyph(string glyph, IBrush brush, Action click)
    {
        var button = new Button
        {
            Content = glyph, Foreground = brush, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            FontSize = 21 * _k, Padding = new Thickness(9 * _k, 0), Height = 40 * _k, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A sign of the window at <paramref name="size"/> units of the capsule, in the ink of the capsule.</summary>
    Control Mark(string name, double size)
    {
        var sign = Icons.Sign(name);
        sign.Stroke = Quiet;
        double grow = size * _k / 12.6; // a sign of the window is 12.6 across
        return new LayoutTransformControl { LayoutTransform = new ScaleTransform(grow, grow), Child = sign, VerticalAlignment = VerticalAlignment.Center };
    }

    Button Signed(string name, double size, Action click)
    {
        var button = new Button
        {
            Content = Mark(name, size), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8 * _k, 0),
            Height = 34 * _k, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The engine in a word: GigaAM or Whisper on this machine, the name of a service with ☁ - the model in full is in the record.</summary>
    static string Short(string engine, bool cloud)
    {
        var name = engine.Split(" · ")[0];
        if (name.Length == 0) return "";
        if (cloud) return "☁ " + name;
        return name.StartsWith("gigaam", StringComparison.OrdinalIgnoreCase) ? "GigaAM"
            : name.StartsWith("parakeet", StringComparison.OrdinalIgnoreCase) ? "Parakeet"
            : name.Contains("whisper", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) ? "Whisper" : name;
    }

    /// <summary>A button of the capsule open at rest: its mark, its word, its hotkey in grey; a press starts it and the capsule follows.</summary>
    Button Start(Control mark, string word, string keys, Action click)
    {
        var button = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(14 * _k, 0), Height = 40 * _k, MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 9 * _k,
                Children =
                {
                    mark,
                    new TextBlock { Text = word, Foreground = Ink, FontSize = 17 * _k, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = keys, Foreground = Quiet, FontSize = 12.5 * _k, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Click += (_, _) =>
        {
            _readyShown = false;
            _fold.Stop();
            click();
        };
        return button;
    }

#if DEBUG
    /// <summary>The pictures of the capsule (MainWindow.Dev.cs): open at rest as the pointer would open it.</summary>
    public void DevReady()
    {
        _readyShown = true;
        Show(_last);
    }
#endif

    /// <summary>The pointer came to the strip: the capsule opens at rest - unless a dictation or a recording has it.</summary>
    void ShowReady()
    {
        _fold.Stop();
        if (_offScreen || Dictating || Recording) return;
        _readyShown = true;
        Show(_last);
    }

    void Place()
    {
        if (_offScreen)
        {
            // A stand draws the capsule beyond the screens: nothing appears in front of the person.
            Position = new PixelPoint(-30000, -30000);
            return;
        }
        var screen = Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        double scaling = screen.Scaling;
        int width = (int)Math.Round(Width * scaling), height = (int)Math.Round(Height * scaling);
        int top = (int)Math.Max(24 * _k * scaling, area.Height * 0.055);
        // At the bottom (the menu of the tray, «Положение панели») it stands just above the taskbar, as Letterly AI puts its panel.
        int y = Host?.Config.Panel == "bottom" ? area.Y + area.Height - height - (int)Math.Round(14 * _k * scaling) : area.Y + top;
        Position = new PixelPoint(area.X + (area.Width - width) / 2, y);
    }

    /// <summary>No activation, no button on the taskbar, a tool window: what 2.3.0 asked of Qt by flags.</summary>
    void QuietStyle()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        long style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        style = (style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)style);
    }

    /// <summary>At rest only the strip is the window; open, the capsule - the margins of the window let the clicks through.</summary>
    void Cut(bool atRest, bool nothing = false)
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        if (nothing)
        {
            Native.SetWindowRgn(hwnd, Native.CreateRectRgn(0, 0, 0, 0), true);
            return;
        }
        double s = RenderScaling;
        int w = (int)Math.Round(Width * s), h = (int)Math.Round(Height * s);
        int pw = (int)Math.Round((atRest ? PillWidth : BodyWidth) * _k * s), ph = (int)Math.Round((atRest ? PillHeight : BaseHeight) * _k * s);
        int x = (w - pw) / 2, y = (h - ph) / 2;
        Native.SetWindowRgn(hwnd, Native.CreateRectRgn(x, y, x + pw, y + ph), true);
    }

    /// <summary>The place or the strip changed in the menu of the tray: put again and drawn again. From any thread.</summary>
    public void Refresh() => Dispatcher.UIThread.Post(() =>
    {
        Place();
        Show(_last);
    });

    /// <summary>Called from any thread: the snapshot is drawn on the window thread.</summary>
    public void Post(Snapshot snapshot) => Dispatcher.UIThread.Post(() => Show(snapshot));

    /// <summary>The recorder changed its state; called from any thread.</summary>
    public void PostRecording(RecSnapshot snapshot) => Dispatcher.UIThread.Post(() =>
    {
        _rec = snapshot;
        Show(_last);
    });

    /// <summary>The level of the recording, shown while the recording has the capsule.</summary>
    public void RecordLevel(float peak) => Dispatcher.UIThread.Post(() =>
    {
        if (Dictating || _rec.State != RecState.Recording) return;
        Bars(peak);
    });

    public void Level(float peak) => Dispatcher.UIThread.Post(() =>
    {
        if (_last.State != State.Listening) return;
        Bars(peak);
    });

    void Bars(float peak)
    {
        double level = Math.Sqrt(peak);
        for (int i = 0; i < _bars.Length; i++)
        {
            double threshold = (i + 0.5) / _bars.Length;
            _bars[i].Height = (4 + 22 * Math.Clamp((level - threshold * 0.6) * 2.2, 0, 1)) * _k;
        }
    }

    void Show(Snapshot s)
    {
        var was = _last.State;
        _last = s;
        bool open = Dictating || Recording || (s.State == State.Ready && s.Text.Length > 0);
        if (open) _readyShown = false;
        bool ready = !open && _readyShown;
        // The strip at rest is not shown when the person said so («Показывать плавающую панель» off): the capsule comes only for
        // a dictation or a recording, and nothing of it is in the way meanwhile.
        bool hidden = !open && !ready && Host?.Config.ShowPanel == false;
        _pill.IsVisible = !open && !ready && !hidden;
        _body.IsVisible = open;
        _ready.IsVisible = ready;
        Cut(!open && !ready, hidden);
        _pill.Opacity = s.State is State.Off or State.Arming ? 0.45 : 1.0;

        _engine.Text = Short(s.Engine, s.Cloud);
        // Dictation starts once its engine is loaded; till then its button waits, dimmed.
        _dictate.IsEnabled = s.State == State.Ready;
        _dictate.Opacity = _dictate.IsEnabled ? 1.0 : 0.45;
        _stop.IsVisible = _cancel.IsVisible = _again.IsVisible = _more.IsVisible = _apart.IsVisible = s.State == State.Listening;
        _pause.IsVisible = false;
        if (!Dictating && Recording)
        {
            ShowRecording();
            return;
        }
        switch (s.State)
        {
            case State.Listening:
                _dot.Fill = Green;
                // A stream of the cloud sends its words while one speaks: the last of them are in sight.
                _text.Text = s.Text.Length > 0 ? Tail(s.Text) : Core.Lang.T("Слушаю…");
                _text.Foreground = s.Text.Length > 0 ? Ink : Quiet;
                Pulse(true);
                _clock.Start();
                Tick();
                if (was != State.Listening)
                {
                    // The first frame that shows "listening" closes the measure of the start.
                    RequestAnimationFrame(_ => Session?.Shown(Stopwatch.GetTimestamp()));
                }
                break;
            case State.Finalizing:
            case State.Pasting:
                _dot.Fill = Amber;
                // The mode of the text at work: its name, the words of the engine are already known but not yet pasted.
                _text.Text = s.State == State.Pasting ? Tail(s.Text) : s.Mode.Length > 0 ? Core.Lang.F("Обработка: {0}…", s.Mode) : Core.Lang.T("Распознаю…");
                _text.Foreground = s.State == State.Finalizing ? Quiet : Ink;
                _clock.Stop();
                Pulse(true);
                foreach (var bar in _bars) bar.Height = 4 * _k;
                break;
            case State.Error:
                _dot.Fill = Red;
                _text.Text = Tail(s.Error);
                _text.Foreground = Red;
                _clock.Stop();
                Pulse(false);
                break;
            default:
                _text.Text = Tail(s.Text);
                _text.Foreground = Ink;
                _dot.Fill = Green;
                _clock.Stop();
                Pulse(false);
                break;
        }
    }

    void ShowRecording()
    {
        bool paused = _rec.State == RecState.Paused, saving = _rec.State == RecState.Saving;
        _dot.Fill = Red;
        _text.Text = Core.Lang.T(saving ? "Сохраняю запись…" : paused ? "Пауза" : "Запись");
        _text.Foreground = paused || saving ? Quiet : Ink;
        _engine.Text = Core.Lang.T("в библиотеку");
        _pause.Content = Mark(paused ? "play" : "pause", 17);
        _pause.IsVisible = _stop.IsVisible = _cancel.IsVisible = !saving;
        _again.IsVisible = _more.IsVisible = _apart.IsVisible = false;
        Pulse(_rec.State == RecState.Recording);
        if (paused || saving) foreach (var bar in _bars) bar.Height = 4 * _k;
        _clock.Start();
        Tick();
    }

    /// <summary>
    /// The menu of ⌄ and of the right click, after Letterly AI (the reference of the owner, 19.10.2026): the modes of the text -
    /// the one in use with the green dot, the others dimmed with the reason when the model cannot be asked; the engines of
    /// dictation in a menu of their own; SETTINGS, where the model of the modes is set; the manager. Built as it opens: a key
    /// saved meanwhile counts. A mode chosen while one speaks works on this take already.
    /// </summary>
    System.Collections.Generic.List<CapsuleMenu.Item?> MenuItems()
    {
        var items = new System.Collections.Generic.List<CapsuleMenu.Item?>();
        if (Host is not { } host) return items;
        var problem = TextModes.Problem(Paths.Root, host.Config);
        foreach (var mode in TextModes.Modes)
        {
            bool can = mode == "raw" || problem.Length == 0;
            var name = Core.Lang.T(TextModes.TitleOf(mode));
            var chosen = mode;
            items.Add(new CapsuleMenu.Item(can ? name : name + " - " + problem, host.Config.Mode == mode, can, () => host.SetMode(chosen)));
        }
        items.Add(null);
        var engines = new System.Collections.Generic.List<CapsuleMenu.Item?>();
        foreach (var engine in DictationConfig.Engines)
        {
            var provider = DictationConfig.ProviderOf(engine);
            bool keyed = provider.Length == 0 || CloudKeys.Has(Paths.Root, provider);
            var name = engine == "local" ? host.LocalTitle : DictationConfig.TitleOf(engine) + (engine == "openai_batch" ? " · " + Core.Lang.T("после остановки") : "");
            var chosen = engine;
            engines.Add(new CapsuleMenu.Item(keyed ? name : name + " - " + Core.Lang.T("нет ключа"), host.Config.Engine == engine, keyed, () => host.SetEngine(chosen)));
        }
        items.Add(new CapsuleMenu.Item(Core.Lang.T("Движок диктовки"), false, true, () => { }, Sub: engines));
        // The place of the panel from the panel itself, as the tray has it.
        items.Add(new CapsuleMenu.Item(Core.Lang.T("Положение панели"), false, true, () => { }, Sub: new System.Collections.Generic.List<CapsuleMenu.Item?>
        {
            new(Core.Lang.T("Вверху экрана"), host.Config.Panel == "top", true, () => host.SetPanel("top")),
            new(Core.Lang.T("Внизу экрана"), host.Config.Panel == "bottom", true, () => host.SetPanel("bottom")),
        }));
        // The model of the modes, the engine with its model, the microphone - on DICTATIONS of the manager, not in SETTINGS.
        items.Add(new CapsuleMenu.Item(Core.Lang.T("Настроить…"), false, true, () => { App.ShowManager(); App.Manager?.OpenSection("dictations"); }));
        items.Add(new CapsuleMenu.Item(Core.Lang.T("Открыть Audion Voice Tools"), false, true, App.ShowManager));
        return items;
    }

    /// <summary>The last words of a long text are the ones in sight (2.3.0 showed the first ones).</summary>
    static string Last(string text) => text.Length <= 64 ? text : "…" + text[^63..];

    /// <summary>
    /// The end of the text that fits the room of the words: whole words go from the start, "…" in their place - a trimming of
    /// Avalonia put its "…" in the middle. Before the first layout, the last 64 letters.
    /// </summary>
    string Tail(string text)
    {
        _raw = text;
        _fitted = Fit(text);
        return _fitted;
    }

    string Fit(string text)
    {
        text = Last(text);
        double room = _textBox.Bounds.Width;
        if (room <= 0 || text.Length == 0) return text;
        var face = new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight);
        double Width(string line) => new FormattedText(line, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, _text.FontSize, Ink).Width;
        if (Width(text) <= room) return text;
        for (int space = text.IndexOf(' ', 1); space > 0; space = text.IndexOf(' ', space + 1))
        {
            var tail = "…" + text[(space + 1)..];
            if (Width(tail) <= room) return tail;
        }
        for (int i = 1; i < text.Length; i++)
        {
            var tail = "…" + text[i..];
            if (Width(tail) <= room) return tail;
        }
        return "…";
    }

    void Tick()
    {
        TimeSpan elapsed;
        if (!Dictating && Recording && Host is { } owner) elapsed = owner.Recorder.Elapsed;
        else if (_last.State == State.Listening && _last.ListeningSince != 0) elapsed = Stopwatch.GetElapsedTime(_last.ListeningSince);
        else return;
        _timer.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }

    void Pulse(bool on)
    {
        if (on == _pulsing) return;
        _pulsing = on;
        var visual = ElementComposition.GetElementVisual(_dot);
        if (visual == null) return;
        if (!on)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = 1f;
            return;
        }
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 1f);
        animation.InsertKeyFrame(0.5f, 0.25f);
        animation.InsertKeyFrame(1f, 1f);
        animation.Duration = TimeSpan.FromMilliseconds(960);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", animation);
    }
}
