#requires -Version 5.1
<#
.SYNOPSIS
    이미 만들어진 배포 패키지(스테이지 폴더 또는 ZIP)를 검증한다.

.DESCRIPTION
    빌드 스크립트가 만든 결과물을 "배포 받은 사람 입장"에서 다시 확인한다.
    - 필수 파일과 폴더 구조
    - deployment.json 내용과 예제 모델 해시
    - manifest.sha256 자체 검증(누락/변조/미등록 파일)
    - 패키지 안에 개발 PC 경로가 남아 있지 않은지
    - App 폴더의 PE 의존성 누락

    ZIP을 주면 임시 폴더에 풀어서 검사한다. 원본 ZIP은 수정하지 않는다.

.PARAMETER PackagePath
    스테이지 폴더 또는 ZIP 파일 경로. 생략하면 outputRoot 아래 가장 최근 패키지를 고른다.

.PARAMETER ExtractTo
    ZIP을 풀 위치. 생략하면 outputRoot 아래 verify-test 폴더를 새로 만든다.

.PARAMETER RunLauncher
    시작 도구를 --check-only --quiet 로 실제 실행해 종료 코드를 확인한다.

.PARAMETER SkipDependencyCheck
    App 폴더 PE 의존성 검사를 건너뛴다.

.PARAMETER SkipIntegrity
    manifest.sha256 검사를 건너뛴다. (권장하지 않음)

.PARAMETER ReportPath
    결과 JSON 경로. 생략하면 패키지 폴더 옆에 package-test.json 으로 쓴다.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\deploy\Test-Package.ps1 -RunLauncher

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\deploy\Test-Package.ps1 -PackagePath artifacts\deploy\20261113-0249-cb5cc60-dirty\HeatingMold-Test-20261113-0249-cb5cc60-dirty-win-x64.zip -RunLauncher
#>
[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$ExtractTo,
    [switch]$RunLauncher,
    [switch]$SkipDependencyCheck,
    [switch]$SkipIntegrity,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')
. (Join-Path $PSScriptRoot 'VcRuntime.ps1')
. (Join-Path $PSScriptRoot 'DependencyResolver.ps1')

$config = Get-DeployConfig
$repoRoot = Get-RepoRoot
$outputRoot = Resolve-RepoPath -Path $config.outputRoot

# Windows PowerShell 5.1 에서는 ZIP API 어셈블리를 명시적으로 불러와야 한다.
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---------------------------------------------------------------- 유틸리티

function New-ResultList {
    return New-Object System.Collections.Generic.List[string]
}

function Get-PackageRelativeFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    $trimmed = $Root.TrimEnd('\')
    $prefixLength = $trimmed.Length + 1
    $items = New-Object System.Collections.Generic.List[string]
    foreach ($file in (Get-ChildItem -LiteralPath $trimmed -Recurse -File -Force)) {
        $relative = $file.FullName.Substring($prefixLength).Replace('\', '/')
        $items.Add($relative)
    }
    # 배열을 그대로 내보낸다. 호출부에서 @(...) 로 감싸면 파일별 항목이 된다.
    return $items.ToArray()
}

function Find-DefaultPackage {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root)) {
        throw "배포 결과 폴더가 없습니다: $Root`n빌드를 먼저 실행하세요: powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Package.ps1"
    }

    # 실제 산출물은 <outputRoot>\<Version>\<PackageName>\ 와 <PackageName>.zip 이다.
    # 배포물 자체(ZIP)를 우선 검사하고, ZIP 이 없으면 스테이지 폴더를 쓴다.
    $zips = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.zip' -ErrorAction SilentlyContinue |
        Sort-Object -Property LastWriteTime -Descending)
    if ($zips.Count -gt 0) {
        return $zips[0].FullName
    }

    $stages = @(Get-ChildItem -LiteralPath $Root -Recurse -Directory -Filter '*-win-x64' -ErrorAction SilentlyContinue |
        Sort-Object -Property LastWriteTime -Descending)
    if ($stages.Count -gt 0) {
        return $stages[0].FullName
    }

    throw "검사할 패키지를 찾지 못했습니다: $Root"
}

