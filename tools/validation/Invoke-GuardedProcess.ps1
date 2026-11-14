<#
.SYNOPSIS
    검증 프로세스를 감시 실행하는 명령줄 진입점.

.DESCRIPTION
    GuardedProcess.ps1 의 Invoke-GuardedProcess 함수를 호출한다.
    -ResultPath 가 생기면 성공, 예상하지 않은 모달 대화상자/조기 종료/timeout 이면 실패한다.
    실패 시 추적 중인 프로세스 트리만 종료하고 실행 폴더에 진단 파일을 남긴다.
    성공 시 검증 앱 창은 그대로 둔다(육안 확인 계약 유지).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\validation\Invoke-GuardedProcess.ps1 `
        -ExePath .\app.exe -Arguments "--visual-test scenario.json" `
        -ResultPath .\run\result.json -RunDirectory .\run -TimeoutSeconds 90 -ExitWithCode

.EXAMPLE
    # 임시 오프스크린 하네스처럼 오류 창이 뜰 수 있는 검증 프로세스
    powershell -ExecutionPolicy Bypass -File tools\validation\Invoke-GuardedProcess.ps1 `
        -ExePath dotnet.exe -Arguments "harness.dll --model x.step" `
        -RunDirectory C:\temp\harness-run -TimeoutSeconds 600 -ExitWithCode
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
    [int]$TextCaptureLimitMs = 1000,
    [switch]$ExitWithCode
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GuardedProcess.ps1')

$result = Invoke-GuardedProcess `
    -ExePath $ExePath `
    -Arguments $Arguments `
    -WorkingDirectory $WorkingDirectory `
    -ResultPath $ResultPath `
    -RunDirectory $RunDirectory `
    -TimeoutSeconds $TimeoutSeconds `
    -PollIntervalMs $PollIntervalMs `
    -TextCaptureLimitMs $TextCaptureLimitMs

if ($ExitWithCode) {
    if ($result.Success) { exit 0 }
    exit 1
}
