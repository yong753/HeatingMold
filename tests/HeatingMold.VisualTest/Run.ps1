param(
    [string]$Scenario,
    [double]$HoldSeconds = 3,
    [switch]$Step,
    [switch]$NoBuild,
    [string]$RunDir,
    [int]$TimeoutSeconds = 90,
    # 배포 패키지 검증용: 개발 빌드 대신 지정한 exe 를 실행한다. 지정하면 빌드를 건너뛴다.
    [string]$AppExe,
    [string]$AppWorkingDirectory,
    # 시나리오의 model.path 를 이 경로로 바꾼 사본을 만들어 실행한다. 원본 시나리오는 수정하지 않는다.
    [string]$ModelPath,
    # 실행 후 진단 로그(environment.json)를 확인해 실제로 배포 모드로 떴는지 검사한다.
    [switch]$ExpectPackaged
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

# 배포 패키지의 exe 를 지정하면 개발 저장소 빌드를 하지 않는다.
$usingPackageApp = -not [string]::IsNullOrWhiteSpace($AppExe)
if ($usingPackageApp) {
    if (-not $NoBuild) {
        Write-Host "Using packaged app; development build skipped."
    }
    $exe = [IO.Path]::GetFullPath($AppExe)
}
else {
    if (-not $NoBuild) {
        $msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
        if (-not (Test-Path $msbuild)) { throw "MSBuild not found: $msbuild" }
        & $msbuild "$root/src/OccBridge/OccBridge.vcxproj" /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$root\x64\Release\" /v:minimal
        if ($LASTEXITCODE) { throw 'Native bridge build failed' }
        & dotnet build "$root/src/HeatingMold.App/HeatingMold.App.csproj" -c Release -p:Platform=x64 --nologo
        if ($LASTEXITCODE) { throw 'App build failed. Close any running HeatingMold.App and retry.' }
    }

    $exe = Join-Path $root 'src/HeatingMold.App/bin/x64/Release/net8.0-windows/HeatingMold.App.exe'
}
if (-not (Test-Path $exe)) { throw "App executable not found: $exe (run without -NoBuild)" }

$workingDirectory = $AppWorkingDirectory
if ([string]::IsNullOrWhiteSpace($workingDirectory)) {
    $workingDirectory = Split-Path -Parent $exe
}

# model.path 만 절대 경로로 바꾼 시나리오 사본. 기대값과 해시는 그대로 둔다.
if (-not [string]::IsNullOrWhiteSpace($ModelPath)) {
    $modelFull = [IO.Path]::GetFullPath($ModelPath)
    if (-not (Test-Path $modelFull)) { throw "Model not found: $modelFull" }

    $document = [IO.File]::ReadAllText($scenarioPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if (-not $document.model) { throw "Scenario has no 'model' section: $scenarioPath" }
    $document.model.path = $modelFull

    $scenarioPath = Join-Path $RunDir 'scenario.json'
    $json = $document | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($scenarioPath, $json, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Scenario copy: $scenarioPath"
    Write-Host "Model path: $modelFull"
}

function Quote-Argument {
    param([string]$Value)
    if ($Value -match '[\s"]') {
        return '"' + ($Value -replace '"', '\"') + '"'
    }
    return $Value
}

$argumentParts = @('--visual-test', $scenarioPath, '--visual-run-dir', $RunDir,
    '--visual-hold-seconds', $HoldSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($Step) { $argumentParts += '--visual-step' }
$argumentLine = ($argumentParts | ForEach-Object { Quote-Argument $_ }) -join ' '

$startedAt = Get-Date
$process = Start-Process -FilePath $exe -ArgumentList $argumentLine -WorkingDirectory $workingDirectory -PassThru
$resultPath = Join-Path $RunDir 'result.json'
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$isLauncher = [IO.Path]::GetFileName($exe) -ieq 'HeatingMold.Start.exe'
while ((Get-Date) -lt $deadline -and -not (Test-Path $resultPath)) {
    if ($process.HasExited) {
        # 시작 도구는 앱을 띄운 뒤 바로 정상 종료하므로, 그 경우에는 앱 결과를 계속 기다린다.
        if (-not $isLauncher) { break }
        if ($process.ExitCode -ne 0) { break }
    }
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

$packagedCheckFailed = $false
if ($ExpectPackaged) {
    # 앱이 실제로 배포 모드로 떴는지 진단 로그로 확인한다.
    $logsRoot = Join-Path $env:LOCALAPPDATA 'HeatingMold\Logs'
    $session = $null
    if (Test-Path $logsRoot) {
        $session = Get-ChildItem $logsRoot -Directory |
            Where-Object { $_.LastWriteTime -ge $startedAt.AddSeconds(-5) } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
    }
    if (-not $session) {
        Write-Host "PACKAGED CHECK FAILED: no diagnostic log session was created under $logsRoot"
        $packagedCheckFailed = $true
    }
    else {
        $environmentPath = Join-Path $session.FullName 'environment.json'
        if (-not (Test-Path $environmentPath)) {
            Write-Host "PACKAGED CHECK FAILED: environment.json missing in $($session.FullName)"
            $packagedCheckFailed = $true
        }
        else {
            $environment = [IO.File]::ReadAllText($environmentPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
            # 시작 도구를 거치면 앱은 <패키지>\App 에서 실행되므로 기대 기준 폴더가 달라진다.
            $expectedBase = [IO.Path]::GetFullPath((Split-Path -Parent $exe)).TrimEnd('\')
            if ([IO.Path]::GetFileName($exe) -ieq 'HeatingMold.Start.exe') {
                $expectedBase = [IO.Path]::GetFullPath((Join-Path $expectedBase 'App')).TrimEnd('\')
            }
            $actualBase = ([string]$environment.baseDirectory).TrimEnd('\')
            Write-Host "Packaged: $($environment.packaged) / base: $actualBase"
            Write-Host "Log session: $($session.FullName)"
            if ($environment.packaged -ne $true) {
                Write-Host "PACKAGED CHECK FAILED: app ran in development mode (deployment.json was not used)."
                $packagedCheckFailed = $true
            }
            if (-not [string]::Equals($actualBase, $expectedBase, [StringComparison]::OrdinalIgnoreCase)) {
                Write-Host "PACKAGED CHECK FAILED: base directory is '$actualBase', expected '$expectedBase'."
                $packagedCheckFailed = $true
            }
        }
    }
}

if ($result.Status -ne 'Completed') { exit 1 }
if ($packagedCheckFailed) { exit 4 }
exit 0
