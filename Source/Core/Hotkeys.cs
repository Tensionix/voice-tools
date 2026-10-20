using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudionVoiceTools.Core;

/// <summary>
/// The hotkey of dictation, read by raw input on a thread of its own.
///
/// 2.3.0 read the keys with a low-level hook (WH_KEYBOARD_LL) installed from the thread of the window. Windows calls
/// such a hook through the message queue of the thread that installed it and waits for it at most LowLevelHooksTimeout
/// (up to a second on Windows 10 1709+); a hook that keeps the system waiting is passed by, and after a series of such
/// timeouts it is removed without a word. While the window thread opened the microphone, a WebSocket or a model, the
/// release of a key was lost. Raw input is a message (WM_INPUT) to a window of this thread: Windows never waits for it,
/// nothing can remove it, and the thread does nothing else. It only observes - no key is swallowed.
///
/// The chord is Right Alt + F12 (2.3.0): Right Alt is VK_MENU with the E0 flag; Right Alt + F11 starts and stops a
/// recording of the recorder (3.0: the recorder has a hotkey of its own). A press of a chord is told once;
/// Esc while the chord is armed for cancelling is told as Escape. Keys sent by a program (SendInput - the paste of this
/// program among them) come with no device; they are passed by, except on a stand, where the keys are sent that way.
/// </summary>
public sealed class Hotkeys : IDisposable
{
    public enum Key { Chord, Escape, Record }

    /// <summary>Told on the input thread: what was pressed, when (Stopwatch ticks), and the window in the foreground at
    /// that moment - the window the text will go to.</summary>
    public event Action<Key, long, IntPtr>? Pressed;

    readonly bool _acceptInjected;
    readonly Thread _thread;
    IntPtr _hwnd;
    Native.WndProc? _proc; // held: the window procedure must outlive the window
    bool _rightAlt, _f12Down, _f11Down;
    volatile bool _escapeWanted;

    public Hotkeys(bool acceptInjected)
    {
        _acceptInjected = acceptInjected;
        _thread = new Thread(Run) { IsBackground = true, Name = "hotkeys" };
        _thread.Start();
    }

    /// <summary>Esc is told only while a dictation is on: otherwise it is a key of other programs.</summary>
    public bool EscapeWanted { set => _escapeWanted = value; }

    void Run()
    {
        _proc = WindowProc;
        var cls = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = "AudionVoiceTools.Hotkeys",
        };
        Native.RegisterClassEx(ref cls);
        // A plain hidden window, not a message-only one: raw input with INPUTSINK goes to a window that can be the
        // target of input even when another program is in front.
        _hwnd = Native.CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        var device = new[]
        {
            new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd },
        };
        if (!Native.RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            Journal.Log($"[ERR] raw input not registered: {Marshal.GetLastWin32Error()}");
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_INPUT) Read(lParam);
        else if (msg == Native.WM_CLOSE)
        {
            Native.DestroyWindow(hWnd);
            Native.PostMessage(IntPtr.Zero, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    unsafe void Read(IntPtr handle)
    {
        long now = Stopwatch.GetTimestamp();
        uint headerSize = (uint)sizeof(Native.RAWINPUTHEADER);
        uint size = 0;
        Native.GetRawInputData(handle, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0 || size > 256) return;
        byte* buffer = stackalloc byte[(int)size];
        if (Native.GetRawInputData(handle, Native.RID_INPUT, (IntPtr)buffer, ref size, headerSize) != size) return;
        var header = *(Native.RAWINPUTHEADER*)buffer;
        if (header.dwType != Native.RIM_TYPEKEYBOARD) return;
        if (header.hDevice == IntPtr.Zero && !_acceptInjected) return;
        var key = *(Native.RAWKEYBOARD*)(buffer + headerSize);
        bool up = (key.Flags & Native.RI_KEY_BREAK) != 0;
        bool e0 = (key.Flags & Native.RI_KEY_E0) != 0;

        if ((key.VKey == Native.VK_MENU && e0) || key.VKey == Native.VK_RMENU) _rightAlt = !up;
        else if (key.VKey == Native.VK_F12)
        {
            // Auto-repeat of a held F12 comes as presses again: the chord is told on the first one only.
            if (!up && !_f12Down && _rightAlt) Pressed?.Invoke(Key.Chord, now, Native.GetForegroundWindow());
            _f12Down = !up;
        }
        else if (key.VKey == Native.VK_F11)
        {
            if (!up && !_f11Down && _rightAlt) Pressed?.Invoke(Key.Record, now, Native.GetForegroundWindow());
            _f11Down = !up;
        }
        else if (key.VKey == Native.VK_ESCAPE && !up && _escapeWanted) Pressed?.Invoke(Key.Escape, now, Native.GetForegroundWindow());
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) Native.PostMessage(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
}
