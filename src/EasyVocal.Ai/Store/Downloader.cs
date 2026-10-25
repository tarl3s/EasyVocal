using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace EasyVocal.Ai.Store;

/// <summary>How far a download is: bytes so far and the total when the server said it.</summary>
public readonly record struct DownloadProgress(long Done, long? Total);

/// <summary>
/// Downloads one file to disk: HTTPS (or plain HTTP to this computer, for the tests), a size cap checked against the
/// announced length and while reading, SHA-256 computed on the way (and checked when one is expected), progress,
/// cancel. A failed or cancelled download leaves nothing behind.
/// </summary>
public static class Downloader
{
    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10, AutomaticDecompression = System.Net.DecompressionMethods.None })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EasyVocal", typeof(Downloader).Assembly.GetName().Version?.ToString(3) ?? "2"));
        return c;
    });

    /// <summary>
    /// A link the person pasted, made directly downloadable: Hugging Face <c>/blob/</c> pages become <c>/resolve/</c>
    /// links. Only https (and http to localhost) are accepted; null = not a link EasyVocal downloads from.
    /// </summary>
    public static Uri? Normalize(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u)) return null;
        bool local = u.IsLoopback;
        if (u.Scheme != Uri.UriSchemeHttps && !(local && u.Scheme == Uri.UriSchemeHttp)) return null;
        if (u.Host.EndsWith("huggingface.co", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.Contains("/blob/", StringComparison.Ordinal))
        {
            var b = new UriBuilder(u) { Path = u.AbsolutePath.Replace("/blob/", "/resolve/") };
            u = b.Uri;
        }
        return u;
    }

    /// <summary>The file name the URL ends with (decoded), or null.</summary>
    public static string? FileNameOf(Uri u)
    {
        var last = Uri.UnescapeDataString(u.AbsolutePath.Split('/').LastOrDefault() ?? "");
        return Core.Storage.FileNames.IsPlain(last) ? last : null;
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="path"/>; returns its SHA-256 (lowercase hex).</summary>
    /// <param name="expectedSha256">When given, a different file is deleted and refused.</param>
    public static async Task<string> DownloadAsync(Uri url, string path, long maxBytes, string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default, HttpClient? client = null)
    {
        var tmp = path + ".part";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await (client ?? Shared.Value).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new IOException($"The download failed: {(int)resp.StatusCode} {resp.ReasonPhrase}.");
            long? total = resp.Content.Headers.ContentLength;
            if (total > maxBytes) throw new IOException($"The file is too big ({Mb(total.Value)}; at most {Mb(maxBytes)}).");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 16];
                long done = 0;
                long lastReport = -1;
                while (true)
                {
                    int n = await src.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    done += n;
                    if (done > maxBytes) throw new IOException($"The file is too big (more than {Mb(maxBytes)}).");
                    sha.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (progress != null && done - lastReport >= 256 * 1024) { lastReport = done; progress.Report(new DownloadProgress(done, total)); }
                }
                if (total.HasValue && done != total.Value) throw new IOException("The download stopped before the end.");
                progress?.Report(new DownloadProgress(done, total));
            }
            var hex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha256 != null && !string.Equals(hex, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file isn't the expected one (SHA-256 differs).");
            File.Move(tmp, path, overwrite: true);
            return hex;
        }
        finally
        {
            Core.Storage.Cleanup.TryDelete(tmp);
        }
    }

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):0} MB";

    /// <summary>SHA-256 of a file (lowercase hex).</summary>
    public static string Sha256Of(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }
}

/// <summary>Reports on the reporting thread (unlike Progress&lt;T&gt;, which posts and may arrive after the work is done).</summary>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> report;
    public InlineProgress(Action<T> report) => this.report = report;
    public void Report(T value) => report(value);
}
