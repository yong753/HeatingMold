#requires -Version 5.1
<#
    배포 의존성 기록 생성 CLI.

    사용법
      powershell -ExecutionPolicy Bypass -File tools\deploy\Get-Dependencies.ps1

    결과
      tools\deploy\generated\native-dependencies.json
        - 네이티브 브리지의 PE import 폐포로 확인한 앱 DLL 목록
        - VC 재배포 DLL 목록(Visual Studio 재배포 폴더 버전 포함)
        - Windows 가 제공하는 DLL, publish 출력이 제공하는 DLL
      이 파일은 검토용 기록이다. 패키징(Build-Package.ps1)은 같은 계산을 실행 시점에
      다시 수행하므로, 기록이 오래되면 경고로 알린다.

    네이티브를 다시 빌드했거나 OCCT/서드파티 DLL 을 바꿨으면 이 스크립트를 다시 실행한다.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')
. (Join-Path $PSScriptRoot 'VcRuntime.ps1')
. (Join-Path $PSScriptRoot 'DependencyResolver.ps1')

$config = Get-DeployConfig

Write-Step '배포 의존성 확인'
Write-Detail "저장소: $(Get-RepoRoot)"
Write-Detail "브리지: $($config.bridgeDll)"

$report = Get-NativeDependencyReport -Config $config
$report | Add-Member -NotePropertyName generatedUtc -NotePropertyValue ((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))

$outPath = Join-Path $PSScriptRoot 'generated\native-dependencies.json'
Write-JsonFile -Path $outPath -Value $report

Write-Ok "앱 DLL $($report.appDlls.Count)개"
foreach ($d in $report.appDlls) { Write-Detail ("  {0}  {1}  <- {2}" -f $d.name, (Format-Size $d.size), ((@($d.importedBy)) -join ', ')) }

Write-Ok "VC 재배포 DLL $($report.vcRuntimeDlls.Count)개 (버전 $($report.vcRedistVersion), 앱 로컬 복사)"
foreach ($d in $report.vcRuntimeDlls) { Write-Detail ("  {0}  {1}  [{2}]  <- {3}" -f $d.name, (Format-Size $d.size), $d.component, ((@($d.importedBy)) -join ', ')) }

if ($report.publishProvided.Count -gt 0) {
    Write-Ok "publish 출력 제공 $($report.publishProvided.Count)개"
    foreach ($d in $report.publishProvided) { Write-Detail ("  {0}  <- {1}" -f $d.name, ((@($d.importedBy)) -join ', ')) }
}

Write-Ok "Windows 시스템 DLL $($report.systemDlls.Count)개 (패키지에 넣지 않음)"
Write-Detail ('  ' + ((@($report.systemDlls) | ForEach-Object { $_.name }) -join ', '))

Write-Step '기록 저장'
Write-Ok $outPath
