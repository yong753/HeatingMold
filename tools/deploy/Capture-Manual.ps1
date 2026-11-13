#requires -Version 5.1
<#
.SYNOPSIS
매뉴얼용 화면 캡처. 배포 패키지의 시작 도구로 시각테스트를 실행하면서
각 단계가 끝난 화면을 실제 화면에서 캡처한다.

.DESCRIPTION
- 배포 패키지( ZIP 또는 이미 푼 폴더 )의 HeatingMold.Start.exe 를 그대로 사용한다.
- 시나리오 사본의 model.path 만 패키지 안의 예제 STEP 절대경로로 바꾼다.
- 시작 도구에 --visual-hide-bar 를 넘겨 진행 표시줄을 숨긴다(제품 화면만 남긴다).
- 각 단계가 끝나면( steps.jsonl 의 event=end ) 창을 앞으로 올리고 PNG 로 저장한다.
- 캡처만 담당한다. 자르기/가리기/주석은 Build-Manual.ps1 이 한다.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\deploy\Capture-Manual.ps1 -Scenario e2e-full
#>
param(
    [string]$PackagePath,
    [string]$Scenario = 'e2e-full',
    [string]$RunDir,
    [string]$OutDir,
    [double]$HoldSeconds = 2.5,
    [int]$TimeoutSeconds = 900,
    [switch]$NoResize,
    [switch]$KeepAppOpen
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 이 프로세스를 per-monitor DPI aware 로 만든다. 그렇게 하지 않으면 125% 배율에서
# 화면 좌표가 가상화되어 창의 오른쪽/아래가 잘린 캡처가 만들어진다.
# System.Drawing/WinForms 를 로드하기 전에 호출해야 한다.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ManualCaptureDpi {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
'@
[void][ManualCaptureDpi]::SetProcessDpiAwarenessContext([IntPtr](-4))

. (Join-Path $PSScriptRoot 'Common.ps1')

$repoRoot = Get-RepoRoot
$config = Get-DeployConfig
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$captureRoot = Join-Path $repoRoot (Join-Path 'artifacts/manual-capture' $stamp)
if (-not $RunDir) { $RunDir = Join-Path $captureRoot 'run' }
if (-not $OutDir) { $OutDir = Join-Path $captureRoot 'raw' }
$RunDir = [IO.Path]::GetFullPath($RunDir)
$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $RunDir, $OutDir | Out-Null

# ---- 패키지 해석 ----
function Resolve-PackageRoot {
    param([string]$Path)
    if (-not $Path) {
        $zip = Get-ChildItem (Join-Path $repoRoot $config.outputRoot) -Recurse -Filter '*.zip' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "$($config.packagePrefix)-*" } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $zip) { Stop-Deploy '배포 ZIP 을 찾지 못했습니다. 먼저 Build-Package.ps1 을 실행하세요.' }
        $Path = $zip.FullName
    }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path $full)) { Stop-Deploy "패키지를 찾지 못했습니다: $full" }
    $item = Get-Item $full
    if ($item.PSIsContainer) {
        if (Test-Path (Join-Path $item.FullName 'HeatingMold.Start.exe')) { return $item.FullName }
        $inner = Get-ChildItem $item.FullName -Directory -Filter "$($config.packagePrefix)-*" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($inner -and (Test-Path (Join-Path $inner.FullName 'HeatingMold.Start.exe'))) { return $inner.FullName }
        Stop-Deploy "패키지 폴더에 시작 도구가 없습니다: $full"
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $extractRoot = Join-Path $item.DirectoryName ('manual-extract-' + [IO.Path]::GetFileNameWithoutExtension($item.Name))
    if (Test-Path $extractRoot) { Remove-Item $extractRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($item.FullName, $extractRoot)
    $inner = Get-ChildItem $extractRoot -Directory -Filter "$($config.packagePrefix)-*" |
        Sort-Object Name | Select-Object -First 1
    $rootDir = if ($inner) { $inner.FullName } else { $extractRoot }
    if (-not (Test-Path (Join-Path $rootDir 'HeatingMold.Start.exe'))) {
        Stop-Deploy "압축을 푼 패키지에 시작 도구가 없습니다: $rootDir"
    }
    return $rootDir
}

$packageRoot = Resolve-PackageRoot -Path $PackagePath
$startExe = Join-Path $packageRoot 'HeatingMold.Start.exe'
$appExe = Join-Path $packageRoot 'App/HeatingMold.App.exe'
$modelPath = Join-Path $packageRoot 'App/resource/Mold_Cover_Trim.step'
foreach ($required in @($startExe, $appExe, $modelPath)) {
    if (-not (Test-Path $required)) { Stop-Deploy "패키지 구성이 완전하지 않습니다: $required" }
}
Write-Step '매뉴얼 화면 캡처'
Write-Detail "패키지: $packageRoot"

# ---- 시나리오 사본 ----
$scenarioSource = if (Test-Path $Scenario) { (Resolve-Path $Scenario).Path }
    else { Join-Path $repoRoot ('tests/HeatingMold.VisualTest/scenarios/' + $Scenario + '.json') }
if (-not (Test-Path $scenarioSource)) { Stop-Deploy "시나리오를 찾지 못했습니다: $Scenario" }
$scenarioCopy = Join-Path $RunDir 'scenario.json'
$scenarioText = [IO.File]::ReadAllText($scenarioSource)
$document = $scenarioText | ConvertFrom-Json
$document.model.path = $modelPath
$json = $document | ConvertTo-Json -Depth 100
[IO.File]::WriteAllText($scenarioCopy, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Detail "시나리오 사본: $scenarioCopy"

# ---- 창 제어 ----
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$win32 = @'
using System;
using System.Runtime.InteropServices;
public static class ManualCaptureWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
'@
if (-not ('ManualCaptureWin32' -as [type])) { Add-Type -TypeDefinition $win32 }

function Get-AppWindow {
    param([long]$StartedAfterTicks)
    $candidates = Get-Process -Name 'HeatingMold.App' -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.StartTime.Ticks -ge $StartedAfterTicks } |
        Sort-Object StartTime
    if (-not $candidates) { return [IntPtr]::Zero }
    return ($candidates | Select-Object -Last 1).MainWindowHandle
}

function Save-WindowCapture {
    param([IntPtr]$Handle, [string]$Path)
    if ($Handle -eq [IntPtr]::Zero) { throw '캡처할 창 핸들이 없습니다.' }
    [void][ManualCaptureWin32]::ShowWindow($Handle, 9)
    [void][ManualCaptureWin32]::SetForegroundWindow($Handle)
    Start-Sleep -Milliseconds 350
    $rect = New-Object ManualCaptureWin32+RECT
    if (-not [ManualCaptureWin32]::GetWindowRect($Handle, [ref]$rect)) { throw '창 크기를 읽지 못했습니다.' }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { throw "창 크기가 올바르지 않습니다: $width x $height" }
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $width, $height))
        }
        finally { $graphics.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
    return @{ Width = $width; Height = $height }
}

