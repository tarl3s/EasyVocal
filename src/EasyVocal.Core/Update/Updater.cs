using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyVocal.Core.Update;

// The in-app updater, minus its windows (UpdateWindow, UpdateCoordinator) and its platform part (IUpdaterPlatform:
// which release file, what is replaced, the helper script; WindowsUpdaterPlatform, UnixUpdaterPlatform).
// EasyVocal asks GitHub for the latest release; when it is newer, the person is asked, the release's download is
// fetched, checked and unpacked to a temp folder (UpdatePackage.cs), and a small helper script (it waits for EasyVocal to
// close; PowerShell on Windows, sh elsewhere) puts it in place of the install root (the exe, the .app, the folder) —
// keeping a .old copy until the new one is in place — and opens EasyVocal again (UpdateService.cs).
// This file: versions, when to offer a release, its notes. The network and the script launch go through IUpdateSource
// and IHelperLauncher, so the tests use fakes.

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
