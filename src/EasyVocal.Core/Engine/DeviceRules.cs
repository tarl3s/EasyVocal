using System;
using System.Collections.Generic;

namespace EasyVocal.Core.Engine;

/// <summary>
/// How one virtual cable's devices are named, so EasyVocal finds where to send the voice and never takes any of the
/// cable's devices for a real microphone or headphones. Each platform's virtual mic provider hands its own out
/// (<c>IVirtualMicProvider.Cable</c>). Names are matched case-insensitively, as parts of the device's name.
/// </summary>
/// <param name="PlaybackEnd">The playback device the voice is sent to (any of these names).</param>
/// <param name="AllParts">Every device of the cable (playback, recording, extra ends): never a mic or headphones.</param>
/// <param name="Vendor">A playback end that also says this wins over another device that merely starts the same way.</param>
public sealed record CableNames(IReadOnlyList<string> PlaybackEnd, IReadOnlyList<string> AllParts, string? Vendor = null)
{
    /// <summary>
    /// VB-CABLE (Windows): its playback end is "CABLE Input" (where we send the voice) and its recording end "CABLE
    /// Output" (the virtual microphone calls pick). It also adds "CABLE In 16ch" (a 16-channel playback end), and every
    /// one of its devices says "VB-Audio Virtual Cable".
    /// </summary>
    public static CableNames VbCable { get; } = new(
        new[] { "CABLE Input" },
        new[] { "CABLE Input", "CABLE Output", "CABLE In 16ch", "VB-Audio Virtual Cable" },
        "VB-Audio");

    /// <summary>BlackHole (macOS): one device, "BlackHole 2ch", both the playback end and the call's microphone; its 16ch and 64ch editions are cables too.</summary>
    public static CableNames BlackHole { get; } = new(new[] { "BlackHole 2ch" }, new[] { "BlackHole" });

    /// <summary>
    /// The devices EasyVocal makes on Linux (PipeWire / PulseAudio): the sink "EasyVocal-Output" (the voice goes there),
    /// its monitor ("Monitor of EasyVocal-Output"), and the source "EasyVocal-Microphone" the call app picks.
    /// </summary>
    public static CableNames EasyVocalLinux { get; } = new(
        new[] { "EasyVocal-Output" },
        new[] { "EasyVocal-Output", "EasyVocal-Microphone", "easyvocal_out", "easyvocal_mic" });

    /// <summary>One of the cable's own devices.</summary>
    public bool IsCable(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var part in AllParts)
            if (Has(name, part)) return true;
        return false;
    }

    /// <summary>The device's name says it is the cable's playback end.</summary>
    public bool IsPlaybackEnd(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var part in PlaybackEnd)
            if (Has(name, part)) return true;
        return false;
    }

    internal static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Which devices EasyVocal uses: the virtual cable's playback end for the call, real devices (never the cable's) for
/// the microphone and the headphones. Every rule takes the platform's <see cref="CableNames"/>.
/// </summary>
public static class DeviceRules
{
    /// <summary>One of the cable's own devices: either end, an extra end, anything named after the product.</summary>
    public static bool IsVirtualCable(CableNames cable, string name) => cable.IsCable(name);

    /// <summary>The playback device to send the call audio to, or null when the cable is not there.</summary>
    public static DeviceInfo? FindVirtualCable(CableNames cable, IEnumerable<DeviceInfo> outputs)
    {
        DeviceInfo? any = null;
        foreach (var d in outputs)
        {
            if (!cable.IsPlaybackEnd(d.Name)) continue;
            if (cable.Vendor == null || CableNames.Has(d.Name, cable.Vendor)) return d;
            any ??= d;
        }
        return any;
    }

    /// <summary>The microphone to use when the user has not picked one: the system default unless it is the cable, else the first real mic.</summary>
    public static DeviceInfo? DefaultMic(CableNames cable, IEnumerable<DeviceInfo> inputs)
    {
        DeviceInfo? first = null;
        foreach (var d in inputs)
        {
            if (cable.IsCable(d.Name)) continue;
            if (d.IsDefault) return d;
            first ??= d;
        }
        return first;
    }

    /// <summary>The devices a person may pick as microphone or headphones: everything except the cable's own.</summary>
    public static List<DeviceInfo> ExcludeVirtualCable(CableNames cable, IEnumerable<DeviceInfo> devices)
    {
        var list = new List<DeviceInfo>();
        foreach (var d in devices)
            if (!cable.IsCable(d.Name)) list.Add(d);
        return list;
    }

    /// <summary>The call device: the person's pick, else the cable's playback end. Null when there is none.</summary>
    public static string? PickCall(CableNames cable, string? savedId, IEnumerable<DeviceInfo> outputs) =>
        savedId ?? FindVirtualCable(cable, outputs)?.Id;

    /// <summary>
    /// The headphones: the person's pick while it is connected, else the best real output (<see cref="BestReal"/>: the
    /// system default unless it is the cable, else headphones or speakers). A saved pick that is one of the cable's
    /// devices (older versions listed "CABLE In 16ch") counts as no pick. Never the call device (that would play the
    /// monitor into the call). Null = no monitor.
    /// </summary>
    public static string? PickMonitor(CableNames cable, string? savedId, IReadOnlyList<DeviceInfo> outputs, string? callId)
    {
        string? id = null;
        bool saved = false;
        if (savedId != null)
        {
            DeviceInfo? pick = null;
            foreach (var d in outputs)
                if (d.Id == savedId) pick = d;
            if (pick == null) return null; // unplugged: no monitor until it comes back (the device list change brings it)
            if (!cable.IsCable(pick.Name))
            {
                id = pick.Id;
                saved = true;
            }
        }
        if (!saved) id = BestReal(cable, outputs, null, playback: true)?.Id;
        return id == callId ? null : id;
    }

    private static readonly string[] PlaybackWords = { "headphone", "headset", "earphone", "speaker", "fone", "alto-falante", "auricular", "altavoz" };
    private static readonly string[] RecordingWords = { "microphone", "headset", "microfone", "micrófono" };

    /// <summary>
    /// A real device (never one of the cable's): <paramref name="rememberedId"/> while it is connected, else the system
    /// default, else the first whose name says headphones or speakers (a microphone for recording devices), else the
    /// first real one. Null when there is none.
    /// </summary>
    public static DeviceInfo? BestReal(CableNames cable, IReadOnlyList<DeviceInfo> devices, string? rememberedId, bool playback)
    {
        DeviceInfo? remembered = null, isDefault = null, named = null, first = null;
        var words = playback ? PlaybackWords : RecordingWords;
        foreach (var d in devices)
        {
            if (cable.IsCable(d.Name)) continue;
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
            if (CableNames.Has(name, w)) return true;
        return false;
    }
}
