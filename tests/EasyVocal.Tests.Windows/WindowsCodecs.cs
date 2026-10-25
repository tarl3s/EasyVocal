using System.Runtime.CompilerServices;
using EasyVocal.Platform.Windows;

namespace EasyVocal.Tests;

internal static class WindowsCodecs
{
    /// <summary>These tests load sounds the way the Windows app does: Media Foundation and its MP3 encoder.</summary>
    [ModuleInitializer]
    internal static void Use() => WindowsPlatform.UseCodecs();
}
