using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;

namespace AudionVoiceTools.Core;

/// <summary>
/// The guard of the rule "the window thread waits for nothing": a thread posts a mark to the window thread every 20 ms
/// and reads how late it ran. A delay longer than 50 ms goes to the journal; the worst one since the last look is kept
/// for each dictation. In 2.3.0 nothing measured this: the freezes were seen only as lost key releases.
/// </summary>
public static class UiWatchdog
{
    static long _worst, _worstEver, _late;
    static int _busy;

    public static long WorstEverMs => Interlocked.Read(ref _worstEver);
    public static long LateCount => Interlocked.Read(ref _late);

    public static void Start()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(20);
                if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) continue; // the last mark has not run yet
                long posted = Stopwatch.GetTimestamp();
                Dispatcher.UIThread.Post(() =>
                {
                    long lag = (long)Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                    if (lag > Interlocked.Read(ref _worst)) Interlocked.Exchange(ref _worst, lag);
                    if (lag > Interlocked.Read(ref _worstEver)) Interlocked.Exchange(ref _worstEver, lag);
                    if (lag > 50)
                    {
                        Interlocked.Increment(ref _late);
                        Journal.Log($"[UI] the window thread was late {lag} ms");
                    }
                    Interlocked.Exchange(ref _busy, 0);
                }, DispatcherPriority.Send);
            }
        }) { IsBackground = true, Name = "ui-watchdog" };
        thread.Start();
    }

    /// <summary>The worst delay since the last call, and a fresh start.</summary>
    public static long TakeWorst() => Interlocked.Exchange(ref _worst, 0);
}
