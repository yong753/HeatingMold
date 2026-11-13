#requires -Version 5.1
<#
    HeatingMold 배포 도구 공통 헬퍼.

    이 파일은 다른 배포 스크립트에서 dot-source 로 사용한다.
    저장소 경로, 설정 로드, 해시, Visual Studio 도구 탐색, 메시지 출력만 담당하며
    파일을 배포 폴더 밖으로 복사하거나 삭제하지 않는다.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:DeployScriptDir = $PSScriptRoot
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Get-RepoRoot {
    return $script:RepoRoot
}

function Get-DeployDir {
    return $script:DeployScriptDir
}

function Resolve-RepoPath {
    param([Parameter(Mandatory)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot $Path))
}

function Get-DeployConfig {
    $path = Join-Path $script:DeployScriptDir 'ReleaseConfig.json'
    if (-not (Test-Path -LiteralPath $path)) { throw "배포 설정을 찾지 못했습니다: $path" }
    $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    return ($raw | ConvertFrom-Json)
}

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ''
    Write-Host "== $Message" -ForegroundColor Cyan
}

function Write-Detail {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "   $Message" -ForegroundColor Gray
}

function Write-Ok {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "   [OK] $Message" -ForegroundColor Green
}

function Write-Warn {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "   [WARN] $Message" -ForegroundColor Yellow
}

function Stop-Deploy {
    param([Parameter(Mandatory)][string]$Message)
    throw $Message
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "해시 대상 파일이 없습니다: $Path" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-Sha256Text {
    param([Parameter(Mandatory)][string]$Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '')
    }
    finally { $sha.Dispose() }
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content
    )
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-JsonFile {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    return ($raw | ConvertFrom-Json)
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value,
        [int]$Depth = 24
    )
    $json = $Value | ConvertTo-Json -Depth $Depth
    Write-Utf8File -Path $Path -Content $json
}

function Get-VsWherePath {
    $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if (-not $pf86) { $pf86 = 'C:\Program Files (x86)' }
    $candidate = Join-Path $pf86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $candidate) { return $candidate }
    return $null
}

function Get-VsInstallPath {
    $vswhere = Get-VsWherePath
    if (-not $vswhere) { return $null }
    $out = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $out) { return $null }
    $first = @($out) | Select-Object -First 1
    if (-not $first) { return $null }
    if (-not (Test-Path -LiteralPath $first)) { return $null }
    return $first.Trim()
}

function Get-MSBuildPath {
    param([string]$VsInstallPath)

    if (-not $VsInstallPath) { $VsInstallPath = Get-VsInstallPath }
    if (-not $VsInstallPath) { return $null }
    $candidate = Join-Path $VsInstallPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path -LiteralPath $candidate) { return $candidate }
    return $null
}

function Get-VcRedistRoot {
    param([string]$VsInstallPath)

    if (-not $VsInstallPath) { $VsInstallPath = Get-VsInstallPath }
    if (-not $VsInstallPath) { return $null }
    $candidate = Join-Path $VsInstallPath 'VC\Redist\MSVC'
    if (Test-Path -LiteralPath $candidate) { return $candidate }
    return $null
}

function Select-NewestVersionDirectory {
    param([Parameter(Mandatory)][string]$Parent)

    if (-not (Test-Path -LiteralPath $Parent)) { return $null }
    $dirs = Get-ChildItem -LiteralPath $Parent -Directory | Sort-Object {
        try { [version]$_.Name } catch { [version]'0.0.0.0' }
    } -Descending
    return ($dirs | Select-Object -First 1)
}

function Test-IsSubPathOf {
    param(
        [Parameter(Mandatory)][string]$Child,
        [Parameter(Mandatory)][string]$Parent
    )
    $c = [System.IO.Path]::GetFullPath($Child).TrimEnd('\') + '\'
    $p = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    return $c.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)
}

function New-CleanDirectory {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Get-DirectorySize {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0 }
    return [int64]$sum
}

function Format-Size {
    param([Parameter(Mandatory)][int64]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

function Get-GitValue {
    param([Parameter(Mandatory)][string[]]$Arguments)

    try {
        $out = & git @Arguments 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return (@($out) -join "`n").Trim()
    }
    catch { return $null }
}
