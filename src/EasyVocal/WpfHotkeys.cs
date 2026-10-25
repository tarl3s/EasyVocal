using System.Windows;
using System.Windows.Interop;
using EasyVocal.Core.Hotkeys;

namespace EasyVocal;

/// <summary>
/// Windows' global hotkeys on a WPF window: <see cref="WindowsGlobalHotkeys"/> registers them on the window's handle and
/// this hooks the window's messages so WM_HOTKEY reaches it (on the UI thread).
/// </summary>
public sealed class WpfHotkeys : IGlobalHotkeys
{
    private readonly HwndSource source;
    private readonly WindowsGlobalHotkeys inner;
    private bool disposed;

    public WpfHotkeys(Window owner)
    {
        var handle = new WindowInteropHelper(owner).EnsureHandle();
        source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Owner window has no HwndSource.");
        inner = new WindowsGlobalHotkeys(handle);
        source.AddHook(WndProc);
    }

    public event Action<string>? Pressed
    {
        add => inner.Pressed += value;
        remove => inner.Pressed -= value;
    }

    public bool Register(string actionId, Hotkey key) => inner.Register(actionId, key);

    public bool IsFree(Hotkey key) => inner.IsFree(key);

    public void UnregisterAll() => inner.UnregisterAll();

    public int Count => inner.Count;

    public int HighestId => inner.HighestId;

    public bool SimulatePress(string actionId) => inner.SimulatePress(actionId);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (inner.ProcessMessage(msg, wParam)) handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        inner.Dispose();
        source.RemoveHook(WndProc);
    }
}
