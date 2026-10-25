using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Storage;
using EasyVocal.Core.Update;

namespace EasyVocal.Platform.Unix;

/// <summary>
/// The helper script for macOS and Linux (POSIX sh): waits for EasyVocal to close, renames the installed
/// <c>EasyVocal.app</c> (macOS) or <c>EasyVocal</c> folder (Linux) to <c>.old</c>, moves the new one in its place, puts the
/// old one back if that fails, makes the program executable (and, on macOS, clears the download quarantine), opens
/// EasyVocal again and removes its own folder.
/// </summary>
public static class ShHelperScript
{
    /// <summary>A POSIX sh single-quoted literal (a quote inside becomes <c>'\''</c>).</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>The words EasyVocal gets when the helper opens it again, each quoted for sh (e.g. <c>--data '/x y'</c>).</summary>
    public static string RelaunchArguments(string? dataFolder) =>
        dataFolder == null ? "" : "--data " + Quote(Path.TrimEndingDirectorySeparator(dataFolder));

    /// <param name="mac">macOS: the target is a .app bundle (opened with <c>open</c>; quarantine cleared).</param>
    public static string Build(HelperPlan plan, bool mac)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n");
        sb.Append("# EasyVocal update helper: written by EasyVocal, runs once after it closes, then removes its folder.\n");
        sb.Append("WAIT_PID=").Append(plan.WaitPid.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("TARGET=").Append(Quote(plan.Target)).Append('\n');
        sb.Append("NEW=").Append(Quote(plan.NewPayload)).Append('\n');
        sb.Append("STAGING=").Append(Quote(plan.StagingFolder ?? "")).Append('\n');
        sb.Append("LOG=").Append(Quote(plan.LogFile)).Append('\n');
        sb.Append("RELAUNCH=").Append(plan.Relaunch ? "1" : "0").Append('\n');
        sb.Append("PROGRAM=").Append(mac ? "\"$TARGET/Contents/MacOS/EasyVocal\"" : "\"$TARGET/EasyVocal\"").Append('\n');
        sb.Append('\n');
        sb.Append("log() {\n    printf '%s [update helper] %s\\n' \"$(date '+%Y-%m-%d %H:%M:%S')\" \"$1\" >> \"$LOG\" 2>/dev/null\n}\n");
        sb.Append("start_app() {\n    [ \"$RELAUNCH\" = 1 ] || return 0\n");
        // The words after the program are pre-quoted by RelaunchArguments (never typed by a person).
        var args = plan.RelaunchArguments;
        if (mac)
            sb.Append("    if [ -d \"$TARGET/Contents\" ]; then\n        /usr/bin/open \"$TARGET\"")
              .Append(args.Length > 0 ? " --args " + args : "")
              .Append(" >/dev/null 2>&1 || log 'could not open EasyVocal again'\n        return 0\n    fi\n");
        sb.Append("    nohup \"$PROGRAM\"").Append(args.Length > 0 ? " " + args : "")
          .Append(" >/dev/null 2>&1 &\n}\n");
        sb.Append("remove_staging() {\n    [ -n \"$STAGING\" ] && rm -rf \"$STAGING\" 2>/dev/null\n    return 0\n}\n");
        sb.Append(Body);
        if (mac) sb.Append("xattr -dr com.apple.quarantine \"$TARGET\" 2>/dev/null\n");
        sb.Append(Tail);
        return sb.ToString();
    }

    private const string Body = """

log "updating $TARGET"
if [ "$WAIT_PID" -gt 0 ]; then
    i=0
    while kill -0 "$WAIT_PID" 2>/dev/null; do
        i=$((i + 1))
        if [ "$i" -ge 240 ]; then log 'EasyVocal did not close: nothing was changed'; remove_staging; exit 4; fi
        sleep 0.25
    done
fi
BACKUP="$TARGET.old"
rm -rf "$BACKUP" 2>/dev/null
if ! mv "$TARGET" "$BACKUP" 2>/dev/null; then
    log 'could not move the old version aside: nothing was changed'
    start_app
    remove_staging
    exit 2
fi
if ! mv "$NEW" "$TARGET" 2>/dev/null; then
    log 'the new version could not be put in place: the old one is put back'
    rm -rf "$TARGET" 2>/dev/null
    mv "$BACKUP" "$TARGET" 2>/dev/null || log 'could not put the old version back'
    start_app
    remove_staging
    exit 3
fi
chmod +x "$PROGRAM" 2>/dev/null

""";

    private const string Tail = """
rm -rf "$BACKUP" 2>/dev/null || log 'the old copy stays for now'
log 'updated'
start_app
remove_staging
exit 0

""";
}

/// <summary>Starts the helper with /bin/sh, detached (nohup, in the background), so it outlives EasyVocal.</summary>
public sealed class ShLauncher(ICommandRunner? runner = null) : IHelperLauncher
{
    /// <summary>The command that starts <paramref name="scriptPath"/> detached: <c>/bin/sh -c 'nohup /bin/sh "$1" … &amp;' sh &lt;script&gt;</c>.</summary>
    public static IReadOnlyList<string> Arguments(string scriptPath) =>
        new[] { "-c", "nohup /bin/sh \"$1\" >/dev/null 2>&1 &", "sh", scriptPath };

