using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EasyVocal.Core.Engine;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace EasyVocal.Audio;

/// <summary>
/// The active microphones and playback devices, and <see cref="Changed"/> when one is plugged in, removed,
/// enabled/disabled or becomes the Windows default.
/// </summary>
public static class DeviceList
{
    private static readonly object Sync = new();
    private static MMDeviceEnumerator? watcher;
    private static Notifications? client;
    private static Action? changed;

    public static IReadOnlyList<DeviceInfo> Inputs() => List(DataFlow.Capture);

    public static IReadOnlyList<DeviceInfo> Outputs() => List(DataFlow.Render);

    /// <summary>
    /// Raised on a Windows audio thread (not the UI thread) when the device list may have changed. Handlers must
    /// return quickly and must not list devices from inside the call: post to the UI thread and do it there.
    /// </summary>
    public static event Action? Changed
    {
        add
        {
            lock (Sync)
            {
                changed += value;
                if (changed != null && watcher == null) Watch();
            }
        }
        remove
        {
            lock (Sync)
            {
                changed -= value;
                if (changed == null && watcher != null) Unwatch();
            }
        }
    }

    private static List<DeviceInfo> List(DataFlow flow)
    {
        var list = new List<DeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using var d = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            defaultId = d.ID;
        }
        catch (COMException) { /* no default device (none plugged in) */ }

        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                string name;
                try { name = device.FriendlyName; }
                catch (COMException) { continue; } // removed while we were listing
                list.Add(new DeviceInfo(device.ID, name, device.ID == defaultId));
            }
        }
        return list;
    }

    private static void Watch()
    {
        try
        {
            watcher = new MMDeviceEnumerator();
            client = new Notifications();
            watcher.RegisterEndpointNotificationCallback(client);
        }
        catch (COMException)
        {
            watcher?.Dispose();
            watcher = null;
            client = null;
        }
    }

    private static void Unwatch()
    {
        try { if (client != null) watcher!.UnregisterEndpointNotificationCallback(client); }
        catch (COMException) { /* nothing to undo */ }
        watcher!.Dispose();
        watcher = null;
        client = null;
    }

    private static void Raise()
    {
        Action? handlers;
        lock (Sync) handlers = changed;
        if (handlers == null) return;
        foreach (Action h in handlers.GetInvocationList())
        {
            try { h(); }
            catch (Exception) { /* never throw back into Windows */ }
        }
    }

    private sealed class Notifications : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Raise();
        public void OnDeviceAdded(string pwstrDeviceId) => Raise();
        public void OnDeviceRemoved(string deviceId) => Raise();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Console) Raise(); // Windows sends one per role; one is enough
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { /* fires constantly; not a list change */ }
    }
}
