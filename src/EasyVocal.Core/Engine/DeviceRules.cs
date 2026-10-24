using System;
using System.Collections.Generic;

namespace EasyVocal.Core.Engine;

/// <summary>
/// How EasyVocal recognises VB-CABLE: its playback end is "CABLE Input" (where we send the voice) and its
/// recording end is "CABLE Output" (the virtual microphone calls pick). It also adds "CABLE In 16ch" (a 16-channel
/// playback end), and every one of its devices says "VB-Audio Virtual Cable". Names are matched case-insensitively;
/// a name that also says "VB-Audio" wins over another device that merely starts the same way.
/// </summary>
public static class DeviceRules
{
    private const string RenderEnd = "CABLE Input";
    private const string CaptureEnd = "CABLE Output";
    private const string SixteenChannels = "CABLE In 16ch";
    private const string Product = "VB-Audio Virtual Cable";
    private const string Vendor = "VB-Audio";

    /// <summary>One of VB-CABLE's own devices: either end, the 16-channel one, anything named after the product.</summary>
    public static bool IsVirtualCable(string name) =>
        !string.IsNullOrEmpty(name) &&
        (Has(name, RenderEnd) || Has(name, CaptureEnd) || Has(name, SixteenChannels) || Has(name, Product));

    /// <summary>The playback device to send the call audio to, or null when VB-CABLE is not installed.</summary>
    public static DeviceInfo? FindVirtualCable(IEnumerable<DeviceInfo> outputs)
    {
        DeviceInfo? any = null;
        foreach (var d in outputs)
        {
            if (string.IsNullOrEmpty(d.Name) || !Has(d.Name, RenderEnd)) continue;
            if (Has(d.Name, Vendor)) return d;
            any ??= d;
        }
        return any;
    }

    /// <summary>The microphone to use when the user has not picked one: the Windows default unless it is the cable, else the first real mic.</summary>
    public static DeviceInfo? DefaultMic(IEnumerable<DeviceInfo> inputs)
    {
        DeviceInfo? first = null;
        foreach (var d in inputs)
        {
            if (IsVirtualCable(d.Name)) continue;
            if (d.IsDefault) return d;
            first ??= d;
        }
        return first;
    }

    /// <summary>The devices a person may pick as microphone or headphones: everything except VB-CABLE's own ends.</summary>
    public static List<DeviceInfo> ExcludeVirtualCable(IEnumerable<DeviceInfo> devices)
    {
        var list = new List<DeviceInfo>();
        foreach (var d in devices)
            if (!IsVirtualCable(d.Name)) list.Add(d);
        return list;
    }


    /// <summary>The call device: the person's pick, else VB-CABLE's playback end. Null when there is none.</summary>
    public static string? PickCall(string? savedId, IEnumerable<DeviceInfo> outputs) =>
        savedId ?? FindVirtualCable(outputs)?.Id;

    /// <summary>
    /// The headphones: the person's pick while it is connected, else the best real output (<see cref="BestReal"/>: the
    /// Windows default unless it is the cable, else headphones or speakers). A saved pick that is one of VB-CABLE's
    /// devices (older versions listed "CABLE In 16ch") counts as no pick. Never the call device (that would play the
    /// monitor into the call). Null = no monitor.
    /// </summary>
    public static string? PickMonitor(string? savedId, IReadOnlyList<DeviceInfo> outputs, string? callId)
    {
        string? id = null;
        bool saved = false;
        if (savedId != null)
        {
            DeviceInfo? pick = null;
            foreach (var d in outputs)
                if (d.Id == savedId) pick = d;
            if (pick == null) return null; // unplugged: no monitor until it comes back (the device list change brings it)
            if (!IsVirtualCable(pick.Name))
            {
                id = pick.Id;
                saved = true;
            }
        }
        if (!saved) id = BestReal(outputs, null, playback: true)?.Id;
        return id == callId ? null : id;
    }

    private static readonly string[] PlaybackWords = { "headphone", "headset", "earphone", "speaker", "fone", "alto-falante", "auricular", "altavoz" };
    private static readonly string[] RecordingWords = { "microphone", "headset", "microfone", "micrófono" };

    /// <summary>
    /// A real device (never one of VB-CABLE's): <paramref name="rememberedId"/> while it is connected, else the Windows
    /// default, else the first whose name says headphones or speakers (a microphone for recording devices), else the
    /// first real one. Null when there is none.
    /// </summary>
    public static DeviceInfo? BestReal(IReadOnlyList<DeviceInfo> devices, string? rememberedId, bool playback)
    {
        DeviceInfo? remembered = null, isDefault = null, named = null, first = null;
        var words = playback ? PlaybackWords : RecordingWords;
        foreach (var d in devices)
        {
            if (IsVirtualCable(d.Name)) continue;
            if (rememberedId != null && d.Id == rememberedId) remembered = d;
            if (d.IsDefault) isDefault ??= d;
            if (named == null && HasAny(d.Name, words)) named = d;
            first ??= d;
        }
        return remembered ?? isDefault ?? named ?? first;
    }

    private static bool HasAny(string name, string[] words)
    {
        foreach (var w in words)
            if (Has(name, w)) return true;
        return false;
    }
    private static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);
}
