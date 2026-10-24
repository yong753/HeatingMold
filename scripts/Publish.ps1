[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64')]
    [string]$Platform = 'x64',

    [string]$NativeBridgeAssembly,
    [string]$OcctRoot,
    [string]$ThirdPartyRoot,
    [string]$ResourceModelPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$applicationProject = Join-Path $repositoryRoot 'src/HeatingMold.App/HeatingMold.App.csproj'
$resolvedBridge = if ([string]::IsNullOrWhiteSpace($NativeBridgeAssembly)) {
    Join-Path $repositoryRoot 'x64/Release/OccBridge.dll'
} else {
    [System.IO.Path]::GetFullPath($NativeBridgeAssembly)
}
$resolvedOcctRoot = if ([string]::IsNullOrWhiteSpace($OcctRoot)) {
    Join-Path $repositoryRoot 'third_party/opencascade-7.9.3-vc14-64'
} else {
    [System.IO.Path]::GetFullPath($OcctRoot)
}
$resolvedThirdPartyRoot = if ([string]::IsNullOrWhiteSpace($ThirdPartyRoot)) {
    Join-Path $repositoryRoot 'third_party/3rdparty-vc14-64'
} else {
    [System.IO.Path]::GetFullPath($ThirdPartyRoot)
}
$occtBinaryDirectory = Join-Path $resolvedOcctRoot 'win64/vc14/bin'
$occtResourceDirectory = Join-Path $resolvedOcctRoot 'src'

function Assert-SameFileHash {
    param(
        [Parameter(Mandatory)] [string]$SourcePath,
        [Parameter(Mandatory)] [string]$PublishedPath
    )

    if (-not (Test-Path $PublishedPath -PathType Leaf)) {
        throw "게시 폴더에 OccBridge.dll이 없습니다: $PublishedPath"
    }

    $sourceHash = (Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash
    $publishedHash = (Get-FileHash -LiteralPath $PublishedPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $publishedHash) {
        throw "게시된 OccBridge.dll의 해시가 원본과 다릅니다: $PublishedPath"
    }
}

if (-not (Test-Path $applicationProject -PathType Leaf)) {
    throw "App 프로젝트를 찾을 수 없습니다: $applicationProject"
}
if (-not (Test-Path $resolvedBridge -PathType Leaf)) {
    throw "OccBridge.dll이 없습니다. 먼저 Native Release|x64 빌드를 완료하세요: $resolvedBridge"
}
if (-not (Test-Path $occtBinaryDirectory -PathType Container)) {
    throw "OCCT 실행 DLL 폴더가 없습니다: $occtBinaryDirectory"
}
if (-not (Test-Path $occtResourceDirectory -PathType Container)) {
    throw "OCCT 리소스 폴더가 없습니다: $occtResourceDirectory"
}
if (-not (Test-Path $resolvedThirdPartyRoot -PathType Container)) {
    throw "OCCT 제3자 런타임 폴더가 없습니다: $resolvedThirdPartyRoot"
}

$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$publishRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repositoryRoot "x64/Publish/win-x64/$runId"
} else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
$allowedPublishRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'x64/Publish'))
$allowedPublishPrefix = $allowedPublishRoot.TrimEnd('\') + '\'
if (-not $publishRoot.StartsWith($allowedPublishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish 출력은 저장소 x64/Publish 아래에 두어야 합니다: $publishRoot"
}
if (Test-Path $publishRoot) {
    throw "기존 폴더를 덮어쓰지 않도록 새 출력 경로를 지정하세요: $publishRoot"
}

$verificationIntermediateRoot = Join-Path $repositoryRoot "x64/Publish/.intermediate/$runId"
$dotnetCliHome = if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME)) {
    Join-Path $env:TEMP 'HeatingMold.DotnetCliHome'
} else {
    $env:DOTNET_CLI_HOME
}
$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $verificationIntermediateRoot -Force | Out-Null

$restoreArguments = @(
    'restore', $applicationProject, '--runtime', 'win-x64', '--ignore-failed-sources',
    "/p:NativeBridgeAssembly=$resolvedBridge",
    "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
    "/p:Platform=$Platform",
    '-p:NuGetAudit=false')
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) {
    throw "App 패키지 복원에 실패했습니다. 종료 코드: $LASTEXITCODE"
}

$publishArguments = @(
    'publish', $applicationProject,
    '--configuration', $Configuration,
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '--output', $publishRoot,
    "/p:NativeBridgeAssembly=$resolvedBridge",
    "/p:VerificationIntermediateRoot=$verificationIntermediateRoot",
    "/p:Platform=$Platform",
    '-p:PublishSingleFile=false',
    '-p:PublishReadyToRun=false',
    '-p:UseAppHost=true',
    '-p:UseIJWHost=true',
    '-p:NuGetAudit=false')
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "App 게시에 실패했습니다. 종료 코드: $LASTEXITCODE"
}

