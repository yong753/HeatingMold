#requires -Version 5.1
<#
    배포 의존성 보고서 생성.

    - 네이티브 브리지의 실제 PE import 폐포를 계산한다.
    - 폐포에 닿는 앱 DLL, VC 재배포 DLL, Windows 시스템 DLL, publish 출력 제공 DLL 로 나눈다.
    - 앱 DLL 이 모두 x64 인지 확인한다.
    - 해석되지 않는 DLL 이 하나라도 있으면 실패한다(추측으로 파일 목록을 채우지 않는다).

    Common.ps1, NativeDependencies.ps1, VcRuntime.ps1 을 dot-source 한 뒤 사용한다.
    이 파일은 파일을 읽기만 한다.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-RepoRelativePath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { return $null }
    return ($full.Substring($root.Length) -replace '\\', '/')
}

function Get-NativeDependencyReport {
    param([Parameter(Mandatory)] $Config)

    $repoRoot = Get-RepoRoot

    $vs = Get-VsInstallPath
    if (-not $vs) {
        throw "Visual Studio C++ 도구를 찾지 못했습니다.`n  vswhere 로 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' 구성요소를 확인하세요."
    }

    $bridgePath = Resolve-RepoPath $Config.bridgeDll
    if (-not (Test-Path -LiteralPath $bridgePath)) {
        throw "네이티브 브리지가 없습니다: $bridgePath`n  먼저 네이티브를 빌드하세요: MSBuild src\OccBridge\OccBridge.vcxproj /p:Configuration=Release /p:Platform=x64 /p:OutDir=<repo>\x64\Release\"
    }

    $searchRoots = @()
    foreach ($root in $Config.nativeSearchRoots) { $searchRoots += (Resolve-RepoPath $root) }

    $closure = Resolve-NativeDependencyClosure -SeedPaths @($bridgePath) -SearchRoots $searchRoots

    foreach ($seed in $closure.seeds) {
        if ($seed.machine -ne 'AMD64') {
            throw "브리지가 x64 바이너리가 아닙니다: $($seed.name) ($($seed.machine))"
        }
    }

    $appDlls = New-Object System.Collections.Generic.List[object]
    foreach ($dep in $closure.appDlls) {
        $info = Get-PeInfo -Path $dep.source
        if ($info.MachineName -ne 'AMD64') {
            throw "배포 대상 DLL 이 x64 가 아닙니다: $($dep.name) ($($info.MachineName))`n  $($dep.source)"
        }
        $appDlls.Add([pscustomobject]@{
            name           = $dep.name
            sourceRelative = (Get-RepoRelativePath -Path $dep.source -RepoRoot $repoRoot)
            sha256         = $dep.sha256
            size           = [int64]$dep.size
            machine        = $info.MachineName
            importedBy     = @($dep.importedBy)
        })
    }

    # dotnet publish 출력이 이미 제공하는 DLL(ijwhost.dll)은 OCCT 검색 폴더에서 찾지 않는다.
    $publishNames = @()
    if ($Config.PSObject.Properties.Name -contains 'publishProvidedNames') {
        $publishNames = @($Config.publishProvidedNames)
    }

    $publishProvided = New-Object System.Collections.Generic.List[object]
    $unresolved = New-Object System.Collections.Generic.List[object]
    foreach ($item in $closure.unresolvedDlls) {
        $match = $publishNames | Where-Object { $_ -ieq $item.name } | Select-Object -First 1
        if ($match) {
            $publishProvided.Add([pscustomobject]@{
                name       = $item.name
                providedBy = 'dotnet-publish'
                importedBy = @($item.importedBy)
            })
            continue
        }
        $unresolved.Add([pscustomobject]@{ name = $item.name; importedBy = @($item.importedBy) })
    }

    if ($unresolved.Count -gt 0) {
        $lines = ($unresolved | ForEach-Object { "  - $($_.name)  <- $((@($_.importedBy)) -join ', ')" }) -join "`n"
        throw "의존 DLL 을 찾지 못했습니다. 파일 목록을 추측으로 정하지 않습니다.`n$lines`n  검색 폴더:`n    $(($searchRoots) -join "`n    ")"
    }

    $vcSource = Find-VcRuntimeSource -Config $Config -VsInstallPath $vs
    $vcRequired = @($closure.vcRuntimeDlls | ForEach-Object { $_.name })
    $vcFiles = Resolve-VcRuntimeFiles -Source $vcSource -RequiredNames $vcRequired

    $vcOut = New-Object System.Collections.Generic.List[object]
    foreach ($file in $vcFiles) {
        $match = $closure.vcRuntimeDlls | Where-Object { $_.name -ieq $file.name } | Select-Object -First 1
        $importedBy = @()
        if ($match) { $importedBy = @($match.importedBy) }
        $vcOut.Add([pscustomobject]@{
            name          = $file.name
            component     = $file.component
            redistVersion = $vcSource.Version
            sha256        = $file.sha256
            size          = [int64]$file.size
            importedBy    = $importedBy
        })
    }

    $systemOut = New-Object System.Collections.Generic.List[object]
    foreach ($item in $closure.systemDlls) {
        $systemOut.Add([pscustomobject]@{ name = $item.name; importedBy = @($item.importedBy) })
    }

    $bridgeSha = Get-Sha256 -Path $bridgePath
    $bridgeSize = (Get-Item -LiteralPath $bridgePath).Length

    return [pscustomobject]@{
        schemaVersion    = 1
        bridgeDll        = $Config.bridgeDll
        bridgeSha256     = $bridgeSha
        bridgeSize       = [int64]$bridgeSize
        vcRedistVersion  = $vcSource.Version
        nativeSearchRoots = @($Config.nativeSearchRoots)
        appDlls          = $appDlls.ToArray()
        vcRuntimeDlls    = $vcOut.ToArray()
        publishProvided  = $publishProvided.ToArray()
        systemDlls       = $systemOut.ToArray()
    }
}
