using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyVocal;

// The in-app updater, minus its windows (UpdateWindow, UpdateCoordinator): no WPF here, so the tests compile this file.
// EasyVocal asks GitHub for the latest release; when it is newer, the person is asked, the release's zip is downloaded,
// checked and its EasyVocal.exe unpacked to %TEMP%, and a small PowerShell script (it waits for EasyVocal to close)
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
    public const string ExeName = "EasyVocal.exe";
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

    /// <summary>The zip for this PC: <c>EasyVocal-win-x64.zip</c> (also on ARM PCs, which run x64 programs), or null.</summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo release, Architecture os)
    {
        string[] names = os switch
        {
            Architecture.X64 => new[] { "EasyVocal-win-x64.zip" },
            Architecture.Arm64 => new[] { "EasyVocal-win-arm64.zip", "EasyVocal-win-x64.zip" },
            _ => Array.Empty<string>(),
        };
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

/// <summary>Checks a downloaded release zip and takes its EasyVocal.exe out.</summary>
public static class UpdatePackage
{
    /// <summary>A zip that unpacks to more than this is not a release (a "zip bomb").</summary>
    public const long MaxUnpackedBytes = 600L * 1024 * 1024;

    /// <summary>
    /// Checks <paramref name="zipPath"/> (a zip; no entry outside its folder; an EasyVocal.exe at its top or in its one
    /// top folder that is a Windows program) and writes that exe to <paramref name="folder"/>. Returns its path.
    /// A cancellation while it is written stops it and removes what was written.
    /// </summary>
    public static string ExtractExe(string zipPath, string folder, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException ex) { throw new UpdateException(UpdateProblem.NotAZip, "The download is not a zip file.", ex); }
        using (archive)
        {
            ZipArchiveEntry? exe = null;
            int exeDepth = int.MaxValue;
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                string target;
                try { target = Path.GetFullPath(Path.Combine(root, entry.FullName)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    throw new UpdateException(UpdateProblem.UnsafeZip, "The download has a file with a broken name.", ex);
                }
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException(UpdateProblem.UnsafeZip, "The download contains a file outside its folder: " + entry.FullName);
                total += Math.Max(0, entry.Length);
                if (total > MaxUnpackedBytes) throw new UpdateException(UpdateProblem.TooBig, "The download unpacks to more than expected.");
                if (!string.Equals(entry.Name, UpdateRules.ExeName, StringComparison.OrdinalIgnoreCase)) continue;
                int depth = entry.FullName.Replace('\\', '/').Trim('/').Split('/').Length;
                if (depth <= 2 && depth < exeDepth) { exe = entry; exeDepth = depth; }
            }
            if (exe == null) throw new UpdateException(UpdateProblem.MissingExe, "The download doesn't contain EasyVocal.exe.");

            Directory.CreateDirectory(root);
            var dest = Path.Combine(root, UpdateRules.ExeName);
            ct.ThrowIfCancellationRequested();
            CopyOut(exe, dest, ct);
            if (!LooksLikeProgram(dest))
            {
                TryDelete(dest);
                throw new UpdateException(UpdateProblem.NotAnExe, "The EasyVocal.exe in the download is not a Windows program.");
            }
            return dest;
        }
    }

    /// <summary>Writes the entry to <paramref name="dest"/> in chunks, so a cancellation stops it quickly (the exe is ~80 MB).</summary>
    private static void CopyOut(ZipArchiveEntry entry, string dest, CancellationToken ct)
    {
        using (var from = entry.Open())
        using (var to = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int n;
            while (!ct.IsCancellationRequested && (n = from.Read(buffer, 0, buffer.Length)) > 0) to.Write(buffer, 0, n);
        }
        if (!ct.IsCancellationRequested) return;
        TryDelete(dest);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>A Windows program starts with "MZ".</summary>
    internal static bool LooksLikeProgram(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return f.Length > 2 && f.ReadByte() == 'M' && f.ReadByte() == 'Z';
        }
        catch (IOException) { return false; }
    }

    internal static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception) { /* in use: the stale-folder cleanup gets it later */ }
    }
}

/// <summary>What the helper script does: wait for <see cref="WaitPid"/>, put <see cref="NewExe"/> in place of
/// <see cref="TargetExe"/> (with a .old copy until it worked), open EasyVocal again, delete <see cref="StagingFolder"/>.</summary>
public sealed record HelperPlan(int WaitPid, string TargetExe, string NewExe, string? StagingFolder, string LogFile,
    bool Relaunch, string RelaunchArguments);