function ConvertTo-SafeName {
    param([string]$Text, [int]$Index)
    # 파일 이름은 ASCII 로 둔다. 한글 단계 이름은 captures.json 에 기록한다.
    return ('{0:D2}' -f $Index)
}

# ---- 실행 ----
$arguments = @(
    '--visual-test', $scenarioCopy,
    '--visual-run-dir', $RunDir,
    '--visual-hold-seconds', $HoldSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--visual-hide-bar'
)
Write-Detail ("앱 인수: " + ($arguments -join ' '))
$startedAt = Get-Date
$process = Start-Process -FilePath $startExe -ArgumentList $arguments -WorkingDirectory $packageRoot -PassThru
Write-Detail "시작 도구 PID: $($process.Id)"

$stepsPath = Join-Path $RunDir 'steps.jsonl'
$resultPath = Join-Path $RunDir 'result.json'
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$handled = 0
$captured = 0
$windowHandle = [IntPtr]::Zero
$resizeDone = $false
$captureRecords = New-Object System.Collections.ArrayList

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 120
    if ($windowHandle -eq [IntPtr]::Zero) {
        $windowHandle = Get-AppWindow -StartedAfterTicks $startedAt.Ticks
    }
    if ($windowHandle -ne [IntPtr]::Zero -and -not $resizeDone -and -not $NoResize) {
        $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        [void][ManualCaptureWin32]::MoveWindow($windowHandle, $work.X, $work.Y, $work.Width, $work.Height, $true)
        Start-Sleep -Milliseconds 600
        $resizeDone = $true
        Write-Detail "창 크기 조정: $($work.Width) x $($work.Height)"
    }

    if (Test-Path $stepsPath) {
        # steps.jsonl 은 UTF-8 이다. Get-Content 기본 인코딩으로 읽으면 한글 단계 이름이 깨진다.
        $lines = [IO.File]::ReadAllLines($stepsPath, (New-Object System.Text.UTF8Encoding($false)))
        $next = $handled
        for ($i = $handled; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -notmatch '"event"\s*:\s*"end"') { $next = $i + 1; continue }
            try { $record = $line | ConvertFrom-Json } catch { break }
            if (-not $record.success) { $next = $i + 1; continue }
            $name = ConvertTo-SafeName -Text $record.name -Index ($record.index + 1)
            $file = Join-Path $OutDir ($name + '.png')
            $size = Save-WindowCapture -Handle $windowHandle -Path $file
            [void]$captureRecords.Add([ordered]@{
                index   = [int]$record.index + 1
                step    = [string]$record.name
                file    = [IO.Path]::GetFileName($file)
                width   = $size.Width
                height  = $size.Height
                detail  = [string]$record.detail
            })
            $captured++
            $next = $i + 1
            Write-Detail ("캡처 {0}: {1} ({2}x{3})" -f $captured, $record.name, $size.Width, $size.Height)
        }
        $handled = $next
    }

    if (Test-Path $resultPath) {
        Start-Sleep -Milliseconds 400
        break
    }
    if ($process.HasExited -and -not (Test-Path $stepsPath)) { break }
}

