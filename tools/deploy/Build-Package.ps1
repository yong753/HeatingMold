#requires -Version 5.1
<#
    HeatingMold 오프라인 배포 패키지 빌드.

    한 번 실행으로 다음을 수행한다.
      1) 네이티브 브리지(OccBridge.dll)를 Release/x64 로 빌드
      2) HeatingMold.App 을 self-contained win-x64 로 publish
      3) 시작 도구(HeatingMold.Start.exe, /MT 정적 CRT) 를 빌드
      4) PE 의존 폐포로 계산한 네이티브 DLL 과 app-local VC 런타임을 복사
      5) OCCT 런타임 리소스/라이선스, 예제 STEP, 안내 문서를 배치
      6) deployment.json / build-info.json / manifest.sha256 생성
      7) ZIP 생성, ZIP 해시 기록, 추출본에서 시작 도구 무결성 검사

    이 스크립트는 저장소 밖(사용자 PC)에 아무것도 설치하지 않는다.
    산출물은 artifacts/deploy 아래에만 만든다.

    사용 예
      powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Package.ps1
      powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Package.ps1 -Version 2026.11.13-test1
      powershell -ExecutionPolicy Bypass -File tools\deploy\Build-Package.ps1 -SkipNativeBuild -SkipPublish -NoZip
#>
[CmdletBinding()]
param(
    # 패키지 버전 문자열. 생략하면 <yyyyMMdd-HHmm>-<git short sha>[-dirty].
    [string]$Version,

    # 배포용 릴리스 노트 파일. 생략하면 assets\ReleaseNotes.txt 를 사용한다.
    [string]$ReleaseNotesPath,

    # 이미 빌드된 x64\Release\OccBridge.dll 을 그대로 사용한다.
    [switch]$SkipNativeBuild,

    # 이미 publish 된 폴더를 재사용한다. -StageDir 와 함께 쓰지 않으면 의미가 없다.
    [switch]$SkipPublish,

    # ZIP 을 만들지 않고 폴더까지만 만든다.
    [switch]$NoZip,

    # 추출본 검증(시작 도구 --check-only)을 생략한다.
    [switch]$SkipVerify,

    # Build-Manual.ps1 이 만든 매뉴얼 폴더(index.html 이 있는 폴더). 생략하면 assets\Manual 을 쓴다.
    [string]$ManualPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')
. (Join-Path $PSScriptRoot 'VcRuntime.ps1')
. (Join-Path $PSScriptRoot 'DependencyResolver.ps1')

# ---------------------------------------------------------------------------
# 공통 헬퍼
# ---------------------------------------------------------------------------

function Invoke-ConsoleTool {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Label
    )

    Write-Detail $Label
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Exe @Arguments
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $saved }

    if ($code -ne 0) {
        throw "$Label 실패 (종료 코드 $code).`n  $Exe $($Arguments -join ' ')"
    }
}

# 패키지 루트 기준 상대 경로 목록. ZIP 과 manifest.sha256 이 같은 목록을 쓴다.
function Get-PackageFileList {
    param(
        [Parameter(Mandatory)][string]$Root,
        [string]$ExcludeRelative = ''
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $prefix = $rootFull.TrimEnd('\').Length + 1
    $list = New-Object System.Collections.Generic.List[object]

    foreach ($file in Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force) {
        $relative = $file.FullName.Substring($prefix).Replace('\', '/')
        if ($ExcludeRelative -and $relative -eq $ExcludeRelative) { continue }
        $list.Add([pscustomobject]@{
            Relative = $relative
            FullName = $file.FullName
            Size     = [int64]$file.Length
        })
    }

    return $list.ToArray()
}

function Remove-ExcludedFiles {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string[]]$Patterns
    )

    $removed = 0
    foreach ($pattern in $Patterns) {
        $matches = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force -Filter $pattern -ErrorAction SilentlyContinue)
        foreach ($file in $matches) {
            Remove-Item -LiteralPath $file.FullName -Force
            $removed++
        }
    }
    return $removed
}

# ---------------------------------------------------------------------------
# 1. 환경 확인
# ---------------------------------------------------------------------------

Write-Step '배포 환경 확인'

$repoRoot = Get-RepoRoot
$deployDir = Get-DeployDir
$config = Get-DeployConfig