/// <summary>The helper script (Windows PowerShell 5.1, present on every Windows 10/11).</summary>
public static class HelperScript
{
    /// <summary>The script's file encoding: Windows PowerShell reads a file without a BOM in the ANSI code page,
    /// which would break paths like "Área de Trabalho".</summary>
    public static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>A PowerShell single-quoted literal: every kind of single quote PowerShell knows is doubled.</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('\'');
        foreach (char c in value)
        {
            sb.Append(c);
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B') sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>The command line EasyVocal gets when the helper opens it again (e.g. <c>--data "C:\x y"</c>).</summary>
    public static string RelaunchArguments(string? dataFolder) =>
        dataFolder == null ? "" : "--data " + QuoteArgument(Path.TrimEndingDirectorySeparator(dataFolder));

    /// <summary>
    /// One argument in double quotes, read back exactly by Windows' command-line rules: backslashes right before a
    /// quote (the closing one included, as in <c>"D:\\"</c>) are doubled and a quote inside is escaped.
    /// </summary>
    public static string QuoteArgument(string value)
    {
        var sb = new StringBuilder(value.Length + 4).Append('"');
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            if (c == '"') sb.Append('\\', 2 * slashes + 1);
            else sb.Append('\\', slashes);
            sb.Append(c);
            slashes = 0;
        }
        return sb.Append('\\', 2 * slashes).Append('"').ToString();
    }

    public static string Build(HelperPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("# EasyVocal update helper: written by EasyVocal, runs once after it closes, then removes its folder.\r\n");
        sb.Append("$WaitPid = ").Append(plan.WaitPid.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("$Target = ").Append(Quote(plan.TargetExe)).Append("\r\n");
        sb.Append("$NewExe = ").Append(Quote(plan.NewExe)).Append("\r\n");
        sb.Append("$Staging = ").Append(Quote(plan.StagingFolder ?? "")).Append("\r\n");
        sb.Append("$Log = ").Append(Quote(plan.LogFile)).Append("\r\n");
        sb.Append("$Relaunch = ").Append(plan.Relaunch ? "$true" : "$false").Append("\r\n");
        sb.Append("$RelaunchArgs = ").Append(Quote(plan.RelaunchArguments)).Append("\r\n");
        sb.Append(Body);
        return sb.ToString();
    }

    /// <summary>Writes the script (with its BOM) and returns how to run it, hidden.</summary>
    public static ProcessStartInfo StartInfo(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptPath })
            psi.ArgumentList.Add(a);
        return psi;
    }

    // Plain ASCII; the paths are the literals above it.
    private const string Body = """
$ErrorActionPreference = 'Stop'
function Write-Log([string]$Text) {
    try { Add-Content -LiteralPath $Log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' [update helper] ' + $Text) -Encoding UTF8 } catch { }
}
function Start-EasyVocal {
    if (-not $Relaunch) { return }
    try {
        $dir = Split-Path -Parent $Target
        if ($RelaunchArgs) { Start-Process -FilePath $Target -WorkingDirectory $dir -ArgumentList $RelaunchArgs }
        else { Start-Process -FilePath $Target -WorkingDirectory $dir }
    } catch { Write-Log ('could not open EasyVocal again: ' + $_) }
}
function Remove-Staging {
    if ($Staging) { try { Remove-Item -LiteralPath $Staging -Recurse -Force } catch { } }
}

Write-Log ('updating ' + $Target)
if ($WaitPid -gt 0) {
    $proc = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
    if ($proc -and -not $proc.WaitForExit(60000)) { Write-Log 'EasyVocal did not close: nothing was changed'; Remove-Staging; exit 4 }
}
$Backup = $Target + '.old'
$moved = $false
for ($i = 0; $i -lt 40 -and -not $moved; $i++) {
    try {
        if (Test-Path -LiteralPath $Backup) { Remove-Item -LiteralPath $Backup -Force }
        Move-Item -LiteralPath $Target -Destination $Backup -Force
        $moved = $true
    } catch { Start-Sleep -Milliseconds 250 }
}
if (-not $moved) { Write-Log 'could not move the old version aside: nothing was changed'; Start-EasyVocal; Remove-Staging; exit 2 }
try {
    Copy-Item -LiteralPath $NewExe -Destination $Target -Force
} catch {
    Write-Log ('the copy failed, the old version is put back: ' + $_)
    try { if (Test-Path -LiteralPath $Target) { Remove-Item -LiteralPath $Target -Force } } catch { }
    try { Move-Item -LiteralPath $Backup -Destination $Target -Force } catch { Write-Log ('could not put the old version back: ' + $_) }
    Start-EasyVocal
    Remove-Staging
    exit 3
}
try { Remove-Item -LiteralPath $Backup -Force } catch { Write-Log ('the old copy stays for now: ' + $_) }
Write-Log 'updated'
Start-EasyVocal
Remove-Staging
exit 0

""";
}

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

