using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EasyVocal.E2E;

/// <summary>
/// For the real-window e2e test on Windows: a WM_HOTKEY put in this process' message queue (PostMessage, the way
/// Windows delivers a real press), so the test proves Avalonia's message loop hands it to the hotkeys' own window.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class QueuedHotkeyPress
{
    private const int WM_HOTKEY = 0x0312;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Posts hotkey <paramref name="id"/> to this process' "EasyVocal hotkeys" window. False when there is none.</summary>
    public static bool Post(int id)
    {
        uint me = (uint)Environment.ProcessId;
        for (var w = FindWindowExW(HWND_MESSAGE, IntPtr.Zero, null, "EasyVocal hotkeys"); w != IntPtr.Zero;
             w = FindWindowExW(HWND_MESSAGE, w, null, "EasyVocal hotkeys"))
        {
            GetWindowThreadProcessId(w, out var pid);
            if (pid == me) return PostMessageW(w, WM_HOTKEY, (IntPtr)id, IntPtr.Zero);
        }
        return false;
    }
}