$vsPath = Get-VsInstallPath
if (-not $vsPath) {
    throw "Visual Studio(C++ 데스크톱 개발) 설치를 찾지 못했습니다.`n  vswhere 로 Microsoft.VisualStudio.Component.VC.Tools.x86.x64 구성 요소를 확인하세요."
}
$msbuild = Get-MSBuildPath $vsPath
if (-not $msbuild) { throw "MSBuild.exe 를 찾지 못했습니다: $vsPath" }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if (-not $dotnet) { throw 'dotnet CLI 를 찾지 못했습니다. .NET SDK 가 PATH 에 있어야 합니다.' }
$dotnetExe = $dotnet.Source

Write-Ok "저장소: $repoRoot"
Write-Ok "Visual Studio: $vsPath"
Write-Ok "dotnet: $dotnetExe"

$gitCommit = Get-GitValue @('rev-parse', '--short', 'HEAD')
$gitBranch = Get-GitValue @('rev-parse', '--abbrev-ref', 'HEAD')
$gitStatus = Get-GitValue @('status', '--porcelain')
$gitDirty = -not [string]::IsNullOrWhiteSpace($gitStatus)

if (-not $Version) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmm'
    $sha = if ($gitCommit) { $gitCommit } else { 'nogit' }
    $Version = "$stamp-$sha"
    if ($gitDirty) { $Version = "$Version-dirty" }
}

$packageName = "$($config.packagePrefix)-$Version-win-x64"
$outputRoot = Resolve-RepoPath $config.outputRoot
$workDir = Join-Path $outputRoot $Version
$stageDir = Join-Path $workDir $packageName
$appDir = Join-Path $stageDir 'App'
$zipPath = Join-Path $workDir ($packageName + '.zip')

Write-Detail "패키지 버전: $Version"
Write-Detail "패키지 폴더: $stageDir"
if ($gitDirty) { Write-Warn '작업 트리에 커밋되지 않은 변경이 있습니다. 낡은 산출물이 섞이지 않도록 네이티브/앱을 다시 빌드합니다.' }

# ---------------------------------------------------------------------------
# 2. 네이티브 브리지 빌드
# ---------------------------------------------------------------------------

Write-Step '네이티브 브리지 빌드'

$bridgeProject = Join-Path $repoRoot 'src\OccBridge\OccBridge.vcxproj'
$bridgeDll = Resolve-RepoPath $config.bridgeDll
$nativeOutDir = Split-Path -Parent $bridgeDll

if ($SkipNativeBuild) {
    if (-not (Test-Path -LiteralPath $bridgeDll)) { throw "-SkipNativeBuild 를 지정했지만 브리지가 없습니다: $bridgeDll" }
    Write-Warn "네이티브 빌드를 건너뜁니다. 기존 DLL 을 사용합니다: $bridgeDll"
}
else {
    # OutDir 를 절대 경로로 지정하지 않으면 src\OccBridge\x64\Release 에 만들어져
    # 앱이 참조하는 x64\Release\OccBridge.dll 이 갱신되지 않는다(프로젝트 AGENTS.md 기록).
    $nativeArgs = @(
        $bridgeProject,
        '/t:Build',
        '/p:Configuration=Release',
        '/p:Platform=x64',
        "/p:OutDir=$nativeOutDir\",
        '/v:minimal',
        '/nologo'
    )
    try {
        Invoke-ConsoleTool -Exe $msbuild -Arguments $nativeArgs -Label 'MSBuild OccBridge(Release/x64)'
    }
    catch {
        throw "$($_.Exception.Message)`n  실행 중인 HeatingMold.App 이 파일을 잠그고 있다면 앱을 닫고 다시 실행하세요."
    }
}

if (-not (Test-Path -LiteralPath $bridgeDll)) { throw "네이티브 브리지가 없습니다: $bridgeDll" }
$bridgeSha = Get-Sha256 $bridgeDll
$bridgeSize = (Get-Item -LiteralPath $bridgeDll).Length
Write-Ok "OccBridge.dll $(Format-Size $bridgeSize)  $($bridgeSha.Substring(0,16))…"

# ---------------------------------------------------------------------------
# 3. 스테이징 준비 + self-contained publish
# ---------------------------------------------------------------------------

Write-Step '패키지 스테이징'

