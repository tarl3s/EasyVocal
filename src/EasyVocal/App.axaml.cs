using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace EasyVocal;

/// <summary>
/// The Avalonia application: the theme (App.axaml, Theme.axaml) and, for a normal launch, one <see cref="AppSession"/>.
/// A test host (the headless UI tests) builds the same App and starts its own sessions.
/// </summary>
public partial class App : Application
{
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
