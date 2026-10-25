<#
.SYNOPSIS
    STEP 진단 도구 3종을 빌드한다 (faceinspect / dupcheck / cleanuptest).

.DESCRIPTION
    OCCT prebuilt(third_party\opencascade-7.9.3-vc14-64) + VS2022 x64 환경에서 컴파일한다.
    실행 시에는 OCCT bin + 3rdparty DLL 디렉터리를 PATH 에 넣어야 한다
    (run.ps1 참고, 그렇지 않으면 STATUS_DLL_NOT_FOUND 로 즉시 종료된다).

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
    $repo = (Resolve-Path (Join-Path $here '..\..\..')).Path
    $OcctRoot = Join-Path $repo 'third_party\opencascade-7.9.3-vc14-64'
}
$inc = Join-Path $OcctRoot 'inc'
$lib = Join-Path $OcctRoot 'win64\vc14\lib'
$vcvars = Join-Path $VsRoot 'VC\Auxiliary\Build\vcvars64.bat'

if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found: $vcvars" }
if (-not (Test-Path $inc)) { throw "OCCT include not found: $inc" }

$libs = @(
    'TKernel', 'TKMath', 'TKG2d', 'TKG3d', 'TKGeomBase', 'TKGeomAlgo',
    'TKBRep', 'TKTopAlgo', 'TKBool', 'TKBO', 'TKShHealing', 'TKMesh',
    'TKDESTEP', 'TKXSBase'
) | ForEach-Object { "$_.lib" }

$linkLibs = ($libs | ForEach-Object { $_ }) -join ' '

foreach ($src in @('faceinspect.cpp', 'dupcheck.cpp', 'cleanuptest.cpp')) {
    $exe = [IO.Path]::GetFileNameWithoutExtension($src)
    $cmd = "call `"$vcvars`" >nul && cl /nologo /EHsc /O2 /MD /std:c++17 /utf-8 /Fe:$exe.exe $src /I `"$inc`" /link /SUBSYSTEM:CONSOLE /LIBPATH:`"$lib`" $linkLibs"
    Write-Host ">>> $src"
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "build failed: $src" }
}
Write-Host "완료. 실행은 run.ps1 참고."