if (Test-Path -LiteralPath $workDir) {
    # 같은 버전 폴더를 재사용하면 이전 실행의 잔여 파일이 manifest 에 섞인다.
    Write-Detail "기존 작업 폴더를 비웁니다: $workDir"
    Remove-Item -LiteralPath $workDir -Recurse -Force
}
New-Item -ItemType Directory -Path $appDir -Force | Out-Null

$appProject = Resolve-RepoPath $config.appProject

if ($SkipPublish) {
    Write-Warn '-SkipPublish 를 지정했습니다. publish 를 실행하지 않습니다.'
}
else {
    $publishArgs = @(
        'publish',
        $appProject,
        '-c', 'Release',
        '-r', $config.runtimeIdentifier,
        '--self-contained', 'true',
        '-p:Platform=x64',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:PublishAot=false',
        '-p:PublishReadyToRun=false',
        '-p:DebugType=none',
        '-p:GenerateDocumentationFile=false',
        '-o', $appDir,
        '--nologo'
    )
    Invoke-ConsoleTool -Exe $dotnetExe -Arguments $publishArgs -Label 'dotnet publish (self-contained win-x64)'
}

$appExe = Join-Path $appDir "$($config.appAssemblyName).exe"
if (-not (Test-Path -LiteralPath $appExe)) { throw "앱 실행 파일이 publish 출력에 없습니다: $appExe" }
Write-Ok "HeatingMold.App.exe $(Format-Size (Get-Item -LiteralPath $appExe).Length)"

# ---------------------------------------------------------------------------
# 4. C++/CLI 호스트(Ijwhost.dll) 확인
# ---------------------------------------------------------------------------

Write-Step 'C++/CLI 호스트 확인'

$ijw = @(Get-ChildItem -LiteralPath $appDir -File -Filter 'ijwhost.dll' -ErrorAction SilentlyContinue)
if ($ijw.Count -eq 0) {
    $fallback = Join-Path $repoRoot 'src\HeatingMold.App\bin\x64\Release\net8.0-windows\Ijwhost.dll'
    if (Test-Path -LiteralPath $fallback) {
        Copy-Item -LiteralPath $fallback -Destination (Join-Path $appDir 'Ijwhost.dll')
        Write-Warn "publish 출력에 ijwhost.dll 이 없어 앱 빌드 출력에서 복사했습니다: $fallback"
        $ijw = @(Get-ChildItem -LiteralPath $appDir -File -Filter 'ijwhost.dll')
    }
}
if ($ijw.Count -eq 0) {
    throw 'ijwhost.dll 이 없습니다. UseIJWHost=true 인 C++/CLI 참조 프로젝트에서 self-contained publish 가 올바르게 되었는지 확인하세요.'
}
Write-Ok "ijwhost.dll $(Format-Size $ijw[0].Length)"

# ---------------------------------------------------------------------------
# 5. 네이티브 의존 폐포 + VC 런타임 복사
# ---------------------------------------------------------------------------

Write-Step '네이티브 의존성 복사'

$report = Get-NativeDependencyReport -Config $config
Write-Ok "브리지 의존 폐포: 앱 DLL $($report.appDlls.Count)개 / VC 런타임 $($report.vcRuntimeDlls.Count)개 / 시스템 DLL $($report.systemDlls.Count)개"

$storedPath = Join-Path $deployDir 'generated\native-dependencies.json'
if (Test-Path -LiteralPath $storedPath) {
    $stored = Read-JsonFile $storedPath
    $storedNames = @($stored.appDlls | ForEach-Object { $_.name }) | Sort-Object
    $freshNames = @($report.appDlls | ForEach-Object { $_.name }) | Sort-Object
    $added = @($freshNames | Where-Object { $storedNames -notcontains $_ })
    $removed = @($storedNames | Where-Object { $freshNames -notcontains $_ })
    if ($added.Count -gt 0) { Write-Warn "기록된 의존성에 없던 DLL 이 추가되었습니다: $($added -join ', ')" }
    if ($removed.Count -gt 0) { Write-Warn "기록된 의존성에 있던 DLL 이 사라졌습니다: $($removed -join ', ')" }
    if ($added.Count -eq 0 -and $removed.Count -eq 0) { Write-Detail 'generated\native-dependencies.json 과 같은 구성입니다.' }
}

