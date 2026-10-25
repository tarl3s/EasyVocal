using System.Runtime.InteropServices;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Windows;

/// <summary>
/// System-wide hotkeys through RegisterHotKey. Windows sends WM_HOTKEY to a window of the thread that created this
/// (the UI thread, whose message loop delivers it): a message-only window of its own, so no UI framework is involved.
/// <see cref="Pressed"/> fires on that thread.
/// </summary>
public sealed class WindowsGlobalHotkeys : IGlobalHotkeys, IHotkeyTestHooks
{
    public const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    /// <summary>The highest id an application may use; <see cref="Check"/> borrows it, the real ones count up from 1.</summary>
    private const int ProbeId = 0xBFFF;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    private readonly IntPtr _window;
    private readonly string _className;
    private readonly WndProc _wndProc; // kept alive as long as the window
    private readonly Dictionary<int, string> _actions = new();
    private int _nextId = 1;
    private bool _disposed;

    public event Action<string>? Pressed;

    /// <summary>Create this on the UI thread: its message loop delivers the presses.</summary>
    public WindowsGlobalHotkeys()
    {
        _wndProc = OwnWindowProc;
        _className = "EasyVocal.Hotkeys." + Guid.NewGuid().ToString("N");
        var instance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
        _window = CreateWindowExW(0, _className, "EasyVocal hotkeys", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            UnregisterClassW(_className, instance);
            throw new InvalidOperationException("CreateWindowEx failed: " + error);
        }
    }

    /// <summary>Called by Windows: an exception must never cross back into it (that ends the process).</summary>
    private IntPtr OwnWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (ProcessMessage((int)msg, wParam)) return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            AppLog.Error("A hotkey press failed", ex);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Windows always has global hotkeys.</summary>
    public bool Available => true;

    public string? UnavailableReason => null;

    /// <summary>True when the message was one of our hotkeys (handled).</summary>
    private bool ProcessMessage(int msg, IntPtr wParam)
    {
        if (msg != WM_HOTKEY || _disposed || !_actions.TryGetValue(wParam.ToInt32(), out var action)) return false;
        Pressed?.Invoke(action);
        return true;
    }

    /// <summary>False when another app already owns that combination.</summary>
    public bool Register(string actionId, Hotkey key)
    {
        if (_disposed) return false;
        if (_nextId >= ProbeId) return false; // can't happen: ids start over at every UnregisterAll
        var id = _nextId++;
        if (!RegisterHotKey(_window, id, (uint)key.Mods | MOD_NOREPEAT, (uint)key.VirtualKey)) return false;
        _actions[id] = actionId;
        return true;
    }

    /// <summary>
    /// Windows can tell: registers the combination for an instant and lets it go again. Taken when anyone (EasyVocal
    /// included) owns it.
    /// </summary>
    public HotkeyCheck Check(Hotkey key)
    {
        if (_disposed) return HotkeyCheck.Unknown;
        if (!RegisterHotKey(_window, ProbeId, (uint)key.Mods | MOD_NOREPEAT, (uint)key.VirtualKey)) return HotkeyCheck.Taken;
        UnregisterHotKey(_window, ProbeId);
        return HotkeyCheck.Free;
    }

    /// <summary>How many hotkeys are registered now.</summary>
    public int Count => _actions.Count;

    /// <summary>The highest id in use (0 when none): at most the number of keys asked for since the last <see cref="UnregisterAll"/>.</summary>
    public int HighestId => _actions.Count == 0 ? 0 : _actions.Keys.Max();

    /// <summary>
    /// For the e2e test: sends the window the WM_HOTKEY Windows would send for the action's hotkey, so the press
    /// goes through the same path as a real one. False when that action has no registered hotkey.
    /// </summary>
    public bool SimulatePress(string actionId)
    {
        if (_disposed) return false;
        foreach (var (id, action) in _actions)
        {
            if (action != actionId) continue;
            SendMessage(_window, WM_HOTKEY, (IntPtr)id, IntPtr.Zero);
            return true;
        }
        return false;
    }

    /// <summary>Lets go of every hotkey. The ids start over (this runs on every text-box focus change).</summary>
    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_window, id);
        _actions.Clear();
        _nextId = 1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        UnregisterAll();
        _disposed = true;
        DestroyWindow(_window);
        UnregisterClassW(_className, GetModuleHandleW(null));
        GC.KeepAlive(_wndProc);
    }
}
