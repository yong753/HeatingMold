param(
    [switch]$Strict   # throw when the known defect is still present (use after fixing the projection)
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$occt = "$root/third_party/opencascade-7.9.3-vc14-64"
$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'

# The project references x64/Release/OccBridge.dll, so the native bridge must be current.
& $msbuild "$root/src/OccBridge/OccBridge.vcxproj" /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$root\x64\Release\" /v:minimal
if ($LASTEXITCODE) { throw 'Native bridge build failed' }

$dllDirs = @("$occt/win64/vc14/bin") + @(Get-ChildItem "$root/third_party/3rdparty-vc14-64" -Recurse -Filter *.dll | ForEach-Object DirectoryName | Select-Object -Unique)
$env:PATH = ($dllDirs -join ';') + ';' + $env:PATH
$env:CSF_OCCTResourcePath = "$occt/src"

& dotnet run --project "$PSScriptRoot/PatternRegion.Repro.csproj" -c Release -- "$root/resource/Mold.stp"
$exit = $LASTEXITCODE
if ($exit -ne 0 -and $Strict) { throw 'Pattern region repro still fails' }
if ($exit -ne 0) {
    Write-Host 'This is the expected outcome while the defect is open (exit code 1). Pass -Strict once it is fixed.' -ForegroundColor Yellow
}