foreach ($dll in $report.appDlls) {
    $source = Resolve-RepoPath $dll.sourceRelative
    if (-not (Test-Path -LiteralPath $source)) { throw "배포할 네이티브 DLL 이 없습니다: $source" }
    $target = Join-Path $appDir $dll.name
    if (Test-Path -LiteralPath $target) {
        $existing = Get-Sha256 $target
        if ($existing -ne $dll.sha256) {
            throw "publish 출력과 의존 DLL 이 같은 이름으로 충돌합니다: $($dll.name)`n  publish: $existing`n  의존성: $($dll.sha256)"
        }
    }
    Copy-Item -LiteralPath $source -Destination $target -Force
}

$vcSource = Find-VcRuntimeSource -Config $config -VsInstallPath $vsPath
$vcNames = @($report.vcRuntimeDlls | ForEach-Object { $_.name })
$vcFiles = Resolve-VcRuntimeFiles -Source $vcSource -RequiredNames $vcNames
foreach ($file in $vcFiles) {
    Copy-Item -LiteralPath $file.source -Destination (Join-Path $appDir $file.name) -Force
}
Write-Ok "네이티브 DLL $($report.appDlls.Count)개 / VC 런타임 $($vcFiles.Count)개 (VC 재배포 $($vcSource.Version))"

$publishProvided = @($report.publishProvided | ForEach-Object { $_.name })
foreach ($name in $publishProvided) {
    $found = @(Get-ChildItem -LiteralPath $appDir -File -Filter $name -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0) { throw "publish 가 제공해야 하는 파일이 없습니다: $name" }
}

# ---------------------------------------------------------------------------
# 6. OCCT 런타임 리소스 + 라이선스
# ---------------------------------------------------------------------------

Write-Step 'OCCT 리소스와 라이선스'

$occtResourceSource = Resolve-RepoPath $config.occt.resourceSourceDir
$occtResourceTarget = Join-Path $appDir 'occt-resources'
New-Item -ItemType Directory -Path $occtResourceTarget -Force | Out-Null

foreach ($folder in $config.occt.runtimeResourceFolders) {
    $source = Join-Path $occtResourceSource $folder
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "OCCT 런타임 리소스 폴더가 없습니다: $source`n  ReleaseConfig.json 의 occt.runtimeResourceFolders 를 확인하세요."
    }
    $target = Join-Path $occtResourceTarget $folder
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source '*') -Destination $target -Recurse -Force
}
Write-Ok "OCCT 리소스 $($config.occt.runtimeResourceFolders.Count)개 폴더"

$licenseDir = Join-Path $stageDir 'Licenses'
New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null

