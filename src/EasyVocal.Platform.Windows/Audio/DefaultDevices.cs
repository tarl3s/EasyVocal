using System.Runtime.InteropServices;
using EasyVocal.Core.Engine;
using NAudio.CoreAudioApi;

namespace EasyVocal.Platform.Windows;

/// <summary>
/// Windows' default playback and recording devices. Reading uses NAudio; changing uses IPolicyConfig, the
/// undocumented COM interface Windows' own Sound settings use (the same one SoundSwitch and AudioDeviceCmdlets call),
/// with its Vista-era variant as a fallback. Nothing here throws.
/// </summary>
public static class DefaultDevices
{
    private static readonly Guid PolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid PolicyConfigVistaClient = new("294935ce-f637-4e7c-a41b-ab255460b862");

    /// <summary>The default playback (or recording) device for everything else and for calls.</summary>
    public static DefaultIds Get(bool playback)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var flow = playback ? DataFlow.Render : DataFlow.Capture;
            return new DefaultIds(Read(enumerator, flow, Role.Console), Read(enumerator, flow, Role.Communications));
        }
        catch (Exception)
        {
            return new DefaultIds(null, null);
        }
    }

    private static string? Read(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch (Exception)
        {
            return null; // none plugged in, or the audio service is restarting
        }
    }

    /// <summary>
    /// Makes the device Windows' default for every role (sounds, multimedia, calls); whether it is the playback or
    /// the recording default follows from the device. False when Windows refused (or the interface isn't there).
    /// </summary>
    public static bool Set(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        try
        {
            int hr = WithPolicyConfig(PolicyConfigClient, o => o is IPolicyConfig p ? SetAll(role => p.SetDefaultEndpoint(deviceId, role)) : null)
                     ?? WithPolicyConfig(PolicyConfigVistaClient, o => o is IPolicyConfigVista v ? SetAll(role => v.SetDefaultEndpoint(deviceId, role)) : null)
                     ?? unchecked((int)0x80004002); // E_NOINTERFACE
            if (hr < 0) AppLog.Info($"Windows refused to change the default device (0x{hr:X8})");
            return hr >= 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Changing the default device failed", ex);
            return false;
        }
    }

    /// <summary>The first failure, else 0.</summary>
    private static int SetAll(Func<ERole, int> set)
    {
        int result = 0;
        foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
        {
            int hr = set(role);
            if (hr < 0 && result >= 0) result = hr;
        }
        return result;
    }

    /// <summary>Creates the COM class and runs <paramref name="use"/> on it (null = it doesn't have the interface).</summary>
    private static int? WithPolicyConfig(Guid clsid, Func<object, int?> use)
    {
        object? com = null;
        try
        {
            var type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
            if (type == null) return null;
            com = Activator.CreateInstance(type);
            return com == null ? null : use(com);
        }
        catch (COMException)
        {
            return null; // not registered on this Windows
        }
        finally
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    // Only SetDefaultEndpoint is called; the methods before it keep the vtable order.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, bool isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string device, bool visible);
    }

    [ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, bool isDefault, IntPtr format);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string device, bool visible);
    }
}
