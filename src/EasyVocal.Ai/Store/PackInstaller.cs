using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using EasyVocal.Core.Logging;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Storage;

namespace EasyVocal.Ai.Store;

/// <summary>The AI pack to download for this computer: its release asset, size and SHA-256.</summary>
public sealed record PackOffer(string AssetName, Uri Url, long Size, string Sha256);

/// <summary>What the AI pack's install is doing.</summary>
public enum PackStage { Looking, Downloading, Checking, Unpacking, Installing }

public readonly record struct PackProgress(PackStage Stage, long Done, long? Total);

/// <summary>Why the AI pack can't be had (shown to the person as it is).</summary>
public sealed class PackUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Where the AI pack comes from: one GitHub release of its own (tag <see cref="Tag"/>), so a new EasyVocal doesn't
/// upload a gigabyte again; an asset per OS (<see cref="AssetName"/>). Each asset's SHA-256 comes from the release
/// (GitHub's <c>digest</c> of the asset, else a <c>&lt;asset&gt;.sha256</c> file next to it): without one nothing is
/// installed.
/// </summary>
public static class PackSource
{
    public const string Repo = "tarl3s/EasyVocal";
    /// <summary>The pack's own release; a new pack format gets a new tag.</summary>
    public const string Tag = "ai-pack-v1";
    public static string ReleaseApi => $"https://api.github.com/repos/{Repo}/releases/tags/{Tag}";
    /// <summary>The release's direct links (<c>&lt;base&gt;&lt;asset&gt;</c>): used when the API can't be.</summary>
    public static string DirectBase => $"https://github.com/{Repo}/releases/download/{Tag}/";

    /// <summary>Roughly how big the download is (shown before the release was looked at).</summary>
    public const long ApproxBytes = 540L * 1024 * 1024;

    /// <summary>Most bytes a pack may have (download and unpacked).</summary>
    public const long MaxBytes = 2000L * 1024 * 1024;

    /// <summary>The asset for an OS and processor ("win"/"macos"/"linux", Arm64/X64); null = no pack for it.</summary>
    public static string? AssetName(string os, Architecture arch) => (os, arch) switch
    {
        ("win", Architecture.X64) => "EasyVocal-AI-win-x64.zip",
        ("macos", Architecture.Arm64) => "EasyVocal-AI-macos-arm64.zip",
        ("macos", Architecture.X64) => "EasyVocal-AI-macos-x64.zip",
        ("linux", Architecture.X64) => "EasyVocal-AI-linux-x64.zip",
        _ => null,
    };

    /// <summary>This computer's asset (null: none for it).</summary>
    public static string? ThisAssetName =>
        AssetName(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "", RuntimeInformation.OSArchitecture);

    /// <summary>
    /// Reads a GitHub release's JSON: the asset's link, size and SHA-256 (from its <c>digest</c>; else
    /// <paramref name="sidecarUrl"/> is the <c>.sha256</c> asset to fetch). Throws <see cref="PackUnavailableException"/>.
    /// </summary>
    public static (Uri Url, long Size, string? Sha256, Uri? SidecarUrl) Pick(string releaseJson, string assetName)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new PackUnavailableException(UiText.Mark("The AI pack isn't available for download yet."));
        (Uri, long, string?)? main = null;
        Uri? sidecar = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null || !Uri.TryCreate(url, UriKind.Absolute, out var link)) continue;
            if (name == assetName)
            {
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is string dg && dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? dg["sha256:".Length..].ToLowerInvariant() : null;
                main = (link, size, sha);
            }
            else if (name == assetName + ".sha256") sidecar = link;
        }
        if (main is not var (mu, ms, msha))
            throw new PackUnavailableException(UiText.Mark("There is no AI pack for this computer yet."));
        if (ms <= 0 || ms > MaxBytes) throw new PackUnavailableException(UiText.Mark("The AI pack on the server looks wrong. Try again later."));
        if (msha != null && !IsSha(msha)) msha = null;
        if (msha == null && sidecar == null) throw new PackUnavailableException(UiText.Mark("The AI pack can't be checked (its SHA-256 is missing), so it isn't installed."));
        return (mu, ms, msha, sidecar);
    }

    public static bool IsSha(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);
}