foreach ($license in $config.occt.licenseFiles) {
    $source = Resolve-RepoPath $license
    if (-not (Test-Path -LiteralPath $source)) { throw "라이선스 파일이 없습니다: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $licenseDir (Split-Path -Leaf $source)) -Force
}

$assetLicenseDir = Join-Path $deployDir 'assets\Licenses'
if (Test-Path -LiteralPath $assetLicenseDir) {
    Copy-Item -LiteralPath (Join-Path $assetLicenseDir '*') -Destination $licenseDir -Recurse -Force
}

# ---------------------------------------------------------------------------
# 7. 예제 모델
# ---------------------------------------------------------------------------

Write-Step '예제 모델'

$exampleSource = Resolve-RepoPath $config.exampleModel.source
if (-not (Test-Path -LiteralPath $exampleSource)) { throw "예제 모델이 없습니다: $exampleSource" }
$exampleSha = Get-Sha256 $exampleSource
if ($exampleSha -ne $config.exampleModel.sha256) {
    throw "예제 모델 해시가 설정과 다릅니다: $exampleSource`n  실제: $exampleSha`n  설정: $($config.exampleModel.sha256)"
}

$packagedExample = Join-Path $appDir ($config.exampleModel.packageRelativePath -replace '/', '\')
if (Test-Path -LiteralPath $packagedExample) {
    $packagedSha = Get-Sha256 $packagedExample
    if ($packagedSha -ne $config.exampleModel.sha256) {
        throw "publish 로 복사된 예제 모델 해시가 원본과 다릅니다: $packagedExample"
    }
}
else {
    $targetDir = Split-Path -Parent $packagedExample
    if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
    Copy-Item -LiteralPath $exampleSource -Destination $packagedExample -Force
    Write-Warn '예제 모델이 publish 출력에 없어 직접 복사했습니다. csproj 의 CopyToPublishDirectory 를 확인하세요.'
}
Write-Ok "Mold_Cover_Trim.step $(Format-Size (Get-Item -LiteralPath $packagedExample).Length)"

# ---------------------------------------------------------------------------
# 8. 시작 도구 빌드/복사
# ---------------------------------------------------------------------------

Write-Step '시작 도구'

$launcherProject = Join-Path $deployDir 'Launcher\HeatingMold.Start.vcxproj'
$launcherArgs = @(
    $launcherProject,
    '/t:Build',
    '/p:Configuration=Release',
    '/p:Platform=x64',
    '/v:minimal',
    '/nologo'
)
Invoke-ConsoleTool -Exe $msbuild -Arguments $launcherArgs -Label 'MSBuild HeatingMold.Start(Release/x64)'

$launcherOut = Join-Path $repoRoot 'x64\StartLauncher\Release\HeatingMold.Start.exe'
if (-not (Test-Path -LiteralPath $launcherOut)) { throw "시작 도구가 빌드되지 않았습니다: $launcherOut" }
Copy-Item -LiteralPath $launcherOut -Destination (Join-Path $stageDir 'HeatingMold.Start.exe') -Force
Write-Ok "HeatingMold.Start.exe $(Format-Size (Get-Item -LiteralPath $launcherOut).Length)"

# ---------------------------------------------------------------------------
# 9. 안내 문서/매뉴얼
# ---------------------------------------------------------------------------

Write-Step '안내 문서'

$assetsDir = Join-Path $deployDir 'assets'
$readmeSource = Join-Path $assetsDir '처음_읽어주세요.txt'
if (-not (Test-Path -LiteralPath $readmeSource)) { throw "안내 문서 원본이 없습니다: $readmeSource" }
Copy-Item -LiteralPath $readmeSource -Destination (Join-Path $stageDir '처음_읽어주세요.txt') -Force

if ($ReleaseNotesPath) {
    $notesSource = Resolve-RepoPath $ReleaseNotesPath
    if (-not (Test-Path -LiteralPath $notesSource)) { throw "릴리스 노트가 없습니다: $notesSource" }
}
else {
    $notesSource = Join-Path $assetsDir 'ReleaseNotes.txt'
    if (-not (Test-Path -LiteralPath $notesSource)) { throw "기본 릴리스 노트가 없습니다: $notesSource" }
}
Copy-Item -LiteralPath $notesSource -Destination (Join-Path $stageDir 'ReleaseNotes.txt') -Force

$noticesSource = Join-Path $assetsDir 'THIRD-PARTY-NOTICES.txt'
if (-not (Test-Path -LiteralPath $noticesSource)) { throw "제3자 고지 원본이 없습니다: $noticesSource" }
Copy-Item -LiteralPath $noticesSource -Destination (Join-Path $stageDir 'THIRD-PARTY-NOTICES.txt') -Force

$manualSource = if ($ManualPath) { [IO.Path]::GetFullPath($ManualPath) } else { Join-Path $assetsDir 'Manual' }
if ((Test-Path -LiteralPath $manualSource) -and (Test-Path -LiteralPath (Join-Path $manualSource 'index.html'))) {
    # 매뉴얼 원본 폴더에는 정의 파일(screenshots.json, index.template.html)도 있으므로
    # 배포에 필요한 산출물만 골라 넣는다.
    $manualTarget = Join-Path $stageDir 'Manual'
    New-Item -ItemType Directory -Path $manualTarget -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $manualSource 'index.html') -Destination $manualTarget -Force
    $cssSource = Join-Path $manualSource 'manual.css'
    if (Test-Path -LiteralPath $cssSource) {
        Copy-Item -LiteralPath $cssSource -Destination $manualTarget -Force
    }
    $imagesSource = Join-Path $manualSource 'images'
    if (Test-Path -LiteralPath $imagesSource) {
        Copy-Item -LiteralPath $imagesSource -Destination $manualTarget -Recurse -Force
    }
    $pdfFiles = @(Get-ChildItem -LiteralPath $manualSource -File -Filter *.pdf -ErrorAction SilentlyContinue)
    foreach ($pdfFile in $pdfFiles) {
        Copy-Item -LiteralPath $pdfFile.FullName -Destination $manualTarget -Force
    }
    Write-Ok ("매뉴얼 포함 (PDF {0}개)" -f $pdfFiles.Count)
}
else {
    Write-Warn "매뉴얼(index.html)이 아직 없습니다. 패키지에는 포함되지 않습니다. Build-Manual.ps1 -ManualPath 로 경로를 지정할 수 있습니다."
}

# ---------------------------------------------------------------------------
# 10. 배포 설정 파일
# ---------------------------------------------------------------------------

Write-Step '배포 설정 파일'

$builtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

$deploymentManifest = [ordered]@{
    schemaVersion     = 1
    layout            = 'packaged'
    productName       = $config.productName
    packageVersion    = $Version
    builtUtc          = $builtUtc
    runtimeIdentifier = $config.runtimeIdentifier
    appExecutable     = "$($config.appAssemblyName).exe"
    occtResources     = 'occt-resources'
    exampleModel      = $config.exampleModel.packageRelativePath
}
Write-JsonFile -Path (Join-Path $appDir 'deployment.json') -Value $deploymentManifest

$publishFiles = Get-PackageFileList -Root $appDir
$buildInfo = [ordered]@{
    schemaVersion     = 1
    packageName       = $packageName
    version           = $Version
    productName       = $config.productName
    builtUtc          = $builtUtc
    gitBranch         = $gitBranch
    gitCommit         = $gitCommit
    gitDirty          = $gitDirty
    dotnetVersion     = (& $dotnetExe --version)
    msbuild           = $msbuild
    runtimeIdentifier = $config.runtimeIdentifier
    targetFramework   = $config.targetFramework
    bridgeDll         = $config.bridgeDll
    bridgeSha256      = $bridgeSha
    bridgeSize        = $bridgeSize
    vcRedistVersion   = $vcSource.Version
    appDllCount       = $report.appDlls.Count
    vcRuntimeCount    = $vcFiles.Count
    exampleModel      = $config.exampleModel.packageRelativePath
    exampleModelSha256 = $config.exampleModel.sha256
    publishFileCount  = $publishFiles.Count
    policy            = $config.policy
}
Write-JsonFile -Path (Join-Path $stageDir 'build-info.json') -Value $buildInfo
Write-Ok 'deployment.json / build-info.json'

# ---------------------------------------------------------------------------
# 11. 제외 패턴 정리 + 의존성 최종 검사
# ---------------------------------------------------------------------------

Write-Step '패키지 정리와 의존성 검사'

$removed = Remove-ExcludedFiles -Root $appDir -Patterns $config.excludedFromPackage
Write-Detail "publish 출력에서 제외 패턴으로 $removed 개 파일을 정리했습니다."

$depCheck = Test-PackageDependencyFolder -Folder $appDir -ExtraProvidedNames @($report.publishProvided | ForEach-Object { $_.name })
if ($depCheck.missing.Count -gt 0) {
    $detail = ($depCheck.missing | ForEach-Object { "  $($_.file) -> $($_.missing)" }) -join "`n"
    throw "배포 폴더에 네이티브 의존성이 빠졌습니다($($depCheck.missing.Count)건):`n$detail"
}
Write-Ok "PE 파일 $($depCheck.checkedFiles)개 의존성 검사 통과"

foreach ($name in @('HeatingMold.App.exe', 'OccBridge.dll', 'Ijwhost.dll', 'deployment.json', 'resource\Mold_Cover_Trim.step')) {
    $path = Join-Path $appDir $name
    if (-not (Test-Path -LiteralPath $path)) { throw "패키지 필수 파일이 없습니다: App\$name" }
}

# ---------------------------------------------------------------------------
# 12. manifest.sha256
# ---------------------------------------------------------------------------

Write-Step '무결성 manifest'

$manifestPath = Join-Path $stageDir 'manifest.sha256'
$files = Get-PackageFileList -Root $stageDir -ExcludeRelative 'manifest.sha256'
$files = @($files | Sort-Object -Property { $_.Relative })

$builder = New-Object System.Text.StringBuilder
foreach ($file in $files) {
    $hash = Get-Sha256 $file.FullName
    [void]$builder.Append($hash)
    [void]$builder.Append('  ')
    [void]$builder.Append($file.Relative)
    [void]$builder.Append("`r`n")
}
Write-Utf8File -Path $manifestPath -Content $builder.ToString()
Write-Ok "manifest.sha256: $($files.Count)개 파일"

# ---------------------------------------------------------------------------
# 13. ZIP
# ---------------------------------------------------------------------------

$zipSha = $null
if ($NoZip) {
    Write-Warn '-NoZip 지정: ZIP 을 만들지 않았습니다.'
}
else {
    Write-Step 'ZIP 생성'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stageDir,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $true)

    $zipSha = Get-Sha256 $zipPath
    $zipSize = (Get-Item -LiteralPath $zipPath).Length
    Write-Utf8File -Path ($zipPath + '.sha256.txt') -Content ("$zipSha  " + (Split-Path -Leaf $zipPath) + "`r`n")
    Write-Ok "$(Split-Path -Leaf $zipPath) $(Format-Size $zipSize)"
    Write-Ok "SHA-256 $zipSha"
}

