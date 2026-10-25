using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.MacOS;

/// <summary>
/// The saved hotkeys (Windows virtual-key codes, see <see cref="Hotkey"/>) in macOS' terms: Carbon's virtual key codes
/// (kVK_*, the key's place on an ANSI keyboard) and modifier flags. Win is ⌘ Command, Alt is ⌥ Option.
/// </summary>
public static class MacKeys
{
    public const uint CmdKey = 0x0100, ShiftKey = 0x0200, OptionKey = 0x0800, ControlKey = 0x1000;

    private static readonly Dictionary<int, uint> Codes = Build();

    private static Dictionary<int, uint> Build()
    {
        var d = new Dictionary<int, uint>();
        // Letters (kVK_ANSI_A…): the codes follow the keyboard's layout, not the alphabet.
        uint[] letters = { 0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E, 0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06 };
        for (int i = 0; i < 26; i++) d['A' + i] = letters[i];
        uint[] digits = { 0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19 }; // 0..9
        for (int i = 0; i < 10; i++) d['0' + i] = digits[i];
        uint[] f = { 0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F, 0x69, 0x6B, 0x71, 0x6A, 0x40, 0x4F, 0x50, 0x5A }; // F1..F20
        for (int i = 0; i < f.Length; i++) d[0x70 + i] = f[i];
        uint[] pad = { 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C }; // NumPad0..9
        for (int i = 0; i < 10; i++) d[0x60 + i] = pad[i];
        d[0x6A] = 0x43; d[0x6B] = 0x45; d[0x6D] = 0x4E; d[0x6F] = 0x4B;       // * + - / on the keypad
        d[0x20] = 0x31;                                                       // Space
        d[0x2D] = 0x72; d[0x2E] = 0x75;                                       // Insert (Help), Delete (forward delete)
        d[0x24] = 0x73; d[0x23] = 0x77; d[0x21] = 0x74; d[0x22] = 0x79;       // Home, End, PageUp, PageDown
        d[0x25] = 0x7B; d[0x26] = 0x7E; d[0x27] = 0x7C; d[0x28] = 0x7D;       // Left, Up, Right, Down
        d[0xBA] = 0x29; d[0xBB] = 0x18; d[0xBC] = 0x2B; d[0xBD] = 0x1B; d[0xBE] = 0x2F; // ; = , - .
        d[0xBF] = 0x2C; d[0xC0] = 0x32; d[0xDB] = 0x21; d[0xDC] = 0x2A; d[0xDD] = 0x1E; d[0xDE] = 0x27; // / ` [ \ ] '
        return d;
    }

    /// <summary>Carbon's key code for a saved key, or null when a Mac keyboard has no such key (Pause, Scroll Lock, F21–F24).</summary>
    public static uint? KeyCode(int virtualKey) => Codes.TryGetValue(virtualKey, out var code) ? code : null;

    /// <summary>Carbon's modifier flags.</summary>
    public static uint Modifiers(HotkeyMods mods)
    {
        uint m = 0;
        if ((mods & HotkeyMods.Ctrl) != 0) m |= ControlKey;
        if ((mods & HotkeyMods.Alt) != 0) m |= OptionKey;
        if ((mods & HotkeyMods.Shift) != 0) m |= ShiftKey;
        if ((mods & HotkeyMods.Win) != 0) m |= CmdKey;
        return m;
    }
}

