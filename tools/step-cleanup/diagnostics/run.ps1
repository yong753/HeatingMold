<#
.SYNOPSIS
    진단 도구 실행 래퍼. OCCT + 3rdparty DLL 경로를 PATH 에 구성한 뒤 실행한다.

.EXAMPLE
    .\run.ps1 -Tool faceinspect -Args "..\..\..\resource\Mold.stp","79","598"
    .\run.ps1 -Tool dupcheck -Args "..\..\..\resource\Mold_Solids.stp"
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('faceinspect', 'dupcheck', 'cleanuptest')][string]$Tool,
    [string[]]$Args = @(),
    [string]$OcctRoot = ""
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $OcctRoot) {
    $repo = (Resolve-Path (Join-Path $here '..\..\..')).Path
    $OcctRoot = Join-Path $repo 'third_party\opencascade-7.9.3-vc14-64'
}
$repo2 = (Resolve-Path (Join-Path $here '..\..\..')).Path
$tp = Join-Path $repo2 'third_party\3rdparty-vc14-64'

# TKDESTEP -> TKXCAF -> TKBinXCAF -> TKService 체인 때문에 3rdparty 하위 DLL 디렉터리를 모두 PATH 에 넣어야 한다.
function Get-DllDirs($dir, $depth, $acc) {
    if ($depth -gt 4) { return }
    if ((Get-ChildItem $dir -Filter *.dll -File -ErrorAction SilentlyContinue).Count -gt 0) { [void]$acc.Add($dir) }
    Get-ChildItem $dir -Directory -ErrorAction SilentlyContinue | ForEach-Object { Get-DllDirs $_.FullName ($depth + 1) $acc }
}

$dirs = New-Object System.Collections.ArrayList
[void]$dirs.Add((Join-Path $OcctRoot 'win64\vc14\bin'))
if (Test-Path $tp) { Get-DllDirs $tp 0 $dirs }
$env:PATH = ($dirs -join ';') + ';' + $env:PATH

$exe = Join-Path $here "$Tool.exe"
if (-not (Test-Path $exe)) { throw "$exe not found - run build.ps1 first" }

& $exe @Args
exit $LASTEXITCODE
