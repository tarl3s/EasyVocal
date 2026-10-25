using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Storage;
using EasyVocal.Core.Update;

namespace EasyVocal.Platform.Windows;

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
        sb.Append("$Target = ").Append(Quote(plan.Target)).Append("\r\n");
        sb.Append("$NewExe = ").Append(Quote(plan.NewPayload)).Append("\r\n");
        sb.Append("$Staging = ").Append(Quote(plan.StagingFolder ?? "")).Append("\r\n");
        sb.Append("$Log = ").Append(Quote(plan.LogFile)).Append("\r\n");
        sb.Append("$Relaunch = ").Append(plan.Relaunch ? "$true" : "$false").Append("\r\n");
        sb.Append("$RelaunchArgs = ").Append(Quote(plan.RelaunchArguments)).Append("\r\n");
        sb.Append(Body);
        return sb.ToString();
    }

    /// <summary>How to run the script: Windows PowerShell, hidden, no profile, this script only.</summary>
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
$NewMarker = Join-Path (Split-Path -Parent $NewExe) 'easyvocal-files.txt'
if (Test-Path -LiteralPath $NewMarker) {
    try { Copy-Item -LiteralPath $NewMarker -Destination (Join-Path (Split-Path -Parent $Target) 'easyvocal-files.txt') -Force }
    catch { Write-Log ('the file list was not copied: ' + $_) }
}
try { Remove-Item -LiteralPath $Backup -Force } catch { Write-Log ('the old copy stays for now: ' + $_) }
Write-Log 'updated'
Start-EasyVocal
Remove-Staging
exit 0

""";
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

/// <summary>
/// The Windows side of the updater: <c>EasyVocal-win-x64.zip</c>, whose <c>EasyVocal.exe</c> (a Windows program)
/// replaces the running one through the PowerShell helper.
/// </summary>
public sealed class WindowsUpdaterPlatform : IUpdaterPlatform
{
    public static WindowsUpdaterPlatform Instance { get; } = new();

    /// <summary>The zip for this PC: <c>EasyVocal-win-x64.zip</c> (also on ARM PCs, which run x64 programs).</summary>
    public IReadOnlyList<string> AssetNames(Architecture arch) => arch switch
    {
        Architecture.X64 => new[] { "EasyVocal-win-x64.zip" },
        Architecture.Arm64 => new[] { "EasyVocal-win-arm64.zip", "EasyVocal-win-x64.zip" },
        _ => Array.Empty<string>(),
    };

    public string ExeName => "EasyVocal.exe";

    /// <summary>On Windows the update replaces the exe itself (a single-file program).</summary>
    public string InstallRoot(string exePath) => exePath;

    /// <summary>The release's marker next to EasyVocal.exe (written by the release zip and by every update).</summary>
    public const string Marker = "easyvocal-files.txt";

    /// <summary>A single file (the exe, or the .old copy of it the helper left): replacing or deleting it touches nothing else.</summary>
    public bool IsInstallRoot(string path) => File.Exists(path) && !Directory.Exists(path);

    /// <summary>The exe's folder can be written (a probe file is created and removed).</summary>
    public bool CanSelfUpdate(string exePath, Action<string>? log = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
            var probe = Path.Combine(dir, "EasyVocal.update-check." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("Update: the folder of " + exePath + " can't be written: " + ex.Message);
            return false;
        }
    }

    /// <summary>Takes the release zip's EasyVocal.exe out (the shared checks) and makes sure it is a Windows program.</summary>
    public string Unpack(string archivePath, string folder, CancellationToken ct)
    {
        var exe = UpdatePackage.ExtractFile(archivePath, folder, ExeName, ct);
        if (LooksLikeProgram(exe)) {
            // The release's marker goes next to the exe too (the helper copies it), so every install carries one.
            try { UpdatePackage.ExtractFile(archivePath, folder, Marker, ct); }
            catch (UpdateException ex) when (ex.Problem == UpdateProblem.MissingExe) { /* an older release had none */ }
            return exe;
        }
        Cleanup.TryDelete(exe);
        throw new UpdateException(UpdateProblem.NotAnExe, "The " + ExeName + " in the download is not a Windows program.");
    }

    /// <summary>A Windows program starts with "MZ".</summary>
    public static bool LooksLikeProgram(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return f.Length > 2 && f.ReadByte() == 'M' && f.ReadByte() == 'Z';
        }
        catch (IOException) { return false; }
    }

    public string HelperFileName => "update.ps1";

    public Encoding HelperEncoding => HelperScript.FileEncoding;

    public string BuildHelper(HelperPlan plan) => HelperScript.Build(plan);

    public IHelperLauncher Launcher { get; } = new PowerShellLauncher();

    public string RelaunchArguments(string? dataFolder) => HelperScript.RelaunchArguments(dataFolder);
}
