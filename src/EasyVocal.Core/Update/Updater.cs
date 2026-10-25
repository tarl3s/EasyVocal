using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Storage;

namespace EasyVocal.Core.Update;

// The in-app updater, minus its windows (UpdateWindow, UpdateCoordinator) and its platform part (IUpdaterPlatform:
// which release file, which program, the helper script; Windows: EasyVocal.Platform.Windows.WindowsUpdaterPlatform).
// EasyVocal asks GitHub for the latest release; when it is newer, the person is asked, the release's zip is downloaded,
// checked and its EasyVocal.exe unpacked to %TEMP%, and a small helper script (it waits for EasyVocal to close; PowerShell on Windows)
// swaps the exe — keeping a .old copy until the new one is in place — and opens EasyVocal again.
// The network and the script launch go through IUpdateSource and IHelperLauncher, so the tests use fakes.

/// <summary>A release version: <c>v1.2.3</c>, <c>1.2</c>, <c>1.2.3-beta.1</c> (a "+build" part is ignored).</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<AppVersion>
{
    private static readonly Regex PrereleasePart = new(@"^[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*$", RegexOptions.CultureInvariant);

    /// <summary>Reads a tag or version text; false for anything else ("latest", "1.2.3.4", "v", "1.x").</summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        var s = (text ?? "").Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (!PrereleasePart.IsMatch(pre)) return false;
        }
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        var n = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length is 0 or > 9) return false;
            foreach (char c in p) if (c is < '0' or > '9') return false;
            n[i] = int.Parse(p, CultureInfo.InvariantCulture);
        }
        version = new AppVersion(n[0], n[1], n[2], pre);
        return true;
    }

    public static AppVersion? Parse(string? text) => TryParse(text, out var v) ? v : null;

    public bool IsPrerelease => Prerelease != null;

    public int CompareTo(AppVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Prerelease == null || other.Prerelease == null)
            return (Prerelease == null ? 1 : 0) - (other.Prerelease == null ? 1 : 0); // 1.0.0-beta < 1.0.0
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out long x);
            bool bn = long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out long y);
            c = an && bn ? x.CompareTo(y) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease != null ? "-" + Prerelease : "");
}

/// <summary>A file of a release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>GitHub's "latest release", the parts the updater uses.</summary>
public sealed record ReleaseInfo(AppVersion Version, string Tag, string Notes, IReadOnlyList<ReleaseAsset> Assets,
    bool Prerelease, bool Draft, string PageUrl);

/// <summary>Why an update stopped (the window turns it into a sentence for the person).</summary>
public enum UpdateProblem { Download, TooBig, NotAZip, UnsafeZip, MissingExe, NotAnExe, FolderNotWritable, Launch }

public sealed class UpdateException(UpdateProblem problem, string detail, Exception? inner = null) : Exception(detail, inner)
{
    public UpdateProblem Problem { get; } = problem;
}

/// <summary>The rules of the updater: versions, GitHub's answer, which file, when to ask.</summary>
public static class UpdateRules
{
    public const string Repo = "tarl3s/EasyVocal";
    public const string LatestUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string PageUrl = "https://github.com/" + Repo + "/releases/latest";
    /// <summary>The release zip is under 80 MB; anything far bigger is not ours.</summary>
    public const long MaxDownloadBytes = 200L * 1024 * 1024;
    /// <summary>Notes longer than this are cut (a release body can be anything).</summary>
    public const int MaxNotesChars = 8000;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The version of the running EasyVocal (its informational version, without the "+commit" part).</summary>
    public static AppVersion RunningVersion(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (AppVersion.TryParse(info, out var v)) return v;
        var n = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new AppVersion(n.Major, n.Minor, Math.Max(0, n.Build));
    }

