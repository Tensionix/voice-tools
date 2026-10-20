using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AudionVoiceTools.Core;

/// <summary>
/// The paste of a dictation, on an STA thread of its own (the clipboard wants one). The text of the clipboard the
/// person had is kept and put back after the paste - 2.3.0 overwrote it. The window that was in front when the chord
/// was pressed is brought back first if the focus went away; the modifiers still held are let go, then Ctrl+V.
/// </summary>
public sealed class Paste : IDisposable
{
    public int RestoreAfterMs { get; set; } = 300;

    readonly BlockingCollection<Action> _jobs = new();

    public Paste()
    {
        var thread = new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) job(); })
        { IsBackground = true, Name = "paste" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Pastes the text into the target window; the milliseconds until Ctrl+V was sent come back, or an error.</summary>
    public Task<(long Ms, string? Error)> Into(IntPtr target, string text)
    {
        var done = new TaskCompletionSource<(long, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Add(() =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                if (target != IntPtr.Zero && Native.GetForegroundWindow() != target) Native.SetForegroundWindow(target);
                string? before = ReadText();
                if (!WriteText(text))
                {
                    done.SetResult((watch.ElapsedMilliseconds, "the clipboard is held by another program"));
                    return;
                }
                ReleaseModifiers();
                SendCtrlV();
                long sent = watch.ElapsedMilliseconds;
                done.SetResult((sent, null));
                // The target reads the clipboard when it handles Ctrl+V; the person's text comes back after that.
                Thread.Sleep(RestoreAfterMs);
                if (before != null) WriteText(before);
            }
            catch (Exception e)
            {
                done.TrySetResult((watch.ElapsedMilliseconds, e.Message));
            }
        });
        return done.Task;
    }

    static bool Open()
    {
        for (int i = 0; i < 10; i++)
        {
            if (Native.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    static string? ReadText()
    {
        if (!Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT) || !Open()) return null;
        try
        {
            var handle = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var pointer = Native.GlobalLock(handle);
            try { return Marshal.PtrToStringUni(pointer); }
            finally { Native.GlobalUnlock(handle); }
        }
        finally { Native.CloseClipboard(); }
    }

    static bool WriteText(string text)
    {
        if (!Open()) return false;
        try
        {
            Native.EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes);
            var pointer = Native.GlobalLock(handle);
            var chars = (text + "\0").ToCharArray();
            Marshal.Copy(chars, 0, pointer, chars.Length);
            Native.GlobalUnlock(handle);
            if (Native.SetClipboardData(Native.CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                Native.GlobalFree(handle);
                return false;
            }
            return true;
        }
        finally { Native.CloseClipboard(); }
    }

    static void ReleaseModifiers()
    {
        int[] keys = { Native.VK_LSHIFT, Native.VK_RSHIFT, Native.VK_LCONTROL, Native.VK_RCONTROL, Native.VK_LMENU, Native.VK_RMENU, Native.VK_LWIN, Native.VK_RWIN };
        foreach (var vk in keys)
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) Send((ushort)vk, true);
    }

    static void SendCtrlV()
    {
        Send(Native.VK_CONTROL, false);
        Send(Native.VK_V, false);
        Send(Native.VK_V, true);
        Send(Native.VK_CONTROL, true);
    }

    static void Send(int vk, bool up)
    {
        var input = new[]
        {
            new Native.INPUT { type = Native.INPUT_KEYBOARD, ki = new Native.KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } },
        };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }

    public void Dispose() => _jobs.CompleteAdding();
}