/// <summary>
/// Downloads, checks and installs the AI pack into the data folder (<c>&lt;data&gt;/AI</c>), and removes it.
/// <para>
/// The download goes to <c>&lt;data&gt;/AI.download/</c> and survives a cancel, a lost connection or a quit: the next
/// try goes on from there. Only a file with the release's size and SHA-256 is unpacked, into a staging folder; the pack
/// must have its engine (HuBERT fp32 and int8, RMVPE, the base generator) and its voices, then each part is moved
/// into place. The person's imported voices are never touched. A runtime library that is loaded can't be deleted on
/// Windows: it is marked and removed at the next start (<see cref="CleanLeftovers"/>).
/// </para>
/// </summary>
public sealed class PackInstaller
{
    public const string DownloadFolderName = "AI.download";
    public const string StateFile = "pack.json";
    private const string RemoveMarker = "runtime.remove";
    /// <summary>A new runtime that couldn't replace the loaded one: swapped in at the next start.</summary>
    private const string PendingRuntime = "runtime.pending";

    private readonly AiPack pack;
    private readonly HttpClient? client;
    private readonly string releaseApi;
    private readonly string? assetName;
    private readonly Uri? directBase;

    /// <param name="releaseApi">The release's API URL (tests and the e2e run: a local server).</param>
    /// <param name="assetName">The asset to install (null = this computer's).</param>
    /// <param name="directBase">Where the release's files are when its API fails (null: GitHub's for the real release, none for another <paramref name="releaseApi"/>).</param>
    public PackInstaller(AiPack pack, HttpClient? client = null, string? releaseApi = null, string? assetName = null, string? directBase = null)
    {
        this.pack = pack;
        this.client = client;
        this.releaseApi = releaseApi ?? PackSource.ReleaseApi;
        var direct = directBase ?? (releaseApi == null ? PackSource.DirectBase : null);
        this.directBase = direct == null ? null : new Uri(direct.EndsWith('/') ? direct : direct + "/");
        this.assetName = assetName ?? PackSource.ThisAssetName;
    }

    private string DataFolder => Path.GetDirectoryName(pack.Root)!;

    public string DownloadFolder => Path.Combine(DataFolder, DownloadFolderName);

    /// <summary>
    /// Looks at the release: what to download for this computer. The release's API (api.github.com) allows few
    /// requests per network without an account (a shared office, school or cloud network runs out): when it refuses
    /// or can't be reached, the release's direct links are used instead (the <c>.sha256</c> file and the asset's size
    /// from the download server), which have no such limit.
    /// </summary>
    public async Task<PackOffer> FindAsync(CancellationToken ct = default)
    {
        var name = assetName ?? throw new PackUnavailableException(UiText.Mark("There is no AI pack for this computer yet."));
        var http = client ?? SharedClient.Value;
        string json;
        ApiFailure = null;
        try
        {
            json = await Ask(http, new Uri(releaseApi), "application/vnd.github+json", ct, resp =>
                resp.StatusCode == System.Net.HttpStatusCode.NotFound ? new PackUnavailableException(UiText.Mark("The AI pack isn't available for download yet.")) : null).ConfigureAwait(false);
        }
        catch (PackUnavailableException api) when (api.InnerException != null && directBase != null)
        {
            // the API refused or couldn't be reached (not a missing release): the direct links
            ApiFailure = api;
            AppLog.Error("AI: the release API failed, trying the release's direct links", api);
            try { return await FindDirectAsync(http, name, ct).ConfigureAwait(false); }
            catch (PackUnavailableException direct)
            {
                // a server problem: the API's reason (the first one met); else what the direct links said (no pack, wrong size)
                var why = direct.InnerException != null ? api.Message : direct.Message;
                throw new PackUnavailableException(why, new AggregateException(api.InnerException!, direct.InnerException ?? direct));
            }
        }
        var (url, size, sha, sidecar) = PackSource.Pick(json, name);
        sha ??= await SidecarSha(http, sidecar!, ct).ConfigureAwait(false);
        return new PackOffer(name, url, size, sha);
    }

    /// <summary>Why the release's API wasn't used by the last <see cref="FindAsync"/> (null: it was).</summary>
    public Exception? ApiFailure { get; private set; }

