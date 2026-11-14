#requires -Version 5.1
<#
.SYNOPSIS
    Invoke-GuardedProcess 함수 정의. 이 파일은 dot-source 해서 사용한다.

.DESCRIPTION
    검증용 프로세스 트리 하나를 실행하고 다음을 감시한다.
      - 예상하지 않은 모달 대화상자(표준 #32770, owner가 비활성화된 owned 창,
        HeatingMold 오류/경고/시작 실패 제목) 감지
      - 결과 파일 작성
      - root 조기 종료 / 결과 없음 / 전체 timeout

    실패하면 추적 중인 job tree만 종료하고(다른 프로세스는 건드리지 않는다)
    실행 폴더에 validation-guard.json / validation-guard.txt를 남긴다.
    성공하면 앱 창을 그대로 두어 육안 확인을 방해하지 않는다.
    대화상자의 확인/승인 버튼은 절대 자동으로 누르지 않는다.

    CLI로 쓰려면 Invoke-GuardedProcess.ps1 을 사용한다.
#>

function Get-ValidationGuardAssembly {
    [CmdletBinding()]
    param()

    $sourcePath = Join-Path $PSScriptRoot 'GuardedProcessHost.cs'
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "GuardedProcessHost.cs not found next to GuardedProcess.ps1: $sourcePath"
    }

    Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop

    $hash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    $cacheRoot = Join-Path $env:LOCALAPPDATA 'HeatingMold\ValidationGuard'
    New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
    $assemblyPath = Join-Path $cacheRoot ("GuardedProcessHost-" + $hash + ".dll")

    $cacheValid = (Test-Path -LiteralPath $assemblyPath -PathType Leaf) -and
        ((Get-Item -LiteralPath $assemblyPath).Length -gt 0)
    if (-not $cacheValid) {
        $source = [IO.File]::ReadAllText($sourcePath, [Text.Encoding]::UTF8)
        $references = @(
            [System.Windows.Automation.AutomationElement].Assembly.Location,
            [System.Windows.Automation.ControlType].Assembly.Location
        )
        try {
            Add-Type -TypeDefinition $source -ReferencedAssemblies $references `
                -OutputAssembly $assemblyPath -ErrorAction Stop
        }
        catch {
            # 동시에 다른 프로세스가 같은 캐시를 컴파일했을 수 있다.
            if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw }
        }
    }

    try {
        Add-Type -Path $assemblyPath -ErrorAction Stop
    }
    catch {
        # 이미 이 세션에 로드된 어셈블리면 Add-Type이 실패할 수 있다.
        if (-not ('HeatingMold.Validation.GuardedProcessRunner' -as [type])) { throw }
    }
    return $assemblyPath
}

function Resolve-GuardExecutable {
<#
.SYNOPSIS
    검증 exe 경로를 해석한다. 경로 구분자가 없는 이름은 PATH에서 .exe만 찾는다.
#>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        throw 'Validation target executable is empty.'
    }

    $hasSeparator = $Name.IndexOfAny([char[]]@('\', '/')) -ge 0
    if (-not $hasSeparator) {
        $command = Get-Command -Name $Name -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($command -and $command.Source) {
            $resolved = [IO.Path]::GetFullPath($command.Source)
            if ([IO.Path]::GetExtension($resolved) -ine '.exe') {
                throw "PATH에서 찾은 '$Name'은 .exe가 아닙니다: $resolved — exe 파일 경로를 지정하세요."
            }
            return $resolved
        }
    }

    $full = [IO.Path]::GetFullPath($Name)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Validation target executable not found: $full"
    }
    return $full
}

function Invoke-GuardedProcess {
<#
.SYNOPSIS
    검증 프로세스를 감시 실행한다.

.PARAMETER ExePath
    실행할 exe 경로(절대/상대 모두 가능).

.PARAMETER Arguments
    exe에 넘길 인자 문자열. 호출자가 따옴표까지 맞춰서 넘긴다.

.PARAMETER WorkingDirectory
    작업 폴더. 생략하면 exe 폴더.

.PARAMETER ResultPath
    이 파일이 생성되면 검증 성공으로 본다. 생략하면 프로세스가 스스로 정상 종료할 때만 성공.
    실행 전에 이미 존재하면 이전 결과 재사용을 막기 위해 시작하지 않고 실패한다.

.PARAMETER RunDirectory
    감시 보고서를 남길 폴더. 생략하면 ResultPath 폴더 또는 임시 폴더.

.PARAMETER TimeoutSeconds
    전체 대기 시간(기본 90초). 초과하면 남은 프로세스 트리를 정리하고 실패.

.PARAMETER PollIntervalMs
    감시 주기(기본 250ms, 50~2000으로 제한).

.PARAMETER TextCaptureLimitMs
    대화상자 본문 수집 상한(기본 1000ms). UIA가 응답하지 않아도 감시를 막지 않는다.
#>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ExePath,
        [string]$Arguments = '',
        [string]$WorkingDirectory,
        [string]$ResultPath,
        [string]$RunDirectory,
        [int]$TimeoutSeconds = 90,
        [int]$PollIntervalMs = 250,
        [int]$TextCaptureLimitMs = 1000
    )

    $exeFull = Resolve-GuardExecutable -Name $ExePath

    if ($WorkingDirectory) {
        $workingFull = [IO.Path]::GetFullPath($WorkingDirectory)
    }
    else {
        $workingFull = Split-Path -Parent $exeFull
    }

    $resultFull = $null
    if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $resultFull = [IO.Path]::GetFullPath($ResultPath)
    }

    if (-not [string]::IsNullOrWhiteSpace($RunDirectory)) {
        $runFull = [IO.Path]::GetFullPath($RunDirectory)
    }
    elseif ($resultFull) {
        $runFull = Split-Path -Parent $resultFull
    }
    else {
        $runFull = Join-Path ([IO.Path]::GetTempPath()) (
            'heatingmold-validation\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID)
    }
    New-Item -ItemType Directory -Force -Path $runFull | Out-Null

    if ($PollIntervalMs -le 0) { $PollIntervalMs = 250 }
    if ($TextCaptureLimitMs -le 0) { $TextCaptureLimitMs = 1000 }

    Get-ValidationGuardAssembly | Out-Null

    $options = New-Object HeatingMold.Validation.GuardLaunchOptions
    $options.ExePath = $exeFull
    $options.Arguments = $Arguments
    $options.WorkingDirectory = $workingFull
    $options.ResultPath = $resultFull
    $options.RunDirectory = $runFull
    $options.TimeoutSeconds = $TimeoutSeconds
    $options.PollIntervalMs = $PollIntervalMs
    $options.TextCaptureLimitMs = $TextCaptureLimitMs

    $outcome = [HeatingMold.Validation.GuardedProcessRunner]::Run($options)

    $success = [string]::Equals($outcome.Status, 'succeeded', [StringComparison]::Ordinal)
    $reason = [string]$outcome.Reason
    $message = [string]$outcome.Message
    if ($outcome.ReportWriteFailed -and $success) {
        $success = $false
        $reason = 'report-write-failed'
        $message = '검증은 끝났지만 감시 보고서를 쓰지 못했습니다: ' + [string]$outcome.ReportWriteError
    }

    $dialogTitle = $null
    $dialogClass = $null
    $dialogProcessId = 0
    $dialogProcessPath = $null
    $dialogBody = $null
    $dialogBodySource = $null
    $dialogOwnerDisabled = $false
    $dialogBodyLength = 0
    if ($null -ne $outcome.Dialog) {
        $dialogTitle = $outcome.Dialog.Title
        $dialogClass = $outcome.Dialog.ClassName
        $dialogProcessId = $outcome.Dialog.ProcessId
        $dialogProcessPath = $outcome.Dialog.ProcessPath
        $dialogBody = $outcome.Dialog.Body
        $dialogBodySource = $outcome.Dialog.BodySource
        $dialogOwnerDisabled = $outcome.Dialog.OwnerDisabled
        $dialogBodyLength = $outcome.Dialog.BodyLength
    }

    $result = [pscustomobject]@{
        Success              = $success
        Status               = [string]$outcome.Status
        Reason               = $reason
        Message              = $message
        Detail               = $outcome.Detail
        RootProcessId        = $outcome.RootProcessId
        RootExitCode         = $outcome.RootExitCode
        RootExited           = $outcome.RootExited
        Pids                 = @($outcome.RemainingProcessIds)
        TrackedProcessIds    = @($outcome.TrackedProcessIds)
        RemainingProcessIds  = @($outcome.RemainingProcessIds)
        CleanupStatus        = [string]$outcome.CleanupStatus
        TerminationConfirmed = [string]::Equals([string]$outcome.CleanupStatus, 'confirmed', [StringComparison]::Ordinal)
        Log                  = @($outcome.Log)
        CommandLine          = $outcome.CommandLine
        StartTimeUtc         = $outcome.StartTimeUtc
        DialogTitle          = $dialogTitle
        DialogClass          = $dialogClass
        DialogProcessId      = $dialogProcessId
        DialogProcessPath    = $dialogProcessPath
        DialogBody           = $dialogBody
        DialogBodySource     = $dialogBodySource
        DialogOwnerDisabled  = $dialogOwnerDisabled
        DialogBodyLength     = $dialogBodyLength
        LastStep             = $outcome.LastStep
        ReportPath           = $outcome.JsonPath
        TextReportPath       = $outcome.TextPath
        ReportWriteFailed    = $outcome.ReportWriteFailed
        ReportWriteError     = $outcome.ReportWriteError
        RunDirectory         = $runFull
    }

    if ($result.Success) {
        Write-Host ("Validation guard: OK ({0}) — {1}" -f $result.Reason, $result.Message)
        Write-Host ("  root pid {0}, tracked pids: {1}" -f $result.RootProcessId, (($result.TrackedProcessIds | ForEach-Object { $_ }) -join ','))
        Write-Host ("  report: {0}" -f $result.ReportPath)
        Write-Host '  결과 파일 게시/정상 종료만 확인했습니다. 시나리오 성공 판정은 호출자가 result.json Status로 합니다.'
    }
    else {
        Write-Host ("Validation guard: FAILED ({0}) — {1}" -f $result.Reason, $result.Message)
        if ($result.DialogTitle) {
            Write-Host ("  dialog: title='{0}' class='{1}' pid={2}" -f $result.DialogTitle, $result.DialogClass, $result.DialogProcessId)
            if ($result.DialogProcessPath) { Write-Host ("  dialog path: {0}" -f $result.DialogProcessPath) }
            Write-Host ("  body source: {0} (length {1})" -f $result.DialogBodySource, $result.DialogBodyLength)
            if ($result.DialogBody) {
                $preview = [string]$result.DialogBody
                if ($preview.Length -gt 400) { $preview = $preview.Substring(0, 400) + '…' }
                $firstLines = ($preview -split "`r?`n" | Select-Object -First 6) -join [Environment]::NewLine
                Write-Host $firstLines
            }
        }
        if ($result.LastStep) { Write-Host ("  last step: {0}" -f $result.LastStep) }
        Write-Host ("  report: {0}" -f $result.ReportPath)
        Write-Host ("  run directory: {0}" -f $result.RunDirectory)
        Write-Host '  대화상자는 자동으로 닫거나 승인하지 않았습니다.'
        if ($result.CleanupStatus -eq 'confirmed') {
            Write-Host '  추적 중이던 검증 프로세스는 종료가 확인되었습니다(job 비어 있음).'
        }
        elseif ($result.CleanupStatus -eq 'unconfirmed') {
            $left = ($result.RemainingProcessIds | ForEach-Object { $_ }) -join ','
            Write-Host ("  종료가 확인되지 않은 프로세스가 있습니다: {0} — 직접 확인하세요." -f $left)
        }
        else {
            Write-Host '  종료를 요청한 추적 프로세스가 없습니다(실행 전 실패 또는 이미 종료).'
        }
    }

    return $result
}
