using System.Diagnostics;
using Xunit.Abstractions;

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
            for (int attempt = 0; attempt < 10 && Directory.Exists(data); attempt++)
            {
                try { Directory.Delete(data, recursive: true); }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }

    /// <summary>src/EasyVocal/bin/&lt;same configuration&gt;/&lt;same framework&gt;/EasyVocal.exe, found from this test's folder.</summary>
    private static string? FindAppExe()
    {
        var tfmDir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string tfm = tfmDir.Name, config = tfmDir.Parent?.Name ?? "Debug";
        for (var dir = tfmDir; dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "EasyVocal.sln"))) continue;
            var exe = Path.Combine(dir.FullName, "src", "EasyVocal", "bin", config, tfm, "EasyVocal.exe");
            return File.Exists(exe) ? exe : null;
        }
        return null;
    }
}
