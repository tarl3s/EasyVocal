using System;
using System.Collections.Generic;

namespace EasyVocal.Core.Engine;

/// <summary>Which microphone to open, and whether it is a stand-in for the person's chosen one.</summary>
/// <param name="Id">The endpoint to open; null = there is no microphone at all.</param>
/// <param name="Fallback">The chosen mic has been gone a while: this is Windows' default, for this session only.</param>
/// <param name="Name">The device's name when it is connected (for "using … for now").</param>
public sealed record MicPick(string? Id, bool Fallback, string? Name);

/// <summary>
/// Decides which microphone the engine opens. The person's choice wins while it is connected. When it has been
/// missing for <see cref="FallbackAfter"/> and Windows has another real microphone, that one is used for now (the
/// saved choice is never changed, and is used again as soon as it is back). A saved choice that is one of
/// VB-CABLE's own ends is ignored: listening to it would loop the voice. UI thread.
/// </summary>
public sealed class MicPicker
{
    public static readonly TimeSpan FallbackAfter = TimeSpan.FromSeconds(10);

    private string? missingId;
    private DateTime missingSince;

    public MicPick Pick(string? savedId, IReadOnlyList<DeviceInfo> inputs, DateTime now)
    {
        if (savedId != null)
        {
            DeviceInfo? saved = null;
            foreach (var d in inputs)
                if (d.Id == savedId) saved = d;
            if (saved != null)
            {
                missingId = null;
                if (!DeviceRules.IsVirtualCable(saved.Name)) return new MicPick(saved.Id, false, saved.Name);
            }
            else
            {
                if (missingId != savedId)
                {
                    missingId = savedId;
                    missingSince = now;
                }
                var stand = DeviceRules.DefaultMic(inputs);
                if (stand == null || now - missingSince < FallbackAfter)
                    return new MicPick(savedId, false, null); // keep trying the chosen one for now
                return new MicPick(stand.Id, true, stand.Name);
            }
        }
        var auto = DeviceRules.DefaultMic(inputs);
        return new MicPick(auto?.Id, false, auto?.Name);
    }
}

/// <summary>The devices one audio session uses (endpoint ids; null = none).</summary>
public sealed record AudioRoutes(string? Mic, string? Call, string? Monitor)
{
    /// <summary>
    /// After the device list changed: restart a running session only when it would now pick other devices, or its
    /// headphones stopped working (a new device may be what they should switch to). Plugging in an unrelated device does nothing.
    /// </summary>
    public static bool NeedsRestart(AudioRoutes inUse, AudioRoutes now, bool monitorLost) => monitorLost || inUse != now;

    /// <summary>
    /// A session waiting for the virtual cable keeps its microphone running (nothing else restarts it); it switches only
    /// when a different, connected mic should be used now: the chosen one is back, Windows has a new default…
    /// </summary>
    public static bool ShouldSwitchMic(string? micInUse, MicPick now) => now.Id != null && now.Name != null && now.Id != micInUse;
}
