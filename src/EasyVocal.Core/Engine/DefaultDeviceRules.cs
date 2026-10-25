using System;
using System.Collections.Generic;

namespace EasyVocal.Core.Engine;

/// <summary>
/// The system's default device of one kind (playback or recording), as device ids (null = none). Windows has two roles:
/// <see cref="Console"/> for everything else and <see cref="Communications"/> for calls. A system with a single default
/// (macOS, Linux) reports it in both, so the rules below work the same there.
/// </summary>
public sealed record DefaultIds(string? Console, string? Communications);

/// <summary>
/// Installing VB-CABLE can make it Windows' default playback device (and default microphone): then all of the PC's
/// sound goes into the cable and the person hears nothing. These rules decide what EasyVocal puts back: right after
/// the install exactly the device the person had (remembered before it ran); later, only when the person presses
/// Fix, the remembered device or the best real one.
/// </summary>
public static class DefaultDeviceRules
{
    /// <summary>True when Windows' default (for either role) is one of VB-CABLE's devices.</summary>
    public static bool TakenByCable(DefaultIds defaults, IReadOnlyList<DeviceInfo> devices) =>
        IsCable(defaults.Console, devices) || IsCable(defaults.Communications, devices);

    /// <summary>What to remember before VB-CABLE is installed: the default device, unless it is a cable device already.</summary>
    public static string? Remember(DefaultIds defaults, IReadOnlyList<DeviceInfo> devices)
    {
        if (defaults.Console != null && !IsCable(defaults.Console, devices)) return defaults.Console;
        if (defaults.Communications != null && !IsCable(defaults.Communications, devices)) return defaults.Communications;
        return null;
    }

    /// <summary>
    /// Right after the install: when the default is now the cable, the device the person had (while it is connected
    /// and real). Null = change nothing (no fallback: this runs without a click).
    /// </summary>
    public static string? RestoreTo(DefaultIds now, IReadOnlyList<DeviceInfo> devices, string? rememberedId)
    {
        if (rememberedId == null || !TakenByCable(now, devices)) return null;
        foreach (var d in devices)
            if (d.Id == rememberedId) return DeviceRules.IsVirtualCable(d.Name) ? null : d.Id;
        return null;
    }

    /// <summary>
    /// The banner's Fix: when the default is the cable, the remembered device or the best real one
    /// (<see cref="DeviceRules.BestReal"/>). Null when nothing needs fixing or there is no real device.
    /// </summary>
    public static string? FixTo(DefaultIds now, IReadOnlyList<DeviceInfo> devices, string? rememberedId, bool playback) =>
        TakenByCable(now, devices) ? DeviceRules.BestReal(devices, rememberedId, playback)?.Id : null;

    /// <summary>A device that isn't listed (not active) says nothing about its name: not counted as the cable.</summary>
    private static bool IsCable(string? id, IReadOnlyList<DeviceInfo> devices)
    {
        if (id == null) return false;
        foreach (var d in devices)
            if (d.Id == id) return DeviceRules.IsVirtualCable(d.Name);
        return false;
    }
}
