using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudionVoiceTools.Core;

/// <summary>
/// The icon of the program in the tray, put there by Windows itself (Shell_NotifyIcon) on a thread of its own. The tray
/// icon of Avalonia never showed its menu (the owner, 19.10.2026: the right button did nothing), and the right button of
/// the old way of the tray (version 0) did not reach the icon on Windows 11; version 4 brings it as WM_CONTEXTMENU. The left
/// button opens the manager; the right one asks the program for its menu at that point - the menu of the window, not the
/// white one of Windows. A restart of Explorer (TaskbarCreated) puts the icon back.
/// </summary>
public sealed class Tray : IDisposable
{
    const uint Callback = Native.WM_USER + 77, IconId = 1;
    const uint NIM_ADD = 0, NIM_DELETE = 2, NIM_SETVERSION = 4, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80, NOTIFYICON_VERSION_4 = 4;
    const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;

    readonly string _tip;
    readonly Action _clicked;
    readonly Action<int, int> _menuAt;
    readonly Thread _thread;
    IntPtr _hwnd, _icon;
    Native.WndProc? _proc; // held: the window procedure must outlive the window
    uint _taskbarCreated;
    bool _shown, _v4;
    WinEventProc? _onForeground; // held: Windows calls it as long as the hook lives
    IntPtr _hook;

    /// <summary>
    /// The window of a program that was last in front - not the taskbar, not the tray, not this program: where the menu pastes
    /// the last dictation (a click on the tray brings the taskbar forward, so the window in front then is not the one meant).
    /// </summary>
    public IntPtr LastApp { get; private set; }

    /// <param name="clicked">The left button; called on the thread of the tray.</param>
    /// <param name="menuAt">The right button, at that point of the screen in pixels; called on the thread of the tray, the window of the
    /// tray already brought forward - so the menu the program opens there may take the focus.</param>
    public Tray(string tip, Action clicked, Action<int, int> menuAt)
    {
        _tip = tip;
        _clicked = clicked;
        _menuAt = menuAt;
        _thread = new Thread(Run) { IsBackground = true, Name = "tray" };
        _thread.Start();
    }

    void Run()
    {
        _proc = WindowProc;
        var cls = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = "AudionVoiceTools.Tray",
        };
        Native.RegisterClassEx(ref cls);
        // A hidden window, not a message-only one: TaskbarCreated is a broadcast, and a message-only window does not get it.
        _hwnd = Native.CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadIcon();
        Show();
        _onForeground = (_, _, window, _, _, _, _) => { if (Worth(window)) LastApp = window; };
        _hook = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, _onForeground, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
        var now = Native.GetForegroundWindow();
        if (Worth(now)) LastApp = now;
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    /// <summary>The mark of the program at the size of the tray: system_core\icons\app.ico, else the icon of the exe.</summary>
    static IntPtr LoadIcon()
    {
        int size = GetSystemMetrics(49 /* SM_CXSMICON */);
        var file = Path.Combine(Paths.Root, "system_core", "icons", "app.ico");
        var icon = File.Exists(file) ? LoadImage(IntPtr.Zero, file, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */) : IntPtr.Zero;
        if (icon == IntPtr.Zero && Environment.ProcessPath is { } exe) ExtractIconEx(exe, 0, IntPtr.Zero, out icon, 1);
        return icon;
    }

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = IconId, uFlags = flags,
        uCallbackMessage = Callback, hIcon = _icon, szTip = _tip, szInfo = "", szInfoTitle = "",
    };

    void Show()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _shown = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!_shown) { Journal.Log("[ERR] the icon of the tray was not shown: " + Marshal.GetLastWin32Error()); return; }
        // The way of Windows Vista and later: the right button comes as WM_CONTEXTMENU with the point in wParam, the left one
        // as NIN_SELECT. Windows 11 did not bring the right button of the old way (version 0) to the icon at all.
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        _v4 = Shell_NotifyIcon(NIM_SETVERSION, ref data);
        Journal.Log($"[TRAY] the icon is in the tray, version {(_v4 ? 4 : 0)}");
    }

    IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Callback)
        {
            uint what = (uint)(lParam.ToInt64() & 0xFFFF);
            if (_v4)
            {
                if (what is NIN_SELECT or NIN_KEYSELECT) Safe(_clicked);
                else if (what == WM_CONTEXTMENU) Safe(() => MenuAt((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF)));
            }
            else if (what == WM_LBUTTONUP) Safe(_clicked);
            else if (what == WM_RBUTTONUP) Safe(() => { GetCursorPos(out var at); MenuAt(at.X, at.Y); });
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            Show();
            return IntPtr.Zero;
        }
        if (msg == Native.WM_CLOSE)
        {
            Remove();
            Native.DestroyWindow(hWnd);
            Native.PostMessage(IntPtr.Zero, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    static readonly string[] NotPrograms = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "Progman", "WorkerW" };

    static bool Worth(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(window, out uint process);
        if (process == (uint)Environment.ProcessId) return false;
        var name = new System.Text.StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return Array.IndexOf(NotPrograms, name.ToString()) < 0;
    }

    static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { Journal.Log($"[ERR] tray: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The window of the tray comes forward first: Windows lets the program that got the click take the focus, and the
    /// menu it opens closes when the person clicks elsewhere.</summary>
    void MenuAt(int x, int y)
    {
        if (!Native.SetForegroundWindow(_hwnd)) Journal.Log("[TRAY] the window of the tray did not come forward");
        _menuAt(x, y);
    }

    void Remove()
    {
        if (!_shown) return;
        var data = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _shown = false;
    }

    /// <summary>The icon leaves the tray at once, before the program ends: no dead icon is left behind.</summary>
    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        Remove();
        if (_hwnd != IntPtr.Zero) Native.PostMessage(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint icons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
    static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint load);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int max);
}
