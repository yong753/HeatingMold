param(
    [string]$Scenario,
    [double]$HoldSeconds = 3,
    [switch]$Step,
    [switch]$NoBuild,
    [string]$RunDir,
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
if (-not $Scenario) {
    throw "Scenario is required. Pass -Scenario <path|name>, e.g. -Scenario C:\work\my-scenario.json"
}
$scenarioPath = if (Test-Path $Scenario) { (Resolve-Path $Scenario).Path }
    else { Join-Path $PSScriptRoot "scenarios/$Scenario.json" }
if (-not (Test-Path $scenarioPath)) { throw "Scenario not found: $Scenario" }
if (-not $RunDir) {
    $RunDir = Join-Path $root ("artifacts/visual-test/" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$RunDir = [IO.Path]::GetFullPath($RunDir)
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null

if (-not $NoBuild) {
    $msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
    if (-not (Test-Path $msbuild)) { throw "MSBuild not found: $msbuild" }
    & $msbuild "$root/src/OccBridge/OccBridge.vcxproj" /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$root\x64\Release\" /v:minimal
    if ($LASTEXITCODE) { throw 'Native bridge build failed' }
    & dotnet build "$root/src/HeatingMold.App/HeatingMold.App.csproj" -c Release -p:Platform=x64 --nologo
    if ($LASTEXITCODE) { throw 'App build failed. Close any running HeatingMold.App and retry.' }
}

$exe = Join-Path $root 'src/HeatingMold.App/bin/x64/Release/net8.0-windows/HeatingMold.App.exe'
if (-not (Test-Path $exe)) { throw "App executable not found: $exe (run without -NoBuild)" }

$arguments = @('--visual-test', $scenarioPath, '--visual-run-dir', $RunDir,
    '--visual-hold-seconds', $HoldSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($Step) { $arguments += '--visual-step' }

$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
$resultPath = Join-Path $RunDir 'result.json'
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and -not (Test-Path $resultPath)) {
    if ($process.HasExited) { break }
    Start-Sleep -Milliseconds 300
}

if (-not (Test-Path $resultPath)) {
    Write-Host "No result.json yet. The app may still be running (PID $($process.Id))."
    Write-Host "Run directory: $RunDir"
    exit 3
}

$result = [IO.File]::ReadAllText($resultPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
Write-Host "Status: $($result.Status) / automation: $($result.Automation) / visual review: $($result.VisualReview)"
Write-Host "Run directory: $RunDir"
foreach ($record in $result.Steps) {
    $mark = if ($record.Success) { 'OK  ' } else { 'FAIL' }
    Write-Host ("{0} {1}. {2}" -f $mark, ($record.Index + 1), $record.Name)
    if (-not $record.Success -and $record.Error) { Write-Host "     $($record.Error)" }
}
if ($result.Error) { Write-Host "Error: $($result.Error)" }
if ($result.DxfPath) { Write-Host "DXF: $($result.DxfPath)" }
Write-Host "The app window stays open for visual review (PID $($process.Id))."

if ($result.Status -ne 'Completed') { exit 1 }
exit 0
