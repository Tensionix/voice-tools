using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudionVoiceTools.Core;

/// <summary>
/// A check of the microphone for SETUP: the microphone chosen in the tray, else the one of Windows by default - the one dictation and the recorder take -
/// listened to for a few seconds in the shared mode, beside the stream dictation holds, its level told as it comes.
/// Heard: a peak above the noise of an empty line. Nothing is recorded or kept.
/// </summary>
public static class MicCheck
{
    /// <summary>A peak below this (about -40 dB) is a microphone that hears nothing: switched off, muted, another one chosen.</summary>
    public const float Heard = 0.01f;

    public sealed record Result(string Device, float Peak, string Error);

    /// <summary>The microphone by default and every one that is on, its name first.</summary>
    public static (string Default, List<string> All) Devices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var all = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(device => device.FriendlyName).ToList();
            string chosen = Microphones.NameInUse();
            return (chosen, all);
        }
        catch { return ("", new List<string>()); }
    }

    /// <summary>The microphone listened to for <paramref name="length"/>; <paramref name="level"/> told about 30 times a second, on the thread of the stream.</summary>
    public static Task<Result> Run(TimeSpan length, Action<float> level) => Task.Run(() =>
    {
        WasapiCapture? capture = null;
        float peak = 0, block = 0;
        int frames = 0;
        try
        {
            var device = Microphones.Open();
            capture = new WasapiCapture(device, true, 50);
            var format = capture.WaveFormat;
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            int bytes = format.BitsPerSample / 8, rate = format.SampleRate;
            capture.DataAvailable += (_, e) =>
            {
                for (int at = 0; at + bytes <= e.BytesRecorded; at += bytes)
                {
                    float s = Math.Abs(isFloat ? BitConverter.ToSingle(e.Buffer, at)
                        : bytes == 2 ? BitConverter.ToInt16(e.Buffer, at) / 32768f : BitConverter.ToInt32(e.Buffer, at) / 2147483648f);
                    peak = Math.Max(peak, s);
                    block = Math.Max(block, s);
                }
                frames += e.BytesRecorded / (bytes * format.Channels);
                if (frames >= rate / 30)
                {
                    level(Math.Min(1f, block));
                    block = 0;
                    frames = 0;
                }
            };
            capture.StartRecording();
            Thread.Sleep(length);
            capture.StopRecording();
            return new Result(device.FriendlyName, Math.Min(1f, peak), "");
        }
        catch (Exception ex) { return new Result("", 0, ex.Message); }
        finally { capture?.Dispose(); }
    });
}
