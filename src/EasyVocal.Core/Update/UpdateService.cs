using System.Runtime.InteropServices;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Storage;

namespace EasyVocal.Core.Update;

/// <summary>What the helper script does: wait for <see cref="WaitPid"/>, put <see cref="NewPayload"/> in place of
/// <see cref="Target"/> (the platform's install root, with a .old copy until it worked), open EasyVocal again, delete
/// <see cref="StagingFolder"/>.</summary>
public sealed record HelperPlan(int WaitPid, string Target, string NewPayload, string? StagingFolder, string LogFile,
    bool Relaunch, string RelaunchArguments);

/// <summary>Where releases come from (GitHub; a fake in the tests).</summary>
public interface IUpdateSource
{
    /// <summary>The JSON of the latest release.</summary>
    Task<string> GetLatestReleaseJsonAsync(CancellationToken ct);

    /// <summary>Saves <paramref name="url"/> to <paramref name="path"/>; more than <paramref name="maxBytes"/> is an error.</summary>
    Task DownloadAsync(string url, string path, long maxBytes, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Starts the helper script (a fake in the tests: it never really runs).</summary>
public interface IHelperLauncher
{
    void Start(string scriptPath);
}

/// <summary>GitHub's API, no sign-in.</summary>
public sealed class GitHubUpdateSource(string userAgent) : IUpdateSource
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return http;
    }

