using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EasyVocal.Core.Hotkeys;

namespace EasyVocal.Hotkeys;

/// <summary>System-wide hotkeys through RegisterHotKey. <see cref="Pressed"/> fires on the UI thread.</summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _actions = new();
    private int _nextId = 1;
    private bool _disposed;

    public event Action<string>? Pressed;

    public GlobalHotkeys(Window owner)
    {
        var handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Owner window has no HwndSource.");
        _source.AddHook(WndProc);
    }

    /// <summary>False when another app already owns that combination.</summary>
    public bool Register(string actionId, Hotkey key)
    {
        if (_disposed) return false;
        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, (uint)key.Mods | MOD_NOREPEAT, (uint)key.VirtualKey)) return false;
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
        const int probeId = 0xBFFF; // the highest id an application may use; the real ones count up from 1
        if (!RegisterHotKey(_source.Handle, probeId, (uint)key.Mods | MOD_NOREPEAT, (uint)key.VirtualKey)) return false;
        UnregisterHotKey(_source.Handle, probeId);
        return true;
    }

    /// <summary>How many hotkeys are registered now.</summary>
    public int Count => _actions.Count;

    /// <summary>
    /// For the e2e test: sends this window the WM_HOTKEY Windows would send for the action's hotkey, so the press
    /// goes through the same path as a real one. False when that action has no registered hotkey.
    /// </summary>
    internal bool SimulatePress(string actionId)
    {
        if (_disposed) return false;
        foreach (var (id, action) in _actions)
        {
            if (action != actionId) continue;
            SendMessage(_source.Handle, WM_HOTKEY, (IntPtr)id, IntPtr.Zero);
            return true;
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _disposed = true;
    }
}
