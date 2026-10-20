using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;

namespace AudionVoiceTools.Core;

/// <summary>
/// The microphone the program listens to, chosen in the menu of the tray (config\dictation.json, Microphone): an id of
/// Windows, or "" - «Автоопределение», the microphone Windows has by default, as Letterly AI offers it (the reference of
/// the owner, 19.10.2026). Dictation, the recorder and the check of SETUP open the same one; a chosen microphone that is
/// gone - unplugged, switched off - gives way to the one by default.
/// </summary>
public static class Microphones
{
    public sealed record Device(string Id, string Name, bool ByDefault);

    /// <summary>The id chosen; "" - the microphone of Windows by default.</summary>
    public static string Chosen { get; set; } = "";

    /// <summary>Every microphone that is on, the one by default marked.</summary>
    public static List<Device> List()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string byDefault = enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID : "";
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(device => new Device(device.ID, device.FriendlyName, device.ID == byDefault)).ToList();
        }
        catch { return new List<Device>(); }
    }

    /// <summary>The device to open: the chosen one while it is on, else the one by default.</summary>
    public static MMDevice Open()
    {
        var enumerator = new MMDeviceEnumerator();
        if (Chosen.Length > 0)
        {
            try
            {
                var device = enumerator.GetDevice(Chosen);
                if (device.State == DeviceState.Active) return device;
            }
            catch { }
            Journal.Log("[MIC] the chosen microphone is not there - the one by default is taken");
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    /// <summary>The name of the microphone that would be opened now; "" - none.</summary>
    public static string NameInUse()
    {
        try { return Open().FriendlyName; }
        catch { return ""; }
    }
}