    /// <summary>Reads GitHub's release JSON; null when it isn't one (no version tag, not JSON, not an object).</summary>
    public static ReleaseInfo? ParseRelease(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, JsonOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var tag = Text(root, "tag_name");
            if (!AppVersion.TryParse(tag, out var version)) return null;
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var name = Text(a, "name");
                    var url = Text(a, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || url == null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                    assets.Add(new ReleaseAsset(name, url, Number(a, "size")));
                }
            }
            var notes = Text(root, "body") ?? "";
            if (notes.Length > MaxNotesChars) notes = notes[..MaxNotesChars] + "…";
            var page = Text(root, "html_url");
            if (page == null || !page.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) page = PageUrl;
            return new ReleaseInfo(version, tag!.Trim(), notes, assets, Flag(root, "prerelease"), Flag(root, "draft"), page);
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True
            || (p.ValueKind == JsonValueKind.String && string.Equals(p.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    private static long Number(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return -1;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out long n)) return n;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return n;
        return -1;
    }

    /// <summary>The first of <paramref name="names"/> (the platform's release files, best first) the release has, or null.</summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo release, IReadOnlyList<string> names)
    {
        foreach (var name in names)
            foreach (var a in release.Assets)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    /// <summary>
    /// Whether to tell the person about <paramref name="release"/>: a finished (not draft, not pre-) release newer than
    /// <paramref name="current"/>, and not the version they chose to skip — unless they asked (<paramref name="manual"/>).
    /// </summary>
    public static bool ShouldOffer(ReleaseInfo release, AppVersion current, string? skippedVersion, bool manual)
    {
        if (release.Draft || release.Prerelease || release.Version.IsPrerelease) return false;
        if (release.Version <= current) return false;
        return manual || !(AppVersion.TryParse(skippedVersion, out var skipped) && skipped == release.Version);
    }

    /// <summary>
    /// Running from a source checkout (an <c>EasyVocal.sln</c> or <c>src\EasyVocal\EasyVocal.csproj</c> in a folder
    /// above the exe, e.g. bin\Debug or publish\win-x64)? Such a build never updates itself: the code is the update.
    /// </summary>
    public static bool IsSourceBuild(string exeFolder)
    {
        try
        {
            var d = new DirectoryInfo(exeFolder);
            for (int i = 0; i < 10 && d != null; i++, d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "EasyVocal.sln"))
                    || File.Exists(Path.Combine(d.FullName, "src", "EasyVocal", "EasyVocal.csproj"))) return true;
        }
        catch (Exception) { /* an unreadable folder: not a checkout we can see */ }
        return false;
    }
}

/// <summary>A line of release notes, ready to show without a Markdown renderer.</summary>
public enum NoteKind { Heading, Bullet, Text, Blank }

public sealed record NoteLine(NoteKind Kind, string Text);

