using System;
using System.Collections.Generic;

namespace EasyVocal.Core.Engine;

/// <summary>
/// How EasyVocal recognises VB-CABLE: its playback end is "CABLE Input" (where we send the voice) and its
/// recording end is "CABLE Output" (the virtual microphone calls pick). Names are matched case-insensitively;
/// a name that also says "VB-Audio" wins over another device that merely starts the same way.
/// </summary>
public static class DeviceRules
{
    private const string RenderEnd = "CABLE Input";
    private const string CaptureEnd = "CABLE Output";
    private const string Vendor = "VB-Audio";

    public static bool IsVirtualCable(string name) =>
        !string.IsNullOrEmpty(name) &&
        (Has(name, RenderEnd) || Has(name, CaptureEnd));

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

    private static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);
}
