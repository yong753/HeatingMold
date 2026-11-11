<#
.SYNOPSIS
    step-trim 회귀 실행. 검사 모드 / offset API 검증 / 실제 절단을 차례로 실행하고 종료 코드와 원본 해시를 확인한다.

.DESCRIPTION
    원본 STEP은 읽기만 한다. 절단 결과는 기본적으로 artifacts\step-trim 아래에만 쓰고,
    테스트 전후 원본 SHA-256이 같은지 확인한다.
    실제 제품 파일에 결과를 남기려면 step-trim.exe를 직접 --output resource\Mold_Item_trim100.step 로 실행한다.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\step-trim\Test.ps1
#>
[CmdletBinding()]
param(
    [string]$InputStep = "",
    [string]$OutputDirectory = "",
    [string]$Origin = "-4183.68,-843.20,1396",
    [string]$Normal = "1,0,0",
    [string]$XAxis = "0,1,0",
    [double]$Margin = 100,
    [switch]$SkipTrim
)

$ErrorActionPreference = 'Stop'

$toolDir = Split-Path -Parent $PSCommandPath
$repo = Split-Path -Parent (Split-Path -Parent $toolDir)
if ([string]::IsNullOrWhiteSpace($InputStep)) { $InputStep = Join-Path $repo 'resource\Mold_Item.step' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repo 'artifacts\step-trim' }
$exe = Join-Path $toolDir 'bin\step-trim.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "step-trim.exe not found: $exe (run build.ps1 first)"
}
if (-not (Test-Path -LiteralPath $InputStep)) {
    throw "input STEP not found: $InputStep"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# OCCT 런타임 DLL 검색 경로. TKDESTEP -> TKXCAF -> TKBinXCAF -> TKService 연쇄 때문에 3rdparty 하위도 필요하다.
$occtBin = Join-Path $repo 'third_party\opencascade-7.9.3-vc14-64\win64\vc14\bin'
if (-not (Test-Path -LiteralPath $occtBin)) { throw "OCCT bin not found: $occtBin" }
$env:PATH = $occtBin + ';' + $env:PATH
$thirdParty = Join-Path $repo 'third_party\3rdparty-vc14-64'
if (Test-Path -LiteralPath $thirdParty) {
    Get-ChildItem -LiteralPath $thirdParty -Recurse -Filter *.dll -File |
        Select-Object -ExpandProperty DirectoryName -Unique |
        ForEach-Object { $env:PATH = $_ + ';' + $env:PATH }
}

$sourceHashBefore = (Get-FileHash -LiteralPath $InputStep -Algorithm SHA256).Hash
Write-Host "input : $InputStep"
Write-Host "sha256: $sourceHashBefore"

$failures = New-Object System.Collections.Generic.List[string]

function Invoke-Step {
    param([string]$Name, [string[]]$Arguments)
    Write-Host ''
    Write-Host "=== $Name ==="
    & $exe @Arguments
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        $failures.Add("$Name exited with code $code")
        Write-Host "FAILED: $Name (exit $code)" -ForegroundColor Red
    }
    else {
        Write-Host "OK: $Name" -ForegroundColor Green
    }
}

Invoke-Step -Name 'inspect' -Arguments @('--mode', 'inspect', '--input', $InputStep)

Invoke-Step -Name 'offset-probe' -Arguments @('--mode', 'offset-probe', '--input', $InputStep, '--margin', "$Margin")

if (-not $SkipTrim) {
    $trimOut = Join-Path $OutputDirectory 'Mold_Item_trim100.step'
    if (Test-Path -LiteralPath $trimOut) { Remove-Item -LiteralPath $trimOut -Force }
    Invoke-Step -Name 'trim' -Arguments @(
        '--mode', 'trim',
        '--input', $InputStep,
        '--output', $trimOut,
        '--origin', $Origin,
        '--normal', $Normal,
        '--xaxis', $XAxis,
        '--margin', "$Margin"
    )
    if (-not (Test-Path -LiteralPath $trimOut)) {
        $failures.Add('trim did not produce an output file')
    }
    else {
        $bytes = (Get-Item -LiteralPath $trimOut).Length
        Write-Host "output: $trimOut ($bytes bytes)"
        Invoke-Step -Name 'inspect-output' -Arguments @('--mode', 'inspect', '--input', $trimOut)
    }
    # 중간 산출물 정리. 실패 시 남겨 두면 원인 파악에 쓸 수 있으므로 지우지 않는다.
    if ($failures.Count -eq 0) {
        foreach ($side in @("$trimOut.tmp", "$trimOut.baseline")) {
            if (Test-Path -LiteralPath $side) { Remove-Item -LiteralPath $side -Force }
        }
    }
}

$sourceHashAfter = (Get-FileHash -LiteralPath $InputStep -Algorithm SHA256).Hash
if ($sourceHashAfter -ne $sourceHashBefore) {
    $failures.Add("source STEP was modified during the test ($sourceHashBefore -> $sourceHashAfter)")
}
else {
    Write-Host ''
    Write-Host "source unchanged: $sourceHashAfter" -ForegroundColor Green
}

Write-Host ''
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'ALL PASSED' -ForegroundColor Green
exit 0