/// <summary>Release notes (GitHub Markdown) as plain lines: headings, bullets and text, markup removed.</summary>
public static class ReleaseNotes
{
    private const int MaxLines = 200;
    private static readonly Regex Image = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.CultureInvariant);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant);
    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(@"^#{1,6}\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Bullet = new(@"^[-*+]\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Rule = new(@"^([-*_]\s*){3,}$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<NoteLine> ToLines(string? markdown)
    {
        var lines = new List<NoteLine>();
        foreach (var raw in (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (lines.Count >= MaxLines) break;
            var line = raw.Trim();
            if (line.Length == 0 || Rule.IsMatch(line))
            {
                if (lines.Count > 0 && lines[^1].Kind != NoteKind.Blank) lines.Add(new NoteLine(NoteKind.Blank, ""));
                continue;
            }
            var kind = NoteKind.Text;
            if (Heading.IsMatch(line)) { kind = NoteKind.Heading; line = Heading.Replace(line, ""); }
            else if (Bullet.IsMatch(line)) { kind = NoteKind.Bullet; line = Bullet.Replace(line, ""); }
            line = Inline(line);
            if (line.Length > 0) lines.Add(new NoteLine(kind, line));
        }
        while (lines.Count > 0 && lines[^1].Kind == NoteKind.Blank) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string Inline(string s)
    {
        s = Image.Replace(s, "");
        s = Link.Replace(s, "$1");
        s = Tag.Replace(s, "");
        s = s.Replace("**", "").Replace("__", "").Replace("~~", "").Replace("`", "");
        return System.Net.WebUtility.HtmlDecode(s).Trim();
    }
}

/// <summary>
/// Unpacks downloaded releases with the same safety checks for every platform: no entry outside its folder (zip-slip),
/// no links, a size limit; Unix permission bits (the executable bit) are kept on macOS and Linux.
/// </summary>
public static class UpdatePackage
{
    /// <summary>A release that unpacks to more than this is not a release (a "zip bomb").</summary>
    public const long MaxUnpackedBytes = 600L * 1024 * 1024;

    /// <summary>
    /// Checks the zip at <paramref name="zipPath"/> (every entry stays inside its folder, the size limit) and writes its
    /// <paramref name="fileName"/> (at the top or in its one top folder) to <paramref name="folder"/>. Returns its path.
    /// A cancellation while it is written stops it and removes what was written. The platform checks the file itself.
    /// </summary>
    public static string ExtractFile(string zipPath, string folder, string fileName, CancellationToken ct = default)
    {
        var root = Root(folder);
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException ex) { throw new UpdateException(UpdateProblem.NotAZip, "The download is not a zip file.", ex); }
        using (archive)
        {
            ZipArchiveEntry? found = null;
            int foundDepth = int.MaxValue;
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                SafeTarget(root, entry.FullName);
                total += Math.Max(0, entry.Length);
                if (total > MaxUnpackedBytes) throw TooBig();
                if (!string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase)) continue;
                int depth = entry.FullName.Replace('\\', '/').Trim('/').Split('/').Length;
                if (depth <= 2 && depth < foundDepth) { found = entry; foundDepth = depth; }
            }
            if (found == null) throw new UpdateException(UpdateProblem.MissingExe, "The download doesn't contain " + fileName + ".");

            var dest = Path.Combine(root, fileName);
            ct.ThrowIfCancellationRequested();
            long budget = MaxUnpackedBytes;
            Writing(fileName, () =>
            {
                Directory.CreateDirectory(root);
                using var from = found.Open();
                CopyOut(from, dest, ref budget, ct);
            });
            SetMode(dest, UnixMode(found));
            return dest;
        }
    }

    /// <summary>
    /// One write of the unpacking. A file system error (a file where the archive wants a folder, as with the entries "a"
    /// and "a/b"; a name the system refuses) means a broken download, not a crash.
    /// </summary>
    private static void Writing(string entryName, Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is (IOException and not EndOfStreamException) or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateProblem.UnsafeZip, "The download's " + entryName + " can't be written: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Unpacks a whole zip or tar.gz (told apart by their first bytes) into <paramref name="folder"/>, with the checks
    /// above; symbolic and hard links are refused. A cancellation stops it (what was written stays for the caller to remove).
    /// </summary>
    public static void ExtractAll(string archivePath, string folder, CancellationToken ct = default)
    {
        var root = Root(folder);
        Directory.CreateDirectory(root);
        if (IsGzip(archivePath)) ExtractTarGz(archivePath, root, ct);
        else ExtractZip(archivePath, root, ct);
    }

    private static void ExtractZip(string zipPath, string root, CancellationToken ct)
    {
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException ex) { throw new UpdateException(UpdateProblem.NotAZip, "The download is not a zip or tar.gz file.", ex); }
        using (archive)
        {
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                SafeTarget(root, entry.FullName);
                total += Math.Max(0, entry.Length);
                if (total > MaxUnpackedBytes) throw TooBig();
            }
            long budget = MaxUnpackedBytes; // the sizes above are what the zip claims; this counts what really comes out
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = SafeTarget(root, entry.FullName);
                var mode = UnixMode(entry);
                if (mode.HasValue && (mode.Value & UnixTypeMask) == UnixSymlink)
                    throw new UpdateException(UpdateProblem.UnsafeZip, "The download contains a link: " + entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Writing(entry.FullName, () => Directory.CreateDirectory(target));
                    continue;
                }
                Writing(entry.FullName, () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var from = entry.Open();
                    CopyOut(from, target, ref budget, ct);
                });
                SetMode(target, mode);
            }
        }
    }

    private static void ExtractTarGz(string path, string root, CancellationToken ct)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            long total = 0, budget = MaxUnpackedBytes;
            while (tar.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                var target = SafeTarget(root, entry.Name);
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Writing(entry.Name, () => Directory.CreateDirectory(target));
                        continue;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        total += Math.Max(0, entry.Length);
                        if (total > MaxUnpackedBytes) throw TooBig();
                        Writing(entry.Name, () =>
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            if (entry.DataStream is { } data) CopyOut(data, target, ref budget, ct);
                            else File.WriteAllBytes(target, Array.Empty<byte>());
                        });
                        SetMode(target, (int)entry.Mode);
                        continue;
                    case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                        throw new UpdateException(UpdateProblem.UnsafeZip, "The download contains a link: " + entry.Name);
                    default:
                        continue; // pax/GNU headers and the like carry no file
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new UpdateException(UpdateProblem.NotAZip, "The download is not a valid tar.gz file.", ex);
        }
    }

    private static string Root(string folder) =>
        Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>Where an entry lands; an entry outside <paramref name="root"/> (or with a broken name) is an error.</summary>
    private static string SafeTarget(string root, string entryName)
    {
        string target;
        try { target = Path.GetFullPath(Path.Combine(root, entryName.Replace('\\', '/'))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new UpdateException(UpdateProblem.UnsafeZip, "The download has a file with a broken name.", ex);
        }
        if (!PathCompare.IsUnder(target, root) && !PathCompare.Same(target + Path.DirectorySeparatorChar, root))
            throw new UpdateException(UpdateProblem.UnsafeZip, "The download contains a file outside its folder: " + entryName);
        return target;
    }

    private static UpdateException TooBig() => new(UpdateProblem.TooBig, "The download unpacks to more than expected.");

    private static bool IsGzip(string path)
    {
        using var f = File.OpenRead(path);
        return f.ReadByte() == 0x1F && f.ReadByte() == 0x8B;
    }

    private const int UnixTypeMask = 0xF000, UnixSymlink = 0xA000;

    /// <summary>The Unix mode a zip made on macOS/Linux stores in the high half of its external attributes, or null.</summary>
    private static int? UnixMode(ZipArchiveEntry entry)
    {
        int mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        return mode == 0 ? null : mode;
    }

    /// <summary>The permission bits kept from an archive: read/write/execute only (never setuid, setgid or sticky).</summary>
    public const int KeptModeBits = 0x1FF;

    /// <summary>Keeps the permission bits (the executable one above all) where the system has them.</summary>
    private static void SetMode(string path, int? mode)
    {
        if (mode is not { } m || OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, (UnixFileMode)(m & KeptModeBits)); }
        catch (Exception) { /* a file system without permissions: nothing to keep */ }
    }

    /// <summary>
    /// Writes the stream to <paramref name="dest"/> in chunks, so a cancellation stops it quickly (the exe is ~80 MB).
    /// <paramref name="budget"/> is what may still be written for the whole archive: an entry that unpacks to more than
    /// its header said (a lying zip) is stopped there.
    /// </summary>
    private static void CopyOut(Stream from, string dest, ref long budget, CancellationToken ct)
    {
        bool tooBig = false;
        using (var to = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int n;
            while (!ct.IsCancellationRequested && (n = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                budget -= n;
                if (budget < 0) { tooBig = true; break; }
                to.Write(buffer, 0, n);
            }
        }
        if (tooBig)
        {
            TryDelete(dest);
            throw TooBig();
        }
        if (!ct.IsCancellationRequested) return;
        TryDelete(dest);
        ct.ThrowIfCancellationRequested();
    }

    public static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception) { /* in use: the stale-folder cleanup gets it later */ }
    }

    /// <summary>Deletes a file or a whole folder (a .app bundle, an app folder). Never throws.</summary>
    public static void TryDeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception) { /* in use: the next cleanup gets it */ }
    }
}

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
            UpdatePackage.TryDelete(archive);
            log("Update downloaded and checked: " + payload);
            return payload;
        }
        catch
        {
            TryDeleteFolder(folder);
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
        if (StagingOf(newPayload) is { } staging) TryDeleteFolder(staging);
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
        UpdatePackage.TryDeletePath(platform.InstallRoot(ExePath) + ".old");
    }

    private static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception) { /* in use: CleanStale gets it later */ }
    }
}