/// <summary>Runs the helper with Windows PowerShell, hidden. It outlives EasyVocal.</summary>
public sealed class PowerShellLauncher : IHelperLauncher
{
    public void Start(string scriptPath)
    {
        using var p = Process.Start(HelperScript.StartInfo(scriptPath))
            ?? throw new UpdateException(UpdateProblem.Launch, "PowerShell did not start.");
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
    private readonly IHelperLauncher launcher;
    private readonly Action<string> log;

    /// <param name="workRoot">Where downloads are unpacked, one folder per attempt (<c>%TEMP%\EasyVocal_update</c>).</param>
    public UpdateService(IUpdateSource source, IHelperLauncher launcher, AppVersion current, string exePath, string workRoot,
        Action<string>? log = null)
    {
        this.source = source;
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
        var asset = UpdateRules.PickAsset(release, RuntimeInformation.OSArchitecture);
        if (asset == null)
        {
            log($"Update check: {release.Tag} has no zip for this PC ({RuntimeInformation.OSArchitecture})");
            return new UpdateCheck(UpdateStatus.Failed, release, Detail: "no zip for this PC");
        }
        log($"Update check: {release.Tag} is available (this is {Current})");
        return new UpdateCheck(UpdateStatus.Available, release, asset);
    }

    /// <summary>Downloads the zip, checks it and unpacks its EasyVocal.exe; returns that exe's path (in its own
    /// folder under <see cref="WorkRoot"/>). On a failure or cancellation nothing is left behind.</summary>
    public async Task<string> DownloadAsync(ReleaseAsset asset, IProgress<double>? progress, CancellationToken ct)
    {
        if (asset.Size > UpdateRules.MaxDownloadBytes) throw new UpdateException(UpdateProblem.TooBig, "The download is bigger than expected.");
        VbCableInstaller.CleanStale(WorkRoot, StaleAfter);
        var folder = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var zip = Path.Combine(folder, "release.zip");
            try { await source.DownloadAsync(asset.Url, zip, UpdateRules.MaxDownloadBytes, progress, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not UpdateException and not OperationCanceledException)
            {
                throw new UpdateException(UpdateProblem.Download, ex.GetType().Name + ": " + ex.Message, ex);
            }
            ct.ThrowIfCancellationRequested();
            var exe = UpdatePackage.ExtractExe(zip, Path.Combine(folder, "new"), ct);
            UpdatePackage.TryDelete(zip);
            log("Update downloaded and checked: " + exe);
            return exe;
        }
        catch
        {
            TryDeleteFolder(folder);
            throw;
        }
    }

    /// <summary>Can the running exe be swapped (its folder is writable)?</summary>
    public bool CanReplaceExe()
    {
        try
        {
            var dir = Path.GetDirectoryName(ExePath)!;
            var probe = Path.Combine(dir, "EasyVocal.update-check." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            log("Update: the folder of " + ExePath + " can't be written: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Writes the helper next to <paramref name="newExe"/> and starts it; it waits for <paramref name="waitPid"/> to
    /// exit, so the caller quits right after. Returns the script's path.
    /// </summary>
    public string StartInstall(string newExe, int waitPid, string logFile, string relaunchArguments, bool relaunch = true)
    {
        if (!CanReplaceExe()) throw new UpdateException(UpdateProblem.FolderNotWritable, "The folder of EasyVocal.exe can't be written.");
        var staging = StagingOf(newExe);
        var script = Path.Combine(staging, "update.ps1");
        var plan = new HelperPlan(waitPid, ExePath, newExe, staging, logFile, relaunch, relaunchArguments);
        File.WriteAllText(script, HelperScript.Build(plan), HelperScript.FileEncoding);
        try { launcher.Start(script); }
        catch (Exception ex) when (ex is not UpdateException)
        {
            throw new UpdateException(UpdateProblem.Launch, ex.GetType().Name + ": " + ex.Message, ex);
        }
        log("Update helper started: " + script);
        return script;
    }

    /// <summary>A downloaded exe nobody will install (the window closed while it was checked): its folder goes.</summary>
    public void Discard(string newExe)
    {
        var staging = StagingOf(newExe);
        var root = Path.GetFullPath(WorkRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(staging).StartsWith(root, StringComparison.OrdinalIgnoreCase)) TryDeleteFolder(staging);
        log("Update download discarded: " + newExe);
    }

    /// <summary>The attempt's own folder under <see cref="WorkRoot"/> (the exe is in its "new" subfolder).</summary>
    private static string StagingOf(string newExe) => Path.GetDirectoryName(Path.GetDirectoryName(newExe)!)!;

    /// <summary>Removes old attempts' folders and the .old copy a finished update could not delete.</summary>
    public void CleanStale()
    {
        VbCableInstaller.CleanStale(WorkRoot, StaleAfter);
        UpdatePackage.TryDelete(ExePath + ".old");
    }

    private static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception) { /* in use: CleanStale gets it later */ }
    }
}
