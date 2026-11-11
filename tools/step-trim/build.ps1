<#
.SYNOPSIS
    STEP 절단 도구(step-trim)를 빌드한다.

.DESCRIPTION
    OCCT prebuilt(third_party\opencascade-7.9.3-vc14-64) + VS2022 x64 환경에서 컴파일한다.
    실행은 run.ps1 을 사용한다(OCCT bin + 3rdparty DLL 디렉터리를 PATH 에 넣어야 한다).

.EXAMPLE
    .\build.ps1
#>
param(
    [string]$OcctRoot = "",
    [string]$VsRoot = "C:\Program Files\Microsoft Visual Studio\2022\Community"
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $OcctRoot) {
    $repo = (Resolve-Path (Join-Path $here '..\..')).Path
    $OcctRoot = Join-Path $repo 'third_party\opencascade-7.9.3-vc14-64'
}
$inc = Join-Path $OcctRoot 'inc'
$lib = Join-Path $OcctRoot 'win64\vc14\lib'
$vcvars = Join-Path $VsRoot 'VC\Auxiliary\Build\vcvars64.bat'
$out = Join-Path $here 'bin'

if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found: $vcvars" }
if (-not (Test-Path $inc)) { throw "OCCT include not found: $inc" }
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }

$libs = @(
    'TKernel', 'TKMath', 'TKG2d', 'TKG3d', 'TKGeomBase', 'TKGeomAlgo',
    'TKBRep', 'TKTopAlgo', 'TKBool', 'TKBO', 'TKShHealing', 'TKMesh', 'TKOffset',
    'TKPrim', 'TKFillet', 'TKHLR',
    'TKDESTEP', 'TKXSBase', 'TKCDF', 'TKLCAF', 'TKCAF', 'TKXCAF'
) | ForEach-Object { "$_.lib" }
$linkLibs = $libs -join ' '

$exe = Join-Path $out 'step-trim.exe'
$src = Join-Path $here 'step-trim.cpp'
$cmd = "call `"$vcvars`" >nul && cl /nologo /EHsc /O2 /MD /std:c++17 /utf-8 /Fo:$out\ /Fe:$exe $src /I `"$inc`" /link /SUBSYSTEM:CONSOLE /LIBPATH:`"$lib`" $linkLibs"
Write-Host ">>> step-trim.cpp"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "build failed: step-trim.cpp" }
Write-Host "완료: $exe"
