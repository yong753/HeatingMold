[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory,

    [string]$OutputArchivePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishRoot = [System.IO.Path]::GetFullPath($PublishDirectory)
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if (-not $publishRoot.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "게시 폴더는 저장소 안에 있어야 합니다: $publishRoot"
}
if (-not (Test-Path $publishRoot -PathType Container)) {
    throw "게시 폴더를 찾을 수 없습니다: $publishRoot"
}

$requiredFiles = @(
    'HeatingMold.App.exe',
    'HeatingMold.App.dll',
    'OccBridge.dll',
    'ijwhost.dll',
    'INSTALL.md',
    'runtime-manifest.json')
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path (Join-Path $publishRoot $relativePath) -PathType Leaf)) {
        throw "패키지에 필요한 게시 파일이 없습니다: $relativePath"
    }
}

$occtRuntimeDirectory = Join-Path $publishRoot 'native/occt/bin'
if (-not (Test-Path $occtRuntimeDirectory -PathType Container) -or
    -not (Get-ChildItem -LiteralPath $occtRuntimeDirectory -Filter '*.dll' -File | Select-Object -First 1)) {
    throw "OCCT 실행 DLL이 게시 폴더에 없습니다: $occtRuntimeDirectory"
}

$manifestPath = Join-Path $publishRoot 'runtime-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($manifest.RuntimeIdentifier -ne 'win-x64' -or $manifest.SelfContained -ne $true) {
    throw '런타임 manifest가 self-contained win-x64 게시물을 가리키지 않습니다.'
}
if (@($manifest.Files).Count -eq 0) {
    throw '런타임 manifest에 파일 목록이 없습니다.'
}

$manifestPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.Files) {
    $normalizedPath = ([string]$entry.Path).Replace('/', '\').TrimStart('\')
    if ([string]::IsNullOrWhiteSpace($normalizedPath) -or -not $manifestPaths.Add($normalizedPath)) {
        throw "manifest 파일 경로가 비어 있거나 중복됐습니다: $normalizedPath"
    }
}

$actualPaths = Get-ChildItem -LiteralPath $publishRoot -File -Recurse |
    Where-Object { $_.Name -ne 'runtime-manifest.json' } |
    ForEach-Object { $_.FullName.Substring($publishRoot.TrimEnd('\').Length).TrimStart('\') }
if ($actualPaths.Count -ne $manifestPaths.Count) {
    throw '게시 폴더 파일 수와 runtime manifest 파일 수가 다릅니다.'
}
foreach ($actualPath in $actualPaths) {
    if (-not $manifestPaths.Contains($actualPath)) {
        throw "runtime manifest에 없는 게시 파일이 있습니다: $actualPath"
    }
}

foreach ($entry in $manifest.Files) {
    $relativePath = [string]$entry.Path
    if ([string]::IsNullOrWhiteSpace($relativePath) -or [System.IO.Path]::IsPathRooted($relativePath)) {
        throw "manifest 파일 경로가 올바르지 않습니다: $relativePath"
    }

    $filePath = [System.IO.Path]::GetFullPath((Join-Path $publishRoot $relativePath))
    if (-not $filePath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $filePath.StartsWith($publishRoot.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "manifest 파일 경로가 게시 폴더를 벗어납니다: $relativePath"
    }
    if (-not (Test-Path $filePath -PathType Leaf)) {
        throw "manifest 파일이 게시 폴더에 없습니다: $relativePath"
    }

    $file = Get-Item -LiteralPath $filePath
    $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.Length -ne [long]$entry.Length -or $actualHash -ne [string]$entry.Sha256) {
        throw "manifest의 크기 또는 SHA-256이 다릅니다: $relativePath"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputArchivePath)) {
    $packageDirectory = Join-Path $repositoryRoot 'x64/Packages'
    $version = (Get-Item -LiteralPath (Join-Path $publishRoot 'HeatingMold.App.exe')).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = 'dev'
    }
    $runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
    $OutputArchivePath = Join-Path $packageDirectory "HeatingMold-$version-win-x64-$runId.zip"
} else {
    $OutputArchivePath = [System.IO.Path]::GetFullPath($OutputArchivePath)
}

if (-not $OutputArchivePath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ZIP 출력은 저장소 안에 두어야 합니다: $OutputArchivePath"
}
if (Test-Path $OutputArchivePath) {
    throw "기존 ZIP을 덮어쓰지 않도록 새 출력 경로를 지정하세요: $OutputArchivePath"
}

$archiveDirectory = Split-Path -Parent $OutputArchivePath
New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $OutputArchivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $OutputArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$OutputArchivePath.sha256" -Value "$archiveHash  $(Split-Path -Leaf $OutputArchivePath)" -Encoding ascii

Write-Host "설치 ZIP 패키지를 만들었습니다: $OutputArchivePath"
Write-Host "SHA-256: $archiveHash"