function Expand-PackageZip {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$Target
    )

    if (Test-Path -LiteralPath $Target) {
        New-CleanDirectory -Path $Target
    }
    else {
        [void](New-Item -ItemType Directory -Path $Target -Force)
    }

    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $Target)

    # ZIP은 배포 폴더 하나를 담고 있으므로 그 안쪽을 실제 루트로 쓴다.
    $children = @(Get-ChildItem -LiteralPath $Target -Directory)
    $rootFiles = @(Get-ChildItem -LiteralPath $Target -File)
    if ($rootFiles.Count -eq 0 -and $children.Count -eq 1) {
        return $children[0].FullName
    }
    return $Target
}

# ---------------------------------------------------------------- 검사 항목

function Test-RequiredLayout {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]$Config
    )

    $problems = New-ResultList
    $appDir = Join-Path $Root 'App'
    $bridgeName = Split-Path -Leaf $Config.bridgeDll

    $required = @(
        'HeatingMold.Start.exe',
        '처음_읽어주세요.txt',
        'ReleaseNotes.txt',
        'THIRD-PARTY-NOTICES.txt',
        'build-info.json',
        'manifest.sha256',
        'App',
        'Licenses'
    ) | ForEach-Object { $_ }

    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $name))) {
            $problems.Add("루트에 '$name' 이(가) 없습니다.")
        }
    }

    $appRequired = @(
        ($Config.appAssemblyName + '.exe'),
        ($Config.appAssemblyName + '.dll'),
        $bridgeName,
        'deployment.json'
    )
    foreach ($name in $appRequired) {
        if (-not (Test-Path -LiteralPath (Join-Path $appDir $name))) {
            $problems.Add("App 폴더에 '$name' 이(가) 없습니다.")
        }
    }

    # ijwhost.dll 은 대소문자가 실행 환경에 따라 달라질 수 있으므로 이름을 비교해서 찾는다.
    $ijw = @(Get-ChildItem -LiteralPath $appDir -File -Filter 'ijwhost.dll' -ErrorAction SilentlyContinue)
    if ($ijw.Count -eq 0) {
        $problems.Add("App 폴더에 'ijwhost.dll' 이(가) 없습니다. C++/CLI 브리지를 로드할 수 없습니다.")
    }

    $exampleRelative = $Config.exampleModel.packageRelativePath.Replace('/', '\')
    if (-not (Test-Path -LiteralPath (Join-Path $appDir $exampleRelative))) {
        $problems.Add("예제 모델이 없습니다: App\$exampleRelative")
    }

    foreach ($folder in $Config.occt.runtimeResourceFolders) {
        $path = Join-Path (Join-Path $appDir 'occt-resources') $folder
        if (-not (Test-Path -LiteralPath $path)) {
            $problems.Add("OCCT 리소스 폴더가 없습니다: App\occt-resources\$folder")
        }
    }

    foreach ($license in $Config.occt.licenseFiles) {
        # 설정에는 저장소 기준 경로가 적혀 있고, 패키지에는 파일 이름만으로 Licenses\ 아래에 놓인다.
        $licenseName = Split-Path -Leaf $license
        if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $Root 'Licenses') $licenseName))) {
            $problems.Add("라이선스 원문이 없습니다: Licenses\$licenseName")
        }
    }

    return , $problems.ToArray()
}

