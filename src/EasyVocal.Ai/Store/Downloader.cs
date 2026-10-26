using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace EasyVocal.Ai.Store;

/// <summary>How far a download is: bytes so far and the total when the server said it.</summary>
public readonly record struct DownloadProgress(long Done, long? Total);

/// <summary>What a download found out besides its bytes.</summary>
public sealed class DownloadInfo
{
    /// <summary>The file name the server gave (Content-Disposition), if any.</summary>
    public string? ServerFileName { get; set; }
}

/// <summary>
/// Downloads one file to disk: HTTPS only (plain HTTP to this computer only when <see cref="AllowLoopbackHttp"/>: the
/// tests), a size cap checked against the announced length and while reading, SHA-256 computed on the way (and
/// checked when one is expected), progress, cancel, and a stall limit (nothing received for <see cref="IdleTimeout"/>).
/// A failed or cancelled download leaves nothing behind, except a resumable one (<see cref="DownloadResumableAsync"/>),
/// which keeps its <c>.part</c> to go on from.
/// </summary>
public static class Downloader
{
    /// <summary>Plain http:// to 127.0.0.1 / localhost is accepted (tests and the e2e run only; never in the app).</summary>
    public static bool AllowLoopbackHttp { get; set; }

    /// <summary>A download that receives nothing for this long fails.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 10, AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EasyVocal", typeof(Downloader).Assembly.GetName().Version?.ToString(3) ?? "2"));
        return c;
    });

    /// <summary>
    /// A link the person pasted, made directly downloadable: Hugging Face <c>/blob/</c> pages become <c>/resolve/</c>
    /// links. Only https (and loopback http when allowed) are accepted; null = not a link EasyVocal downloads from.
    /// </summary>
    public static Uri? Normalize(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u)) return null;
        if (!Allowed(u)) return null;
        string host = u.Host.ToLowerInvariant();
        if (host is "huggingface.co" or "www.huggingface.co" or "hf.co" && u.AbsolutePath.Contains("/blob/", StringComparison.Ordinal))
        {
            var b = new UriBuilder(u) { Path = u.AbsolutePath.Replace("/blob/", "/resolve/") };
            u = b.Uri;
        }
        return u;
    }

    private static bool Allowed(Uri u) => u.Scheme == Uri.UriSchemeHttps || (AllowLoopbackHttp && u.IsLoopback && u.Scheme == Uri.UriSchemeHttp);

    /// <summary>The file name the URL ends with (decoded), or null.</summary>
    public static string? FileNameOf(Uri u)
    {
        var last = Uri.UnescapeDataString(u.AbsolutePath.Split('/').LastOrDefault() ?? "");
        return Core.Storage.FileNames.IsPlain(last) ? last : null;
    }

    /// <summary>
    /// What a downloaded file is from its first bytes: ".pth" (a zip with a data.pkl: torch.save), ".zip", ".index"
    /// (faiss), ".onnx" (a protobuf ModelProto starts with its ir_version), or null.
    /// </summary>
    public static string? Sniff(string path)
    {
        var head = new byte[8];
        int n;
        using (var f = File.OpenRead(path)) n = f.Read(head, 0, head.Length);
        if (n >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K')
        {
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                return zip.Entries.Any(e => e.FullName.EndsWith("data.pkl", StringComparison.Ordinal)) ? ".pth" : ".zip";
            }
            catch (InvalidDataException) { return null; }
        }
        if (n >= 4 && head[0] == (byte)'I' && head[1] == (byte)'w') return ".index";
        if (n >= 2 && head[0] == 0x08) return ".onnx";
        return null;
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="path"/>; returns its SHA-256 (lowercase hex).</summary>
    /// <param name="expectedSha256">When given, a different file is deleted and refused.</param>
    public static async Task<string> DownloadAsync(Uri url, string path, long maxBytes, string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default, HttpClient? client = null, DownloadInfo? info = null)
    {
        var tmp = path + ".part";
        try
        {
            return await Fetch(url, path, tmp, maxBytes, expectedSha256, null, resume: false, progress, ct, client, info).ConfigureAwait(false);
        }
        finally
        {
            Core.Storage.Cleanup.TryDelete(tmp);
        }
    }

    /// <summary>
    /// Like <see cref="DownloadAsync"/>, but a cancelled or broken download keeps its <c>path.part</c> and the next try
    /// goes on from where it stopped (an HTTP range; a server that won't, sends the whole file again). The finished
    /// file must have <paramref name="expectedSize"/> bytes and <paramref name="expectedSha256"/>, else it is deleted.
    /// The <c>.part</c> carries the SHA-256 it is meant to become (<c>path.part.sha256</c>): a part of another file (a
    /// newer pack, another asset) is dropped instead of being continued.
    /// </summary>
    public static async Task<string> DownloadResumableAsync(Uri url, string path, long expectedSize, string expectedSha256,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default, HttpClient? client = null)
    {
        var tmp = path + ".part";
        var meant = tmp + ".sha256";
        if (ResumableBytes(path, expectedSha256) == 0 || new FileInfo(tmp).Length > expectedSize)
        {
            Core.Storage.Cleanup.TryDelete(tmp);
            Core.Storage.Cleanup.TryDelete(meant);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(meant, expectedSha256.ToLowerInvariant());
        try
        {
            var hex = await Fetch(url, path, tmp, expectedSize, expectedSha256, expectedSize, resume: true, progress, ct, client, null).ConfigureAwait(false);
            Core.Storage.Cleanup.TryDelete(meant);
            return hex;
        }
        catch (InvalidDataException)
        {
            Core.Storage.Cleanup.TryDelete(tmp); // the bytes are wrong: never go on from them
            Core.Storage.Cleanup.TryDelete(meant);
            throw;
        }
    }

    /// <summary>
    /// Bytes of <paramref name="path"/>'s <c>.part</c> that a resumable download of <paramref name="expectedSha256"/>
    /// would go on from: 0 when there is none or it was meant to become another file.
    /// </summary>
    public static long ResumableBytes(string path, string expectedSha256)
    {
        var tmp = path + ".part";
        try
        {
            if (!File.Exists(tmp) || !File.Exists(tmp + ".sha256")) return 0;
            var meant = File.ReadAllText(tmp + ".sha256").Trim();
            return string.Equals(meant, expectedSha256, StringComparison.OrdinalIgnoreCase) ? new FileInfo(tmp).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static async Task<string> Fetch(Uri url, string path, string tmp, long maxBytes, string? expectedSha256, long? expectedSize,
        bool resume, IProgress<DownloadProgress>? progress, CancellationToken ct, HttpClient? client, DownloadInfo? info)
    {
        if (!Allowed(url)) throw new IOException("Only https links can be downloaded.");
        long have = resume && File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        HttpResponseMessage resp;
        try { resp = await (client ?? Shared.Value).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("The server didn't answer."); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new IOException($"The download failed: {(int)resp.StatusCode} {resp.ReasonPhrase}.");
            bool partial = have > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.From == have;
            if (!partial) have = 0; // the whole file again
            if (info != null && resp.Content.Headers.ContentDisposition is { } cd)
            {
                var name = (cd.FileNameStar ?? cd.FileName)?.Trim('"');
                if (name != null && Core.Storage.FileNames.IsPlain(name)) info.ServerFileName = name;
            }
            long? total = resp.Content.Headers.ContentLength is long len ? have + len : null;
            if (total > maxBytes) throw new IOException($"The file is too big ({Mb(total.Value)}; at most {Mb(maxBytes)}).");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (have > 0)
            {
                await using var old = File.OpenRead(tmp);
                var b = new byte[1 << 16];
                int r;
                while ((r = await old.ReadAsync(b, ct).ConfigureAwait(false)) > 0) sha.AppendData(b, 0, r);
            }
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 16];
                long done = have;
                long lastReport = -1;
                while (true)
                {
                    idle.CancelAfter(IdleTimeout);
                    int n;
                    try { n = await src.ReadAsync(buf, idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("The download stalled (nothing arrived for a minute)."); }
                    if (n <= 0) break;
                    done += n;
                    if (done > maxBytes) throw new IOException($"The file is too big (more than {Mb(maxBytes)}).");
                    sha.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (progress != null && done - lastReport >= 256 * 1024) { lastReport = done; progress.Report(new DownloadProgress(done, total ?? expectedSize)); }
                }
                if ((total.HasValue && done != total.Value) || (expectedSize.HasValue && done != expectedSize.Value))
                    throw new IOException("The download stopped before the end.");
                progress?.Report(new DownloadProgress(done, total ?? expectedSize));
            }
            var hex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha256 != null && !string.Equals(hex, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file isn't the expected one (SHA-256 differs).");
            File.Move(tmp, path, overwrite: true);
            return hex;
        }
    }

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):0} MB";

    /// <summary>SHA-256 of a file (lowercase hex), with progress (bytes read so far) and cancel for a big one.</summary>
    public static string Sha256Of(string path, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long done = 0, total = f.Length, lastReport = 0;
        int n;
        while ((n = f.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buf, 0, n);
            done += n;
            if (progress != null && (done - lastReport >= 8L << 20 || done == total)) { lastReport = done; progress.Report(new DownloadProgress(done, total)); }
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>Reports on the reporting thread (unlike Progress&lt;T&gt;, which posts and may arrive after the work is done).</summary>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> report;
    public InlineProgress(Action<T> report) => this.report = report;
    public void Report(T value) => report(value);
}
