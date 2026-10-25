using System.Runtime.InteropServices;
using System.Text;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Update;

namespace EasyVocal.Tests.Helpers;

/// <summary>Releases without the network: a JSON answer and a local file to "download".</summary>
public sealed class FakeUpdateSource : IUpdateSource
{
    public string? Json;
    public Exception? CheckError;
    public string? ArchivePath;
    public int Downloads;
    public TaskCompletionSource? Gate;

    public Task<string> GetLatestReleaseJsonAsync(CancellationToken ct) =>
        CheckError != null ? Task.FromException<string>(CheckError) : Task.FromResult(Json ?? "");

    public async Task DownloadAsync(string url, string path, long maxBytes, IProgress<double>? progress, CancellationToken ct)
    {
        Downloads++;
        if (Gate != null) await Gate.Task.WaitAsync(ct);
        File.Copy(ArchivePath ?? throw new HttpRequestException("offline"), path, overwrite: true);
        progress?.Report(1);
    }
}

/// <summary>Records the helper scripts it was asked to start; never runs them.</summary>
public sealed class FakeHelperLauncher : IHelperLauncher
{
    public readonly List<string> Started = new();

    public void Start(string scriptPath) => Started.Add(scriptPath);
}

/// <summary>
/// A pretend platform: its release is <c>EasyVocal-test.zip</c> (or <c>.tar.gz</c>), its program a file that starts
/// with "PROG", its helper a plain text file listing the plan. <see cref="WholeArchive"/> unpacks everything and returns
/// a folder (like the macOS .app or the Linux folder) instead of one file.
/// </summary>
public sealed class FakeUpdaterPlatform : IUpdaterPlatform
{
    public static readonly byte[] Program = Encoding.ASCII.GetBytes("PROG pretend EasyVocal");

    public bool WholeArchive { get; init; }
    public bool Writable { get; set; } = true;
    public FakeHelperLauncher FakeLauncher { get; } = new();

    public IReadOnlyList<string> AssetNames(Architecture arch) => new[] { "EasyVocal-test.zip", "EasyVocal-test.tar.gz" };

    public string ExeName => "EasyVocal-test";

    public string InstallRoot(string exePath) => WholeArchive ? Path.GetDirectoryName(exePath)! : exePath;

    /// <summary>Like the real ones: a whole-folder install carries the release's easyvocal-files.txt; a single exe is just the file.</summary>
    public bool IsInstallRoot(string path) => WholeArchive ? File.Exists(Path.Combine(path, "easyvocal-files.txt")) : File.Exists(path);

    public bool CanSelfUpdate(string exePath, Action<string>? log = null)
    {
        if (!Writable) log?.Invoke("not writable (fake)");
        return Writable;
    }

    public string Unpack(string archivePath, string folder, CancellationToken ct)
    {
        if (WholeArchive)
        {
            UpdatePackage.ExtractAll(archivePath, folder, ct);
            var app = Path.Combine(folder, "EasyVocal");
            if (!File.Exists(Path.Combine(app, ExeName)))
                throw new UpdateException(UpdateProblem.MissingExe, "The download doesn't contain " + ExeName + ".");
            return app;
        }
        var exe = UpdatePackage.ExtractFile(archivePath, folder, ExeName, ct);
        var head = new byte[4];
        using (var f = File.OpenRead(exe)) f.ReadExactly(head);
        if (Encoding.ASCII.GetString(head) == "PROG") return exe;
        UpdatePackage.TryDelete(exe);
        throw new UpdateException(UpdateProblem.NotAnExe, "Not a program.");
    }

    public string HelperFileName => "update.txt";

    public Encoding HelperEncoding => new UTF8Encoding(false);

    public string BuildHelper(HelperPlan plan) =>
        "pid=" + plan.WaitPid + "\ntarget=" + plan.Target + "\nnew=" + plan.NewPayload + "\nstaging=" + plan.StagingFolder +
        "\nrelaunch=" + plan.Relaunch + " " + plan.RelaunchArguments + "\n";

    public IHelperLauncher Launcher => FakeLauncher;

    public string RelaunchArguments(string? dataFolder) => dataFolder == null ? "" : "--data " + dataFolder;
}

/// <summary>Reports progress right away (Progress&lt;T&gt; would post it later).</summary>
public sealed class SyncProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