# ---------------------------------------------------------------------------
# 14. 추출본 검증
# ---------------------------------------------------------------------------

$verifySummary = 'skipped'
if (-not $NoZip -and -not $SkipVerify) {
    Write-Step '추출본 검증'

    $verifyRoot = Join-Path $workDir 'verify'
    if (Test-Path -LiteralPath $verifyRoot) { Remove-Item -LiteralPath $verifyRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $verifyRoot -Force | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $verifyRoot)

    $verifyStage = Join-Path $verifyRoot $packageName
    $verifyLauncher = Join-Path $verifyStage 'HeatingMold.Start.exe'
    if (-not (Test-Path -LiteralPath $verifyLauncher)) { throw "추출본에 시작 도구가 없습니다: $verifyLauncher" }

    $proc = Start-Process -FilePath $verifyLauncher -ArgumentList '--check-only --quiet' -WorkingDirectory $verifyStage -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        throw "추출본 무결성 검사가 실패했습니다(종료 코드 $($proc.ExitCode)).`n  %LOCALAPPDATA%\HeatingMold\Logs\launcher.log 를 확인하세요."
    }
    Write-Ok '시작 도구 무결성 검사 통과(exit 0)'
    $verifySummary = "passed (exit 0)"
}
elseif ($SkipVerify) { Write-Warn '-SkipVerify 지정: 추출본 검증을 건너뛰었습니다.' }

