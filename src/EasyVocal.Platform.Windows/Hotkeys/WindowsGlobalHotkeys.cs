using System.Runtime.InteropServices;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Windows;

/// <summary>
/// System-wide hotkeys through RegisterHotKey on a window of the UI thread. Windows sends WM_HOTKEY to that window: its
/// owner (the UI layer, which knows how to hook a window's messages) passes every message to <see cref="ProcessMessage"/>,
/// so <see cref="Pressed"/> fires on the UI thread.
/// </summary>
public sealed class WindowsGlobalHotkeys : IGlobalHotkeys
{
    public const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    /// <summary>The highest id an application may use; <see cref="IsFree"/> borrows it, the real ones count up from 1.</summary>
    private const int ProbeId = 0xBFFF;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _window;
    private readonly Dictionary<int, string> _actions = new();
    private int _nextId = 1;
    private bool _disposed;

    public event Action<string>? Pressed;

    /// <param name="window">The window handle (HWND) WM_HOTKEY goes to; its messages must reach <see cref="ProcessMessage"/>.</param>
    public WindowsGlobalHotkeys(IntPtr window)
    {
        if (window == IntPtr.Zero) throw new ArgumentException("The window has no handle.", nameof(window));
        _window = window;
    }

    /// <summary>Call for every message the window gets; true when it was one of our hotkeys (handled).</summary>
    public bool ProcessMessage(int msg, IntPtr wParam)
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
    /// True when Windows would accept that combination right now (nobody, including EasyVocal itself, owns it).
    /// Registers it for an instant and lets it go again.
    /// </summary>
    public bool IsFree(Hotkey key)
    {
        if (_disposed) return false;
        if (!RegisterHotKey(_window, ProbeId, (uint)key.Mods | MOD_NOREPEAT, (uint)key.VirtualKey)) return false;
        UnregisterHotKey(_window, ProbeId);
        return true;
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
    }
}
