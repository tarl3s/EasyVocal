# Unzips the Windows download the way a person does and runs its e2e test (CI's ai-pack job, on a real Windows runner).
# With an AI pack (a release asset EasyVocal-AI-win-x64.zip) the e2e run also tests the AI voices on it: served on
# 127.0.0.1 like the release, installed, and the real models run on the pack's own ONNX Runtime (--e2e-ai-pack).
# usage: tools/Test-WindowsPackage.ps1 -Zip <EasyVocal-win-x64.zip> -Work <empty folder> [-AiPack <EasyVocal-AI-win-x64.zip>]
param(
    [Parameter(Mandatory = $true)][string]$Zip,
    [Parameter(Mandatory = $true)][string]$Work,
    [string]$AiPack
)
$ErrorActionPreference = 'Stop'
function Fail($why) { Write-Host "FAIL: $why"; exit 1 }

if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
$unpacked = Join-Path $Work 'unpacked'
$data = Join-Path $Work 'e2e'
New-Item -ItemType Directory -Force $unpacked, $data | Out-Null
Expand-Archive -Path $Zip -DestinationPath $unpacked
$exe = Join-Path $unpacked 'EasyVocal.exe'
if (-not (Test-Path $exe)) { Fail "no EasyVocal.exe in $Zip" }
Get-ChildItem $unpacked | Format-Table Name, Length -AutoSize

$arguments = @('--data', "`"$data`"", '--e2e')
$limit = 360
if ($AiPack) {
    $AiPack = (Resolve-Path $AiPack).Path
    $arguments += @('--e2e-ai-pack', "`"$AiPack`"")
    $limit = 1800
}
Write-Host "== $exe $($arguments -join ' ')"
$p = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
if (-not $p.WaitForExit($limit * 1000)) {
    Stop-Process -Id $p.Id -Force
    $code = 142
} else {
    $code = $p.ExitCode
}
Write-Host "exit code $code"
$e2eLog = Join-Path $data 'e2e.log'
$appLog = Join-Path $data 'easyvocal.log'
if (Test-Path $e2eLog) { Get-Content $e2eLog }
if ($code -ne 0) {
    if (Test-Path $appLog) { Get-Content $appLog -Tail 120 }
    Fail "the e2e test of the unzipped download exited with $code"
}
if ($AiPack) {
    Select-String -Path $appLog -Pattern 'AI: ONNX Runtime|AI: .* runs on|AI: graphics' | Select-Object -First 12 | ForEach-Object { $_.Line }
    if (-not (Select-String -Path $appLog -Pattern 'AI: ONNX Runtime .* from .*[\\/]AI[\\/]runtime[\\/]' -Quiet)) { Fail "the AI voices didn't run on the pack's ONNX Runtime" }
    if (Select-String -Path $e2eLog -Pattern 'SKIPPED: no AI test models' -Quiet) { Fail 'the e2e run skipped the AI voices' }
    Write-Host "PASS: $(Split-Path $Zip -Leaf) passes the e2e test with the AI pack $(Split-Path $AiPack -Leaf)"
} else {
    Write-Host "PASS: $(Split-Path $Zip -Leaf) passes the e2e test"
}
