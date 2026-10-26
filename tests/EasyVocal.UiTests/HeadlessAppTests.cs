using Avalonia.Headless.XUnit;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Sounds;
using EasyVocal.E2E;
using EasyVocal.Platform.Windows;
using EasyVocal.Tests.Helpers;
using Xunit.Abstractions;

namespace EasyVocal.UiTests;

/// <summary>
/// The app's own smoke and e2e tests (<c>AppSession.SmokeTest.cs</c>, <c>AppSession.E2E.cs</c>) run headless in this
/// process: every window opened, driven and drawn; the e2e test's audio checked on its test devices; the "relaunch" is a
/// second session on the same folder. Nothing on the computer is touched: pretend hotkeys, startup entry and virtual mic.
/// </summary>
// One at a time with the other app tests: they share the process' current session, its error count and the log.
[Collection("App sessions")]
public class HeadlessAppTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(4);
    private readonly ITestOutputHelper output;

    public HeadlessAppTests(ITestOutputHelper output) => this.output = output;

    [AvaloniaFact]
    public async Task SmokeTest_EveryWindowOpensAndWorks()
    {
        var data = TestFolders.New("EV_UiSmoke_");
        try
        {
            var code = await RunSession(data, "--smoketest", new TestAudioDevices(cablePresent: true));
            var log = File.ReadAllText(Path.Combine(data, "easyvocal.log"));
            output.WriteLine(string.Join("\n", log.Split('\n').Where(l => l.Contains("Smoke") || l.Contains("[error]"))));
            KeepSnapshots(data, "smoke");
            Assert.Contains("Smoke test passed", log);
            Assert.Equal(0, code);
        }
        finally
        {
            TestFolders.Delete(data);
        }
    }

    [AvaloniaFact]
    public async Task E2E_EveryStepPasses()
    {
        var data = TestFolders.New("EV_UiE2E_");
        try
        {
            var code = await RunSession(data, "--e2e", new TestAudioDevices(cablePresent: false));
            var log = Path.Combine(data, "e2e.log");
            output.WriteLine(File.Exists(log) ? File.ReadAllText(log) : "(no e2e.log was written)");
            var appLog = Path.Combine(data, "easyvocal.log");
            if (code != 0 && File.Exists(appLog)) // what went wrong, with its stack
                output.WriteLine(string.Join("\n", File.ReadAllLines(appLog).Where(l => !l.Contains("[info]"))));
            Assert.Equal(0, code);
            Assert.Contains("E2E test passed", File.ReadAllText(log));
        }
        finally
        {
            TestFolders.Delete(data);
        }
    }

    /// <summary>Runs one session (and, for the e2e test, its relaunch) and gives its exit code.</summary>
    private static async Task<int> RunSession(string data, string mode, TestAudioDevices devices)
    {
        Environment.ExitCode = 0;
        var options = new SessionOptions(AppArgs.Parse(new[] { "--data", data, mode }), data)
        {
            Hosted = true,
            Platform = Platform(devices),
            AiFixtures = mode == "--e2e" ? Path.Combine(AppContext.BaseDirectory, "AiFixtures") : null,
            RelaunchHosted = async relaunch =>
            {
                Environment.ExitCode = 0;
                return await Run(relaunch with { Platform = Platform(new TestAudioDevices(cablePresent: true)) });
            },
        };
        return await Run(options);
    }

    private static async Task<int> Run(SessionOptions options)
    {
        var done = new TaskCompletionSource<int>();
        var session = new AppSession(options, code => done.TrySetResult(code));
        session.Start();
        try { return await done.Task.WaitAsync(Limit); }
        catch (TimeoutException)
        {
            session.Abort(); // closes its windows and stops its audio, so the next test starts clean
            throw;
        }
    }

    /// <summary>The test devices, in-memory hotkeys and startup entry, a pretend VB-CABLE; Windows' decoders and updater on Windows.</summary>
    private static PlatformServices Platform(TestAudioDevices devices)
    {
        if (OperatingSystem.IsWindows())
            return TestPlatform.Create(devices, WindowsUpdaterPlatform.Instance, WindowsPlatform.Decoders(), new MediaFoundationMp3Encoder());
        return TestPlatform.Create(devices, new FakeUpdaterPlatform(), SoundDecoders.Default);
    }

    /// <summary>EV_UI_SNAPSHOTS=&lt;folder&gt; keeps the PNGs (for looking at them); otherwise they go with the scratch folder.</summary>
    private void KeepSnapshots(string data, string prefix)
    {
        var keep = Environment.GetEnvironmentVariable("EV_UI_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(keep)) return;
        Directory.CreateDirectory(keep);
        foreach (var png in Directory.EnumerateFiles(data, "*.png"))
            File.Copy(png, Path.Combine(keep, "headless_" + Path.GetFileName(png)), overwrite: true);
        output.WriteLine("Snapshots kept in " + keep);
    }
}
