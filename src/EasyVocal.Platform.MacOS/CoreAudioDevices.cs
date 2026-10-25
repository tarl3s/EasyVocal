using System.Runtime.InteropServices;
using EasyVocal.Platform.Unix;

namespace EasyVocal.Platform.MacOS;

/// <summary>
/// The device list from CoreAudio (it sees a device the moment it is plugged in, unlike PortAudio): every device with
/// its UID (stable across runs: the id saved in the settings), its name, whether it records and/or plays, and the
/// system's default input and output.
/// </summary>
public sealed unsafe class CoreAudioDevices : IDeviceSource
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint SystemObject = 1;
    private static readonly uint Devices_ = Code("dev#");
    private static readonly uint DefaultInput = Code("dIn ");
    private static readonly uint DefaultOutput = Code("dOut");
    private static readonly uint ObjectName = Code("lnam");
    private static readonly uint DeviceUid = Code("uid ");
    private static readonly uint Streams = Code("stm#");
    private static readonly uint ScopeGlobal = Code("glob");
    private static readonly uint ScopeInput = Code("inpt");
    private static readonly uint ScopeOutput = Code("outp");

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange
    {
        public nint Location;
        public nint Length;
    }

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyDataSize(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* size);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* size, void* data);

    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(IntPtr text);
    [DllImport(CoreFoundation)] private static extern void CFStringGetCharacters(IntPtr text, CFRange range, char* buffer);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr item);

    public static CoreAudioDevices Instance { get; } = new();

    /// <summary>A four-character code as CoreAudio writes it ('dev#').</summary>
    internal static uint Code(string four) => (uint)(four[0] << 24 | four[1] << 16 | four[2] << 8 | four[3]);

    public IReadOnlyList<SystemDevice> Devices()
    {
        var list = new List<SystemDevice>();
        try
        {
            foreach (var id in DeviceIds())
            {
                var uid = Text(id, DeviceUid);
                if (string.IsNullOrEmpty(uid)) continue;
                bool input = HasStreams(id, ScopeInput), output = HasStreams(id, ScopeOutput);
                if (!input && !output) continue;
                list.Add(new SystemDevice(uid, Text(id, ObjectName) ?? uid, input, output));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Error("CoreAudio can't be used", ex);
        }
        return list;
    }

    public string? DefaultId(bool playback)
    {
        try
        {
            var address = new PropertyAddress { Selector = playback ? DefaultOutput : DefaultInput, Scope = ScopeGlobal };
            uint device = 0, size = sizeof(uint);
            if (AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, &device) != 0 || device == 0) return null;
            return Text(device, DeviceUid);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private static uint[] DeviceIds()
    {
        var address = new PropertyAddress { Selector = Devices_, Scope = ScopeGlobal };
        uint size = 0;
        if (AudioObjectGetPropertyDataSize(SystemObject, &address, 0, null, &size) != 0 || size == 0) return Array.Empty<uint>();
        var ids = new uint[size / sizeof(uint)];
        fixed (uint* p = ids)
            if (AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, p) != 0) return Array.Empty<uint>();
        return ids.AsSpan(0, (int)(size / sizeof(uint))).ToArray();
    }

    private static bool HasStreams(uint device, uint scope)
    {
        var address = new PropertyAddress { Selector = Streams, Scope = scope };
        uint size = 0;
        return AudioObjectGetPropertyDataSize(device, &address, 0, null, &size) == 0 && size > 0;
    }

    /// <summary>A CFString property as a .NET string (the CFString is released), or null.</summary>
    private static string? Text(uint objectId, uint selector)
    {
        var address = new PropertyAddress { Selector = selector, Scope = ScopeGlobal };
        IntPtr text = IntPtr.Zero;
        uint size = (uint)IntPtr.Size;
        if (AudioObjectGetPropertyData(objectId, &address, 0, null, &size, &text) != 0 || text == IntPtr.Zero) return null;
        try
        {
            var length = CFStringGetLength(text);
            if (length <= 0) return "";
            var chars = new char[length];
            fixed (char* p = chars) CFStringGetCharacters(text, new CFRange { Location = 0, Length = length }, p);
            return new string(chars);
        }
        finally { CFRelease(text); }
    }
}