$finished = Test-Path $resultPath
if (-not $finished) {
    Stop-Deploy "시간 안에 시각테스트가 끝나지 않았습니다(캡처 $captured 장). 실행 폴더: $RunDir"
}

$result = [IO.File]::ReadAllText($resultPath, (New-Object System.Text.UTF8Encoding($false))) | ConvertFrom-Json
if ($result.Status -ne 'Completed') {
    Write-Warn "시각테스트 상태가 Completed 가 아닙니다: $($result.Status)"
}

$indexPath = Join-Path $captureRoot 'captures.json'
$payload = [ordered]@{
    capturedAtUtc = [DateTime]::UtcNow.ToString('o')
    packageRoot   = $packageRoot
    scenario      = $scenarioSource
    runDirectory  = $RunDir
    holdSeconds   = $HoldSeconds
    steps         = $captureRecords
}
[IO.File]::WriteAllText($indexPath, ($payload | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

if (-not $KeepAppOpen) {
    Get-Process -Name 'HeatingMold.App' -ErrorAction SilentlyContinue |
        Where-Object { $_.StartTime.Ticks -ge $startedAt.Ticks } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

Write-Ok "캡처 $captured 장 · $($result.Status) · $OutDir"
Write-Detail "목록: $indexPath"
[pscustomobject]@{
    CaptureRoot = $captureRoot
    RawDirectory = $OutDir
    IndexPath = $indexPath
    Count = $captured
    Status = $result.Status
}
