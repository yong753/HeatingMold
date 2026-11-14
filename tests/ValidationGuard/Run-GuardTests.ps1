#requires -Version 5.1
<#
.SYNOPSIS
    Validation guard 독립 회귀.

.DESCRIPTION
    tools\validation\GuardedProcess.ps1 을 실제 fixture 프로세스로 검증한다.
    제품 App/네이티브 코드나 모델/프로젝트 파일은 건드리지 않는다.
    fixture 창은 모두 예제 오류 창이며, 가드는 버튼을 자동으로 누르지 않는다.

    확인 항목:
      1. 표준 MessageBox(#32770) 감지
      2. WPF ErrorReporter 형태 대화상자(owner disabled, UIA TextBox 본문)
      3. owner 없는 WPF 오류 대화상자(제목 규칙)
      4. launcher가 정상 종료한 뒤에도 자식 앱의 모달 감지
      5. 결과 파일 작성 후 앱 창 유지(가드가 종료하지 않음)
      6. timeout이 추적 트리만 정리하고 무관한 프로세스는 보존
      7. 모달이 아닌 owned 도구 창은 허용
      8. 결과 없이 비정상/정상 종료
      9. 이전 결과 파일 재사용 거부
     10. 결과 파일 + 모달 동시 발생 시 모달 우선
     11. 긴 본문/빈 본문/수집 상한(UIA hang 방지) 경로

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests\ValidationGuard\Run-GuardTests.ps1
#>
[CmdletBinding()]
param(
    [string]$WorkRoot,
    [int]$PollIntervalMs = 150,
    [int]$TimeoutSeconds = 25,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $root 'tools\validation\GuardedProcess.ps1')
$fixture = & (Join-Path $PSScriptRoot 'Fixtures\Build-Fixtures.ps1')

if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) (
        'opencode\hm-guard-tests\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

$script:PassCount = 0
$script:FailCount = 0
$script:Failures = New-Object System.Collections.Generic.List[string]

function Check {
    param([bool]$Condition, [string]$Label, [string]$Detail = '')
    if ($Condition) {
        $script:PassCount++
        Write-Host ("PASS {0}" -f $Label)
    }
    else {
        $script:FailCount++
        $script:Failures.Add($Label + $(if ($Detail) { " — $Detail" } else { '' }))
        Write-Host ("FAIL {0}{1}" -f $Label, $(if ($Detail) { " — $Detail" } else { '' }))
    }
}

function New-RunDir {
    param([string]$Name)
    $dir = Join-Path $WorkRoot $Name
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return $dir
}

function Invoke-Guard {
    param(
        [string]$Exe,
        [string]$Arguments,
        [string]$RunDirectory,
        [string]$ResultPath,
        [int]$Timeout = 0,
        [int]$TextCaptureLimitMs = 0
    )
    if ($Timeout -le 0) { $Timeout = $TimeoutSeconds }
    if ([string]::IsNullOrWhiteSpace($Exe)) { $Exe = $fixture }
    $parameters = @{
        ExePath       = $Exe
        Arguments     = $Arguments
        RunDirectory  = $RunDirectory
        TimeoutSeconds = $Timeout
        PollIntervalMs = $PollIntervalMs
    }
    if ($ResultPath) { $parameters.ResultPath = $ResultPath }
    if ($TextCaptureLimitMs -gt 0) { $parameters.TextCaptureLimitMs = $TextCaptureLimitMs }
    return (Invoke-GuardedProcess @parameters)
}

function Test-ProcessAlive {
    param([int]$ProcessId)
    if ($ProcessId -le 0) { return $false }
    return [bool](Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
}

# Job query empty is the guard's cleanup confirmation; the process object itself can
# disappear a moment later while the OS finishes termination.
function Wait-ProcessGone {
    param([int]$ProcessId, [int]$Seconds = 5)
    if ($ProcessId -le 0) { return $true }
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-ProcessAlive $ProcessId)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return (-not (Test-ProcessAlive $ProcessId))
}

function Stop-TestProcess {
    param([int]$ProcessId)
    if ($ProcessId -gt 0) {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Read-GuardReport {
    param([string]$Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return ([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json)
}

Write-Host ("Fixture: {0}" -f $fixture)
Write-Host ("Work root: {0}" -f $WorkRoot)
Write-Host ''

# 1. Standard Win32 MessageBox -------------------------------------------------
$run = New-RunDir 'messagebox'
$started = Get-Date
$r = Invoke-Guard -Arguments 'messagebox' -RunDirectory $run
$elapsed = ((Get-Date) - $started).TotalSeconds
Check (-not $r.Success) 'standard MessageBox fails the guard'
Check ($r.Reason -eq 'unexpected-dialog') 'standard MessageBox reason is unexpected-dialog' ([string]$r.Reason)
Check ($r.DialogClass -eq '#32770') 'standard MessageBox class is #32770' ([string]$r.DialogClass)
Check ($r.DialogTitle -ceq 'HeatingMold 오류') 'standard MessageBox title is captured' ([string]$r.DialogTitle)
Check (([string]$r.DialogBody).Contains('3D 스테이징 실패')) 'standard MessageBox body text is captured' ([string]$r.DialogBodySource)
Check ($r.DialogBodySource -eq 'win32') 'standard MessageBox body comes from the Win32 fallback' ([string]$r.DialogBodySource)
Check ($elapsed -lt 10) 'standard MessageBox fails well before the timeout' ([string]$elapsed)
$report = Read-GuardReport $r.ReportPath
Check ($null -ne $report) 'standard MessageBox report json exists'
Check ($report -and $report.reason -eq 'unexpected-dialog') 'standard MessageBox report records the reason'
Check ($report -and ([string]$report.dialog.processPath).EndsWith('FixtureHost.exe')) 'standard MessageBox report records the process path'
Check (Wait-ProcessGone $r.RootProcessId) 'standard MessageBox fixture tree is terminated'

# 2. WPF ErrorReporter-shaped dialog -------------------------------------------
$run = New-RunDir 'reporter'
$r = Invoke-Guard -Arguments 'reporter' -RunDirectory $run
Check (-not $r.Success) 'WPF reporter dialog fails the guard'
Check ($r.Reason -eq 'unexpected-dialog') 'WPF reporter reason is unexpected-dialog' ([string]$r.Reason)
Check ($r.DialogTitle -ceq '오류 — HeatingMold') 'WPF reporter title matches the product reporter title' ([string]$r.DialogTitle)
Check (([string]$r.DialogClass).StartsWith('HwndWrapper')) 'WPF reporter class is a WPF HWND wrapper' ([string]$r.DialogClass)
Check ($r.DialogOwnerDisabled -eq $true) 'WPF reporter owner window is disabled'
Check ($r.DialogBodySource -eq 'uia') 'WPF reporter body is read through UI Automation' ([string]$r.DialogBodySource)
Check (([string]$r.DialogBody).Contains('3D 스테이징 실패')) 'WPF reporter body contains the Korean detail'
Check ($r.DialogBodyLength -gt 100) 'WPF reporter body length is reported' ([string]$r.DialogBodyLength)
Check (Wait-ProcessGone $r.RootProcessId) 'WPF reporter fixture tree is terminated'

# 3. Unowned WPF dialog relies on the reporter title ---------------------------
$run = New-RunDir 'reporter-unowned'
$r = Invoke-Guard -Arguments 'reporter --no-owner' -RunDirectory $run
Check (-not $r.Success) 'unowned WPF reporter dialog fails the guard'
Check ($r.Reason -eq 'unexpected-dialog') 'unowned WPF reporter reason is unexpected-dialog' ([string]$r.Reason)
Check ($r.DialogTitle -ceq '오류 — HeatingMold') 'unowned WPF reporter is caught by its title' ([string]$r.DialogTitle)
Check ($r.DialogOwnerDisabled -eq $false) 'unowned WPF reporter has no disabled owner'

# 4. Launcher exits normally, child modal must still be caught -----------------
$run = New-RunDir 'launcher'
$launcherArguments = 'launcher "' + $fixture + '" reporter'
$r = Invoke-Guard -Arguments $launcherArguments -RunDirectory $run
Check (-not $r.Success) 'launcher child modal fails the guard'
Check ($r.Reason -eq 'unexpected-dialog') 'launcher child modal reason is unexpected-dialog' ([string]$r.Reason)
Check ($r.RootExitCode -eq 0) 'launcher root exited with code 0' ([string]$r.RootExitCode)
Check ($r.RootExited -eq $true) 'guard records that the launcher root exited'
Check ($r.DialogProcessId -ne $r.RootProcessId) 'child dialog belongs to a child process, not the launcher'
Check (@($r.TrackedProcessIds) -contains $r.DialogProcessId) 'guard tracked the child in the same job'
Check ($r.CleanupStatus -eq 'confirmed') 'launcher child cleanup is confirmed by an empty job' ([string]$r.CleanupStatus)
Check (@($r.RemainingProcessIds).Count -eq 0) 'launcher child has no unconfirmed remaining processes'
Check (Wait-ProcessGone $r.DialogProcessId) 'child modal process is terminated'
$report = Read-GuardReport $r.ReportPath
Check ($report -and $report.dialog.processId -eq $r.DialogProcessId) 'launcher report records the child dialog pid'

# 5. Result file keeps the app window alive -----------------------------------
$run = New-RunDir 'result'
$resultPath = Join-Path $run 'result.json'
$r = Invoke-Guard -Arguments ('result "' + $resultPath + '"') -RunDirectory $run -ResultPath $resultPath
Check ($r.Success) 'result file makes the guard succeed' ([string]$r.Reason)
Check ($r.Reason -eq 'result-written') 'result success reason is result-written' ([string]$r.Reason)
Check (Test-Path -LiteralPath $resultPath) 'result file exists after success'
Check (Test-ProcessAlive $r.RootProcessId) 'guard leaves the app window alive on success'
Check ($r.CleanupStatus -eq 'not-required') 'success keeps the window without cleanup' ([string]$r.CleanupStatus)
Check (@($r.TrackedProcessIds) -contains $r.RootProcessId) 'success records the tracked live window'
$successLog = @($r.Log)
$publishedIndex = [Array]::IndexOf($successLog, 'report published while kill-on-close was still set')
$detachedIndex = [Array]::IndexOf($successLog, 'kill-on-close cleared after the report was published; app window may stay open')
Check ($publishedIndex -ge 0 -and $detachedIndex -gt $publishedIndex) 'success report is published before kill-on-close is detached'
$report = Read-GuardReport $r.ReportPath
Check ($report -and $report.status -eq 'succeeded') 'success report records status succeeded'
Check ($report -and $report.cleanupStatus -eq 'not-required') 'success report records cleanupStatus not-required'
Stop-TestProcess $r.RootProcessId

# 6. Timeout cleans only the tracked tree -------------------------------------
$unrelated = Start-Process -FilePath $fixture -ArgumentList 'idle unrelated-window' -PassThru
Start-Sleep -Milliseconds 400
$run = New-RunDir 'timeout'
$r = Invoke-Guard -Arguments 'idle guard-window' -RunDirectory $run -Timeout 2
Check (-not $r.Success) 'timeout fails the guard'
Check ($r.Reason -eq 'timeout') 'timeout reason is timeout' ([string]$r.Reason)
Check (Wait-ProcessGone $r.RootProcessId) 'timeout terminates the tracked fixture'
Check (Test-ProcessAlive $unrelated.Id) 'timeout preserves an unrelated process outside the job'
Check ($r.CleanupStatus -eq 'confirmed') 'timeout cleanup is confirmed by an empty job query' ([string]$r.CleanupStatus)
Check (@($r.TrackedProcessIds) -contains $r.RootProcessId) 'timeout keeps the initial tracked pid as evidence'
Check (@($r.RemainingProcessIds).Count -eq 0) 'timeout leaves no remaining or unconfirmed pid'
Check ($r.Detail -notmatch '종료가 확인되지') 'timeout does not claim cleanup when the job is not empty'
Stop-TestProcess $unrelated.Id

# 7. Owned non-modal tool window is allowed -----------------------------------
$run = New-RunDir 'tool-window'
$resultPath = Join-Path $run 'result.json'
$r = Invoke-Guard -Arguments ('result "' + $resultPath + '" --tool-window') -RunDirectory $run -ResultPath $resultPath
Check ($r.Success) 'owned non-modal tool window does not fail the guard' ([string]$r.Reason)
Check (Test-ProcessAlive $r.RootProcessId) 'tool window fixture stays alive on success'
Stop-TestProcess $r.RootProcessId

# 8. Early exits ---------------------------------------------------------------
$run = New-RunDir 'early-nonzero'
$started = Get-Date
$r = Invoke-Guard -Arguments 'earlyexit' -RunDirectory $run
$elapsed = ((Get-Date) - $started).TotalSeconds
Check (-not $r.Success) 'nonzero exit without a result fails the guard'
Check ($r.Reason -eq 'root-nonzero-exit') 'nonzero exit reason is root-nonzero-exit' ([string]$r.Reason)
Check ($r.RootExitCode -eq 3) 'nonzero exit code is recorded' ([string]$r.RootExitCode)
Check ($elapsed -lt 10) 'nonzero exit fails before the timeout' ([string]$elapsed)

$run = New-RunDir 'early-zero'
$resultPath = Join-Path $run 'result.json'
$r = Invoke-Guard -Arguments 'earlyexit-zero' -RunDirectory $run -ResultPath $resultPath
Check (-not $r.Success) 'zero exit without a result fails the guard'
Check ($r.Reason -eq 'early-exit') 'zero exit without a result reason is early-exit' ([string]$r.Reason)

# 9. Pre-existing result path is refused before launch ------------------------
$run = New-RunDir 'pre-existing'
$resultPath = Join-Path $run 'result.json'
[IO.File]::WriteAllText($resultPath, '{"Status":"Completed"}', (New-Object Text.UTF8Encoding($false)))
$r = Invoke-Guard -Arguments 'idle' -RunDirectory $run -ResultPath $resultPath -Timeout 10
Check (-not $r.Success) 'pre-existing result file fails the guard'
Check ($r.Reason -eq 'result-exists') 'pre-existing result reason is result-exists' ([string]$r.Reason)
Check ($r.RootProcessId -eq 0) 'pre-existing result is refused before launching anything'

# 10. Result + modal together: modal wins -------------------------------------
$run = New-RunDir 'result-dialog'
$resultPath = Join-Path $run 'result.json'
$r = Invoke-Guard -Arguments ('result-dialog "' + $resultPath + '"') -RunDirectory $run -ResultPath $resultPath
Check (-not $r.Success) 'result file plus modal still fails the guard'
Check ($r.Reason -eq 'unexpected-dialog') 'result plus modal reason is unexpected-dialog' ([string]$r.Reason)
Check (([string]$r.Detail).Contains('대화상자')) 'result plus modal report explains the dialog priority' ([string]$r.Detail)
Check (Wait-ProcessGone $r.RootProcessId) 'result plus modal fixture tree is terminated'

# 11. Body size, empty body and capture limits --------------------------------
$run = New-RunDir 'long-body'
$r = Invoke-Guard -Arguments 'reporter --long-body' -RunDirectory $run
Check (-not $r.Success) 'long body dialog fails the guard'
Check ($r.DialogBodyLength -gt 8000) 'long body length is reported before truncation' ([string]$r.DialogBodyLength)
Check (([string]$r.DialogBody).Length -le 8010) 'long body is truncated in the report' ([string]$r.DialogBody.Length)
Check (([string]$r.DialogBody).EndsWith('…(잘림)')) 'truncated body carries the truncation marker'

$run = New-RunDir 'empty-body'
$r = Invoke-Guard -Arguments 'reporter --empty-body' -RunDirectory $run
Check (-not $r.Success) 'empty body dialog still fails the guard'
Check ($r.Reason -eq 'unexpected-dialog') 'empty body dialog reason is unexpected-dialog' ([string]$r.Reason)
Check (-not [string]::IsNullOrEmpty($r.DialogBody)) 'empty body falls back to another text source' ([string]$r.DialogBodySource)
Check ($r.DialogTitle -ceq '오류 — HeatingMold') 'empty body keeps the dialog title'

$run = New-RunDir 'capture-limit'
$started = Get-Date
$r = Invoke-Guard -Arguments 'reporter' -RunDirectory $run -TextCaptureLimitMs 1
$elapsed = ((Get-Date) - $started).TotalSeconds
Check (-not $r.Success) 'text capture limit still detects the dialog'
Check ($r.Reason -eq 'unexpected-dialog') 'text capture limit reason is unexpected-dialog' ([string]$r.Reason)
Check ($r.DialogTitle -ceq '오류 — HeatingMold') 'text capture limit keeps the dialog title'
Check ($elapsed -lt 10) 'text capture limit does not block the watchdog' ([string]$elapsed)

# Bounded body collection: many hung child controls ---------------------------------
$run = New-RunDir 'slow-body-bounded'
$started = Get-Date
$r = Invoke-Guard -Arguments 'slow-body' -RunDirectory $run -TextCaptureLimitMs 300
$elapsed = ((Get-Date) - $started).TotalSeconds
Check (-not $r.Success) 'hung many-control body still fails the guard'
Check ($r.Reason -eq 'unexpected-dialog') 'hung many-control body reason is unexpected-dialog' ([string]$r.Reason)
Check ($r.DialogTitle -ceq '오류 — HeatingMold') 'hung many-control body keeps the title' ([string]$r.DialogTitle)
Check ($elapsed -lt 10) 'failure and cleanup finish within 10 s after the configured collection cap' ([string]$elapsed)
Check ($r.DialogBodySource -ne '') 'hung many-control body records a collection source' ([string]$r.DialogBodySource)
Check ($r.CleanupStatus -eq 'confirmed') 'hung many-control cleanup is confirmed' ([string]$r.CleanupStatus)
Check (Wait-ProcessGone $r.RootProcessId) 'hung many-control fixture is terminated'
$report = Read-GuardReport $r.ReportPath
Check ($report -and $report.dialog.bodyError) 'hung many-control report explains the collection limit'

# Report write failure must not publish success --------------------------------------
$run = New-RunDir 'report-write-failed'
New-Item -ItemType Directory -Force -Path (Join-Path $run 'validation-guard.txt') | Out-Null
$resultPath = Join-Path $run 'result.json'
$r = Invoke-Guard -Arguments ('result "' + $resultPath + '"') -RunDirectory $run -ResultPath $resultPath
Check (-not $r.Success) 'report write failure fails the guard'
Check ($r.Reason -eq 'report-write-failed') 'report write failure reason is report-write-failed' ([string]$r.Reason)
Check ($r.ReportWriteFailed -eq $true) 'report write failure is reported to the caller'
Check ($r.CleanupStatus -eq 'confirmed') 'report write failure cleanup is confirmed' ([string]$r.CleanupStatus)
Check (@($r.RemainingProcessIds).Count -eq 0) 'report write failure leaves no remaining pid'
Check (-not (@($r.Log) -contains 'kill-on-close cleared after the report was published; app window may stay open')) 'report write failure never detaches the job before failure'
Check (Wait-ProcessGone $r.RootProcessId) 'report write failure terminates the validation tree'
$report = Read-GuardReport $r.ReportPath
Check ($report -and $report.status -eq 'failed') 'report write failure rewrites the JSON status to failed'
Check ($report -and $report.reason -eq 'report-write-failed') 'report write failure records its reason in the JSON'
Check ($report -and $report.reportWriteFailed -eq $true) 'report JSON records reportWriteFailed'

# PATH executable resolution ---------------------------------------------------------
$run = New-RunDir 'path-executable'
$r = Invoke-Guard -Exe 'cmd' -Arguments '/c exit 0' -RunDirectory $run -Timeout 15
Check ($r.Success) 'bare PATH executable name is resolved and runs' ([string]$r.Reason)
Check (([string]$r.CommandLine).ToLowerInvariant().Contains('cmd.exe')) 'resolved PATH executable is recorded as a full path' ([string]$r.CommandLine)

$threw = $false
try {
    Invoke-GuardedProcess -ExePath 'hm-guard-no-such-executable-xyz' -RunDirectory (New-RunDir 'path-missing')
}
catch {
    $threw = $true
}
Check ($threw) 'unknown bare executable name is rejected'

# Guard host killed: kill-on-close must clean the tracked tree ------------------------
$run = New-RunDir 'killed-guard'
$unrelated = Start-Process -FilePath $fixture -ArgumentList 'idle unrelated-kill' -PassThru
Start-Sleep -Milliseconds 400
$guardScript = Join-Path $root 'tools\validation\Invoke-GuardedProcess.ps1'
$guardArguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -ExePath "{1}" -Arguments "idle killed-guard" -RunDirectory "{2}" -TimeoutSeconds 300' -f `
    $guardScript, $fixture, $run
$guardProcess = Start-Process -FilePath 'powershell' -ArgumentList $guardArguments -PassThru
$childProcess = $null
$childDeadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $childDeadline -and -not $childProcess) {
    $candidate = Get-CimInstance Win32_Process -Filter "Name='FixtureHost.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ParentProcessId -eq $guardProcess.Id } |
        Select-Object -First 1
    if ($candidate) { $childProcess = $candidate }
    else { Start-Sleep -Milliseconds 200 }
}
Check ($null -ne $childProcess) 'guard host starts the tracked fixture'
if ($childProcess) {
    $childPid = [int]$childProcess.ProcessId
    Stop-Process -Id $guardProcess.Id -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline -and (Test-ProcessAlive $childPid)) { Start-Sleep -Milliseconds 100 }
    Check (-not (Test-ProcessAlive $childPid)) 'killing the guard host terminates the tracked child (kill-on-close)'
    if (Test-ProcessAlive $childPid) { Stop-TestProcess $childPid }
}
else {
    Stop-Process -Id $guardProcess.Id -Force -ErrorAction SilentlyContinue
}
Check (Test-ProcessAlive $unrelated.Id) 'killing the guard host preserves an unrelated process'
Stop-TestProcess $unrelated.Id

# Run.ps1 wiring ---------------------------------------------------------------
$runScript = Join-Path $root 'tests\HeatingMold.VisualTest\Run.ps1'
$scenarioFile = Join-Path $WorkRoot 'minimal-scenario.json'
[IO.File]::WriteAllText($scenarioFile, '{"schemaVersion":2}', (New-Object Text.UTF8Encoding($false)))

$run = New-RunDir 'runps1-success'
$output = (& powershell -NoProfile -ExecutionPolicy Bypass -File $runScript `
    -Scenario $scenarioFile -AppExe $fixture -NoBuild -RunDir $run `
    -TimeoutSeconds 20 -HoldSeconds 1 2>&1 | Out-String)
$exitCode = $LASTEXITCODE
Check ($exitCode -eq 0) 'Run.ps1 returns 0 with a stub app that writes result.json' ([string]$exitCode)
Check ($output -match 'Status: Completed') 'Run.ps1 reports the completed status'
Check (Test-Path -LiteralPath (Join-Path $run 'result.json')) 'Run.ps1 keeps the stub result file'
Check (Test-Path -LiteralPath (Join-Path $run 'validation-guard.json')) 'Run.ps1 leaves the guard report'
$report = Read-GuardReport (Join-Path $run 'validation-guard.json')
Check ($report -and $report.status -eq 'succeeded') 'Run.ps1 guard report is succeeded'
foreach ($leftover in @($report.remainingProcessIds)) { Stop-TestProcess ([int]$leftover) }

$run = New-RunDir 'runps1-dialog'
$previousFixtureMode = $env:HM_GUARD_FIXTURE_MODE
$env:HM_GUARD_FIXTURE_MODE = 'visual-modal'
try {
    $started = Get-Date
    $output = (& powershell -NoProfile -ExecutionPolicy Bypass -File $runScript `
        -Scenario $scenarioFile -AppExe $fixture -NoBuild -RunDir $run `
        -TimeoutSeconds 20 -HoldSeconds 1 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
    $elapsed = ((Get-Date) - $started).TotalSeconds
}
finally {
    $env:HM_GUARD_FIXTURE_MODE = $previousFixtureMode
}
Check ($exitCode -ne 0) 'Run.ps1 exits nonzero when a modal blocks the run' ([string]$exitCode)
Check ($output -match 'Validation guard stopped the run') 'Run.ps1 reports the guard failure'
Check ($elapsed -lt 15) 'Run.ps1 fails fast instead of waiting for the timeout' ([string]$elapsed)
$report = Read-GuardReport (Join-Path $run 'validation-guard.json')
Check ($report -and $report.reason -eq 'unexpected-dialog') 'Run.ps1 modal guard report records unexpected-dialog'
Check ($report -and (Wait-ProcessGone ([int]$report.dialog.processId))) 'Run.ps1 modal fixture process is terminated'

# Summary ---------------------------------------------------------------------
Write-Host ''
Write-Host ("Checks: {0} pass, {1} fail" -f $script:PassCount, $script:FailCount)
Write-Host ("Artifacts: {0}" -f $WorkRoot)
if ($script:Failures.Count -gt 0) {
    Write-Host 'Failed checks:'
    foreach ($failure in $script:Failures) { Write-Host ("  - {0}" -f $failure) }
    exit 1
}
exit 0
