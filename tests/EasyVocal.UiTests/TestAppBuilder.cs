using Avalonia;
using Avalonia.Headless;
using EasyVocal.UiTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace EasyVocal.UiTests;

/// <summary>The real App (theme and all) on Avalonia's headless platform, drawn by Skia so snapshots are real pictures.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