# ---------------------------------------------------------------------------
# 15. 보고서
# ---------------------------------------------------------------------------

Write-Step '완료'

$stageSize = Get-DirectorySize $stageDir
$reportLines = @(
    "HeatingMold 배포 패키지 빌드 보고서",
    "================================================",
    "패키지 이름   : $packageName",
    "버전          : $Version",
    "빌드 시각(UTC): $builtUtc",
    "Git           : $gitBranch @ $gitCommit$(if ($gitDirty) { ' (dirty)' } else { '' })",
    "",
    "브리지        : $($config.bridgeDll) $(Format-Size $bridgeSize)",
    "브리지 SHA256 : $bridgeSha",
    "네이티브 DLL  : $($report.appDlls.Count)개",
    "VC 런타임     : $($vcFiles.Count)개 (재배포 $($vcSource.Version))",
    "시스템 DLL    : $($report.systemDlls.Count)개 (패키지 제외)",
    "예제 모델     : $($config.exampleModel.packageRelativePath) $(Format-Size $config.exampleModel.size)",
    "",
    "패키지 폴더   : $stageDir",
    "패키지 크기   : $(Format-Size $stageSize)",
    "manifest 파일 : $($files.Count)개",
    "의존성 검사   : PE $($depCheck.checkedFiles)개 통과",
    "추출 검증     : $verifySummary",
    "",
    "ZIP           : $(if ($zipSha) { $zipPath } else { '(생성하지 않음)' })",
    "ZIP SHA256    : $(if ($zipSha) { $zipSha } else { '-' })"
)
$reportPath = Join-Path $workDir 'build-report.txt'
Write-Utf8File -Path $reportPath -Content (($reportLines -join "`r`n") + "`r`n")

Write-Host ''
foreach ($line in $reportLines) { Write-Host $line }
Write-Host ''
Write-Ok "보고서: $reportPath"

return [pscustomobject]@{
    PackageName = $packageName
    Version     = $Version
    StageDir    = $stageDir
    ZipPath     = if ($zipSha) { $zipPath } else { $null }
    ZipSha256   = $zipSha
    ReportPath  = $reportPath
}