    /// <summary>The offer from the release's direct links: <c>&lt;asset&gt;.sha256</c> and the asset's size (one byte asked for).</summary>
    private async Task<PackOffer> FindDirectAsync(HttpClient http, string name, CancellationToken ct)
    {
        var url = new Uri(directBase!, Uri.EscapeDataString(name));
        var sha = await SidecarSha(http, new Uri(directBase!, Uri.EscapeDataString(name + ".sha256")), ct).ConfigureAwait(false);
        long size = 0;
        await Ask(http, url, null, ct, resp =>
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new PackUnavailableException(UiText.Mark("There is no AI pack for this computer yet."));
            if (resp.IsSuccessStatusCode)
                size = resp.Content.Headers.ContentRange?.Length ?? (resp.StatusCode == System.Net.HttpStatusCode.OK ? resp.Content.Headers.ContentLength ?? 0 : 0);
            return null;
        }, firstByte: true).ConfigureAwait(false);
        if (size <= 0 || size > PackSource.MaxBytes) throw new PackUnavailableException(UiText.Mark("The AI pack on the server looks wrong. Try again later."));
        return new PackOffer(name, url, size, sha);
    }

    /// <summary>The SHA-256 written in a release's <c>.sha256</c> file.</summary>
    private static async Task<string> SidecarSha(HttpClient http, Uri url, CancellationToken ct)
    {
        var text = await Ask(http, url, null, ct, resp =>
            resp.StatusCode == System.Net.HttpStatusCode.NotFound ? new PackUnavailableException(UiText.Mark("The AI pack can't be checked (its SHA-256 is missing), so it isn't installed.")) : null).ConfigureAwait(false);
        var sha = text.Trim().Split(' ', '\t', '\n', '\r')[0].ToLowerInvariant();
        return PackSource.IsSha(sha) ? sha : throw new PackUnavailableException(UiText.Mark("The AI pack can't be checked (its SHA-256 is missing), so it isn't installed."));
    }

    /// <summary>
    /// One GET (<paramref name="firstByte"/>: only the first byte, for the headers); returns the body. A failure is a
    /// <see cref="PackUnavailableException"/> that says what happened (no connection, no answer in time, too many
    /// requests, the server's fault, a refusal), with the details (the URL, the HTTP status, the rate-limit headers and
    /// the server's own words) in its inner exception for the log. <paramref name="check"/> may give its own exception
    /// for an answer (shown as it is), or null to go on.
    /// </summary>
    private static async Task<string> Ask(HttpClient http, Uri url, string? accept, CancellationToken ct,
        Func<HttpResponseMessage, PackUnavailableException?> check, bool firstByte = false)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (accept != null) req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (firstByte) req.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp = await http.SendAsync(req, firstByte ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (check(resp) is { } own) throw own;
            if (resp.IsSuccessStatusCode) return firstByte ? "" : await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string body;
            try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or IOException) { body = ""; }
            int code = (int)resp.StatusCode;
            throw new PackUnavailableException(code is 403 or 429
                ? UiText.Mark("The download server is refusing requests from this network for now (too many of them). Try again in an hour.")
                : code >= 500
                    ? UiText.Mark("The download server has a problem right now. Try again later.")
                    : UiText.Mark("The download server refused the request. Try again later."),
                new HttpRequestException(Describe(url, resp, body), null, resp.StatusCode));
        }
        catch (HttpRequestException ex)
        {
            throw new PackUnavailableException(UiText.Mark("Couldn't reach the download server. Check your internet connection and try again."),
                new HttpRequestException($"GET {url}: {ex.Message}", ex, ex.StatusCode));
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PackUnavailableException(UiText.Mark("The download server didn't answer in time. Check your internet connection and try again."),
                new TimeoutException($"GET {url}: no answer in time", ex));
        }
    }

    /// <summary>What a refused request was, for the log: URL, status, GitHub's rate-limit headers, the start of the answer.</summary>
    internal static string Describe(Uri url, HttpResponseMessage resp, string body)
    {
        var s = new System.Text.StringBuilder($"GET {url}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        foreach (var h in new[] { "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Reset", "Retry-After", "X-GitHub-Request-Id" })
            if (resp.Headers.TryGetValues(h, out var v)) s.Append("; ").Append(h).Append('=').Append(string.Join(",", v));
        var text = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > 0) s.Append("; ").Append(text.Length > 300 ? text[..300] + "…" : text);
        return s.ToString();
    }

    /// <summary>Bytes already downloaded towards this offer (a stopped download goes on from there).</summary>
    public long Downloaded(PackOffer offer) => Downloader.ResumableBytes(Path.Combine(DownloadFolder, offer.AssetName), offer.Sha256);

    /// <summary>
    /// Downloads (or goes on downloading), checks and installs the pack. True when the runtime in use couldn't be
    /// replaced (it is loaded): the new one is swapped in at the next start, so EasyVocal should be restarted.
    /// </summary>
    public async Task<bool> InstallAsync(PackOffer offer, IProgress<PackProgress>? progress = null, CancellationToken ct = default)
    {
        if (!FileNames.IsPlain(offer.AssetName)) throw new ArgumentException("Bad asset name.", nameof(offer));
        Directory.CreateDirectory(DownloadFolder);
        var file = Path.Combine(DownloadFolder, offer.AssetName);
        if (File.Exists(file))
        {
            // a whole download left from before (an install that failed or was stopped): checked off this thread
            progress?.Report(new PackProgress(PackStage.Checking, 0, offer.Size));
            var check = progress == null ? null : new InlineProgress<DownloadProgress>(d => progress.Report(new PackProgress(PackStage.Checking, d.Done, offer.Size)));
            bool same = await Task.Run(() => new FileInfo(file).Length == offer.Size
                && string.Equals(Downloader.Sha256Of(file, check, ct), offer.Sha256, StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            if (!same) Cleanup.TryDelete(file); // an older or broken one
        }
        if (!File.Exists(file))
        {
            var p = progress == null ? null : new InlineProgress<DownloadProgress>(d => progress.Report(new PackProgress(PackStage.Downloading, d.Done, d.Total ?? offer.Size)));
            await Downloader.DownloadResumableAsync(offer.Url, file, offer.Size, offer.Sha256, p, ct, client).ConfigureAwait(false);
        }
        bool restart = await Task.Run(() => Install(file, offer, progress, ct), ct).ConfigureAwait(false);
        Cleanup.TryDelete(DownloadFolder);
        return restart;
    }

    /// <summary>
    /// Installs a pack archive that was checked already (also the e2e run's local pack). True when the runtime in use
    /// couldn't be replaced: it is swapped in at the next start (<see cref="CleanLeftovers"/>).
    /// </summary>
    public bool Install(string archive, PackOffer? offer, IProgress<PackProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new PackProgress(PackStage.Unpacking, 0, null));
        var stage = Path.Combine(DataFolder, "AI.staging-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Core.Update.UpdatePackage.ExtractAll(archive, stage, ct, PackSource.MaxBytes);
            string root = stage;
            // the archive's top level is the AI folder's content (engine/, runtime/, voices/)
            string Need(string rel) => File.Exists(Path.Combine(root, rel)) ? rel : throw new InvalidDataException("The AI pack is incomplete (" + rel + " is missing).");
            Need(Path.Combine("engine", AiPack.ContentFp32));
            Need(Path.Combine("engine", AiPack.ContentInt8));
            if (!File.Exists(Path.Combine(root, "engine", AiPack.RmvpeF16))) Need(Path.Combine("engine", AiPack.Rmvpe));
            Need(Path.Combine("engine", BaseModels.BaseGeneratorId + ".pth"));
            var voices = Path.Combine(root, "voices");
            if (!Directory.Exists(voices) || !Directory.EnumerateFiles(voices, VoiceManifest.FileName, SearchOption.AllDirectories).Any())
                throw new InvalidDataException("The AI pack is incomplete (it has no voices).");
            ct.ThrowIfCancellationRequested();

            progress?.Report(new PackProgress(PackStage.Installing, 0, null));
            Directory.CreateDirectory(pack.Root);
            Replace(Path.Combine(root, "engine"), pack.EngineFolder);
            bool restart = false;
            var marker = Path.Combine(pack.Root, RemoveMarker);
            var pending = Path.Combine(pack.Root, PendingRuntime);
            if (Directory.Exists(Path.Combine(root, "runtime")))
            {
                Cleanup.TryDelete(pending);
                if (Replace(Path.Combine(root, "runtime"), pack.RuntimeFolder)) Cleanup.TryDelete(marker);
                else
                {
                    // A loaded runtime (this session used AI voices) can't be replaced on Windows, and after Remove it
                    // may be half deleted: the new one waits next to it and takes its place at the next start.
                    Directory.Move(Path.Combine(root, "runtime"), pending);
                    File.WriteAllText(marker, "the runtime was in use: replaced by runtime.pending at the next start");
                    restart = true;
                    AppLog.Info("AI: the runtime in use is replaced at the next start (it is loaded)");
                }
            }
            Directory.CreateDirectory(pack.VoicesFolder);
            foreach (var dir in Directory.EnumerateDirectories(voices))
            {
                var id = Path.GetFileName(dir);
                if (!FileNames.IsPlain(id)) continue;
                Replace(dir, pack.VoiceFolder(id));
            }
            var state = new Dictionary<string, object?>
            {
                ["tag"] = PackSource.Tag, ["asset"] = offer?.AssetName, ["sha256"] = offer?.Sha256, ["installedUtc"] = DateTime.UtcNow,
            };
            File.WriteAllText(Path.Combine(pack.Root, StateFile), JsonSerializer.Serialize(state));
            AppLog.Info("AI: pack installed (" + (offer?.AssetName ?? Path.GetFileName(archive)) + ")");
            return restart;
        }
        finally
        {
            Cleanup.TryDelete(stage);
        }
    }

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>, replacing it; false when the old one couldn't go (kept).</summary>
    private static bool Replace(string from, string to)
    {
        if (Directory.Exists(to))
        {
            var old = to + ".old-" + Guid.NewGuid().ToString("N")[..6];
            try { Directory.Move(to, old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            Cleanup.TryDelete(old);
        }
        Directory.Move(from, to);
        return true;
    }

    /// <summary>
    /// Removes the AI pack: its engine, runtime and bundled voices (and the person's imported voices with
    /// <paramref name="alsoImported"/>), and any download left. What is loaded right now is removed at the next start.
    /// </summary>
    public void Remove(bool alsoImported)
    {
        Cleanup.TryDelete(DownloadFolder);
        if (!Directory.Exists(pack.Root)) return;
        Cleanup.TryDelete(pack.EngineFolder);
        Cleanup.TryDelete(Path.Combine(pack.Root, StateFile));
        Cleanup.TryDelete(Path.Combine(pack.Root, "tmp"));
        if (Directory.Exists(pack.VoicesFolder))
            foreach (var dir in Directory.EnumerateDirectories(pack.VoicesFolder))
            {
                bool builtIn = false;
                try { builtIn = VoiceManifest.Read(Path.Combine(dir, VoiceManifest.FileName)).BuiltIn; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { builtIn = !Path.GetFileName(dir).StartsWith("user-", StringComparison.Ordinal); }
                if (builtIn || alsoImported) Cleanup.TryDelete(dir);
            }
        Cleanup.TryDelete(pack.RuntimeFolder);
        Cleanup.TryDelete(Path.Combine(pack.Root, PendingRuntime));
        if (Directory.Exists(pack.RuntimeFolder))
        {
            try { File.WriteAllText(Path.Combine(pack.Root, RemoveMarker), "the runtime was in use: removed at the next start"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (alsoImported && !Directory.Exists(pack.RuntimeFolder)) Cleanup.TryDelete(pack.Root);
        AppLog.Info("AI: pack removed" + (alsoImported ? " with the imported voices" : ""));
    }

    /// <summary>At start, before anything loads: what a removal or an interrupted install left behind.</summary>
    public static void CleanLeftovers(AiPack pack)
    {
        var data = Path.GetDirectoryName(pack.Root)!;
        try
        {
            if (File.Exists(Path.Combine(pack.Root, RemoveMarker)))
            {
                Cleanup.TryDelete(pack.RuntimeFolder);
                var pending = Path.Combine(pack.Root, PendingRuntime);
                if (!Directory.Exists(pack.RuntimeFolder))
                {
                    // the swap a reinstall couldn't do while the runtime was loaded; the marker goes only once it is done
                    if (Directory.Exists(pending)) Directory.Move(pending, pack.RuntimeFolder);
                    Cleanup.TryDelete(Path.Combine(pack.Root, RemoveMarker));
                }
                if (Directory.Exists(pack.Root) && !Directory.EnumerateFileSystemEntries(pack.Root).Any()) Cleanup.TryDelete(pack.Root);
            }
            if (Directory.Exists(data))
                foreach (var d in Directory.EnumerateDirectories(data, "AI.staging-*")) Cleanup.TryDelete(d);
            if (Directory.Exists(pack.Root))
                foreach (var d in Directory.EnumerateDirectories(pack.Root, "*.old-*", SearchOption.AllDirectories)) Cleanup.TryDelete(d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EasyVocal", typeof(PackInstaller).Assembly.GetName().Version?.ToString(3) ?? "2"));
        return c;
    });
}
