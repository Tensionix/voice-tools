using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AudionVoiceTools.Views;

/// <summary>
/// The menu of the program: a small window of its own - the popups of this program are drawn inside their window, the
/// capsule at rest is a strip of 120 × 12, and the icon of the tray has no window at all. Under the capsule for its right
/// click; over the tray for the right click of its icon, after the menu of Letterly AI (the reference of the owner,
/// 19.10.2026): a sign before the words, the green dot of what is chosen, the red one of a recording under way, and an item
/// with a menu of its own - «Микрофон ›» - opens it to the right, as menus open (the owner, 19.10.2026: to the left it was
/// «дичь»); over the tray the menu stands far enough to the left for the widest of them. A press chooses and closes it, and so
/// does a click anywhere else or Esc.
/// </summary>
public sealed class CapsuleMenu : Window
{
    /// <param name="Sign">A sign of the window before the words (Icons); "" - none.</param>
    /// <param name="Alarm">A red dot: something goes on that the item ends (a recording).</param>
    /// <param name="Sub">A menu of its own, opened beside as the pointer comes to the item.</param>
    public sealed record Item(string Text, bool Chosen, bool Enabled, Action Press, string Sign = "", bool Alarm = false, IReadOnlyList<Item?>? Sub = null);

    static readonly IBrush Ground = new SolidColorBrush(Color.FromArgb(0xF2, 23, 33, 43));
    static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E6EDF3"));
    static readonly IBrush Quiet = new SolidColorBrush(Color.Parse("#8B98A5"));
    static readonly IBrush Green = new SolidColorBrush(Color.Parse("#22C55E"));
    static readonly IBrush Red = new SolidColorBrush(Color.Parse("#EF4444"));
    // The row under the pointer: lighter than the ground, so it is seen at once (the owner, 19.10.2026); pressed - lighter still.
    static readonly IBrush Hover = new SolidColorBrush(Color.Parse("#2F4152"));
    static readonly IBrush Pressed = new SolidColorBrush(Color.Parse("#3A4F63"));

    readonly System.Diagnostics.Stopwatch _age = new();
    bool _fromTray;
    // Over the tray the menu stands on its foot - it grows upward; the room its own menus need on the right, measured once.
    readonly bool _foot;
    double _reserve;
    readonly List<IReadOnlyList<Item?>> _subs = new();
    readonly Border _main, _side;
    readonly StackPanel _sideList;
    Button? _opened;
    Action? _place;