    public async Task<string> GetLatestReleaseJsonAsync(CancellationToken ct)
    {
        using var http = Client(CheckTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, UpdateRules.LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string url, string path, long maxBytes, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = Client(DownloadTimeout);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        if (total > maxBytes) throw new UpdateException(UpdateProblem.TooBig, "The download is bigger than expected.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path);
        var buffer = new byte[64 * 1024];
        long done = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            if (done > maxBytes) throw new UpdateException(UpdateProblem.TooBig, "The download is bigger than expected.");
            if (total > 0) progress?.Report((double)done / total.Value);
        }
    }
}

/// <summary>How a check ended.</summary>
public enum UpdateStatus { UpToDate, Available, Failed }

public sealed record UpdateCheck(UpdateStatus Status, ReleaseInfo? Release = null, ReleaseAsset? Asset = null, string? Detail = null);

/// <summary>Check, download and install, for the running <see cref="ExePath"/>.</summary>
public sealed class UpdateService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);
    private readonly IUpdateSource source;
    private readonly IUpdaterPlatform platform;
    private readonly IHelperLauncher launcher;
    private readonly Action<string> log;

    /// <param name="launcher">Starts the helper (normally <see cref="IUpdaterPlatform.Launcher"/>; the tests pass a fake).</param>
    /// <param name="workRoot">Where downloads are unpacked, one folder per attempt (<c>%TEMP%\EasyVocal_update</c>).</param>
    public UpdateService(IUpdaterPlatform platform, IUpdateSource source, IHelperLauncher launcher, AppVersion current, string exePath, string workRoot,
        Action<string>? log = null)
    {
        this.source = source;
        this.platform = platform;
        this.launcher = launcher;
        this.log = log ?? (_ => { });
        Current = current;
        ExePath = exePath;
        WorkRoot = workRoot;
    }

    public static string DefaultWorkRoot => Path.Combine(Path.GetTempPath(), "EasyVocal_update");

    public AppVersion Current { get; }
    public string ExePath { get; }
    public string WorkRoot { get; }

    /// <summary>Asks for the latest release. Never throws (a cancellation included): a failure is <see cref="UpdateStatus.Failed"/>.</summary>
    public async Task<UpdateCheck> CheckAsync(string? skippedVersion, bool manual, CancellationToken ct = default)
    {
        string json;
        try { json = await source.GetLatestReleaseJsonAsync(ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            log("Update check failed: " + ex.GetType().Name + ": " + ex.Message);
            return new UpdateCheck(UpdateStatus.Failed, Detail: ex.Message);
        }
        var release = UpdateRules.ParseRelease(json);
        if (release == null)
        {
            log("Update check: GitHub's answer has no release version");
            return new UpdateCheck(UpdateStatus.Failed, Detail: "no release version");
        }
        if (!UpdateRules.ShouldOffer(release, Current, skippedVersion, manual))
        {
            log($"Update check: latest is {release.Tag}, this is {Current}: nothing to do");
            return new UpdateCheck(UpdateStatus.UpToDate, release);
        }
        var asset = UpdateRules.PickAsset(release, platform.AssetNames(RuntimeInformation.OSArchitecture));
        if (asset == null)
        {
            log($"Update check: {release.Tag} has no file for this computer ({RuntimeInformation.OSArchitecture})");
            return new UpdateCheck(UpdateStatus.Failed, release, Detail: "no file for this computer");
        }
        log($"Update check: {release.Tag} is available (this is {Current})");
        return new UpdateCheck(UpdateStatus.Available, release, asset);
    }

    /// <summary>Downloads the release file, checks and unpacks it (<see cref="IUpdaterPlatform.Unpack"/>); returns the
    /// payload's path (in its own folder under <see cref="WorkRoot"/>). On a failure or cancellation nothing is left behind.</summary>
    public async Task<string> DownloadAsync(ReleaseAsset asset, IProgress<double>? progress, CancellationToken ct)
    {
        if (asset.Size > UpdateRules.MaxDownloadBytes) throw new UpdateException(UpdateProblem.TooBig, "The download is bigger than expected.");
        TempFolders.CleanStale(WorkRoot, StaleAfter);
        var folder = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var archive = Path.Combine(folder, asset.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? "release.tar.gz" : "release.zip");
            try { await source.DownloadAsync(asset.Url, archive, UpdateRules.MaxDownloadBytes, progress, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not UpdateException and not OperationCanceledException)
            {
                throw new UpdateException(UpdateProblem.Download, ex.GetType().Name + ": " + ex.Message, ex);
            }
            ct.ThrowIfCancellationRequested();
            var payload = platform.Unpack(archive, Path.Combine(folder, "new"), ct);
            Cleanup.TryDelete(archive);
            log("Update downloaded and checked: " + payload);
            return payload;
        }
        catch
        {
            Cleanup.TryDelete(folder);
            throw;
        }
    }

    /// <summary>Can the running EasyVocal replace itself? If not, the release page is opened instead.</summary>
    public bool CanSelfUpdate() => platform.CanSelfUpdate(ExePath, log);

    /// <summary>
    /// Writes the helper next to <paramref name="newPayload"/> and starts it; it waits for <paramref name="waitPid"/> to
    /// exit, so the caller quits right after. Returns the script's path.
    /// </summary>
    public string StartInstall(string newPayload, int waitPid, string logFile, string relaunchArguments, bool relaunch = true)
    {
        if (!CanSelfUpdate())
            throw new UpdateException(UpdateProblem.FolderNotWritable, "EasyVocal can't replace " + platform.ExeName + ": its folder can't be written.");
        // The helper deletes its staging folder when it is done: it must be one of our attempts, never a folder above them.
        var staging = StagingOf(newPayload)
            ?? throw new UpdateException(UpdateProblem.Launch, "The update to install is not one this updater downloaded: " + newPayload);
        var script = Path.Combine(staging, platform.HelperFileName);
        var plan = new HelperPlan(waitPid, platform.InstallRoot(ExePath), newPayload, staging, logFile, relaunch, relaunchArguments);
        File.WriteAllText(script, platform.BuildHelper(plan), platform.HelperEncoding);
        try { launcher.Start(script); }
        catch (Exception ex) when (ex is not UpdateException)
        {
            throw new UpdateException(UpdateProblem.Launch, ex.GetType().Name + ": " + ex.Message, ex);
        }
        log("Update helper started: " + script);
        return script;
    }

    /// <summary>A downloaded payload nobody will install (the window closed while it was checked): its folder goes.</summary>
    public void Discard(string newPayload)
    {
        if (StagingOf(newPayload) is { } staging) Cleanup.TryDelete(staging);
        log("Update download discarded: " + newPayload);
    }

    /// <summary>
    /// The attempt's own folder: the child of <see cref="WorkRoot"/> the payload is in. Null when the payload is not under
    /// <see cref="WorkRoot"/> (then there is nothing of ours to delete or write into).
    /// </summary>
    private string? StagingOf(string newPayload)
    {
        string root, full;
        try
        {
            root = Path.GetFullPath(WorkRoot).TrimEnd(Path.DirectorySeparatorChar);
            full = Path.GetFullPath(newPayload);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!PathCompare.IsUnder(full, root + Path.DirectorySeparatorChar)) return null;
        var dir = Path.GetDirectoryName(full);
        while (dir != null && Path.GetDirectoryName(dir) is { } parent && !PathCompare.Same(parent.TrimEnd(Path.DirectorySeparatorChar), root))
            dir = parent;
        // dir is now a child of root (the payload sat at least one folder below it); the payload itself in root: none.
        return dir != null && PathCompare.Same(Path.GetDirectoryName(dir)?.TrimEnd(Path.DirectorySeparatorChar), root) ? dir : null;
    }

    /// <summary>Removes old attempts' folders and the .old copy (a file, a .app bundle or a folder) a finished update could not delete.</summary>
    public void CleanStale()
    {
        TempFolders.CleanStale(WorkRoot, StaleAfter);
        var old = platform.InstallRoot(ExePath) + ".old";
        if (platform.IsInstallRoot(old)) Cleanup.TryDelete(old); // only a copy of EasyVocal the helper left
    }
}
