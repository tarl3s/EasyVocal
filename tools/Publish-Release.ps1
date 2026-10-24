# Builds the self-contained win-x64 release zip (publish\EasyVocal-win-x64.zip) and, unless -NoUpload, publishes
# it as a GitHub release for the version in Directory.Build.props.
#   powershell -ExecutionPolicy Bypass -File tools\Publish-Release.ps1 -NoUpload
#   powershell -ExecutionPolicy Bypass -File tools\Publish-Release.ps1 -NotesFile notes.md
# The asset name EasyVocal-win-x64.zip is fixed: the README download link points at it.
param(
    [string]$NotesFile,
    [switch]$NoUpload
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gh = 'C:\Program Files\GitHub CLI\gh.exe'
$maxBytes = 80MB

$props = [IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'))
if ($props -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw 'No <Version> in Directory.Build.props' }
$version = $Matches[1]
$tag = "v$version"

if (-not $NoUpload) {
    # Check everything the upload needs before spending minutes on the build.
    if (-not $NotesFile) { throw 'Pass -NotesFile <notes.md> to publish (or -NoUpload).' }
    if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw "Notes file not found: $NotesFile" }
    git -C $root rev-parse --verify --quiet "refs/tags/$tag" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The tag $tag doesn't exist here: create and push it first (git tag -a $tag -m ...)." }
}

$out = Join-Path $root 'publish\win-x64'
$zip = Join-Path $root 'publish\EasyVocal-win-x64.zip'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

dotnet publish (Join-Path $root 'src\EasyVocal') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-Item $zip).Length
Write-Host ("EasyVocal v{0}: {1} ({2:N1} MB)" -f $version, $zip, ($size / 1MB))
if ($size -ge $maxBytes) { throw ("The zip is {0:N1} MB: the limit is 80 MB" -f ($size / 1MB)) }

if ($NoUpload) { Write-Host 'Done (-NoUpload: nothing was published).'; return }

# --verify-tag: never let gh create a missing tag on the server from whatever the default branch is.
& $gh release create $tag $zip --verify-tag --title "EasyVocal $tag" --notes-file $NotesFile
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
