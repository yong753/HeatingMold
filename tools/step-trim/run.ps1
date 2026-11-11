<#
.SYNOPSIS
    step-trim 실행 래퍼. OCCT + 3rdparty DLL 경로를 PATH 에 구성한 뒤 실행한다.

.DESCRIPTION
    - 입력 STEP 은 읽기 전용으로만 사용한다.
    - --input 과 --output 이 같은 파일이면 실행을 거부한다.

.EXAMPLE
    .\run.ps1 -Args "--mode","inspect","--input","..\..\resource\Mold_Item.step"
#>
param(
    # $Args 는 PowerShell 자동 변수와 충돌하므로 이름을 분리한다.
    [Alias('Args')][string[]]$ToolArgs = @(),
    [string]$OcctRoot = ""
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $OcctRoot) {
    $repo = (Resolve-Path (Join-Path $here '..\..')).Path
    $OcctRoot = Join-Path $repo 'third_party\opencascade-7.9.3-vc14-64'
}
$repo2 = (Resolve-Path (Join-Path $here '..\..')).Path
$tp = Join-Path $repo2 'third_party\3rdparty-vc14-64'

# TKDESTEP -> TKXCAF -> TKBinXCAF -> TKService 체인 때문에 3rdparty 하위 DLL 디렉터리를 모두 PATH 에 넣는다.
function Get-DllDirs($dir, $depth, $acc) {
    if ($depth -gt 4) { return }
    if ((Get-ChildItem $dir -Filter *.dll -File -ErrorAction SilentlyContinue).Count -gt 0) { [void]$acc.Add($dir) }
    Get-ChildItem $dir -Directory -ErrorAction SilentlyContinue | ForEach-Object { Get-DllDirs $_.FullName ($depth + 1) $acc }
}

$dirs = New-Object System.Collections.ArrayList
[void]$dirs.Add((Join-Path $OcctRoot 'win64\vc14\bin'))
if (Test-Path $tp) { Get-DllDirs $tp 0 $dirs }
$env:PATH = ($dirs -join ';') + ';' + $env:PATH

$exe = Join-Path $here 'bin\step-trim.exe'
if (-not (Test-Path $exe)) { throw "$exe not found - run build.ps1 first" }

& $exe @ToolArgs
exit $LASTEXITCODE