/// <summary>
/// System-wide hotkeys through Carbon's RegisterEventHotKey (no accessibility permission needed). Carbon delivers the
/// presses to the application's event target, which the main thread's run loop dispatches (Avalonia runs NSApplication
/// there): so they are registered from the UI thread and <see cref="Pressed"/> fires on it. A combination another app
/// holds exclusively is refused (<see cref="HotkeyCheck.Taken"/>); the system's own shortcuts can't be seen.
/// </summary>
public sealed unsafe class CarbonHotkeys : IGlobalHotkeys, IHotkeyTestHooks
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint KeyboardClass = 0x6B657962; // 'keyb'
    private const uint HotKeyPressed = 5;
    private const uint DirectObject = 0x2D2D2D2D;  // '----'
    private const uint HotKeyIdType = 0x686B6964;  // 'hkid'
    private const uint Signature = 0x45566F63;     // 'EVoc'
    private const uint Exclusive = 1 << 1;
    public const int HotKeyExists = -9878;
    /// <summary>eventNotHandledErr: the event goes on to the next handler.</summary>
    public const int NotHandled = -9874;
    /// <summary>The id <see cref="Check"/> borrows; the real ones count up from 1.</summary>
    private const uint ProbeId = 0xBFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(IntPtr target, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> handler,
        nuint typeCount, EventTypeSpec* types, IntPtr userData, out IntPtr handlerRef);

    [DllImport(Carbon)] private static extern int RemoveEventHandler(IntPtr handlerRef);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, IntPtr target, uint options, out IntPtr hotKey);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotKey);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(IntPtr theEvent, uint name, uint desiredType, IntPtr actualType, nuint bufferSize, IntPtr actualSize, EventHotKeyId* data);

    private readonly Dictionary<uint, (string Action, IntPtr Ref)> registered = new();
    private readonly IntPtr target, handlerRef;
    private GCHandle self;
    private uint nextId = 1;
    private bool disposed;

    public event Action<string>? Pressed;

    public CarbonHotkeys()
    {
        target = GetApplicationEventTarget();
        self = GCHandle.Alloc(this);
        var spec = new EventTypeSpec { EventClass = KeyboardClass, EventKind = HotKeyPressed };
        int status = InstallEventHandler(target, &OnEvent, 1, &spec, GCHandle.ToIntPtr(self), out handlerRef);
        if (status != 0)
        {
            self.Free();
            throw new InvalidOperationException("InstallEventHandler failed: " + status);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnEvent(IntPtr callRef, IntPtr theEvent, IntPtr userData)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not CarbonHotkeys me) return NotHandled;
            EventHotKeyId id;
            if (GetEventParameter(theEvent, DirectObject, HotKeyIdType, IntPtr.Zero, (nuint)sizeof(EventHotKeyId), IntPtr.Zero, &id) != 0) return NotHandled;
            if (id.Signature != Signature) return NotHandled; // another handler's hotkey: let it have it
            return me.Raise(id.Id) ? 0 : NotHandled;
        }
        catch (Exception ex) { AppLog.Error("A hotkey press failed", ex); } // never into Carbon
        return 0;
    }

    /// <summary>True when the id is one of this instance's hotkeys (then it was raised).</summary>
    private bool Raise(uint id)
    {
        if (disposed || !registered.TryGetValue(id, out var r)) return false;
        Pressed?.Invoke(r.Action);
        return true;
    }

    public bool Available => true;

    public string? UnavailableReason => null;

    public bool Register(string actionId, Hotkey key)
    {
        if (disposed || MacKeys.KeyCode(key.VirtualKey) is not { } code) return false;
        uint id = nextId++;
        int status = RegisterEventHotKey(code, MacKeys.Modifiers(key.Mods), new EventHotKeyId { Signature = Signature, Id = id }, target, Exclusive, out var hotKey);
        if (status != 0)
        {
            AppLog.Info("RegisterEventHotKey(" + key + ") refused: " + status + (status == HotKeyExists ? " (another app has it)" : ""));
            return false;
        }
        registered[id] = (actionId, hotKey);
        return true;
    }

    public HotkeyCheck Check(Hotkey key)
    {
        if (disposed || MacKeys.KeyCode(key.VirtualKey) is not { } code) return HotkeyCheck.Unknown;
        int status = RegisterEventHotKey(code, MacKeys.Modifiers(key.Mods), new EventHotKeyId { Signature = Signature, Id = ProbeId }, target, Exclusive, out var hotKey);
        if (status == HotKeyExists) return HotkeyCheck.Taken;
        if (status != 0) return HotkeyCheck.Unknown;
        UnregisterEventHotKey(hotKey);
        return HotkeyCheck.Free;
    }

    public void UnregisterAll()
    {
        foreach (var (_, r) in registered) UnregisterEventHotKey(r.Ref);
        registered.Clear();
        nextId = 1;
    }

    public int Count => registered.Count;

    public int HighestId => registered.Count == 0 ? 0 : (int)registered.Keys.Max();

    /// <summary>For the tests: a press through the same path a real one takes after Carbon.</summary>
    public bool SimulatePress(string actionId)
    {
        foreach (var (id, r) in registered)
        {
            if (r.Action != actionId) continue;
            Raise(id);
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        UnregisterAll();
        disposed = true;
        RemoveEventHandler(handlerRef);
        if (self.IsAllocated) self.Free();
    }
}
