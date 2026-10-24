using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EasyVocal;

/// <summary>Asks Windows 10 (20H1+) / 11 for a dark title bar so it matches the dark window.</summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call from the constructor; applies once the window has a handle.</summary>
    public static void Use(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            }
            catch (Exception) { /* older Windows: a light title bar is fine */ }
        };
    }
}