    public void Start(string scriptPath)
    {
        if (!(runner ?? CommandRunner.Instance).Start("/bin/sh", Arguments(scriptPath)))
            throw new UpdateException(UpdateProblem.Launch, "/bin/sh did not start.");
    }
}

/// <summary>
/// The macOS and Linux side of the updater. macOS: <c>EasyVocal-macos-arm64.zip</c> / <c>-x64.zip</c> holding
/// <c>EasyVocal.app</c>, which replaces the running .app. Linux: <c>EasyVocal-linux-x64.tar.gz</c> holding the folder
/// <c>EasyVocal/</c>, which replaces the running app's folder. Where that can't be written (a read-only place, macOS'
/// App Translocation) the release page is opened instead.
/// </summary>
public sealed class UnixUpdaterPlatform : IUpdaterPlatform
{
    public static UnixUpdaterPlatform MacOS { get; } = new(mac: true);
    public static UnixUpdaterPlatform Linux { get; } = new(mac: false);

    private readonly bool mac;

    public UnixUpdaterPlatform(bool mac, IHelperLauncher? launcher = null)
    {
        this.mac = mac;
        Launcher = launcher ?? new ShLauncher();
    }

    public const string AppBundle = "EasyVocal.app";
    public const string AppFolder = "EasyVocal";

    public IReadOnlyList<string> AssetNames(Architecture arch) => mac
        ? arch switch
        {
            Architecture.Arm64 => new[] { "EasyVocal-macos-arm64.zip", "EasyVocal-macos-x64.zip" }, // x64 runs under Rosetta
            Architecture.X64 => new[] { "EasyVocal-macos-x64.zip" },
            _ => Array.Empty<string>(),
        }
        : arch == Architecture.X64 ? new[] { "EasyVocal-linux-x64.tar.gz" } : Array.Empty<string>();

    public string ExeName => "EasyVocal";

    /// <summary>macOS: the .app the program is in (…/EasyVocal.app/Contents/MacOS/EasyVocal); else the program's folder.</summary>
    public string InstallRoot(string exePath)
    {
        var full = Path.GetFullPath(exePath);
        if (mac)
        {
            for (var dir = Path.GetDirectoryName(full); dir != null; dir = Path.GetDirectoryName(dir))
                if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return Path.TrimEndingDirectorySeparator(dir);
        }
        return Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(full)!);
    }

    /// <summary>The folder that holds the install root can be written (the swap renames the root there; a probe file is made and removed).</summary>
    public bool CanSelfUpdate(string exePath, Action<string>? log = null)
    {
        var root = InstallRoot(exePath);
        if (mac && root.Replace(Path.DirectorySeparatorChar, '/').Contains("/AppTranslocation/", StringComparison.Ordinal))
        {
            log?.Invoke("Update: EasyVocal runs from a quarantined copy (App Translocation): move it to Applications to update it");
            return false;
        }
        try
        {
            var parent = Path.GetDirectoryName(root)!;
            var probe = Path.Combine(parent, ".EasyVocal.update-check." + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("Update: the folder of " + root + " can't be written: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Unpacks the whole release (the shared checks) and returns the new .app (macOS) or folder (Linux), at the top of the
    /// archive or one folder down, after checking its program is one for this system.
    /// </summary>
    public string Unpack(string archivePath, string folder, CancellationToken ct)
    {
        UpdatePackage.ExtractAll(archivePath, folder, ct);
        var name = mac ? AppBundle : AppFolder;
        var payload = FindPayload(folder, name)
            ?? throw new UpdateException(UpdateProblem.MissingExe, "The download doesn't contain " + name + ".");
        var program = ProgramIn(payload);
        if (!File.Exists(program)) throw new UpdateException(UpdateProblem.MissingExe, "The download's " + name + " has no " + ExeName + ".");
        if (!LooksLikeProgram(program, mac))
            throw new UpdateException(UpdateProblem.NotAnExe, "The " + ExeName + " in the download is not a " + (mac ? "macOS" : "Linux") + " program.");
        return payload;
    }

    /// <summary>The program inside an install root.</summary>
    public string ProgramIn(string root) => mac && root.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
        ? Path.Combine(root, "Contents", "MacOS", ExeName)
        : Path.Combine(root, ExeName);

    private static string? FindPayload(string folder, string name)
    {
        var top = Path.Combine(folder, name);
        if (Directory.Exists(top)) return top;
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            var inner = Path.Combine(sub, name);
            if (Directory.Exists(inner)) return inner;
        }
        return null;
    }

    /// <summary>A Mach-O (one architecture or universal) on macOS, an ELF on Linux.</summary>
    public static bool LooksLikeProgram(string path, bool mac)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[4];
            if (f.Read(head, 0, 4) != 4) return false;
            uint magic = BitConverter.ToUInt32(head, 0);
            return mac
                ? magic is 0xFEEDFACF or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA
                : head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';
        }
        catch (IOException) { return false; }
    }

    public string HelperFileName => "update.sh";

    public Encoding HelperEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public string BuildHelper(HelperPlan plan) => ShHelperScript.Build(plan, mac);

    public IHelperLauncher Launcher { get; }

    public string RelaunchArguments(string? dataFolder) => ShHelperScript.RelaunchArguments(dataFolder);
}
