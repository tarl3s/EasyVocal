using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace EasyVocal;

/// <summary>
/// The Avalonia application: the theme (App.axaml, Theme.axaml) and, for a normal launch, one <see cref="AppSession"/>.
/// A test host (the headless UI tests) builds the same App and starts its own sessions.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Where a character the UI font doesn't have comes from. On Windows: arrows, shapes and symbols
    /// (▶ ■ ✂ ⌨ ↶) from Segoe UI Symbol, drawn like text in the button's colour; emoji from Segoe UI Emoji, in colour.
    /// Elsewhere the system's own fallback decides.
    /// </summary>
    public static FontManagerOptions Fonts()
    {
        if (!OperatingSystem.IsWindows()) return new FontManagerOptions();
        return new FontManagerOptions
        {
            DefaultFamilyName = "Segoe UI",
            FontFallbacks = new[]
            {
                new FontFallback { FontFamily = new FontFamily("Segoe UI Symbol"), UnicodeRange = UnicodeRange.Parse("U+2190-23FF, U+25A0-27BF, U+2B00-2BFF") },
                new FontFallback { FontFamily = new FontFamily("Segoe UI Emoji") },
                new FontFallback { FontFamily = new FontFamily("Segoe UI Symbol") },
            },
        };
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Program.Launch is { } launch)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var session = new AppSession(launch, code => desktop.Shutdown(code));
            // The system is signing out (or the person quits from the dock on macOS): no questions.
            desktop.ShutdownRequested += (_, _) => session.OnSystemShutdown();
            desktop.Exit += (_, _) => session.OnExit();
            session.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