    CapsuleMenu(IReadOnlyList<Item?> items, bool foot)
    {
        _foot = foot;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        // The look of a button of the theme has its own faint pointer-over on its presenter: the rows of the menu have theirs.
        Avalonia.Styling.Selector Row(string state) => Avalonia.Styling.Selectors.Name(Avalonia.Styling.Selectors.OfType<ContentPresenter>(
            Avalonia.Styling.Selectors.Template(state.Length > 0 ? Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType<Button>(null), state) : Avalonia.Styling.Selectors.OfType<Button>(null))), "PART_ContentPresenter");
        Styles.Add(new Avalonia.Styling.Style(_ => Row("")) { Setters = { new Avalonia.Styling.Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Avalonia.Styling.Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(6)) } });
        Styles.Add(new Avalonia.Styling.Style(_ => Row(":pointerover")) { Setters = { new Avalonia.Styling.Setter(ContentPresenter.BackgroundProperty, Hover) } });
        // The item whose own menu is open stays lit, as Letterly keeps «Микрофон» lit while its devices are out.
        Styles.Add(new Avalonia.Styling.Style(_ => Row("open")) { Setters = { new Avalonia.Styling.Setter(ContentPresenter.BackgroundProperty, Hover) } });
        Styles.Add(new Avalonia.Styling.Style(_ => Row(":pressed")) { Setters = { new Avalonia.Styling.Setter(ContentPresenter.BackgroundProperty, Pressed) } });
        // An item that cannot be chosen now - «ElevenLabs - нет ключа» - is only dimmed words: the theme gave it a ground of its own.
        Styles.Add(new Avalonia.Styling.Style(_ => Row(":disabled")) { Setters = { new Avalonia.Styling.Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) } });

        _main = Panel(Fill(new StackPanel { Spacing = 1 }, items, top: true));
        _sideList = new StackPanel { Spacing = 1 };
        _side = Panel(_sideList);
        _side.MinWidth = 0;
        _side.IsVisible = false;
        // Over the tray the menu grows upward from its foot: both panels stand on the bottom, the main one never moves.
        _main.VerticalAlignment = _side.VerticalAlignment = foot ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _main, _side } };

        // A click anywhere else, Esc: the menu goes. Opened from the tray, the focus may go back to the taskbar at once - the menu
        // takes it back for a moment instead of closing before the person sees it.
        Deactivated += (_, _) =>
        {
            if (_fromTray && _age.ElapsedMilliseconds < 700)
            {
                Core.Journal.Log($"[TRAY] the menu lost the focus after {_age.ElapsedMilliseconds} ms - taken back");
                Activate();
                return;
            }
            Close();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    const double SideWords = 250;

    static Border Panel(Control list) => new()
    {
        Background = Ground, CornerRadius = new CornerRadius(10), Padding = new Thickness(4, 6), Child = list, MinWidth = 260,
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x98, 0xA5)), BorderThickness = new Thickness(1),
    };

    StackPanel Fill(StackPanel list, IReadOnlyList<Item?> items, bool top)
    {
        // A column for the signs when any item has one: the words of every row then start at one line.
        bool signs = items.Any(item => item is { Sign.Length: > 0 });
        foreach (var item in items)
        {
            if (item is null)
            {
                list.Children.Add(new Border { Height = 1, Background = Quiet, Opacity = 0.3, Margin = new Thickness(8, 4) });
                continue;
            }
            list.Children.Add(Row(item, top, signs));
        }
        return list;
    }

    Button Row(Item item, bool top, bool signs)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
            Fill = item.Alarm ? Red : item.Chosen ? Green : Brushes.Transparent,
        };
        row.Children.Add(dot);
        if (signs)
        {
            var box = new Border { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
            if (item.Sign.Length > 0)
            {
                var sign = Icons.Sign(item.Sign);
                sign.Stroke = item.Enabled ? Ink : Quiet;
                box.Child = sign;
            }
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
        }
        var words = new TextBlock { Text = item.Text, Foreground = item.Enabled ? Ink : Quiet, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        // In an own menu a long name - «Набор микрофонов (Технология Intel® Smart Sound…)» - goes onto lines of its own, as Letterly
        // shows it: the menu stays narrow and stands near the tray.
        if (!top) { words.TextWrapping = TextWrapping.Wrap; words.MaxWidth = SideWords; }
        Grid.SetColumn(words, 2);
        row.Children.Add(words);
        if (item.Sub is not null)
        {
            var chevron = Icons.Sign("chevron-right");
            chevron.Stroke = Quiet;
            var box = new Border { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Child = chevron };
            Grid.SetColumn(box, 3);
            row.Children.Add(box);
        }
        var button = new Button
        {
            Content = row, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = item.Enabled,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (item.Sub is { } sub)
        {
            if (top) _subs.Add(sub);
            button.PointerEntered += (_, _) => OpenSide(button, sub);
            button.Click += (_, _) => OpenSide(button, sub);
        }
        else
        {
            if (top) button.PointerEntered += (_, _) => CloseSide();
            var press = item.Press;
            button.Click += (_, _) => { Close(); press(); };
        }
        return button;
    }

    /// <summary>The menu of an item beside the main one, its top at the item - kept within the height of the main one, so the window does not move.</summary>
    void OpenSide(Button owner, IReadOnlyList<Item?> items)
    {
        if (_opened == owner && _side.IsVisible) return;
        _opened?.Classes.Remove("open");
        _opened = owner;
        owner.Classes.Add("open");
        _sideList.Children.Clear();
        Fill(_sideList, items, top: false);
        _side.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            double rowTop = owner.TranslatePoint(new Point(0, 0), _main)?.Y ?? 0;
            _side.Measure(Size.Infinity);
            double height = _side.DesiredSize.Height, room = _main.Bounds.Height;
            // From the head of the main panel to the top of the item; over the tray it is told from the foot, where both stand.
            double gap = Math.Max(0, Math.Min(rowTop - 6, room - height));
            _side.Margin = _foot ? new Thickness(0, 0, 0, Math.Max(0, room - gap - height)) : new Thickness(0, gap, 0, 0);
            Dispatcher.UIThread.Post(() => _place?.Invoke(), DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    void CloseSide()
    {
        if (!_side.IsVisible) return;
        _opened?.Classes.Remove("open");
        _opened = null;
        _side.IsVisible = false;
        _sideList.Children.Clear();
        Dispatcher.UIThread.Post(() => _place?.Invoke(), DispatcherPriority.Background);
    }

    /// <summary>
    /// The menu under the middle of the capsule, on its screen; over it when the capsule stands at the bottom of the screen
    /// («Положение панели»), and always with the room of its own menus on the right.
    /// </summary>
    public static void Open(Window capsule, IReadOnlyList<Item?> items)
    {
        var menu = new CapsuleMenu(items, foot: false);
        Place(menu, size =>
        {
            double s = capsule.RenderScaling;
            var at = capsule.Position;
            int width = (int)Math.Round(menu._main.Bounds.Width * s), room = (int)Math.Round(menu._reserve * s), tall = (int)Math.Round(capsule.Bounds.Height * s);
            int x = at.X + (int)Math.Round(capsule.Bounds.Width * s / 2) - width / 2, y = at.Y + tall;
            if (capsule.Screens.ScreenFromWindow(capsule) is { } screen)
            {
                var area = screen.WorkingArea;
                if (at.Y + tall / 2 > area.Y + area.Height / 2) y = at.Y - size.Height;
                x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width - room));
                y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
            }
            return new PixelPoint(x, y);
        });
    }

    /// <summary>
    /// The menu at a point of the screen in pixels - the right click on the icon of the tray: above the point and to its left
    /// as the menus of the tray open, kept inside the working area of that screen with the room of its own menus on the right.
    /// </summary>
    public static void OpenAt(PixelPoint point, IReadOnlyList<Item?> items)
    {
        var menu = new CapsuleMenu(items, foot: true) { _fromTray = true };
        Place(menu, size =>
        {
            double s = menu.RenderScaling;
            // 5 above the taskbar (the owner, 19.10.2026), in the units of the window - the same gap on any scale.
            int gap = (int)Math.Round(5 * s), main = (int)Math.Round(menu._main.Bounds.Width * s), room = (int)Math.Round(menu._reserve * s);
            int x = point.X - main, y = point.Y - size.Height - gap;
            if (menu.Screens.ScreenFromPoint(point) is { } screen)
            {
                var area = screen.WorkingArea;
                x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - main - room));
                y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - size.Height - gap));
            }
            return new PixelPoint(x, y);
        });
    }

    /// <summary>
    /// The menu put where it belongs once it knows its size: at Opened a window that sizes to its content still has the size
    /// of a window, and «above and to the left» of the tray was the corner of the screen (the owner, 19.10.2026). The menu is
    /// shown clear, laid out, moved, and only then seen; it is put again when its own menu opens or closes.
    /// </summary>
    static void Place(CapsuleMenu menu, Func<PixelSize, PixelPoint> where)
    {
        menu.Opacity = 0;
        menu._place = () =>
        {
            double s = menu.RenderScaling;
            menu.Position = where(new PixelSize((int)Math.Round(menu.ClientSize.Width * s), (int)Math.Round(menu.ClientSize.Height * s)));
        };
        menu.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            menu._reserve = menu.Reserve();
            menu._place();
            menu.Opacity = 1;
            menu._age.Start();
            menu.Activate();
            // Windows may keep the focus away from a menu opened from the tray; then no click elsewhere can close it - it goes
            // when the pointer has left it for a moment, or when the pointer never came to it.
            DispatcherTimer.RunOnce(() => { if (!menu.IsActive) menu.CloseWhenLeft(); }, TimeSpan.FromMilliseconds(400));
        }, DispatcherPriority.Background);
        menu.Show();
    }

    /// <summary>The widest of the own menus with the gap before it: the room they need on the right; none - nothing.</summary>
    double Reserve()
    {
        double widest = 0;
        foreach (var sub in _subs)
        {
            var list = Fill(new StackPanel { Spacing = 1 }, sub, top: false);
            var panel = Panel(list);
            panel.MinWidth = 0;
            _sideList.Children.Add(panel);
            panel.Measure(Size.Infinity);
            widest = Math.Max(widest, panel.DesiredSize.Width);
            _sideList.Children.Remove(panel);
        }
        return widest > 0 ? widest + 6 : 0;
    }

#if DEBUG
    /// <summary>The pictures of the menu (MainWindow.Dev.cs): beyond the screens, as over the tray.</summary>
    public static CapsuleMenu DevAt(PixelPoint at, IReadOnlyList<Item?> items)
    {
        var menu = new CapsuleMenu(items, foot: true) { Position = at };
        menu.Show();
        return menu;
    }

    /// <summary>The own menu of the item with these words opened, as the pointer would open it.</summary>
    public void DevOpen(string text) => ((StackPanel)_main.Child!).Children.OfType<Button>()
        .First(button => ((Grid)button.Content!).Children.OfType<TextBlock>().Any(words => words.Text == text))
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
#endif

    void CloseWhenLeft()
    {
        var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(IsPointerOver ? 1.2 : 6) };
        wait.Tick += (_, _) => { wait.Stop(); Close(); };
        PointerEntered += (_, _) => wait.Stop();
        PointerExited += (_, _) => { wait.Interval = TimeSpan.FromSeconds(1.2); wait.Start(); };
        Closed += (_, _) => wait.Stop();
        wait.Start();
    }
}
