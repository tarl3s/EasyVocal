# Builds every release download from Windows and, unless -NoUpload, publishes them as the GitHub release of the version
# in Directory.Build.props:
#   EasyVocal-win-x64.zip        EasyVocal.exe (self-contained single file)
#   EasyVocal-macos-arm64.zip    EasyVocal.app (Apple Silicon)   } Info.plist, EasyVocal.icns, the program + its native
#   EasyVocal-macos-x64.zip      EasyVocal.app (Intel)           } libraries (PortAudio) in Contents/MacOS, 0755
#   EasyVocal-linux-x64.tar.gz   EasyVocal/ (program 0755, libraries, easyvocal.png, easyvocal.desktop, install.sh)
# The archives themselves are made by tools\EasyVocal.Packager (plain .NET: Unix permission bits from Windows).
#   powershell -ExecutionPolicy Bypass -File tools\Publish-Release.ps1 -NoUpload [-OutDir <folder>]
#   powershell -ExecutionPolicy Bypass -File tools\Publish-Release.ps1 -NotesFile notes.md
# -OutDir (default publish\) holds the downloads and a build\ folder with each RID's publish output, so a copy of
# EasyVocal running from publish\win-x64 never blocks a release. The asset names are fixed: the README's download
# buttons and each platform's updater look for them.
param(
    [string]$NotesFile,
    [switch]$NoUpload,
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gh = 'C:\Program Files\GitHub CLI\gh.exe'
if (-not $OutDir) { $OutDir = Join-Path $root 'publish' }
$OutDir = [IO.Path]::GetFullPath($OutDir)

$props = [IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'))
if ($props -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw 'No <Version> in Directory.Build.props' }
$version = $Matches[1]
$tag = "v$version"

if (-not $NoUpload) {
    # Check everything the upload needs before spending minutes on the builds.
    if (-not $NotesFile) { throw 'Pass -NotesFile <notes.md> to publish (or -NoUpload).' }
    if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw "Notes file not found: $NotesFile" }
    git -C $root rev-parse --verify --quiet "refs/tags/$tag" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The tag $tag doesn't exist here: create and push it first (git tag -a $tag -m ...)." }
}

$build = Join-Path $OutDir 'build'
$icons = Join-Path $build 'icon'
$packagerOut = Join-Path $build 'packager'
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
New-Item -ItemType Directory -Force $OutDir, $build | Out-Null

# The icon as PNGs (16-1024 px) for the .icns and the Linux menu, drawn by the same code as EasyVocal.ico.
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Make-Icon.ps1') -PngDir $icons | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Make-Icon.ps1 failed' }

dotnet build (Join-Path $PSScriptRoot 'EasyVocal.Packager') -c Release -o $packagerOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Building EasyVocal.Packager failed' }
$packager = Join-Path $packagerOut 'EasyVocal.Packager.dll'

$rids = 'win-x64', 'osx-arm64', 'osx-x64', 'linux-x64'
$assets = @()
foreach ($rid in $rids) {
    $out = Join-Path $build $rid
    # Windows: the native libraries go inside EasyVocal.exe. macOS / Linux: next to it (Contents/MacOS, the folder),
    # where the system's loader finds them (libportaudio, Skia, HarfBuzz, Avalonia.Native).
    $natives = if ($rid -eq 'win-x64') { 'true' } else { 'false' }
    Write-Host "== dotnet publish $rid"
    dotnet publish (Join-Path $root 'src\EasyVocal') -c Release -r $rid --self-contained true --nologo `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=$natives `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $rid failed" }

    $line = dotnet $packager $rid $out $OutDir $version $icons
    if ($LASTEXITCODE -ne 0) { throw "Packing $rid failed" }
    $file, $bytes, $note = "$line".Split('|')
    $assets += [pscustomobject]@{ Asset = (Split-Path -Leaf $file); MB = [math]::Round([double]$bytes / 1MB, 1); Contents = $note; Path = $file }
}

Write-Host ""
Write-Host "EasyVocal v$version downloads in ${OutDir}:"
$assets | Format-Table Asset, MB, Contents -AutoSize | Out-Host

if ($NoUpload) { Write-Host 'Done (-NoUpload: nothing was published).'; return }

# --verify-tag: never let gh create a missing tag on the server from whatever the default branch is.
& $gh release create $tag @($assets.Path) --verify-tag --title "EasyVocal $tag" --notes-file $NotesFile
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