Assert-SameFileHash $resolvedBridge (Join-Path $publishRoot 'OccBridge.dll')
foreach ($requiredFile in @('HeatingMold.App.exe', 'HeatingMold.App.dll', 'ijwhost.dll')) {
    $requiredPath = Join-Path $publishRoot $requiredFile
    if (-not (Test-Path $requiredPath -PathType Leaf)) {
        throw "게시 결과에 필요한 파일이 없습니다: $requiredPath"
    }
}

$packagedOcctRoot = Join-Path $publishRoot 'native/occt'
$packagedOcctBinaryDirectory = Join-Path $packagedOcctRoot 'bin'
$packagedOcctResourceDirectory = Join-Path $packagedOcctRoot 'src'
$packagedThirdPartyRoot = Join-Path $publishRoot 'native/3rdparty'
New-Item -ItemType Directory -Path $packagedOcctBinaryDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $packagedOcctResourceDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $packagedThirdPartyRoot -Force | Out-Null

Get-ChildItem -LiteralPath $occtBinaryDirectory -Filter '*.dll' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $packagedOcctBinaryDirectory $_.Name)
}
Get-ChildItem -LiteralPath $occtResourceDirectory | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $packagedOcctResourceDirectory -Recurse
}
Get-ChildItem -LiteralPath $resolvedThirdPartyRoot -Filter '*.dll' -File -Recurse | ForEach-Object {
    $relativeDirectory = $_.DirectoryName.Substring($resolvedThirdPartyRoot.TrimEnd('\').Length).TrimStart('\')
    $destinationDirectory = if ([string]::IsNullOrWhiteSpace($relativeDirectory)) {
        $packagedThirdPartyRoot
    } else {
        Join-Path $packagedThirdPartyRoot $relativeDirectory
    }
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $destinationDirectory $_.Name)
}

if ([string]::IsNullOrWhiteSpace($ResourceModelPath)) {
    $defaultModelPath = Join-Path $repositoryRoot 'resource/Mold.stp'
    if (Test-Path $defaultModelPath -PathType Leaf) {
        $ResourceModelPath = $defaultModelPath
    }
} else {
    $ResourceModelPath = [System.IO.Path]::GetFullPath($ResourceModelPath)
}
if (-not [string]::IsNullOrWhiteSpace($ResourceModelPath)) {
    if (-not (Test-Path $ResourceModelPath -PathType Leaf)) {
        throw "지정한 기본 모델 파일이 없습니다: $ResourceModelPath"
    }

    $resourceDirectory = Join-Path $publishRoot 'resource'
    New-Item -ItemType Directory -Path $resourceDirectory -Force | Out-Null
    Copy-Item -LiteralPath $ResourceModelPath -Destination (Join-Path $resourceDirectory 'Mold.stp')
}

$installationGuide = @'
# HeatingMold 설치 및 실행

1. 이 폴더 전체를 Windows x64 PC의 쓰기 가능한 위치에 풉니다.
2. Microsoft Visual C++ 2015-2022 Redistributable (x64)을 설치합니다.
3. `HeatingMold.App.exe`를 실행하고 STEP 모델을 엽니다.

프로젝트는 사용자가 선택한 `.hmproj` 경로에 저장합니다. OCCT 로그는 `%LOCALAPPDATA%\HeatingMold\Logs`에 기록합니다. 이 배포 폴더에는 원본 개발 저장소, Visual Studio, OCCT SDK가 필요하지 않습니다.
'@
Set-Content -LiteralPath (Join-Path $publishRoot 'INSTALL.md') -Value $installationGuide -Encoding utf8

$manifestFiles = Get-ChildItem -LiteralPath $publishRoot -File -Recurse |
    Where-Object { $_.Name -ne 'runtime-manifest.json' } |
    Sort-Object FullName |
    ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName.Substring($publishRoot.TrimEnd('\').Length).TrimStart('\').Replace('\', '/')
            Length = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
$manifest = [pscustomobject]@{
    Product = 'HeatingMold'
    Configuration = $Configuration
    Platform = $Platform
    RuntimeIdentifier = 'win-x64'
    SelfContained = $true
    Files = @($manifestFiles)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $publishRoot 'runtime-manifest.json') -Encoding utf8

Write-Host "폴더 게시가 완료되었습니다: $publishRoot"
Write-Host 'Microsoft Visual C++ 2015-2022 Redistributable (x64) 설치 후 실행하세요.'
