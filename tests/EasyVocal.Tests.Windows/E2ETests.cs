using System.Diagnostics;
using Xunit.Abstractions;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

/// <summary>
/// Runs the built app's end-to-end test (<c>EasyVocal.exe --data &lt;temp&gt; --e2e</c>): the real app on test audio
/// devices, driven through its windows, checked on the recorded audio, quit and relaunched. Builds nothing itself:
/// <c>dotnet test</c> on the solution builds the app first.
/// </summary>
[Trait("Category", "E2E")]
public class E2ETests
{
    private readonly ITestOutputHelper output;

    public E2ETests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void App_E2E_EveryStepPasses()
    {
        var exe = FindAppExe();
        if (exe == null)
        {
            output.WriteLine("SKIPPED: the built EasyVocal.exe was not found (build src/EasyVocal with the same configuration first).");
            return;
        }
        var data = Path.Combine(Path.GetTempPath(), "EV_E2E_" + Guid.NewGuid().ToString("N"));
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            info.ArgumentList.Add("--data");
            info.ArgumentList.Add(data);
            info.ArgumentList.Add("--e2e");
            using var app = Process.Start(info)!;
            bool finished = app.WaitForExit(120_000);
            if (!finished)
            {
                try { app.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            }
            var log = Path.Combine(data, "e2e.log");
            output.WriteLine(exe);
            output.WriteLine(File.Exists(log) ? File.ReadAllText(log) : "(no e2e.log was written)");
            Assert.True(finished, "the e2e run did not finish within 120 s");
            Assert.Equal(0, app.ExitCode);
        }
        finally
        {
            TestFolders.Delete(data);
        }
    }

    /// <summary>src/EasyVocal/bin/&lt;same configuration&gt;/net8.0/EasyVocal.exe (the app is portable net8.0), found from this test's folder.</summary>
    private static string? FindAppExe()
    {
        var tfmDir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string config = tfmDir.Parent?.Name ?? "Debug";
        for (var dir = tfmDir; dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "EasyVocal.sln"))) continue;
            var exe = Path.Combine(dir.FullName, "src", "EasyVocal", "bin", config, "net8.0", "EasyVocal.exe");
            return File.Exists(exe) ? exe : null;
        }
        return null;
    }
}