function Test-DeploymentManifestFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]$Config
    )

    $problems = New-ResultList
    $appDir = Join-Path $Root 'App'
    $path = Join-Path $appDir 'deployment.json'

    if (-not (Test-Path -LiteralPath $path)) {
        $problems.Add('deployment.json 이 없어 배포 모드를 확인할 수 없습니다.')
        return , $problems.ToArray()
    }

    $manifest = $null
    try {
        $manifest = Read-JsonFile -Path $path
    }
    catch {
        $problems.Add("deployment.json 을 읽지 못했습니다: $($_.Exception.Message)")
        return , $problems.ToArray()
    }

    if ($manifest.schemaVersion -ne 1) {
        $problems.Add("deployment.json 의 schemaVersion 이 1 이 아닙니다: $($manifest.schemaVersion)")
    }
    if ([string]$manifest.layout -ne 'packaged') {
        $problems.Add("deployment.json 의 layout 이 'packaged' 가 아닙니다: $($manifest.layout)")
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.occtResources)) {
        $problems.Add('deployment.json 에 occtResources 가 없습니다.')
    }
    else {
        $occtValue = [string]$manifest.occtResources
        if ([System.IO.Path]::IsPathRooted($occtValue)) {
            $problems.Add("deployment.json 의 occtResources 가 절대 경로입니다: $occtValue")
        }
        elseif (-not (Test-Path -LiteralPath (Join-Path $appDir $occtValue.Replace('/', '\')))) {
            $problems.Add("deployment.json 이 가리키는 occt-resources 폴더가 없습니다: $occtValue")
        }
    }

    if ([string]::IsNullOrWhiteSpace([string]$manifest.exampleModel)) {
        $problems.Add('deployment.json 에 exampleModel 이 없습니다.')
    }
    else {
        $exampleValue = [string]$manifest.exampleModel
        if ([System.IO.Path]::IsPathRooted($exampleValue)) {
            $problems.Add("deployment.json 의 exampleModel 이 절대 경로입니다: $exampleValue")
        }
        else {
            $examplePath = Join-Path $appDir $exampleValue.Replace('/', '\')
            if (-not (Test-Path -LiteralPath $examplePath)) {
                $problems.Add("deployment.json 이 가리키는 예제 모델이 없습니다: $exampleValue")
            }
            else {
                $hash = Get-Sha256 -Path $examplePath
                if ($hash -ne $Config.exampleModel.sha256) {
                    $problems.Add("예제 모델 해시가 설정값과 다릅니다. expected=$($Config.exampleModel.sha256) actual=$hash")
                }
            }
        }
    }

    foreach ($name in @('packageVersion', 'productName', 'builtUtc')) {
        if ([string]::IsNullOrWhiteSpace([string]$manifest.$name)) {
            $problems.Add("deployment.json 에 $name 값이 없습니다.")
        }
    }

    return , $problems.ToArray()
}

function Test-PackageIntegrity {
    param([Parameter(Mandatory = $true)][string]$Root)

    $problems = New-ResultList
    $manifestName = 'manifest.sha256'
    $manifestPath = Join-Path $Root $manifestName

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        $problems.Add('manifest.sha256 이 없습니다.')
        return , $problems.ToArray()
    }

    $lines = [System.IO.File]::ReadAllLines($manifestPath, [System.Text.Encoding]::UTF8)
    $recorded = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $lineNumber = 0

    foreach ($line in $lines) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        # 형식: <64 hex><공백 1~2개><경로>   (경로 앞에 * 가 붙는 형식도 허용)
        $match = [regex]::Match($line, '^([0-9A-Fa-f]{64}) [ *](.+)$')
        if (-not $match.Success) {
            $problems.Add("manifest.sha256 $lineNumber 행 형식이 올바르지 않습니다: $line")
            continue
        }

        $hash = $match.Groups[1].Value.ToUpperInvariant()
        $relative = $match.Groups[2].Value.Trim().Replace('\', '/')
        if ($recorded.ContainsKey($relative)) {
            $problems.Add("manifest.sha256 에 같은 경로가 두 번 있습니다: $relative")
            continue
        }
        $recorded.Add($relative, $hash)
    }

    foreach ($relative in @($recorded.Keys)) {
        $target = Join-Path $Root $relative.Replace('/', '\')
        if (-not (Test-Path -LiteralPath $target)) {
            $problems.Add("manifest.sha256 에 등록된 파일이 없습니다: $relative")
            continue
        }
        $actual = Get-Sha256 -Path $target
        if ($actual -ne $recorded[$relative]) {
            $problems.Add("파일이 변조되었거나 manifest 가 오래되었습니다: $relative")
        }
    }

    $onDisk = @(Get-PackageRelativeFiles -Root $Root)
    foreach ($relative in $onDisk) {
        if ([string]::Equals($relative, $manifestName, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $recorded.ContainsKey($relative)) {
            $problems.Add("manifest.sha256 에 등록되지 않은 파일이 있습니다: $relative")
        }
    }

    Write-Detail ("manifest 항목 {0}개, 디스크 파일 {1}개" -f $recorded.Count, $onDisk.Count)
    return , $problems.ToArray()
}

function Test-DevelopmentPathLeak {
    param([Parameter(Mandatory = $true)][string]$Root)

    $problems = New-ResultList

    # 배포본에 남으면 안 되는 문자열. 소문자로 비교한다.
    $tokens = @(
        'd:\0.development',
        'c:\users\',
        'program files\microsoft visual studio',
        'third_party\third_party',
        'third_party\opencascade',
        'heatingmold.mvp.sln'
    )

    $textExtensions = @('.json', '.txt', '.md', '.html', '.htm', '.css', '.js', '.xml', '.config', '.bat', '.cmd', '.ps1')

    $scanned = 0
    foreach ($file in (Get-ChildItem -LiteralPath $Root -Recurse -File -Force)) {
        if ($textExtensions -notcontains $file.Extension.ToLowerInvariant()) { continue }
        $scanned++

        $text = $null
        try {
            $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
        }
        catch {
            continue
        }
        if ([string]::IsNullOrEmpty($text)) { continue }

        $lower = $text.ToLowerInvariant()
        foreach ($token in $tokens) {
            if ($lower.Contains($token)) {
                $relative = $file.FullName.Substring($Root.TrimEnd('\').Length + 1).Replace('\', '/')
                $problems.Add("개발 PC 경로가 남아 있습니다: $relative (포함: $token)")
                break
            }
        }
    }

    Write-Detail ("텍스트 파일 {0}개 검사" -f $scanned)
    return , $problems.ToArray()
}

function Test-DependencyPresence {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]$Config
    )

    $problems = New-ResultList
    $generatedPath = Join-Path (Get-DeployDir) 'generated\native-dependencies.json'
    if (-not (Test-Path -LiteralPath $generatedPath)) {
        Write-Warn 'generated/native-dependencies.json 이 없어 DLL 목록 검사를 건너뜁니다. Get-Dependencies.ps1 을 실행하세요.'
        return , $problems.ToArray()
    }

    $report = Read-JsonFile -Path $generatedPath
    $appDir = Join-Path $Root 'App'

    foreach ($dll in $report.appDlls) {
        if (-not (Test-Path -LiteralPath (Join-Path $appDir $dll.name))) {
            $problems.Add("앱 DLL 이 없습니다: App\$($dll.name)")
        }
    }
    foreach ($dll in $report.vcRuntimeDlls) {
        if (-not (Test-Path -LiteralPath (Join-Path $appDir $dll.name))) {
            $problems.Add("VC 런타임 DLL 이 없습니다: App\$($dll.name)")
        }
    }

    $appFiles = @(Get-ChildItem -LiteralPath $appDir -File -Filter '*.dll' | ForEach-Object { $_.Name })
    foreach ($name in $report.publishProvided) {
        $found = $false
        foreach ($appFile in $appFiles) {
            if ([string]::Equals($appFile, $name.name, [System.StringComparison]::OrdinalIgnoreCase)) {
                $found = $true
                break
            }
        }
        if (-not $found) {
            $problems.Add("publish 가 제공해야 하는 파일이 없습니다: App\$($name.name)")
        }
    }

    Write-Detail ("앱 DLL {0}개, VC 런타임 {1}개, publish 제공 {2}개 확인" -f $report.appDlls.Count, $report.vcRuntimeDlls.Count, $report.publishProvided.Count)
    return , $problems.ToArray()
}

function Test-FolderPeDependencies {
    param([Parameter(Mandatory = $true)][string]$Root)

    $problems = New-ResultList
    $appDir = Join-Path $Root 'App'

    $result = Test-PackageDependencyFolder -Folder $appDir -ExtraProvidedNames @()
    Write-Detail ("PE 파일 {0}개 검사" -f $result.checkedFiles)

    foreach ($entry in $result.missing) {
        $problems.Add("PE 의존성 누락: $($entry.file) → $($entry.missing)")
    }

    return , $problems.ToArray()
}

function Invoke-LauncherCheck {
    param([Parameter(Mandatory = $true)][string]$Root)

    $problems = New-ResultList
    $launcher = Join-Path $Root 'HeatingMold.Start.exe'
    if (-not (Test-Path -LiteralPath $launcher)) {
        $problems.Add('HeatingMold.Start.exe 가 없어 시작 도구를 실행할 수 없습니다.')
        return , $problems.ToArray()
    }

    $process = Start-Process -FilePath $launcher -ArgumentList @('--check-only', '--quiet') -Wait -PassThru
    Write-Detail ("시작 도구 종료 코드: {0}" -f $process.ExitCode)
    if ($process.ExitCode -ne 0) {
        $problems.Add("시작 도구 --check-only 가 실패했습니다. exit=$($process.ExitCode). 로그: %LOCALAPPDATA%\HeatingMold\Logs\launcher.log")
    }

    return , $problems.ToArray()
}

# ---------------------------------------------------------------- 실행

Write-Step '배포 패키지 검증'

$resolvedPath = $PackagePath
if ([string]::IsNullOrWhiteSpace($resolvedPath)) {
    $resolvedPath = Find-DefaultPackage -Root $outputRoot
}
elseif (-not (Test-Path -LiteralPath $resolvedPath)) {
    $resolvedPath = Resolve-RepoPath -Path $resolvedPath
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        throw "패키지 경로를 찾을 수 없습니다: $PackagePath"
    }
}

$root = $resolvedPath
$isZip = $false
if ((Get-Item -LiteralPath $resolvedPath).PSIsContainer -eq $false) {
    $isZip = $true
    $target = $ExtractTo
    if ([string]::IsNullOrWhiteSpace($target)) {
        $zipBase = [System.IO.Path]::GetFileNameWithoutExtension($resolvedPath)
        $target = Join-Path (Split-Path -Parent $resolvedPath) ('verify-test-' + $zipBase)
    }
    Write-Detail "ZIP 압축 해제: $target"
    $root = Expand-PackageZip -ZipPath $resolvedPath -Target $target
}

Write-Ok "검사 대상: $root"

$allProblems = New-ResultList
$groups = @(
    @{ Name = '필수 구조'; Items = (Test-RequiredLayout -Root $root -Config $config) },
    @{ Name = 'deployment.json'; Items = (Test-DeploymentManifestFile -Root $root -Config $config) },
    @{ Name = 'DLL 목록'; Items = (Test-DependencyPresence -Root $root -Config $config) },
    @{ Name = '개발 경로 노출'; Items = (Test-DevelopmentPathLeak -Root $root) }
)

if (-not $SkipIntegrity) {
    $groups += @{ Name = 'manifest 무결성'; Items = (Test-PackageIntegrity -Root $root) }
}
if (-not $SkipDependencyCheck) {
    $groups += @{ Name = 'PE 의존성'; Items = (Test-FolderPeDependencies -Root $root) }
}
if ($RunLauncher) {
    $groups += @{ Name = '시작 도구 실행'; Items = (Invoke-LauncherCheck -Root $root) }
}

$groupResults = New-Object System.Collections.Generic.List[object]
foreach ($group in $groups) {
    $count = $group.Items.Count
    if ($count -eq 0) {
        Write-Ok ("{0}: 문제 없음" -f $group.Name)
    }
    else {
        Write-Warn ("{0}: 문제 {1}건" -f $group.Name, $count)
        foreach ($item in $group.Items) {
            Write-Detail ("  - " + $item)
            $allProblems.Add($item)
        }
    }
    $groupResults.Add([pscustomobject]@{
        name    = $group.Name
        count   = $count
        problems = @($group.Items)
    })
}

$fileCount = @(Get-PackageRelativeFiles -Root $root).Count
$sizeText = Format-Size -Bytes (Get-DirectorySize -Path $root)

$result = [pscustomobject]@{
    schemaVersion = 1
    checkedUtc    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    source        = $resolvedPath
    sourceIsZip   = $isZip
    packageRoot   = $root
    fileCount     = $fileCount
    sizeBytes     = (Get-DirectorySize -Path $root)
    sizeText      = $sizeText
    problemCount  = $allProblems.Count
    groups        = $groupResults.ToArray()
    problems      = $allProblems.ToArray()
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path (Split-Path -Parent $root) 'package-test.json'
}
elseif (-not [System.IO.Path]::IsPathRooted($ReportPath)) {
    $ReportPath = Resolve-RepoPath -Path $ReportPath
}
Write-JsonFile -Path $ReportPath -Value $result -Depth 8

Write-Step '검증 요약'
Write-Detail ("파일 {0}개 / {1}" -f $fileCount, $sizeText)
Write-Detail ("결과 파일: {0}" -f $ReportPath)

if ($allProblems.Count -gt 0) {
    throw ("배포 패키지 검증에 실패했습니다. 문제 {0}건. 위 항목을 확인하세요." -f $allProblems.Count)
}

Write-Ok '배포 패키지 검증 통과'
$result
